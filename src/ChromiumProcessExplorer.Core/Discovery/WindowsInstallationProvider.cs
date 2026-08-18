using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Discovers Chromium browsers, WebView2 runtimes, and Chromium-based
/// applications installed on Windows.
/// </summary>
public sealed class WindowsInstallationProvider : IInstallationProvider
{
    private static readonly HashSet<string> MarkerFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "app.asar",
            "CefSharp.Core.dll",
            "CefSharp.Core.Runtime.dll",
            "chrome_elf.dll",
            "jcef.dll",
            "libcef.dll",
            "Microsoft.Web.WebView2.Core.dll",
            "nw.dll",
            "package.nw",
            "Qt5WebEngineCore.dll",
            "Qt6WebEngineCore.dll",
            "WebView2Loader.dll",
        };

    private static readonly KnownInstallSpec[] KnownInstallSpecs =
    [
        new("Google Chrome", "Browser", "Chrome", "Stable", "chrome.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Google\Chrome\Application"),
        new("Google Chrome Beta", "Browser", "Chrome", "Beta", "chrome.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Google\Chrome Beta\Application"),
        new("Google Chrome Dev", "Browser", "Chrome", "Dev", "chrome.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Google\Chrome Dev\Application"),
        new("Google Chrome Canary", "Browser", "Chrome", "Canary", "chrome.exe",
            [Environment.SpecialFolder.LocalApplicationData],
            @"Google\Chrome SxS\Application"),
        new("Microsoft Edge", "Browser", "Edge", "Stable", "msedge.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Microsoft\Edge\Application"),
        new("Microsoft Edge Beta", "Browser", "Edge", "Beta", "msedge.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Microsoft\Edge Beta\Application"),
        new("Microsoft Edge Dev", "Browser", "Edge", "Dev", "msedge.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Microsoft\Edge Dev\Application"),
        new("Microsoft Edge Canary", "Browser", "Edge", "Canary", "msedge.exe",
            [Environment.SpecialFolder.LocalApplicationData],
            @"Microsoft\Edge SxS\Application"),
        new("Brave", "Browser", "Brave", "Stable", "brave.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData],
            @"BraveSoftware\Brave-Browser\Application"),
        new("Vivaldi", "Browser", "Vivaldi", null, "vivaldi.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData],
            @"Vivaldi\Application"),
        new("Chromium", "Browser", "Chromium", null, "chromium.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData],
            @"Chromium\Application"),
        new("WebView2 Runtime", "Runtime", "WebView2", "Evergreen", "msedgewebview2.exe",
            [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86],
            @"Microsoft\EdgeWebView\Application"),
    ];

    private readonly WindowsInstallationDiscoveryOptions _options;
    private readonly IInstalledProgramProvider _installedProgramProvider;
    private readonly IWindowsPackageInstallationProvider _packageProvider;

    /// <summary>Creates a provider using default Windows search roots.</summary>
    public WindowsInstallationProvider(
        WindowsInstallationDiscoveryOptions? options = null)
        : this(
            options,
            new WindowsInstalledProgramProvider(),
            new WindowsPackageInstallationProvider())
    {
    }

    /// <summary>Creates a provider using custom registry and package sources.</summary>
    public WindowsInstallationProvider(
        WindowsInstallationDiscoveryOptions? options,
        IInstalledProgramProvider installedProgramProvider,
        IWindowsPackageInstallationProvider packageProvider)
    {
        ArgumentNullException.ThrowIfNull(installedProgramProvider);
        ArgumentNullException.ThrowIfNull(packageProvider);
        _options = options ?? new WindowsInstallationDiscoveryOptions();
        _installedProgramProvider = installedProgramProvider;
        _packageProvider = packageProvider;
        if (_options.MaximumDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumDepth must be non-negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            _options.MaximumDirectories);
    }

    /// <inheritdoc />
    public ValueTask<InstallationDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runningProcesses);
        return new ValueTask<InstallationDiscoveryResult>(
            Task.Run(
                () => Discover(runningProcesses, cancellationToken),
                cancellationToken));
    }

    private InstallationDiscoveryResult Discover(
        IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        Dictionary<string, InstallationBuilder> installations =
            new(StringComparer.OrdinalIgnoreCase);
        List<DiscoveryIssue> issues = [];
        ScanCounters counters = new();

        if (_options.IncludeKnownLocations)
        {
            DiscoverKnownLocations(installations, issues, cancellationToken);
        }

        string[] searchRoots = GetSearchRoots();
        foreach (string searchRoot in searchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanSearchRoot(
                searchRoot,
                installations,
                issues,
                counters,
                cancellationToken);
        }

        DiscoverRunningApplications(
            runningProcesses,
            installations,
            counters,
            cancellationToken);

        if (_options.IncludeRegistry)
        {
            IReadOnlyList<InstalledProgramRecord> registryRecords =
                _installedProgramProvider.Discover(issues, cancellationToken);
            counters.RegistryRecordCount = registryRecords.Count;
            DiscoverRegisteredApplications(
                registryRecords,
                installations,
                cancellationToken);
        }

        if (_options.IncludePackages)
        {
            IReadOnlyList<WindowsPackageInstallation> packages =
                _packageProvider.Discover(
                    runningProcesses,
                    issues,
                    cancellationToken);
            counters.PackageCount = packages.Count;
            DiscoverPackages(packages, installations);
        }

        ChromiumInstallation[] results = installations.Values
            .Select(builder => builder.Build())
            .OrderBy(installation => installation.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(installation => installation.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(installation => installation.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        stopwatch.Stop();
        return new InstallationDiscoveryResult(
            capturedAt,
            results,
            new InstallationDiscoveryStatistics(
                searchRoots.Length,
                counters.DirectoryCount,
                counters.MarkerFileCount,
                counters.RunningProcessCount,
                counters.InaccessibleDirectoryCount,
                counters.TruncatedDirectoryCount,
                counters.RegistryRecordCount,
                counters.PackageCount,
                stopwatch.Elapsed),
            issues);
    }

    private static void DiscoverRegisteredApplications(
        IEnumerable<InstalledProgramRecord> records,
        Dictionary<string, InstallationBuilder> installations,
        CancellationToken cancellationToken)
    {
        foreach (InstalledProgramRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? executablePath = GetRegisteredExecutable(record);
            string? installPath = GetRegisteredInstallPath(record, executablePath);
            if (installPath is null)
            {
                continue;
            }

            string normalizedPath = NormalizePath(installPath);
            InstallationBuilder builder;
            builder = executablePath is null
                ? null!
                : installations.Values.FirstOrDefault(existing =>
                    existing.ExecutablePath is not null
                    && PathsEqual(existing.ExecutablePath, executablePath))!;
            if (builder is null
                && !installations.TryGetValue(normalizedPath, out builder!))
            {
                if (!TryClassifyRegisteredProgram(
                    record,
                    executablePath,
                    installPath,
                    out (string Kind, string Platform, string Name) identity))
                {
                    continue;
                }

                builder = GetOrCreate(
                    installations,
                    installPath,
                    identity.Name,
                    identity.Kind,
                    identity.Platform);
            }
            else
            {
                builder.RefineName(record.DisplayName);
                if (TryClassifyRegisteredProgram(
                    record,
                    executablePath,
                    installPath,
                    out (string Kind, string Platform, string Name) identity))
                {
                    builder.RefineIdentity(
                        identity.Name,
                        identity.Kind,
                        identity.Platform);
                }
            }
            if (executablePath is not null)
            {
                builder.SetExecutable(executablePath);
            }

            builder.SetChannel(builder.Kind == "Browser"
                ? InferChannel($"{record.DisplayName} {installPath}")
                : null);
            builder.SetRegisteredMetadata(record);
            builder.AddEvidence(new InstallationEvidence(
                "uninstall-registry",
                $"{record.Scope}/{record.RegistryView} uninstall record "
                    + $"({GetInstallType(record)}); version "
                    + $"{record.DisplayVersion ?? "unknown"}; publisher "
                    + $"{record.Publisher ?? "unknown"}.",
                record.RegistryPath));
        }
    }

    private static void DiscoverPackages(
        IEnumerable<WindowsPackageInstallation> packages,
        Dictionary<string, InstallationBuilder> installations)
    {
        foreach (WindowsPackageInstallation package in packages)
        {
            string kind = package.Platform == "WebView2"
                ? "Runtime"
                : package.Platform is "Edge" or "Chrome" or "Brave"
                    ? "Browser"
                    : "Application";
            InstallationBuilder builder = installations.Values.FirstOrDefault(
                    existing => existing.Platform.Equals(
                            package.Platform,
                            StringComparison.OrdinalIgnoreCase)
                        && IsSameOrChildPath(
                            existing.InstallPath,
                            package.InstallPath))
                ?? GetOrCreate(
                    installations,
                    package.InstallPath,
                    package.DisplayName,
                    kind,
                    package.Platform);
            builder.RefineIdentity(
                package.DisplayName,
                kind,
                package.Platform);
            if (package.ExecutablePath is not null)
            {
                builder.SetExecutable(package.ExecutablePath);
            }

            builder.SetPackageMetadata(package);
            builder.SetChannel(kind == "Browser"
                ? InferChannel(package.DisplayName)
                : null);
            foreach (InstallationEvidence evidence in package.Evidence)
            {
                builder.AddEvidence(evidence);
            }
        }
    }

    private static void DiscoverKnownLocations(
        Dictionary<string, InstallationBuilder> installations,
        List<DiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (KnownInstallSpec spec in KnownInstallSpecs)
        {
            foreach (Environment.SpecialFolder specialFolder in spec.SpecialFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string basePath = Environment.GetFolderPath(specialFolder);
                if (string.IsNullOrWhiteSpace(basePath))
                {
                    continue;
                }

                string applicationPath = Path.Combine(basePath, spec.RelativePath);
                if (!Directory.Exists(applicationPath))
                {
                    continue;
                }

                try
                {
                    foreach (string executablePath in FindExecutables(
                        applicationPath,
                        spec.ExecutableName,
                        maximumDepth: 2,
                        cancellationToken))
                    {
                        string installPath = spec.Kind == "Runtime"
                            ? Path.GetDirectoryName(executablePath)!
                            : applicationPath;
                        InstallationBuilder builder = GetOrCreate(
                            installations,
                            installPath,
                            spec.Name,
                            spec.Kind,
                            spec.Platform);
                        builder.SetExecutable(executablePath);
                        builder.SetChannel(spec.Channel);
                        builder.SetDiscoveryMetadata(
                            "KnownLocation",
                            "Well-known Windows installation path",
                            spec.Kind == "Runtime" ? true : null,
                            "High");
                        builder.AddEvidence(new InstallationEvidence(
                            "known-location",
                            $"{spec.Name} executable found in a well-known installation location.",
                            executablePath));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException)
                {
                    issues.Add(new DiscoveryIssue(
                        "installation-known-location",
                        $"{applicationPath}: {exception.Message}"));
                }
            }
        }
    }

    private void ScanSearchRoot(
        string searchRoot,
        Dictionary<string, InstallationBuilder> installations,
        List<DiscoveryIssue> issues,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(searchRoot))
        {
            return;
        }

        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((Path.GetFullPath(searchRoot), 0));

        while (pending.TryDequeue(out (string Path, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (counters.DirectoryCount >= _options.MaximumDirectories)
            {
                counters.TruncatedDirectoryCount += pending.Count + 1;
                return;
            }

            counters.DirectoryCount++;

            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current.Path);
                directories = Directory.GetDirectories(current.Path);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException)
            {
                counters.InaccessibleDirectoryCount++;
                if (counters.ReportedAccessIssueCount < 20)
                {
                    issues.Add(new DiscoveryIssue(
                        "installation-filesystem-scan",
                        $"{current.Path}: {exception.Message}"));
                    counters.ReportedAccessIssueCount++;
                }

                continue;
            }

            string[] markerFiles = files
                .Where(file => MarkerFileNames.Contains(Path.GetFileName(file)))
                .ToArray();
            if (markerFiles.Length > 0)
            {
                counters.MarkerFileCount += markerFiles.Length;
                try
                {
                    AddMarkerInstallation(
                        current.Path,
                        markerFiles,
                        files,
                        searchRoot,
                        installations);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException)
                {
                    issues.Add(new DiscoveryIssue(
                        "installation-marker-inspection",
                        $"{current.Path}: {exception.Message}"));
                }
            }

            if (current.Depth >= _options.MaximumDepth)
            {
                counters.TruncatedDirectoryCount += directories.Length;
                continue;
            }

            foreach (string directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Enqueue((directory, current.Depth + 1));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException)
                {
                    counters.InaccessibleDirectoryCount++;
                }
            }
        }
    }

    private static void AddMarkerInstallation(
        string markerDirectory,
        IReadOnlyList<string> markerFiles,
        IReadOnlyList<string> siblingFiles,
        string searchRoot,
        Dictionary<string, InstallationBuilder> installations)
    {
        string? installPath = FindApplicationRoot(
            markerDirectory,
            siblingFiles,
            searchRoot);
        if (installPath is null)
        {
            return;
        }
        string[] markerNames = markerFiles
            .Select(file => Path.GetFileName(file)!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        (string platform, string name) = ClassifyMarkers(markerNames, installPath);
        string? executablePath = FindPreferredExecutable(installPath);
        string? productName = executablePath is null
            ? null
            : GetProductName(executablePath);
        if (!string.IsNullOrWhiteSpace(productName))
        {
            name = productName;
        }

        if (installations.Values.Any(existing =>
            IsSameOrChildPath(installPath, existing.InstallPath)
            && existing.Kind is "Browser" or "Runtime"))
        {
            return;
        }

        InstallationBuilder builder = GetOrCreate(
            installations,
            installPath,
            name,
            "Application",
            platform);
        if (executablePath is not null)
        {
            builder.SetExecutable(executablePath);
        }
        builder.SetDiscoveryMetadata(
            "Portable",
            "Filesystem marker scan",
            platform switch
            {
                "Electron" or "CEF" or "NW.js" or "Qt WebEngine" => false,
                "WebView2" => null,
                _ => null,
            },
            executablePath is null ? "Low" : "Medium");
        builder.SetLayoutPaths(markerFiles);

        foreach (string markerFile in markerFiles)
        {
            builder.AddEvidence(new InstallationEvidence(
                "filesystem-marker",
                $"Found Chromium-related marker {Path.GetFileName(markerFile)}.",
                markerFile));
        }
    }

    private static void DiscoverRunningApplications(
        IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
        Dictionary<string, InstallationBuilder> installations,
        ScanCounters counters,
        CancellationToken cancellationToken)
    {
        ProcessSnapshotEntry[][] processGroups = runningProcesses
            .Where(process => process.IsLikelyChromium
                && !string.IsNullOrWhiteSpace(process.ExecutablePath))
            .GroupBy(
                process => Path.GetFullPath(process.ExecutablePath!),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();

        counters.RunningProcessCount += processGroups.Sum(group => group.Length);
        foreach (ProcessSnapshotEntry[] processGroup in processGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessSnapshotEntry process = processGroup[0];
            string executablePath = Path.GetFullPath(process.ExecutablePath!);
            string installPath = Path.GetDirectoryName(executablePath)!;
            (string kind, string platform, string name, string? channel) =
                ClassifyExecutable(executablePath);
            InstallationBuilder builder = GetOrCreate(
                installations,
                installPath,
                name,
                kind,
                platform);
            builder.SetExecutable(executablePath);
            builder.SetChannel(channel);
            builder.SetDiscoveryMetadata(
                "Portable",
                "Running process executable",
                kind == "Runtime"
                    ? true
                    : platform is "Electron" or "CEF"
                        ? false
                        : null,
                "High");
            builder.AddEvidence(new InstallationEvidence(
                "running-process",
                processGroup.Length == 1
                    ? $"Running Chromium-related process {process.ImageName}."
                    : $"{processGroup.Length} running Chromium-related processes use "
                        + $"{process.ImageName}.",
                executablePath,
                process.ProcessId));
            if (platform == "Electron"
                && GetElectronApplicationPath(executablePath) is string applicationPath)
            {
                builder.AddEvidence(new InstallationEvidence(
                    "electron-packaged-layout",
                    "Found Electron application resources next to the running executable.",
                    applicationPath,
                    process.ProcessId));
            }
        }
    }

    private string[] GetSearchRoots()
    {
        IEnumerable<string> roots = _options.SearchRoots ?? GetDefaultSearchRoots();
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetDefaultSearchRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string localApplicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            yield return Path.Combine(localApplicationData, "Programs");
        }
    }

    private static IEnumerable<string> FindExecutables(
        string root,
        string executableName,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));

        while (pending.TryDequeue(out (string Path, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate = Path.Combine(current.Path, executableName);
            if (File.Exists(candidate))
            {
                yield return candidate;
            }

            if (current.Depth >= maximumDepth)
            {
                continue;
            }

            foreach (string child in Directory.GetDirectories(current.Path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
        }
    }

    private static string? FindApplicationRoot(
        string markerDirectory,
        IReadOnlyList<string> siblingFiles,
        string searchRoot)
    {
        if (siblingFiles.Any(file =>
            string.Equals(Path.GetFileName(file), "app.asar", StringComparison.OrdinalIgnoreCase)))
        {
            DirectoryInfo? resources = Directory.GetParent(markerDirectory);
            if (string.Equals(
                Path.GetFileName(markerDirectory),
                "resources",
                StringComparison.OrdinalIgnoreCase)
                && resources is not null)
            {
                return resources.FullName;
            }
        }

        string current = markerDirectory;
        for (int depth = 0; depth < 8 && IsSameOrChildPath(current, searchRoot); depth++)
        {
            if (Directory.EnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly).Any())
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null || PathsEqual(parent.FullName, searchRoot))
            {
                break;
            }

            current = parent.FullName;
        }

        return IsDependencyOrSdkPath(markerDirectory)
            ? null
            : markerDirectory;
    }

    private static bool IsDependencyOrSdkPath(string path)
    {
        return path.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("sdk", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("packages", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".nuget", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("runtimes", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("native", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindPreferredExecutable(string installPath)
    {
        try
        {
            return Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(path => IsHelperExecutable(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsHelperExecutable(string path)
    {
        string name = Path.GetFileName(path);
        return name.Contains("helper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("notification", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Platform, string Name) ClassifyMarkers(
        IReadOnlyCollection<string> markerNames,
        string installPath)
    {
        if (markerNames.Contains("libcef.dll", StringComparer.OrdinalIgnoreCase)
            || markerNames.Any(name =>
                name.StartsWith("CefSharp.", StringComparison.OrdinalIgnoreCase))
            || markerNames.Contains("jcef.dll", StringComparer.OrdinalIgnoreCase))
        {
            return ("CEF", GetDirectoryName(installPath, "CEF application"));
        }

        if (markerNames.Contains("app.asar", StringComparer.OrdinalIgnoreCase))
        {
            return ("Electron", GetDirectoryName(installPath, "Electron application"));
        }

        if (markerNames.Contains("nw.dll", StringComparer.OrdinalIgnoreCase)
            || markerNames.Contains("package.nw", StringComparer.OrdinalIgnoreCase))
        {
            return ("NW.js", GetDirectoryName(installPath, "NW.js application"));
        }

        if (markerNames.Any(name =>
            name.EndsWith("WebEngineCore.dll", StringComparison.OrdinalIgnoreCase)))
        {
            return ("Qt WebEngine", GetDirectoryName(installPath, "Qt WebEngine application"));
        }

        if (markerNames.Contains("WebView2Loader.dll", StringComparer.OrdinalIgnoreCase)
            || markerNames.Contains(
                "Microsoft.Web.WebView2.Core.dll",
                StringComparer.OrdinalIgnoreCase))
        {
            return ("WebView2", GetDirectoryName(installPath, "WebView2 application"));
        }

        return ("Chromium", GetDirectoryName(installPath, "Chromium application"));
    }

    private static string? GetRegisteredExecutable(InstalledProgramRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.DisplayIconPath)
            && File.Exists(record.DisplayIconPath))
        {
            return Path.GetFullPath(record.DisplayIconPath);
        }

        if (string.IsNullOrWhiteSpace(record.InstallLocation)
            || !Directory.Exists(record.InstallLocation))
        {
            return null;
        }

        string? knownName = record.DisplayName.Contains(
            "WebView2",
            StringComparison.OrdinalIgnoreCase)
            ? "msedgewebview2.exe"
            : record.DisplayName.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                ? "msedge.exe"
                : record.DisplayName.Contains(
                    "Chrome",
                    StringComparison.OrdinalIgnoreCase)
                    ? "chrome.exe"
                    : null;
        if (knownName is not null)
        {
            try
            {
                return FindExecutables(
                    record.InstallLocation,
                    knownName,
                    3,
                    CancellationToken.None).FirstOrDefault();
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return FindPreferredExecutable(record.InstallLocation);
    }

    private static string? GetRegisteredInstallPath(
        InstalledProgramRecord record,
        string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(record.InstallLocation))
        {
            try
            {
                return Path.GetFullPath(record.InstallLocation);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                return null;
            }
        }

        return executablePath is null
            ? null
            : Path.GetDirectoryName(executablePath);
    }

    private static bool TryClassifyRegisteredProgram(
        InstalledProgramRecord record,
        string? executablePath,
        string installPath,
        out (string Kind, string Platform, string Name) identity)
    {
        string name = record.DisplayName;
        if (name.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
        {
            identity = ("Runtime", "WebView2", name);
            return true;
        }

        foreach ((string marker, string platform) in new[]
        {
            ("Google Chrome", "Chrome"),
            ("Microsoft Edge", "Edge"),
            ("Brave", "Brave"),
            ("Vivaldi", "Vivaldi"),
            ("Opera", "Opera"),
            ("Chromium", "Chromium"),
        })
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                identity = ("Browser", platform, name);
                return true;
            }
        }

        if (executablePath is not null)
        {
            (string kind, string platform, _, _) =
                ClassifyExecutable(executablePath);
            if (platform != "Chromium")
            {
                identity = (kind, platform, name);
                return true;
            }
        }

        string installType = GetInstallType(record);
        bool warrantsLayoutInspection = installType is "Squirrel" or "NSIS"
            || name.Contains("CEF", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Electron", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WebView", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Chromium", StringComparison.OrdinalIgnoreCase);
        if (!warrantsLayoutInspection)
        {
            identity = default;
            return false;
        }

        string? markerPlatform = ClassifyApplicationLayout(installPath);
        if (markerPlatform is not null)
        {
            identity = ("Application", markerPlatform, name);
            return true;
        }

        identity = default;
        return false;
    }

    private static string? ClassifyApplicationLayout(string installPath)
    {
        try
        {
            if (File.Exists(Path.Combine(installPath, "resources", "app.asar"))
                || File.Exists(Path.Combine(installPath, "app.asar")))
            {
                return "Electron";
            }

            if (FindFileWithinDepth(installPath, "libcef.dll", 3) is not null)
            {
                return "CEF";
            }

            if (FindFileWithinDepth(installPath, "WebView2Loader.dll", 4) is not null
                || FindFileWithinDepth(
                    installPath,
                    "Microsoft.Web.WebView2.Core.dll",
                    4) is not null)
            {
                return "WebView2";
            }

            if (FindFileWithinDepth(installPath, "nw.dll", 3) is not null)
            {
                return "NW.js";
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? FindFileWithinDepth(
        string root,
        string fileName,
        int maximumDepth)
    {
        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));
        while (pending.TryDequeue(out (string Path, int Depth) current))
        {
            string candidate = Path.Combine(current.Path, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (current.Depth >= maximumDepth)
            {
                continue;
            }

            foreach (string child in Directory.GetDirectories(current.Path))
            {
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
        }

        return null;
    }

    private static string GetInstallType(InstalledProgramRecord record)
    {
        if (record.IsWindowsInstaller)
        {
            return "MSI";
        }

        string uninstall = record.UninstallString ?? string.Empty;
        if (uninstall.Contains("Update.exe", StringComparison.OrdinalIgnoreCase)
            && uninstall.Contains(
                "--uninstall",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Squirrel";
        }

        if (Regex.IsMatch(
                uninstall,
                @"(?:^|[\\/""\s])unins\d*\.exe(?:$|[""\s])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || uninstall.Contains("NSIS", StringComparison.OrdinalIgnoreCase))
        {
            return "NSIS";
        }

        return "Registry";
    }

    private static (string Kind, string Platform, string Name, string? Channel)
        ClassifyExecutable(string executablePath)
    {
        string fileName = Path.GetFileName(executablePath);
        string path = executablePath;

        if (fileName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Runtime", "WebView2", "WebView2 Runtime", "Evergreen");
        }

        if (fileName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Browser", "Edge", "Microsoft Edge", InferChannel(path));
        }

        if (fileName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Browser", "Chrome", "Google Chrome", InferChannel(path));
        }

        if (fileName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Browser", "Brave", "Brave", InferChannel(path));
        }

        if (fileName.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Browser", "Vivaldi", "Vivaldi", null);
        }

        if (fileName.Equals("opera.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Browser", "Opera", "Opera", InferChannel(path));
        }

        if (fileName.Equals("chromium.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Browser", "Chromium", "Chromium", null);
        }

        if (fileName.Equals("electron.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Application", "Electron", GetDirectoryName(
                Path.GetDirectoryName(path)!,
                "Electron application"), null);
        }

        if (GetElectronApplicationPath(executablePath) is not null)
        {
            return ("Application", "Electron", GetProductName(executablePath)
                ?? Path.GetFileNameWithoutExtension(fileName), null);
        }

        return ("Application", "Chromium", GetProductName(executablePath)
            ?? Path.GetFileNameWithoutExtension(fileName), null);
    }

    private static string? GetElectronApplicationPath(string executablePath)
    {
        string? directory = Path.GetDirectoryName(executablePath);
        if (directory is null)
        {
            return null;
        }

        string resources = Path.Combine(directory, "resources");
        string archive = Path.Combine(resources, "app.asar");
        if (File.Exists(archive))
        {
            return archive;
        }

        string looseApplication = Path.Combine(resources, "app");
        return File.Exists(Path.Combine(looseApplication, "package.json"))
            ? looseApplication
            : null;
    }

    private static string? InferChannel(string path)
    {
        if (path.Contains("Canary", StringComparison.OrdinalIgnoreCase)
            || path.Contains("SxS", StringComparison.OrdinalIgnoreCase))
        {
            return "Canary";
        }

        if (path.Contains("Beta", StringComparison.OrdinalIgnoreCase))
        {
            return "Beta";
        }

        if (path.Contains("Dev", StringComparison.OrdinalIgnoreCase))
        {
            return "Dev";
        }

        if (path.Contains("Internal", StringComparison.OrdinalIgnoreCase))
        {
            return "Internal";
        }

        return "Stable";
    }

    private static InstallationBuilder GetOrCreate(
        Dictionary<string, InstallationBuilder> installations,
        string installPath,
        string name,
        string kind,
        string platform)
    {
        string normalizedPath = NormalizePath(installPath);
        if (!installations.TryGetValue(normalizedPath, out InstallationBuilder? builder))
        {
            builder = new InstallationBuilder(
                normalizedPath,
                name,
                kind,
                platform);
            installations.Add(normalizedPath, builder);
        }
        else
        {
            builder.RefineIdentity(name, kind, platform);
        }

        return builder;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar);
        string normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar);
        return PathsEqual(normalizedCandidate, normalizedParent)
            || normalizedCandidate.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDirectoryName(string path, string fallback)
    {
        return new DirectoryInfo(path).Name is { Length: > 0 } name
            ? name
            : fallback;
    }

    private static string? GetProductName(string executablePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).ProductName;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetPortableExecutableArchitecture(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using BinaryReader reader = new(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            {
                return null;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset + 6 > stream.Length)
            {
                return null;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return null;
            }

            return reader.ReadUInt16() switch
            {
                0x014c => "x86",
                0x8664 => "x64",
                0xAA64 => "arm64",
                _ => null,
            };
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record KnownInstallSpec(
        string Name,
        string Kind,
        string Platform,
        string? Channel,
        string ExecutableName,
        IReadOnlyList<Environment.SpecialFolder> SpecialFolders,
        string RelativePath);

    private sealed class ScanCounters
    {
        public int DirectoryCount { get; set; }

        public int MarkerFileCount { get; set; }

        public int RunningProcessCount { get; set; }

        public int InaccessibleDirectoryCount { get; set; }

        public int TruncatedDirectoryCount { get; set; }

        public int ReportedAccessIssueCount { get; set; }

        public int RegistryRecordCount { get; set; }

        public int PackageCount { get; set; }
    }

    private sealed class InstallationBuilder
    {
        private readonly List<InstallationEvidence> _evidence = [];
        private string _name;
        private string _kind;
        private string _platform;
        private string? _executablePath;
        private string? _channel;
        private string? _registeredVersion;
        private string? _publisher;
        private string _installType = "Portable";
        private string? _installSource = "Filesystem discovery";
        private InstallationPackageIdentity? _packageIdentity;
        private string? _resourcesPath;
        private string? _runtimePath;
        private bool? _isSharedRuntime;
        private string _confidence = "Low";

        public InstallationBuilder(
            string installPath,
            string name,
            string kind,
            string platform)
        {
            InstallPath = installPath;
            _name = name;
            _kind = kind;
            _platform = platform;
        }

        public string InstallPath { get; }

        public string Kind => _kind;

        public string Platform => _platform;

        public string? ExecutablePath => _executablePath;

        public void RefineIdentity(string name, string kind, string platform)
        {
            if (GetSpecificity(kind, platform) > GetSpecificity(_kind, _platform))
            {
                _name = name;
                _kind = kind;
                _platform = platform;
            }
        }

        public void RefineName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _name = name;
            }
        }

        public void SetExecutable(string executablePath)
        {
            _executablePath ??= executablePath;
        }

        public void SetChannel(string? channel)
        {
            _channel ??= channel;
        }

        public void SetRegisteredMetadata(InstalledProgramRecord record)
        {
            _registeredVersion ??= record.DisplayVersion;
            _publisher ??= record.Publisher;
            SetInstallIdentity(
                GetInstallType(record),
                record.InstallSource ?? record.RegistryPath);
            RaiseConfidence("High");
        }

        public void SetPackageMetadata(WindowsPackageInstallation package)
        {
            _registeredVersion ??= package.Identity.Version;
            _publisher ??= package.Publisher;
            _packageIdentity ??= package.Identity;
            _resourcesPath ??= package.ResourcesPath;
            _runtimePath ??= package.RuntimePath;
            _isSharedRuntime ??= package.IsSharedRuntime;
            SetInstallIdentity("MSIX/AppX", "Windows package");
            RaiseConfidence("High");
        }

        public void SetDiscoveryMetadata(
            string installType,
            string installSource,
            bool? isSharedRuntime,
            string confidence)
        {
            SetInstallIdentity(installType, installSource);
            _isSharedRuntime ??= isSharedRuntime;
            RaiseConfidence(confidence);
        }

        public void SetLayoutPaths(IEnumerable<string> markerFiles)
        {
            foreach (string markerFile in markerFiles)
            {
                string fileName = Path.GetFileName(markerFile);
                if (fileName.Equals("app.asar", StringComparison.OrdinalIgnoreCase))
                {
                    _resourcesPath ??= Path.GetDirectoryName(markerFile);
                    _runtimePath ??= _executablePath;
                }
                else if (fileName.Equals("libcef.dll", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("nw.dll", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals(
                        "WebView2Loader.dll",
                        StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals(
                        "Microsoft.Web.WebView2.Core.dll",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _runtimePath ??= markerFile;
                    _resourcesPath ??= Path.GetDirectoryName(markerFile);
                }
            }
        }

        public void AddEvidence(InstallationEvidence evidence)
        {
            if (!_evidence.Contains(evidence))
            {
                _evidence.Add(evidence);
            }
        }

        public ChromiumInstallation Build()
        {
            (string? fileVersion, string? filePublisher) = _executablePath is null
                ? (null, null)
                : GetVersionAndPublisher(_executablePath);
            string? version = fileVersion ?? _registeredVersion;
            string? versionProvenance = fileVersion is not null
                ? "Executable version resource"
                : _packageIdentity?.Version is not null
                    ? "Package identity"
                    : _registeredVersion is not null
                        ? "Uninstall registry"
                        : null;
            string? architecture = _executablePath is null
                ? _packageIdentity?.Architecture
                : GetPortableExecutableArchitecture(_executablePath)
                    ?? _packageIdentity?.Architecture;
            string? resourcesPath = _resourcesPath;
            string? runtimePath = _runtimePath;
            if (_platform == "Electron" && _executablePath is not null)
            {
                resourcesPath ??= Path.Combine(
                    Path.GetDirectoryName(_executablePath)!,
                    "resources");
                runtimePath ??= _executablePath;
            }
            else if (_kind is "Browser" or "Runtime")
            {
                runtimePath ??= _executablePath;
                resourcesPath ??= _executablePath is null
                    ? null
                    : Path.GetDirectoryName(_executablePath);
            }

            return new ChromiumInstallation(
                _name,
                _kind,
                _platform,
                InstallPath,
                _executablePath,
                version,
                _channel,
                new InstallationMetadata(
                    architecture,
                    filePublisher ?? _publisher,
                    _installType,
                    _installSource,
                    versionProvenance,
                    _packageIdentity,
                    resourcesPath,
                    runtimePath,
                    _isSharedRuntime,
                    _confidence),
                _evidence
                    .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }

        private void SetInstallIdentity(string installType, string installSource)
        {
            if (GetInstallTypePriority(installType)
                <= GetInstallTypePriority(_installType))
            {
                return;
            }

            _installType = installType;
            _installSource = installSource;
        }

        private void RaiseConfidence(string confidence)
        {
            if (GetConfidenceScore(confidence) > GetConfidenceScore(_confidence))
            {
                _confidence = confidence;
            }
        }

        private static int GetInstallTypePriority(string installType)
        {
            return installType switch
            {
                "MSIX/AppX" => 100,
                "MSI" or "Squirrel" or "NSIS" or "Registry" => 90,
                "KnownLocation" => 70,
                "Portable" => 40,
                _ => 0,
            };
        }

        private static int GetConfidenceScore(string confidence)
        {
            return confidence switch
            {
                "High" => 30,
                "Medium" => 20,
                _ => 10,
            };
        }

        private static int GetSpecificity(string kind, string platform)
        {
            int kindScore = kind switch
            {
                "Browser" => 30,
                "Runtime" => 30,
                _ => 10,
            };
            int platformScore = platform == "Chromium" ? 0 : 5;
            return kindScore + platformScore;
        }

        private static (string? Version, string? Publisher) GetVersionAndPublisher(
            string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return (
                    info.ProductVersion ?? info.FileVersion,
                    info.CompanyName);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or IOException
                    or UnauthorizedAccessException)
            {
                return (null, null);
            }
        }
    }
}
