namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Normalized Electron process roles.</summary>
public enum ElectronProcessRole
{
    /// <summary>The Electron browser/main process.</summary>
    Main,

    /// <summary>A page or embedded-content renderer.</summary>
    Renderer,

    /// <summary>A renderer used for DevTools UI.</summary>
    DevTools,

    /// <summary>The Chromium GPU process.</summary>
    Gpu,

    /// <summary>A Chromium or Electron utility process.</summary>
    Utility,

    /// <summary>A dedicated or shared web worker.</summary>
    Worker,

    /// <summary>A service worker.</summary>
    ServiceWorker,

    /// <summary>The Electron Crashpad handler.</summary>
    Crashpad,

    /// <summary>A Node helper launched through ELECTRON_RUN_AS_NODE.</summary>
    NodeHelper,

    /// <summary>Another Electron-associated process.</summary>
    Other,
}

/// <summary>Confidence-bearing path observed or inferred for Electron.</summary>
public sealed record ElectronPathObservation(
    string Value,
    string Source,
    ProcessRelationshipConfidence Confidence,
    bool Exists);

/// <summary>Separate install, package, app-code, and runtime-data paths.</summary>
public sealed record ElectronRuntimePaths(
    ElectronPathObservation? InstallDirectory,
    ElectronPathObservation? PackageRoot,
    ElectronPathObservation? ResourcesDirectory,
    ElectronPathObservation? ApplicationPath,
    ElectronPathObservation? UnpackedApplicationDirectory,
    ElectronPathObservation? PackageJson,
    ElectronPathObservation? UserDataDirectory,
    ElectronPathObservation? SessionDataDirectory,
    ElectronPathObservation? LogsDirectory,
    ElectronPathObservation? CrashDumpsDirectory,
    ElectronPathObservation? TempDirectory);

/// <summary>Windows package identity inferred from a packaged executable path.</summary>
public sealed record ElectronPackageIdentity(
    string PackageFullName,
    string Name,
    string? Version,
    string? Architecture,
    string? PublisherId);

/// <summary>Observed evidence supporting Electron classification.</summary>
public sealed record ElectronEvidence(
    string Source,
    string Detail,
    string? RawValue = null);

/// <summary>Optional app-side Electron process information.</summary>
public sealed record ElectronCooperativeProcessInfo(
    ProcessIdentity Identity,
    ElectronProcessRole Role,
    string Source,
    string? ServiceName = null,
    string? WebContentsType = null);

/// <summary>Electron-specific information for one process generation.</summary>
public sealed record ElectronProcessInfo(
    int ProcessId,
    ElectronProcessRole Role,
    string? RawProcessType,
    string? UtilitySubType,
    string? WindowType,
    ElectronRuntimePaths Paths,
    ElectronPackageIdentity? PackageIdentity,
    string? PackageName,
    string? PackageVersion,
    bool HasCooperativeEvidence,
    IReadOnlyList<ElectronEvidence> Evidence);

/// <summary>An Electron main-process to child-process relationship.</summary>
public sealed record ElectronProcessAssociation(
    int MainProcessId,
    int ChildProcessId,
    int Score,
    ProcessRelationshipConfidence Confidence,
    bool IsAuthoritative,
    IReadOnlyList<ElectronEvidence> Evidence);

/// <summary>Electron-specific analysis for a process snapshot.</summary>
public sealed record ElectronRuntimeAnalysis(
    IReadOnlyList<ElectronProcessInfo> Processes,
    IReadOnlyList<ElectronProcessAssociation> Associations,
    IReadOnlyList<DiscoveryIssue> Issues)
{
    /// <summary>Gets an empty analysis.</summary>
    public static ElectronRuntimeAnalysis Empty { get; } = new([], [], []);
}
