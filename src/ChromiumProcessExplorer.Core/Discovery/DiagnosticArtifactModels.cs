namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Known diagnostic artifact categories.</summary>
public enum DiagnosticArtifactKind
{
    /// <summary>A diagnostic log file.</summary>
    Log,

    /// <summary>A directory containing diagnostic logs.</summary>
    LogDirectory,

    /// <summary>A process crash dump.</summary>
    CrashDump,

    /// <summary>A Crashpad database or dump directory.</summary>
    CrashDatabase,

    /// <summary>A Windows Error Reporting dump directory.</summary>
    WerDumpDirectory,

    /// <summary>A Chromium network event log.</summary>
    NetLog,

    /// <summary>A Chromium or embedder trace.</summary>
    Trace,

    /// <summary>A crash-reporting configuration file.</summary>
    CrashConfiguration,

    /// <summary>A Windows diagnostic event-log channel.</summary>
    EventLog,
}

/// <summary>Observed state of a diagnostic artifact location.</summary>
public enum DiagnosticArtifactStatus
{
    /// <summary>The file or directory exists and metadata was read.</summary>
    Present,

    /// <summary>The configured or default location does not exist.</summary>
    Missing,

    /// <summary>The location exists or may exist but could not be inspected.</summary>
    Inaccessible,

    /// <summary>The location is configured but cannot be resolved as a filesystem path.</summary>
    Configured,
}

/// <summary>Filesystem metadata collected without reading artifact contents.</summary>
public sealed record DiagnosticPathMetadata(
    DiagnosticArtifactStatus Status,
    bool? IsDirectory,
    long? Length,
    DateTimeOffset? LastWriteTime,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>A passively discovered diagnostic artifact or known location.</summary>
public sealed record DiagnosticArtifact(
    DiagnosticArtifactKind Kind,
    string Platform,
    string Source,
    SensitiveStringValue Location,
    DiagnosticArtifactStatus Status,
    bool? IsDirectory,
    long? Length,
    DateTimeOffset? LastWriteTime,
    bool IsPotentiallySensitive,
    IReadOnlyList<int> AssociatedProcessIds,
    IReadOnlyList<string> Evidence);

/// <summary>A logging, crash, tracing, or risky-debugging configuration finding.</summary>
public sealed record DiagnosticConfigurationFinding(
    ProcessIdentity Identity,
    string Platform,
    string Name,
    string Category,
    string Severity,
    SensitiveStringValue Value,
    string Detail,
    bool RequiresExplicitConsent);

/// <summary>Stable passive diagnostic-artifact discovery result.</summary>
public sealed record DiagnosticArtifactDiscoveryResult(
    string SchemaVersion,
    DateTimeOffset CapturedAt,
    bool IncludesSensitiveValues,
    bool IsPassiveOnly,
    IReadOnlyList<DiagnosticArtifact> Artifacts,
    IReadOnlyList<DiagnosticConfigurationFinding> Configuration,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>Reads filesystem metadata without opening artifact contents.</summary>
public interface IDiagnosticPathInspector
{
    /// <summary>Inspects one expected file or directory location.</summary>
    DiagnosticPathMetadata Inspect(
        string path,
        bool expectDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates bounded matching files beneath a known directory.</summary>
    IReadOnlyList<string> EnumerateFiles(
        string directory,
        IReadOnlySet<string> extensions,
        IReadOnlySet<string>? fileNamePrefixes,
        int maximumFiles,
        CancellationToken cancellationToken = default);
}

/// <summary>A configured Windows Error Reporting LocalDumps location.</summary>
public sealed record WerLocalDumpConfiguration(
    string Scope,
    string? ImageName,
    string DumpFolder,
    int? DumpType);

/// <summary>Finds Windows Error Reporting LocalDumps configuration.</summary>
public interface IWindowsErrorReportingInspector
{
    /// <summary>Inspects global and per-executable LocalDumps registry settings.</summary>
    IReadOnlyList<WerLocalDumpConfiguration> Inspect(
        IReadOnlyCollection<string> imageNames,
        ICollection<DiscoveryIssue> issues);
}
