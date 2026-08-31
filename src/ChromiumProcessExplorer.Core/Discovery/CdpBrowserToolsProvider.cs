using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A target advertised by a Chromium remote-debugging endpoint.</summary>
public sealed record CdpInspectableTarget(
    string TargetId,
    string Type,
    string Title,
    string Url,
    string? DevToolsFrontendUrl,
    string? WebSocketDebuggerUrl);

/// <summary>Targets available through one validated CDP endpoint.</summary>
public sealed record CdpTargetListResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<CdpInspectableTarget> Targets,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>
/// One frame reported by the browser's internal process diagnostics page.
/// </summary>
public sealed record CdpProcessInternalsFrame(
    string WebContentsTitle,
    int Depth,
    int InternalProcessId,
    int RoutingId,
    int AgentSchedulingGroupId,
    ProcessIdentity? Process,
    string Lifecycle,
    string? Url,
    int SiteInstanceId,
    int SiteInstanceGroupId,
    int BrowsingInstanceId,
    string? SiteUrl,
    string? ProcessLockUrl);

/// <summary>
/// Frame/process information extracted from a hidden process-internals target.
/// </summary>
public sealed record CdpProcessInternalsResult(
    DateTimeOffset CapturedAt,
    int BrowserProcessId,
    string InternalPageUrl,
    IReadOnlyList<CdpProcessInternalsFrame> Frames,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>
/// Provides interactive DevTools operations for validated loopback endpoints.
/// </summary>
public sealed class CdpBrowserToolsProvider
{
    private const int MaximumTargetResponseBytes = 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;
    private readonly ICdpBrowserToolsSessionClient _sessionClient;
    private readonly TimeSpan _requestTimeout;

    /// <summary>Creates a provider using bounded loopback HTTP and WebSocket clients.</summary>
    public CdpBrowserToolsProvider()
        : this(
            SharedHttpClient,
            new CdpBrowserToolsSessionClient(),
            TimeSpan.FromSeconds(2))
    {
    }

    internal CdpBrowserToolsProvider(
        HttpClient httpClient,
        ICdpBrowserToolsSessionClient sessionClient,
        TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sessionClient);
        if (requestTimeout <= TimeSpan.Zero
            || requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The CDP request timeout must be finite and positive.");
        }

        _httpClient = httpClient;
        _sessionClient = sessionClient;
        _requestTimeout = requestTimeout;
    }

    /// <summary>Retrieves the inspectable targets advertised by an endpoint.</summary>
    public async ValueTask<CdpTargetListResult> DiscoverTargetsAsync(
        CdpTransportInfo transport,
        CancellationToken cancellationToken = default)
    {
        ValidateTransport(transport);
        Uri endpoint = new(
            $"http://127.0.0.1:{transport.Port}/json/list",
            UriKind.Absolute);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failed(
                    transport,
                    $"The target list returned HTTP {(int)response.StatusCode}.");
            }

            if (response.RequestMessage?.RequestUri is not Uri responseUri
                || !responseUri.IsLoopback
                || responseUri.Port != transport.Port)
            {
                return Failed(
                    transport,
                    "The target-list request was redirected away from the "
                        + "validated loopback endpoint.");
            }

            byte[] content = await ReadBoundedAsync(
                response.Content,
                MaximumTargetResponseBytes,
                timeout.Token);
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Failed(
                    transport,
                    "The target-list response was not a JSON array.");
            }

            CdpInspectableTarget[] targets = document.RootElement
                .EnumerateArray()
                .Select(target => new CdpInspectableTarget(
                    GetString(target, "id") ?? string.Empty,
                    GetString(target, "type") ?? string.Empty,
                    GetString(target, "title") ?? string.Empty,
                    GetString(target, "url") ?? string.Empty,
                    ResolveFrontendUrl(
                        transport.Port!.Value,
                        GetString(target, "devtoolsFrontendUrl")),
                    GetString(target, "webSocketDebuggerUrl")))
                .Where(target => !string.IsNullOrWhiteSpace(target.TargetId))
                .ToArray();
            return new CdpTargetListResult(
                DateTimeOffset.UtcNow,
                targets,
                []);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or JsonException
                or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Failed(transport, exception.Message);
        }
    }

    /// <summary>Asks the inspected browser to open its native DevTools window.</summary>
    public ValueTask OpenDevToolsAsync(
        CdpTransportInfo transport,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ValidateTransport(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return _sessionClient.OpenDevToolsAsync(
            new Uri(transport.WebSocketDebuggerUrl!, UriKind.Absolute),
            targetId,
            cancellationToken);
    }

    /// <summary>
    /// Extracts frame/process details through an invisible process-internals page.
    /// </summary>
    public async ValueTask<CdpProcessInternalsResult>
        CaptureProcessInternalsAsync(
            CdpTransportInfo transport,
            string? imageName,
            IReadOnlyList<ProcessSnapshotEntry> processes,
            CancellationToken cancellationToken = default)
    {
        ValidateTransport(transport);
        ArgumentNullException.ThrowIfNull(processes);

        string preferredScheme = ResolveInternalPageScheme(
            transport.Browser,
            imageName);
        string[] schemes = preferredScheme == "edge"
            ? ["edge", "chrome"]
            : ["chrome", "edge"];
        List<DiscoveryIssue> issues = [];
        foreach (string scheme in schemes)
        {
            string pageUrl = $"{scheme}://process-internals/";
            try
            {
                CdpRawProcessInternalsSnapshot snapshot =
                    await _sessionClient.CaptureProcessInternalsAsync(
                        new Uri(
                            transport.WebSocketDebuggerUrl!,
                            UriKind.Absolute),
                        pageUrl,
                        cancellationToken);
                IReadOnlyDictionary<int, ProcessIdentity> processIds =
                    BuildRendererProcessIdMap(transport.ProcessId, processes);
                CdpProcessInternalsFrame[] frames = snapshot.Frames
                    .Select(frame => new CdpProcessInternalsFrame(
                        frame.WebContentsTitle,
                        frame.Depth,
                        frame.InternalProcessId,
                        frame.RoutingId,
                        frame.AgentSchedulingGroupId,
                        processIds.GetValueOrDefault(frame.InternalProcessId),
                        frame.Lifecycle,
                        frame.Url,
                        frame.SiteInstanceId,
                        frame.SiteInstanceGroupId,
                        frame.BrowsingInstanceId,
                        frame.SiteUrl,
                        frame.ProcessLockUrl))
                    .ToArray();
                if (frames.Any(frame => frame.Process is null))
                {
                    issues.Add(new DiscoveryIssue(
                        "cdp-process-internals",
                        "Some internal renderer IDs could not be correlated "
                            + "to captured Windows processes. Refresh the "
                            + "process list and retry.",
                        transport.ProcessId));
                }

                return new CdpProcessInternalsResult(
                    snapshot.ObservedAt,
                    transport.ProcessId,
                    snapshot.PageUrl,
                    frames,
                    issues);
            }
            catch (OperationCanceledException exception) when (
                !cancellationToken.IsCancellationRequested)
            {
                issues.Add(new DiscoveryIssue(
                    "cdp-process-internals",
                    $"{pageUrl} timed out: {exception.Message}",
                    transport.ProcessId));
            }
            catch (Exception exception) when (
                exception is WebSocketException
                    or IOException
                    or InvalidOperationException
                    or JsonException)
            {
                issues.Add(new DiscoveryIssue(
                    "cdp-process-internals",
                    $"{pageUrl} was unavailable: {exception.Message}",
                    transport.ProcessId));
            }
        }

        return new CdpProcessInternalsResult(
            DateTimeOffset.UtcNow,
            transport.ProcessId,
            $"{preferredScheme}://process-internals/",
            [],
            issues);
    }

    /// <summary>
    /// Resolves the browser-specific scheme used for internal diagnostic pages.
    /// </summary>
    public static string ResolveInternalPageScheme(
        string? browser,
        string? imageName)
    {
        if (browser?.Contains("Edg/", StringComparison.OrdinalIgnoreCase) == true
            || browser?.Contains(
                "Microsoft Edge",
                StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(
                imageName,
                "msedge.exe",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                imageName,
                "msedgewebview2.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return "edge";
        }

        return "chrome";
    }

    internal static IReadOnlyDictionary<int, ProcessIdentity>
        BuildRendererProcessIdMap(
        int browserProcessId,
        IReadOnlyList<ProcessSnapshotEntry> processes)
    {
        Dictionary<int, ProcessSnapshotEntry> byId = processes.ToDictionary(
            process => process.ProcessId);
        Dictionary<int, ProcessIdentity> result = [];
        foreach (ProcessSnapshotEntry process in processes)
        {
            if (process.IsProcessIdReused
                || !IsDescendantOf(process, browserProcessId, byId))
            {
                continue;
            }

            string? internalId = ChromiumCommandLine
                .Parse(process.CommandLine)
                .GetSwitchValue("renderer-client-id");
            if (int.TryParse(
                internalId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsed))
            {
                result.TryAdd(
                    parsed,
                    new ProcessIdentity(
                        process.ProcessId,
                        process.CreationTime));
            }
        }

        return result;
    }

    private static bool IsDescendantOf(
        ProcessSnapshotEntry process,
        int ancestorProcessId,
        Dictionary<int, ProcessSnapshotEntry> processes)
    {
        HashSet<int> visited = [];
        int parentId = process.ParentProcessId;
        while (parentId > 0 && visited.Add(parentId))
        {
            if (parentId == ancestorProcessId)
            {
                return true;
            }

            if (!processes.TryGetValue(parentId, out ProcessSnapshotEntry? parent))
            {
                return false;
            }

            parentId = parent.ParentProcessId;
        }

        return false;
    }

    private static string? ResolveFrontendUrl(
        int port,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute))
        {
            if (absolute.Scheme is "http" or "https")
            {
                return absolute.ToString();
            }

            if (absolute.Scheme == "devtools")
            {
                return $"http://127.0.0.1:{port}/devtools/inspector.html"
                    + absolute.Query;
            }
        }

        return new Uri(
            new Uri($"http://127.0.0.1:{port}", UriKind.Absolute),
            value).ToString();
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(
            cancellationToken);
        using MemoryStream destination = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new IOException(
                    $"The CDP response exceeded {maximumBytes} bytes.");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }

    private static CdpTargetListResult Failed(
        CdpTransportInfo transport,
        string message)
    {
        return new CdpTargetListResult(
            DateTimeOffset.UtcNow,
            [],
            [
                new DiscoveryIssue(
                    "cdp-target-list",
                    message,
                    transport.ProcessId),
            ]);
    }

    private static void ValidateTransport(CdpTransportInfo transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        bool validWebSocket = Uri.TryCreate(
                transport.WebSocketDebuggerUrl,
                UriKind.Absolute,
                out Uri? webSocket)
            && webSocket.Scheme == "ws"
            && webSocket.IsLoopback
            && webSocket.Port == transport.Port
            && webSocket.AbsolutePath.StartsWith(
                "/devtools/",
                StringComparison.Ordinal);
        if (transport.Kind != CdpTransportKind.Tcp
            || transport.Status != CdpTransportStatus.Validated
            || transport.Port is null
            || !validWebSocket)
        {
            throw new InvalidOperationException(
                "This operation requires a validated TCP DevTools endpoint.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        return new HttpClient(handler);
    }
}

internal interface ICdpBrowserToolsSessionClient
{
    ValueTask OpenDevToolsAsync(
        Uri browserWebSocketUrl,
        string targetId,
        CancellationToken cancellationToken);

    ValueTask<CdpRawProcessInternalsSnapshot> CaptureProcessInternalsAsync(
        Uri browserWebSocketUrl,
        string pageUrl,
        CancellationToken cancellationToken);
}

internal sealed record CdpRawProcessInternalsSnapshot(
    DateTimeOffset ObservedAt,
    string PageUrl,
    IReadOnlyList<CdpRawProcessInternalsFrame> Frames);

internal sealed record CdpRawProcessInternalsFrame(
    string WebContentsTitle,
    int Depth,
    int InternalProcessId,
    int RoutingId,
    int AgentSchedulingGroupId,
    string Lifecycle,
    string? Url,
    int SiteInstanceId,
    int SiteInstanceGroupId,
    int BrowsingInstanceId,
    string? SiteUrl,
    string? ProcessLockUrl);

internal sealed class CdpBrowserToolsSessionClient
    : ICdpBrowserToolsSessionClient
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private const string ProcessInternalsExpression =
        """
        (async () => {
          const api = await import('./process_internals.mojom-webui.js');
          const handler = api.ProcessInternalsHandler.getRemote();
          const response = await handler.getAllWebContentsInfo();
          const urlValue = value => value == null
            ? null
            : (typeof value === 'string' ? value : (value.url ?? String(value)));
          const frames = [];
          const append = (title, frame, depth, lifecycle) => {
            frames.push({
              webContentsTitle: title,
              depth,
              internalProcessId: frame.processId,
              routingId: frame.routingId,
              agentSchedulingGroupId: frame.agentSchedulingGroupId,
              lifecycle,
              url: urlValue(frame.lastCommittedUrl),
              siteInstanceId: frame.siteInstance.id,
              siteInstanceGroupId: frame.siteInstance.siteInstanceGroupId,
              browsingInstanceId: frame.siteInstance.browsingInstanceId,
              siteUrl: urlValue(frame.siteInstance.siteUrl),
              processLockUrl: urlValue(frame.siteInstance.processLockUrl)
            });
            for (const child of frame.subframes) {
              append(title, child, depth + 1, lifecycle);
            }
          };
          for (const contents of response.infos) {
            const rootUrl = urlValue(contents.rootFrame.lastCommittedUrl);
            if (rootUrl?.includes('://process-internals')) {
              continue;
            }
            append(contents.title, contents.rootFrame, 0, 'Active');
            for (const frame of contents.bfcachedRootFrames) {
              append(contents.title, frame, 0, 'Back/forward cache');
            }
            for (const frame of contents.prerenderRootFrames) {
              append(contents.title, frame, 0, 'Prerender');
            }
          }
          return { pageUrl: location.href, frames };
        })()
        """;

    public async ValueTask OpenDevToolsAsync(
        Uri browserWebSocketUrl,
        string targetId,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(browserWebSocketUrl);
        using ClientWebSocket socket = new();
        socket.Options.Proxy = null;
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await socket.ConnectAsync(browserWebSocketUrl, timeout.Token);
        await SendCommandAsync(
            socket,
            1,
            "Target.openDevTools",
            new { targetId },
            null,
            timeout.Token);
        socket.Abort();
    }

    public async ValueTask<CdpRawProcessInternalsSnapshot>
        CaptureProcessInternalsAsync(
            Uri browserWebSocketUrl,
            string pageUrl,
            CancellationToken cancellationToken)
    {
        ValidateEndpoint(browserWebSocketUrl);
        using ClientWebSocket socket = new();
        socket.Options.Proxy = null;
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await socket.ConnectAsync(browserWebSocketUrl, timeout.Token);

        try
        {
            JsonElement created = await SendCommandAsync(
                socket,
                1,
                "Target.createTarget",
                new
                {
                    url = pageUrl,
                    background = true,
                    hidden = true,
                },
                null,
                timeout.Token);
            string targetId = created.GetProperty("targetId").GetString()
                ?? throw new InvalidOperationException(
                    "CDP did not return a hidden target ID.");
            JsonElement attached = await SendCommandAsync(
                socket,
                2,
                "Target.attachToTarget",
                new
                {
                    targetId,
                    flatten = true,
                },
                null,
                timeout.Token);
            string sessionId = attached.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException(
                    "CDP did not return a hidden target session ID.");
            await SendCommandAsync(
                socket,
                3,
                "Runtime.enable",
                null,
                sessionId,
                timeout.Token);

            JsonElement evaluation = await EvaluateWhenReadyAsync(
                socket,
                sessionId,
                timeout.Token);
            JsonElement value = evaluation
                .GetProperty("result")
                .GetProperty("value");
            string actualPageUrl = GetString(value, "pageUrl")
                ?? pageUrl;
            CdpRawProcessInternalsFrame[] frames = value
                .GetProperty("frames")
                .EnumerateArray()
                .Select(frame => new CdpRawProcessInternalsFrame(
                    GetString(frame, "webContentsTitle") ?? string.Empty,
                    frame.GetProperty("depth").GetInt32(),
                    frame.GetProperty("internalProcessId").GetInt32(),
                    frame.GetProperty("routingId").GetInt32(),
                    frame.GetProperty("agentSchedulingGroupId").GetInt32(),
                    GetString(frame, "lifecycle") ?? string.Empty,
                    GetString(frame, "url"),
                    frame.GetProperty("siteInstanceId").GetInt32(),
                    frame.GetProperty("siteInstanceGroupId").GetInt32(),
                    frame.GetProperty("browsingInstanceId").GetInt32(),
                    GetString(frame, "siteUrl"),
                    GetString(frame, "processLockUrl")))
                .ToArray();
            return new CdpRawProcessInternalsSnapshot(
                DateTimeOffset.UtcNow,
                actualPageUrl,
                frames);
        }
        finally
        {
            // Hidden targets are scoped to this browser-level CDP session.
            socket.Abort();
        }
    }

    private static async ValueTask<JsonElement> EvaluateWhenReadyAsync(
        ClientWebSocket socket,
        string sessionId,
        CancellationToken cancellationToken)
    {
        InvalidOperationException? lastFailure = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                JsonElement result = await SendCommandAsync(
                    socket,
                    10 + attempt,
                    "Runtime.evaluate",
                    new
                    {
                        expression = ProcessInternalsExpression,
                        awaitPromise = true,
                        returnByValue = true,
                    },
                    sessionId,
                    cancellationToken);
                if (result.TryGetProperty(
                    "exceptionDetails",
                    out JsonElement exceptionDetails))
                {
                    throw new InvalidOperationException(
                        GetString(
                            exceptionDetails,
                            "text")
                            ?? "The process-internals script failed.");
                }

                return result;
            }
            catch (InvalidOperationException exception)
            {
                lastFailure = exception;
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "The hidden process-internals page did not become ready.",
            lastFailure);
    }

    private static async ValueTask<JsonElement> SendCommandAsync(
        ClientWebSocket socket,
        int id,
        string method,
        object? parameters,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> request = new()
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };
        if (sessionId is not null)
        {
            request["sessionId"] = sessionId;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            cancellationToken);
        while (true)
        {
            using JsonDocument message = await ReceiveMessageAsync(
                socket,
                cancellationToken);
            JsonElement root = message.RootElement;
            if (!root.TryGetProperty("id", out JsonElement responseId)
                || responseId.GetInt32() != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out JsonElement error))
            {
                throw new InvalidOperationException(
                    GetString(error, "message")
                        ?? $"CDP command {method} failed.");
            }

            return root.GetProperty("result").Clone();
        }
    }

    private static async ValueTask<JsonDocument> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using MemoryStream stream = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer,
                cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("The CDP WebSocket closed unexpectedly.");
            }

            await stream.WriteAsync(
                buffer.AsMemory(0, result.Count),
                cancellationToken);
            if (stream.Length > MaximumMessageBytes)
            {
                throw new IOException(
                    "A CDP WebSocket message exceeded the 4 MiB limit.");
            }

            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(stream.ToArray());
            }
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != "ws"
            || !endpoint.IsLoopback
            || !endpoint.AbsolutePath.StartsWith(
                "/devtools/",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Interactive DevTools operations only connect to validated "
                    + "loopback /devtools/ WebSocket endpoints.");
        }
    }
}
