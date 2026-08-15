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
    internal const string ProcessIdReuseError =
        "The process ID was reused after the system snapshot was captured.";

    /// <summary>Gets the stable identity available within a snapshot.</summary>
    public string Identity => CreationTime is null
        ? ProcessId.ToString(CultureInfo.InvariantCulture)
        : FormattableString.Invariant($"{ProcessId}@{CreationTime:O}");

    /// <summary>Gets whether enrichment observed a different process generation.</summary>
    public bool IsProcessIdReused => string.Equals(
        MetadataError,
        ProcessIdReuseError,
        StringComparison.Ordinal);
}
