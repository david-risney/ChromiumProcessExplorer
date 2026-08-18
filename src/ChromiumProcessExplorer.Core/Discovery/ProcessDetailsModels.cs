namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A value that can be omitted from potentially shared exports.</summary>
public sealed record SensitiveStringValue(
    string? Value,
    bool IsRedacted,
    string Classification);

/// <summary>One parsed command-line switch and optional sensitive value.</summary>
public sealed record ProcessSwitchDetail(
    string Name,
    bool HasValue,
    SensitiveStringValue Value);

/// <summary>Executable version resource values.</summary>
public sealed record ProcessExecutableVersion(
    string? FileVersion,
    string? ProductVersion,
    string? ProductName,
    string? CompanyName,
    string? OriginalFileName);

/// <summary>Windows-specific process security and binary metadata.</summary>
public sealed record ProcessPlatformDetails(
    DateTimeOffset? ReopenedCreationTime,
    string? Architecture,
    string? NativeArchitecture,
    string? IntegrityLevel,
    bool? IsElevated,
    string? PackageFullName,
    ProcessExecutableVersion? ExecutableVersion,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>A stable process-details record for one process generation.</summary>
public sealed record ProcessDetailEntry(
    ProcessIdentity Identity,
    int ParentProcessId,
    string ImageName,
    SensitiveStringValue ExecutablePath,
    SensitiveStringValue CommandLine,
    IReadOnlyList<ProcessSwitchDetail> Switches,
    string? ChromiumProcessRole,
    string RoleSource,
    SensitiveStringValue UserDataDirectory,
    ProcessExecutableVersion? ExecutableVersion,
    string? Architecture,
    string? NativeArchitecture,
    string? IntegrityLevel,
    bool? IsElevated,
    string? PackageFullName,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<SensitiveStringValue> LoadedModules,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>Versioned process-details output for CLI, GUI, and Copilot consumers.</summary>
public sealed record ProcessDetailsResult(
    string SchemaVersion,
    DateTimeOffset CapturedAt,
    bool IncludesSensitiveValues,
    IReadOnlyList<ProcessDetailEntry> Processes,
    IReadOnlyList<DiscoveryIssue> Issues);
