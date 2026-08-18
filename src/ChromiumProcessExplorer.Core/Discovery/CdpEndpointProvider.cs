using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Discovers configured CDP transports and validates loopback endpoints.</summary>
public sealed class CdpEndpointProvider : ICdpEndpointProvider
{
    private const int MaximumActivePortBytes = 4096;
    private const int MaximumVersionResponseBytes = 64 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;
    private readonly ICdpListenerOwnerResolver _listenerOwnerResolver;
    private readonly IChromeRemoteDebuggingRestrictionDetector _restrictionDetector;
    private readonly TimeSpan _requestTimeout;

    /// <summary>Creates a provider with direct loopback-only HTTP behavior.</summary>
    public CdpEndpointProvider(TimeSpan? requestTimeout = null)
        : this(
            SharedHttpClient,
            new WindowsCdpListenerOwnerResolver(),
            new ChromeRemoteDebuggingRestrictionDetector(),
            requestTimeout)
    {
    }

    internal CdpEndpointProvider(
        HttpClient httpClient,
        ICdpListenerOwnerResolver listenerOwnerResolver,
        IChromeRemoteDebuggingRestrictionDetector restrictionDetector,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(listenerOwnerResolver);
        ArgumentNullException.ThrowIfNull(restrictionDetector);

        _httpClient = httpClient;
        _listenerOwnerResolver = listenerOwnerResolver;
        _restrictionDetector = restrictionDetector;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(1);
        if (_requestTimeout <= TimeSpan.Zero
            || _requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The CDP request timeout must be finite and positive.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<CdpDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        HandleQueryWorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        Dictionary<int, ProcessSnapshotEntry> processById = processes
            .ToDictionary(process => process.ProcessId);
        List<(ProcessSnapshotEntry Process, ChromiumCommandLine CommandLine)>
            candidates = processes
                .Where(IsBrowserCandidate)
                .OrderBy(process => process.ProcessId)
                .Select(process => (
                    process,
                    ChromiumCommandLine.Parse(process.CommandLine)))
                .ToList();
        List<CdpTransportInfo> transports = [];
        List<DiscoveryIssue> issues = [];

        foreach ((ProcessSnapshotEntry process, ChromiumCommandLine commandLine)
            in candidates)
        {
            CdpTransportInfo? pipe = CreateConfiguredPipeTransport(
                process,
                commandLine);
            if (pipe is not null)
            {
                transports.Add(pipe);
            }

            if (commandLine.HasSwitch("remote-debugging-port"))
            {
                transports.Add(await DiscoverTcpAsync(
                    process,
                    commandLine,
                    cancellationToken));
            }
        }

        int[] pipeProcessIds = transports
            .Where(transport => transport.Kind == CdpTransportKind.Pipe)
            .Select(transport => transport.ProcessId)
            .Distinct()
            .ToArray();
        if (pipeProcessIds.Length > 0 && workerOptions is not null)
        {
            ProcessPipeInspectionResult inspection =
                await WindowsProcessPipeInspector.InspectAsync(
                    pipeProcessIds.ToHashSet(),
                    processes,
                    workerOptions,
                    cancellationToken);
            issues.AddRange(inspection.Issues);
            issues.AddRange(inspection.TimedOutQueries.Select(
                timeout => new DiscoveryIssue(
                    "cdp-pipe-handle-query",
                    $"Handle 0x{timeout.HandleValue:X} timed out during "
                        + $"{timeout.QueryStage}.",
                    timeout.OwnerProcessId)));
            for (int index = 0; index < transports.Count; index++)
            {
                CdpTransportInfo transport = transports[index];
                if (transport.Kind != CdpTransportKind.Pipe
                    || !processById.TryGetValue(
                        transport.ProcessId,
                        out ProcessSnapshotEntry? browser))
                {
                    continue;
                }

                ChromiumCommandLine commandLine =
                    ChromiumCommandLine.Parse(browser.CommandLine);
                transports[index] = CorrelatePipeTransport(
                    transport,
                    browser,
                    commandLine,
                    inspection,
                    processById);
            }
        }

        return new CdpDiscoveryResult(capturedAt, transports)
        {
            Issues = issues,
        };
    }

    private static bool IsBrowserCandidate(ProcessSnapshotEntry process)
    {
        if (process.IsProcessIdReused)
        {
            return false;
        }

        ChromiumCommandLine commandLine =
            ChromiumCommandLine.Parse(process.CommandLine);
        if (commandLine.HasSwitch("type"))
        {
            return false;
        }

        return process.IsLikelyChromium
            || process.LoadedModules.Any(module =>
                Path.GetFileName(module) is string name
                && (name.Equals("libcef.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(
                        "chrome_elf.dll",
                        StringComparison.OrdinalIgnoreCase)));
    }

    private static CdpTransportInfo? CreateConfiguredPipeTransport(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine)
    {
        bool hasPipeSwitch = commandLine.HasSwitch("remote-debugging-pipe");
        string? ioPipes =
            commandLine.GetSwitchValue("remote-debugging-io-pipes");
        if (!hasPipeSwitch && ioPipes is null)
        {
            return null;
        }

        List<string> evidence = [];
        if (hasPipeSwitch)
        {
            evidence.Add("--remote-debugging-pipe command-line switch.");
        }

        if (ioPipes is not null)
        {
            evidence.Add(
                "--remote-debugging-io-pipes identifies inherited pipe handles.");
        }

        return new CdpTransportInfo(
            process.ProcessId,
            CdpTransportKind.Pipe,
            CdpTransportStatus.Configured,
            commandLine.GetSwitchValue("remote-debugging-pipe") ?? ioPipes,
            null,
            "command-line",
            null,
            null,
            null,
            null,
            "The debugging pipe is configured, but endpoint ownership has not "
                + "been correlated.",
            evidence);
    }

    internal static CdpTransportInfo CorrelatePipeTransport(
        CdpTransportInfo configured,
        ProcessSnapshotEntry browser,
        ChromiumCommandLine commandLine,
        ProcessPipeInspectionResult inspection,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> processById)
    {
        HashSet<ulong>? explicitHandles = ParseExplicitPipeHandles(
            commandLine.GetSwitchValue("remote-debugging-io-pipes"));
        IEnumerable<ProcessPipeHandleInfo> browserPipes = inspection.Pipes
            .Where(pipe => pipe.OwnerProcessId == browser.ProcessId);
        if (explicitHandles is not null)
        {
            browserPipes = browserPipes.Where(
                pipe => explicitHandles.Contains(pipe.HandleValue));
        }

        var controllerGroups = browserPipes
            .Select(pipe => (Pipe: pipe, ControllerId: GetOtherEndpoint(
                pipe,
                browser.ProcessId)))
            .Where(item => item.ControllerId is not null)
            .Where(item => explicitHandles is not null
                || item.ControllerId == browser.ParentProcessId)
            .GroupBy(item => item.ControllerId!.Value)
            .Select(group => new
            {
                ControllerId = group.Key,
                Pipes = group.Select(item => item.Pipe)
                    .DistinctBy(pipe => pipe.HandleValue)
                    .ToArray(),
            })
            .Where(group => group.Pipes.Length >= 2)
            .OrderByDescending(group => group.Pipes.Length)
            .ThenBy(group => group.ControllerId)
            .FirstOrDefault();
        if (controllerGroups is null)
        {
            return configured;
        }

        processById.TryGetValue(
            controllerGroups.ControllerId,
            out ProcessSnapshotEntry? controller);
        return configured with
        {
            Status = CdpTransportStatus.AlreadyOwned,
            Error = null,
            ControllerProcessId = controllerGroups.ControllerId,
            ControllerImageName = controller?.ImageName,
            PipeConnections = controllerGroups.Pipes
                .Select(pipe => new CdpPipeConnection(
                    pipe.HandleValue,
                    pipe.ObjectName,
                    pipe.ServerProcessId,
                    pipe.ClientProcessId,
                    pipe.LocalEnd,
                    pipe.State))
                .ToArray(),
            Evidence = configured.Evidence
                .Append(
                    "Passively correlated multiple browser-owned pipe handles "
                    + "with the existing controller; no protocol bytes were "
                    + "read or written.")
                .ToArray(),
        };
    }

    private async ValueTask<CdpTransportInfo> DiscoverTcpAsync(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine,
        CancellationToken cancellationToken)
    {
        string? configuredValue =
            commandLine.GetSwitchValue("remote-debugging-port");
        string? restriction = _restrictionDetector.GetRestriction(
            process,
            commandLine);
        if (restriction is not null)
        {
            return CreateUnavailable(
                process.ProcessId,
                configuredValue,
                null,
                "command-line",
                restriction) with
            {
                Restriction = restriction,
            };
        }

        if (!int.TryParse(
            configuredValue,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int configuredPort)
            || configuredPort is < 0 or > ushort.MaxValue)
        {
            return CreateUnavailable(
                process.ProcessId,
                configuredValue,
                null,
                "command-line",
                "The --remote-debugging-port value is not a valid TCP port.");
        }

        ActivePortResult activePort = configuredPort == 0
            ? await TryReadActivePortAsync(process, cancellationToken)
            : new ActivePortResult(configuredPort, null, null);
        string source = configuredPort == 0
            ? "DevToolsActivePort"
            : "command-line";
        if (activePort.Error is not null)
        {
            return CreateUnavailable(
                process.ProcessId,
                configuredValue,
                null,
                source,
                activePort.Error);
        }

        if (activePort.Port is null)
        {
            return new CdpTransportInfo(
                process.ProcessId,
                CdpTransportKind.Tcp,
                CdpTransportStatus.Configured,
                configuredValue,
                null,
                source,
                null,
                null,
                null,
                null,
                "An ephemeral port is configured, but DevToolsActivePort is unavailable.",
                ["--remote-debugging-port command-line switch."]);
        }

        CdpListenerOwnerResult owners =
            _listenerOwnerResolver.Resolve(activePort.Port.Value);
        if (owners.Error is not null)
        {
            return new CdpTransportInfo(
                process.ProcessId,
                CdpTransportKind.Tcp,
                CdpTransportStatus.Discovered,
                configuredValue,
                activePort.Port,
                source,
                GetVersionEndpoint(activePort.Port.Value),
                null,
                null,
                null,
                $"The listener owner could not be verified: {owners.Error}",
                [
                    "--remote-debugging-port command-line switch.",
                    "A concrete loopback port was discovered.",
                ]);
        }

        if (!owners.ProcessIds.Contains(process.ProcessId))
        {
            string ownership = owners.ProcessIds.Count == 0
                ? "No listening process owns the discovered port."
                : $"The port is owned by PID(s) "
                    + $"{string.Join(", ", owners.ProcessIds)}, not this browser.";
            return CreateUnavailable(
                process.ProcessId,
                configuredValue,
                activePort.Port,
                source,
                ownership);
        }

        return await ProbeVersionEndpointAsync(
            process.ProcessId,
            configuredValue,
            activePort.Port.Value,
            source,
            activePort.WebSocketPath,
            cancellationToken);
    }

    private static async ValueTask<ActivePortResult> TryReadActivePortAsync(
        ProcessSnapshotEntry process,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(process.UserDataDirectory))
        {
            return new ActivePortResult(null, null, null);
        }

        string path = Path.Combine(
            process.UserDataDirectory,
            "DevToolsActivePort");
        if (!File.Exists(path))
        {
            return new ActivePortResult(null, null, null);
        }

        try
        {
            DateTimeOffset lastWriteTime = File.GetLastWriteTimeUtc(path);
            if (process.CreationTime is DateTimeOffset creationTime
                && lastWriteTime < creationTime.UtcDateTime.AddSeconds(-2))
            {
                return new ActivePortResult(
                    null,
                    null,
                    "DevToolsActivePort predates the captured browser process.");
            }

            string content = await ReadBoundedFileAsync(
                path,
                cancellationToken);
            string[] lines = content.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0
                || !int.TryParse(
                    lines[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int port)
                || port is <= 0 or > ushort.MaxValue)
            {
                return new ActivePortResult(
                    null,
                    null,
                    "DevToolsActivePort does not contain a valid port.");
            }

            string? webSocketPath = lines.Length > 1 ? lines[1] : null;
            if (webSocketPath is not null
                && !webSocketPath.StartsWith(
                    "/devtools/",
                    StringComparison.Ordinal))
            {
                return new ActivePortResult(
                    null,
                    null,
                    "DevToolsActivePort contains an invalid browser WebSocket path.");
            }

            return new ActivePortResult(port, webSocketPath, null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            return new ActivePortResult(
                null,
                null,
                $"DevToolsActivePort could not be read: {exception.Message}");
        }
    }

    private async ValueTask<CdpTransportInfo> ProbeVersionEndpointAsync(
        int processId,
        string? configuredValue,
        int port,
        string source,
        string? expectedWebSocketPath,
        CancellationToken cancellationToken)
    {
        Uri endpoint = new(GetVersionEndpoint(port));
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.RequestMessage?.RequestUri != endpoint)
            {
                return CreateUnavailable(
                    processId,
                    configuredValue,
                    port,
                    source,
                    "The CDP version request was redirected and was rejected.");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CreateUnavailable(
                    processId,
                    configuredValue,
                    port,
                    source,
                    $"The version endpoint returned HTTP "
                        + $"{(int)response.StatusCode}.");
            }

            byte[] payload = await ReadBoundedAsync(
                response.Content,
                timeout.Token);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string? webSocketUrl = GetString(root, "webSocketDebuggerUrl");
            if (!TryValidateWebSocketUrl(
                webSocketUrl,
                port,
                expectedWebSocketPath))
            {
                return CreateUnavailable(
                    processId,
                    configuredValue,
                    port,
                    source,
                    "The version response did not contain the expected loopback "
                        + "CDP WebSocket URL.");
            }

            return new CdpTransportInfo(
                processId,
                CdpTransportKind.Tcp,
                CdpTransportStatus.Validated,
                configuredValue,
                port,
                source,
                endpoint.AbsoluteUri,
                webSocketUrl,
                GetString(root, "Browser"),
                GetString(root, "Protocol-Version"),
                null,
                [
                    "--remote-debugging-port command-line switch.",
                    "Verified the TCP listener belongs to this browser process.",
                    "Validated /json/version and webSocketDebuggerUrl.",
                ]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateUnavailable(
                processId,
                configuredValue,
                port,
                source,
                $"The loopback CDP probe exceeded {_requestTimeout.TotalMilliseconds:F0} ms.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or JsonException)
        {
            return CreateUnavailable(
                processId,
                configuredValue,
                port,
                source,
                exception.Message);
        }
    }

    private static async Task<string> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            512,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[MaximumActivePortBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(
                buffer.AsMemory(length),
                cancellationToken);
            if (count == 0)
            {
                break;
            }

            length += count;
        }

        if (length > MaximumActivePortBytes)
        {
            throw new IOException(
                "DevToolsActivePort exceeded the 4 KiB limit.");
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(
            cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using MemoryStream result = new();
            while (true)
            {
                int count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    return result.ToArray();
                }

                if (result.Length + count > MaximumVersionResponseBytes)
                {
                    throw new IOException(
                        "The CDP version response exceeded the 64 KiB limit.");
                }

                result.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool TryValidateWebSocketUrl(
        string? value,
        int port,
        string? expectedPath)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "ws" or "wss"
            && uri.IsLoopback
            && uri.Port == port
            && uri.AbsolutePath.StartsWith(
                "/devtools/",
                StringComparison.Ordinal)
            && (expectedPath is null
                || string.Equals(
                    uri.PathAndQuery,
                    expectedPath,
                    StringComparison.Ordinal));
    }

    private static HashSet<ulong>? ParseExplicitPipeHandles(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        HashSet<ulong> handles = [];
        foreach (string part in parts)
        {
            string digits = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? part[2..]
                : part;
            NumberStyles style = part.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase)
                    ? NumberStyles.AllowHexSpecifier
                    : NumberStyles.None;
            if (!ulong.TryParse(
                digits,
                style,
                CultureInfo.InvariantCulture,
                out ulong handle))
            {
                return null;
            }

            handles.Add(handle);
        }

        return handles.Count == 2 ? handles : null;
    }

    private static int? GetOtherEndpoint(
        ProcessPipeHandleInfo pipe,
        int browserProcessId)
    {
        if (pipe.ServerProcessId is int serverProcessId
            && serverProcessId != 0
            && serverProcessId != browserProcessId)
        {
            return serverProcessId;
        }

        if (pipe.ClientProcessId is int clientProcessId
            && clientProcessId != 0
            && clientProcessId != browserProcessId)
        {
            return clientProcessId;
        }

        return null;
    }

    private static string GetVersionEndpoint(int port)
    {
        return $"http://127.0.0.1:{port}/json/version";
    }

    private static CdpTransportInfo CreateUnavailable(
        int processId,
        string? configuredValue,
        int? port,
        string source,
        string error)
    {
        return new CdpTransportInfo(
            processId,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Unavailable,
            configuredValue,
            port,
            source,
            port is null ? null : GetVersionEndpoint(port.Value),
            null,
            null,
            null,
            error,
            ["--remote-debugging-port command-line switch."]);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            },
            disposeHandler: true);
    }

    private sealed record ActivePortResult(
        int? Port,
        string? WebSocketPath,
        string? Error);
}
