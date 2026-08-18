using System.Text.Json.Serialization;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Source of a renderer/frame observation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RendererObservationSource>))]
public enum RendererObservationSource
{
    /// <summary>Supported WebView2 app-side process/frame information.</summary>
    WebView2Cooperative,

    /// <summary>Public CDP target topology without a public target-to-PID join.</summary>
    CdpTopology,

    /// <summary>Version-sensitive CDP tracing correlation.</summary>
    CdpTracing,
}

/// <summary>Sensitivity of renderer enrichment data.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RendererDataSensitivity>))]
public enum RendererDataSensitivity
{
    /// <summary>No page address is present.</summary>
    ProcessMetadata,

    /// <summary>The observation can contain a potentially sensitive URL or origin.</summary>
    PotentiallySensitiveUrl,
}

/// <summary>A frame returned by WebView2 GetProcessExtendedInfosAsync.</summary>
public sealed record WebView2FrameObservation(
    string FrameId,
    string Source,
    bool IsMainFrame);

/// <summary>Cooperative WebView2 renderer and associated-frame data.</summary>
public sealed record WebView2ExtendedProcessObservation(
    ProcessIdentity Identity,
    string ProcessKind,
    IReadOnlyList<WebView2FrameObservation> Frames,
    DateTimeOffset ObservedAt);

/// <summary>An evidence-backed renderer PID to frame/URL observation.</summary>
public sealed record RendererFrameMapping(
    ProcessIdentity Process,
    string FrameId,
    string Url,
    string? Origin,
    bool IsMainFrame,
    RendererObservationSource Source,
    ProcessRelationshipConfidence Confidence,
    bool IsAuthoritative,
    RendererDataSensitivity Sensitivity,
    DateTimeOffset ObservedAt,
    string Lifetime);

/// <summary>Public CDP target metadata, intentionally without an OS PID.</summary>
public sealed record CdpTargetObservation(
    int BrowserProcessId,
    string TargetId,
    string Type,
    string Title,
    string Url,
    string? ParentId,
    string? OpenerId,
    string? BrowserContextId,
    DateTimeOffset ObservedAt);

/// <summary>OS process metadata returned by SystemInfo.getProcessInfo.</summary>
public sealed record CdpProcessObservation(
    int BrowserProcessId,
    int ProcessId,
    string Type,
    double CpuTime,
    DateTimeOffset ObservedAt);

/// <summary>Opt-in renderer enrichment with provenance and explicit gaps.</summary>
public sealed record RendererEnrichmentResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<RendererFrameMapping> FrameMappings,
    IReadOnlyList<CdpTargetObservation> CdpTargets,
    IReadOnlyList<CdpProcessObservation> CdpProcesses,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<DiscoveryIssue> Issues);
