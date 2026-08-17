using System.Text.Json.Serialization;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Supported Chrome DevTools Protocol transport types.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CdpTransportKind>))]
public enum CdpTransportKind
{
    /// <summary>A loopback HTTP and WebSocket endpoint.</summary>
    Tcp,

    /// <summary>An inherited, point-to-point debugging pipe.</summary>
    Pipe,
}

/// <summary>The observed state of one CDP transport.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CdpTransportStatus>))]
public enum CdpTransportStatus
{
    /// <summary>The process command line configures the transport.</summary>
    Configured,

    /// <summary>A concrete endpoint was discovered but not yet validated.</summary>
    Discovered,

    /// <summary>The endpoint returned a valid CDP version response.</summary>
    Validated,

    /// <summary>The configured or discovered endpoint could not be validated.</summary>
    Unavailable,

    /// <summary>The private pipe is already owned by its launching controller.</summary>
    AlreadyOwned,
}

/// <summary>One configured or discovered CDP transport.</summary>
public sealed record CdpTransportInfo(
    int ProcessId,
    CdpTransportKind Kind,
    CdpTransportStatus Status,
    string? ConfiguredValue,
    int? Port,
    string? DiscoverySource,
    string? VersionEndpoint,
    string? WebSocketDebuggerUrl,
    string? Browser,
    string? ProtocolVersion,
    string? Error,
    IReadOnlyList<string> Evidence)
{
    /// <summary>Gets a product restriction that prevents the transport.</summary>
    public string? Restriction { get; init; }

    /// <summary>Gets the existing controller for an occupied pipe transport.</summary>
    public int? ControllerProcessId { get; init; }

    /// <summary>Gets the existing controller image name, when known.</summary>
    public string? ControllerImageName { get; init; }

    /// <summary>Gets passive endpoint metadata for an occupied pipe transport.</summary>
    public IReadOnlyList<CdpPipeConnection> PipeConnections { get; init; } = [];
}

/// <summary>Passive metadata for one inherited debugging-pipe endpoint.</summary>
public sealed record CdpPipeConnection(
    ulong BrowserHandleValue,
    string? ObjectName,
    int? ServerProcessId,
    int? ClientProcessId,
    string? LocalEnd,
    string? State);

/// <summary>CDP transport discovery for one process snapshot.</summary>
public sealed record CdpDiscoveryResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<CdpTransportInfo> Transports)
{
    /// <summary>Gets partial-coverage and inspection issues.</summary>
    public IReadOnlyList<DiscoveryIssue> Issues { get; init; } = [];
}
