using System.Diagnostics;

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

    /// <summary>Creates a provider using default Windows search roots.</summary>
    public WindowsInstallationProvider(
        WindowsInstallationDiscoveryOptions? options = null)
    {
        _options = options ?? new WindowsInstallationDiscoveryOptions();
        if (_options.MaximumDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumDepth must be non-negative.");
        }
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
                stopwatch.Elapsed),
            issues);
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
        string installPath = FindApplicationRoot(
            markerDirectory,
            siblingFiles,
            searchRoot);
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

    private static string FindApplicationRoot(
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

        return markerDirectory;
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

        return "Stable";
    }

    private static InstallationBuilder GetOrCreate(
        Dictionary<string, InstallationBuilder> installations,
        string installPath,
        string name,
        string kind,
        string platform)
    {
        string normalizedPath = Path.GetFullPath(installPath)
            .TrimEnd(Path.DirectorySeparatorChar);
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
    }

    private sealed class InstallationBuilder
    {
        private readonly List<InstallationEvidence> _evidence = [];
        private string _name;
        private string _kind;
        private string _platform;
        private string? _executablePath;
        private string? _channel;

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

        public void RefineIdentity(string name, string kind, string platform)
        {
            if (GetSpecificity(kind, platform) > GetSpecificity(_kind, _platform))
            {
                _name = name;
                _kind = kind;
                _platform = platform;
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

        public void AddEvidence(InstallationEvidence evidence)
        {
            if (!_evidence.Contains(evidence))
            {
                _evidence.Add(evidence);
            }
        }

        public ChromiumInstallation Build()
        {
            string? version = _executablePath is null
                ? null
                : GetVersion(_executablePath);
            return new ChromiumInstallation(
                _name,
                _kind,
                _platform,
                InstallPath,
                _executablePath,
                version,
                _channel,
                _evidence
                    .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
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

        private static string? GetVersion(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return info.ProductVersion ?? info.FileVersion;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or IOException
                    or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
