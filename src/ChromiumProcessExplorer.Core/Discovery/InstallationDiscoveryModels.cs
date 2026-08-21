namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Evidence supporting a discovered Chromium-related installation.</summary>
public sealed record InstallationEvidence(
    string Source,
    string Detail,
    string? Path = null,
    int? ProcessId = null);

/// <summary>Package identity for an MSIX/AppX installation.</summary>
public sealed record InstallationPackageIdentity(
    string PackageFullName,
    string PackageFamilyName,
    string Name,
    string? Version,
    string? Architecture,
    string? PublisherId);

/// <summary>Richer provenance-bearing installation metadata.</summary>
public sealed record InstallationMetadata(
    string? Architecture,
    string? Publisher,
    string InstallType,
    string? InstallSource,
    string? VersionProvenance,
    InstallationPackageIdentity? PackageIdentity,
    string? ResourcesPath,
    string? RuntimePath,
    bool? IsSharedRuntime,
    string Confidence)
{
    /// <summary>Gets the browser-managed application ID, when applicable.</summary>
    public string? ApplicationId { get; init; }

    /// <summary>Gets the browser platform hosting a managed app.</summary>
    public string? BrowserPlatform { get; init; }

    /// <summary>Gets the browser profile name hosting a managed app.</summary>
    public string? BrowserProfileName { get; init; }
}

/// <summary>A Chromium browser, runtime, or application installation.</summary>
public sealed record ChromiumInstallation(
    string Name,
    string Kind,
    string Platform,
    string InstallPath,
    string? ExecutablePath,
    string? Version,
    string? Channel,
    InstallationMetadata Metadata,
    IReadOnlyList<InstallationEvidence> Evidence);

/// <summary>Statistics describing an installation discovery scan.</summary>
public sealed record InstallationDiscoveryStatistics(
    int SearchRootCount,
    int DirectoryCount,
    int MarkerFileCount,
    int RunningProcessCount,
    int InaccessibleDirectoryCount,
    int TruncatedDirectoryCount,
    int RegistryRecordCount,
    int PackageCount,
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
        IncludeRegistry = false;
        IncludePackages = false;
        IncludeBrowserManagedApps = false;
        MaximumDepth = maximumDepth;
    }

    /// <summary>
    /// Gets explicit search roots, or null to use Program Files and LocalAppData\Programs.
    /// </summary>
    public IReadOnlyList<string>? SearchRoots { get; init; }

    /// <summary>
    /// Gets extra filesystem roots appended to the default or explicit roots.
    /// </summary>
    public IReadOnlyList<string> AdditionalSearchRoots { get; init; } = [];

    /// <summary>Gets whether well-known browser and WebView2 locations are checked.</summary>
    public bool IncludeKnownLocations { get; init; } = true;

    /// <summary>Gets whether uninstall registry records are included.</summary>
    public bool IncludeRegistry { get; init; } = true;

    /// <summary>Gets whether accessible WindowsApps package roots are included.</summary>
    public bool IncludePackages { get; init; } = true;

    /// <summary>Gets whether browser profiles, shortcuts, and app registrations are included.</summary>
    public bool IncludeBrowserManagedApps { get; init; } = true;

    /// <summary>Gets the maximum recursive depth beneath each search root.</summary>
    public int MaximumDepth { get; init; } = 12;

    /// <summary>Gets the maximum total directories inspected by one scan.</summary>
    public int MaximumDirectories { get; init; } = 50_000;
}

/// <summary>An installed-program record from a Windows uninstall registry key.</summary>
public sealed record InstalledProgramRecord(
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallLocation,
    string? DisplayIconPath,
    string? InstallSource,
    string? UninstallString,
    bool IsWindowsInstaller,
    string Scope,
    string RegistryView,
    string RegistryPath);

/// <summary>Reads installed-program registrations.</summary>
public interface IInstalledProgramProvider
{
    /// <summary>Reads machine/user uninstall registrations in both registry views.</summary>
    IReadOnlyList<InstalledProgramRecord> Discover(
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken = default);
}

/// <summary>A Chromium-related MSIX/AppX package installation.</summary>
public sealed record WindowsPackageInstallation(
    string DisplayName,
    string Platform,
    string InstallPath,
    string? ExecutablePath,
    InstallationPackageIdentity Identity,
    string? Publisher,
    string? ResourcesPath,
    string? RuntimePath,
    bool? IsSharedRuntime,
    IReadOnlyList<InstallationEvidence> Evidence);

/// <summary>A browser-managed installed app or PWA using a shared runtime.</summary>
public sealed record BrowserManagedAppInstallation(
    string AppId,
    string Name,
    string BrowserPlatform,
    string? BrowserExecutablePath,
    string? ProfileName,
    string? ProfilePath,
    string InstallPath,
    IReadOnlyList<InstallationEvidence> Evidence);

/// <summary>Finds Chromium-related Windows package installations.</summary>
public interface IWindowsPackageInstallationProvider
{
    /// <summary>Discovers accessible package installations.</summary>
    IReadOnlyList<WindowsPackageInstallation> Discover(
        IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken = default);
}

/// <summary>Discovers Chrome/Edge-family installed apps and PWAs.</summary>
public interface IBrowserManagedAppProvider
{
    /// <summary>Discovers browser-managed app records and partial failures.</summary>
    IReadOnlyList<BrowserManagedAppInstallation> Discover(
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken = default);
}
