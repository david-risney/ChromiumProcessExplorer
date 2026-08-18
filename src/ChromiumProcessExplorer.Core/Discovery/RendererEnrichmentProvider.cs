using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Produces opt-in cooperative and CDP renderer/frame enrichment while keeping
/// passive process discovery free of origin claims.
/// </summary>
public sealed class RendererEnrichmentProvider
{
    private readonly ICdpRendererSessionClient _cdpClient;

    /// <summary>Creates a provider using the bounded loopback WebSocket client.</summary>
    public RendererEnrichmentProvider()
        : this(new CdpRendererSessionClient())
    {
    }

    internal RendererEnrichmentProvider(ICdpRendererSessionClient cdpClient)
    {
        ArgumentNullException.ThrowIfNull(cdpClient);
        _cdpClient = cdpClient;
    }

    /// <summary>Collects cooperative WebView2 and validated CDP enrichment.</summary>
    public async ValueTask<RendererEnrichmentResult> EnrichAsync(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CdpDiscoveryResult cdp,
        IReadOnlyList<WebView2ExtendedProcessObservation>? webView2 = null,
        bool includeTracing = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(cdp);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        Dictionary<int, ProcessSnapshotEntry> processesById = processes
            .ToDictionary(process => process.ProcessId);
        List<RendererFrameMapping> mappings = [];
        List<CdpTargetObservation> targets = [];
        List<CdpProcessObservation> cdpProcesses = [];
        List<DiscoveryIssue> issues = [];
        List<string> limitations =
        [
            "Public CDP exposes target topology and OS process information "
                + "separately; it does not provide a stable target-to-PID join.",
            "Passive process inspection does not assign origins to renderer PIDs.",
        ];

        foreach (WebView2ExtendedProcessObservation observation in webView2 ?? [])
        {
            if (!processesById.TryGetValue(
                observation.Identity.ProcessId,
                out ProcessSnapshotEntry? process)
                || process.IsProcessIdReused
                || process.CreationTime is null
                || observation.Identity.CreationTime is null
                || process.CreationTime != observation.Identity.CreationTime)
            {
                issues.Add(new DiscoveryIssue(
                    "webview2-renderer-enrichment",
                    "The cooperative observation does not match the captured "
                        + "process generation.",
                    observation.Identity.ProcessId));
                continue;
            }

            foreach (WebView2FrameObservation frame in observation.Frames)
            {
                mappings.Add(new RendererFrameMapping(
                    observation.Identity,
                    frame.FrameId,
                    frame.Source,
                    GetOrigin(frame.Source),
                    frame.IsMainFrame,
                    RendererObservationSource.WebView2Cooperative,
                    ProcessRelationshipConfidence.High,
                    true,
                    RendererDataSensitivity.PotentiallySensitiveUrl,
                    observation.ObservedAt,
                    "Valid only for the cooperative WebView2 snapshot."));
            }
        }

        foreach (CdpTransportInfo transport in cdp.Transports.Where(
            transport => transport.Kind == CdpTransportKind.Tcp
                && transport.Status == CdpTransportStatus.Validated
                && transport.WebSocketDebuggerUrl is not null))
        {
            try
            {
                CdpRendererSessionSnapshot snapshot =
                    await _cdpClient.CaptureAsync(
                        new Uri(transport.WebSocketDebuggerUrl!),
                        includeTracing,
                        cancellationToken);
                targets.AddRange(snapshot.Targets.Select(target =>
                    new CdpTargetObservation(
                        transport.ProcessId,
                        target.TargetId,
                        target.Type,
                        target.Title,
                        target.Url,
                        target.ParentId,
                        target.OpenerId,
                        target.BrowserContextId,
                        snapshot.ObservedAt)));
                cdpProcesses.AddRange(snapshot.Processes.Select(process =>
                    new CdpProcessObservation(
                        transport.ProcessId,
                        process.ProcessId,
                        process.Type,
                        process.CpuTime,
                        snapshot.ObservedAt)));
                mappings.AddRange(ParseTracingMappings(
                    snapshot.TraceEvents,
                    processesById,
                    snapshot.ObservedAt));
                issues.AddRange(snapshot.Issues.Select(message =>
                    new DiscoveryIssue(
                        "cdp-renderer-enrichment",
                        message,
                        transport.ProcessId)));
            }
            catch (Exception exception) when (
                exception is WebSocketException
                    or IOException
                    or InvalidOperationException
                    or JsonException)
            {
                issues.Add(new DiscoveryIssue(
                    "cdp-renderer-enrichment",
                    exception.Message,
                    transport.ProcessId));
            }
        }

        if (includeTracing)
        {
            limitations.Add(
                "Tracing correlations are version-sensitive snapshots and are "
                    + "not presented as authoritative mappings.");
        }

        return new RendererEnrichmentResult(
            capturedAt,
            mappings,
            targets,
            cdpProcesses,
            limitations,
            issues);
    }

    internal static IReadOnlyList<RendererFrameMapping> ParseTracingMappings(
        IReadOnlyList<JsonElement> events,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> processesById,
        DateTimeOffset observedAt)
    {
        List<RendererFrameMapping> mappings = [];
        foreach (JsonElement traceEvent in events)
        {
            if (!traceEvent.TryGetProperty("args", out JsonElement args)
                || !args.TryGetProperty("data", out JsonElement data))
            {
                continue;
            }

            if (data.TryGetProperty("frames", out JsonElement frames)
                && frames.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement frame in frames.EnumerateArray())
                {
                    TryAddFrame(frame, traceEvent);
                }
            }
            else
            {
                TryAddFrame(data, traceEvent);
            }
        }

        return mappings
            .DistinctBy(mapping => (
                mapping.Process,
                mapping.FrameId,
                mapping.Url))
            .ToArray();

        void TryAddFrame(JsonElement frame, JsonElement traceEvent)
        {
            string? frameId = GetString(frame, "frame")
                ?? GetString(frame, "frameId");
            string? url = GetString(frame, "url");
            int? processId = GetInt32(frame, "processId")
                ?? GetInt32(frame, "pid")
                ?? GetInt32(traceEvent, "pid");
            if (frameId is null
                || url is null
                || processId is not int pid
                || !processesById.TryGetValue(
                    pid,
                    out ProcessSnapshotEntry? process)
                || process.IsProcessIdReused)
            {
                return;
            }

            mappings.Add(new RendererFrameMapping(
                new ProcessIdentity(pid, process.CreationTime),
                frameId,
                url,
                GetOrigin(url),
                GetBoolean(frame, "isMainFrame") ?? false,
                RendererObservationSource.CdpTracing,
                ProcessRelationshipConfidence.Medium,
                false,
                RendererDataSensitivity.PotentiallySensitiveUrl,
                observedAt,
                "Valid only for the bounded CDP tracing capture."));
        }
    }

    private static string? GetOrigin(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static int? GetInt32(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.TryGetInt32(out int result)
                ? result
                : null;
    }

    private static bool? GetBoolean(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }
}

internal interface ICdpRendererSessionClient
{
    ValueTask<CdpRendererSessionSnapshot> CaptureAsync(
        Uri webSocketDebuggerUrl,
        bool includeTracing,
        CancellationToken cancellationToken);
}

internal sealed record CdpRendererSessionSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<CdpProtocolTarget> Targets,
    IReadOnlyList<CdpProtocolProcess> Processes,
    IReadOnlyList<JsonElement> TraceEvents,
    IReadOnlyList<string> Issues);

internal sealed record CdpProtocolTarget(
    string TargetId,
    string Type,
    string Title,
    string Url,
    string? ParentId,
    string? OpenerId,
    string? BrowserContextId);

internal sealed record CdpProtocolProcess(
    int ProcessId,
    string Type,
    double CpuTime);

internal sealed class CdpRendererSessionClient : ICdpRendererSessionClient
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TraceDuration = TimeSpan.FromMilliseconds(500);

    public async ValueTask<CdpRendererSessionSnapshot> CaptureAsync(
        Uri webSocketDebuggerUrl,
        bool includeTracing,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(webSocketDebuggerUrl);
        using ClientWebSocket socket = new();
        socket.Options.Proxy = null;
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        await socket.ConnectAsync(webSocketDebuggerUrl, timeout.Token);

        List<JsonElement> traceEvents = [];
        JsonElement targets = await SendCommandAsync(
            socket,
            1,
            "Target.getTargets",
            null,
            traceEvents,
            timeout.Token);
        JsonElement processes = await SendCommandAsync(
            socket,
            2,
            "SystemInfo.getProcessInfo",
            null,
            traceEvents,
            timeout.Token);
        List<string> issues = [];
        if (includeTracing)
        {
            await SendCommandAsync(
                socket,
                3,
                "Tracing.start",
                new
                {
                    categories =
                        "navigation,disabled-by-default-devtools.timeline",
                    transferMode = "ReportEvents",
                },
                traceEvents,
                timeout.Token);
            await Task.Delay(TraceDuration, cancellationToken);
            await SendCommandAsync(
                socket,
                4,
                "Tracing.end",
                null,
                traceEvents,
                timeout.Token);
            await ReceiveUntilTracingCompleteAsync(
                socket,
                traceEvents,
                timeout.Token);
            if (traceEvents.Count == 0)
            {
                issues.Add(
                    "The bounded trace returned no frame/process correlation events.");
            }
        }

        socket.Abort();
        return new CdpRendererSessionSnapshot(
            DateTimeOffset.UtcNow,
            ParseTargets(targets),
            ParseProcesses(processes),
            traceEvents,
            issues);
    }

    private static async ValueTask<JsonElement> SendCommandAsync(
        ClientWebSocket socket,
        int id,
        string method,
        object? parameters,
        List<JsonElement> traceEvents,
        CancellationToken cancellationToken)
    {
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters,
        });
        await socket.SendAsync(
            request,
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        while (true)
        {
            JsonDocument message = await ReceiveMessageAsync(
                socket,
                cancellationToken);
            using (message)
            {
                JsonElement root = message.RootElement;
                CollectTraceEvents(root, traceEvents);
                if (!root.TryGetProperty("id", out JsonElement responseId)
                    || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    throw new InvalidOperationException(
                        error.TryGetProperty(
                            "message",
                            out JsonElement errorMessage)
                            ? errorMessage.GetString()
                                ?? $"CDP command {method} failed."
                            : $"CDP command {method} failed.");
                }

                return root.GetProperty("result").Clone();
            }
        }
    }

    private static async ValueTask ReceiveUntilTracingCompleteAsync(
        ClientWebSocket socket,
        List<JsonElement> traceEvents,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using JsonDocument message = await ReceiveMessageAsync(
                socket,
                cancellationToken);
            JsonElement root = message.RootElement;
            CollectTraceEvents(root, traceEvents);
            if (root.TryGetProperty("method", out JsonElement method)
                && method.GetString() == "Tracing.tracingComplete")
            {
                return;
            }
        }
    }

    private static void CollectTraceEvents(
        JsonElement message,
        List<JsonElement> traceEvents)
    {
        if (!message.TryGetProperty("method", out JsonElement method)
            || method.GetString() != "Tracing.dataCollected"
            || !message.TryGetProperty("params", out JsonElement parameters)
            || !parameters.TryGetProperty("value", out JsonElement values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        traceEvents.AddRange(values.EnumerateArray().Select(value => value.Clone()));
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

    private static CdpProtocolTarget[] ParseTargets(
        JsonElement result)
    {
        return result.TryGetProperty("targetInfos", out JsonElement targetInfos)
            ? targetInfos.EnumerateArray().Select(target => new CdpProtocolTarget(
                target.GetProperty("targetId").GetString() ?? string.Empty,
                target.GetProperty("type").GetString() ?? string.Empty,
                target.GetProperty("title").GetString() ?? string.Empty,
                target.GetProperty("url").GetString() ?? string.Empty,
                GetOptionalString(target, "parentId"),
                GetOptionalString(target, "openerId"),
                GetOptionalString(target, "browserContextId"))).ToArray()
            : [];
    }

    private static CdpProtocolProcess[] ParseProcesses(
        JsonElement result)
    {
        return result.TryGetProperty("processInfo", out JsonElement processInfo)
            ? processInfo.EnumerateArray().Select(process => new CdpProtocolProcess(
                process.GetProperty("id").GetInt32(),
                process.GetProperty("type").GetString() ?? string.Empty,
                process.GetProperty("cpuTime").GetDouble())).ToArray()
            : [];
    }

    private static string? GetOptionalString(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
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
                "CDP enrichment only connects to validated loopback "
                    + "/devtools/ WebSocket endpoints.");
        }
    }
}
