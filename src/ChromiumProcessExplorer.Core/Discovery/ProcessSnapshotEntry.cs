using System.Globalization;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A process captured during a single system snapshot.</summary>
public sealed record ProcessSnapshotEntry(
    int ProcessId,
    int ParentProcessId,
    DateTimeOffset? CreationTime,
    string ImageName,
    string? ExecutablePath,
    string? CommandLine,
    string? ChromiumProcessType,
    string? UserDataDirectory,
    bool IsLikelyChromium,
    IReadOnlyList<string> Evidence,
    string? MetadataError)
{
    /// <summary>
    /// Gets loaded module paths supplied by the snapshot provider, when available.
    /// </summary>
    public IReadOnlyList<string> LoadedModules { get; init; } = [];

    /// <summary>
    /// Gets the error encountered while collecting loaded modules, when collection
    /// was incomplete.
    /// </summary>
    public string? ModuleInspectionError { get; init; }

    /// <summary>Gets the stable identity available within a snapshot.</summary>
    public string Identity => CreationTime is null
        ? ProcessId.ToString(CultureInfo.InvariantCulture)
        : FormattableString.Invariant($"{ProcessId}@{CreationTime:O}");
}
