namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Evidence supporting a discovered Chromium-related installation.</summary>
public sealed record InstallationEvidence(
    string Source,
    string Detail,
    string? Path = null,
    int? ProcessId = null);

/// <summary>A Chromium browser, runtime, or application installation.</summary>
public sealed record ChromiumInstallation(
    string Name,
    string Kind,
    string Platform,
    string InstallPath,
    string? ExecutablePath,
    string? Version,
    string? Channel,
    IReadOnlyList<InstallationEvidence> Evidence);

/// <summary>Statistics describing an installation discovery scan.</summary>
public sealed record InstallationDiscoveryStatistics(
    int SearchRootCount,
    int DirectoryCount,
    int MarkerFileCount,
    int RunningProcessCount,
    int InaccessibleDirectoryCount,
    int TruncatedDirectoryCount,
    TimeSpan Elapsed);

/// <summary>Result of Chromium-related installation discovery.</summary>
public sealed record InstallationDiscoveryResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ChromiumInstallation> Installations,
    InstallationDiscoveryStatistics Statistics,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>Controls Windows installation discovery.</summary>
public sealed record WindowsInstallationDiscoveryOptions
{
    /// <summary>
    /// Creates default options for Program Files and per-user application roots.
    /// </summary>
    public WindowsInstallationDiscoveryOptions()
    {
    }

    /// <summary>Creates options with explicit filesystem search roots.</summary>
    public WindowsInstallationDiscoveryOptions(
        IReadOnlyList<string> searchRoots,
        bool includeKnownLocations = true,
        int maximumDepth = 12)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        SearchRoots = searchRoots;
        IncludeKnownLocations = includeKnownLocations;
        MaximumDepth = maximumDepth;
    }

    /// <summary>
    /// Gets explicit search roots, or null to use Program Files and LocalAppData\Programs.
    /// </summary>
    public IReadOnlyList<string>? SearchRoots { get; init; }

    /// <summary>Gets whether well-known browser and WebView2 locations are checked.</summary>
    public bool IncludeKnownLocations { get; init; } = true;

    /// <summary>Gets the maximum recursive depth beneath each search root.</summary>
    public int MaximumDepth { get; init; } = 12;
}
