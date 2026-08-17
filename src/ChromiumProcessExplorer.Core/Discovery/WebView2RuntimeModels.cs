namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Roles recognized by the WebView2 runtime adapter.</summary>
public enum WebView2ProcessRole
{
    /// <summary>A native process loading the WebView2 SDK or client library.</summary>
    Host,

    /// <summary>The WebView2 browser process.</summary>
    Browser,

    /// <summary>A WebView2 Chromium subprocess.</summary>
    Subprocess,
}

/// <summary>Observed evidence supporting WebView2 classification or association.</summary>
public sealed record WebView2Evidence(
    string Source,
    string Detail,
    string? RawValue = null,
    long? WindowHandle = null);

/// <summary>WebView2-specific information derived for one process.</summary>
public sealed record WebView2ProcessInfo(
    int ProcessId,
    WebView2ProcessRole Role,
    IReadOnlyList<WebView2Evidence> Evidence,
    string? ModuleInspectionError);

/// <summary>A confidence-scored native host to WebView2 browser relationship.</summary>
public sealed record WebView2HostAssociation(
    int HostProcessId,
    int BrowserProcessId,
    int Score,
    ProcessRelationshipConfidence Confidence,
    bool IsAuthoritative,
    IReadOnlyList<WebView2Evidence> Evidence);

/// <summary>WebView2 process classification, host relationships, and HWND evidence.</summary>
public sealed record WebView2RuntimeAnalysis(
    IReadOnlyList<WebView2ProcessInfo> Processes,
    IReadOnlyList<WebView2HostAssociation> HostAssociations,
    WindowSnapshotResult WindowSnapshot,
    IReadOnlyList<DiscoveryIssue> Issues)
{
    /// <summary>Gets an empty analysis.</summary>
    public static WebView2RuntimeAnalysis Empty { get; } = new(
        [],
        [],
        WindowSnapshotResult.Empty,
        []);
}
