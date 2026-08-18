using Microsoft.Win32;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Passively discovers logging, crash, net-log, trace, and WER artifacts.
/// Artifact contents are never opened.
/// </summary>
public sealed class DiagnosticArtifactProvider
{
    private const string SchemaVersion = "1.0";
    private const int MaximumFilesPerDirectory = 128;
    private static readonly IReadOnlySet<string> LogExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".log",
            ".txt",
        };
    private static readonly IReadOnlySet<string> DumpExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dmp",
            ".mdmp",
        };
    private static readonly SwitchDefinition[] SwitchDefinitions =
    [
        new("enable-logging", "logging", "info",
            "Chromium logging is enabled.", false),
        new("disable-logging", "logging", "info",
            "Chromium logging is explicitly disabled.", false),
        new("log-file", "logging", "info",
            "Overrides the Chromium log file.", false),
        new("log-level", "logging", "info",
            "Changes Chromium log filtering.", false),
        new("v", "logging", "warning",
            "Enables verbose logging that may contain sensitive runtime data.", false),
        new("vmodule", "logging", "warning",
            "Enables component-specific verbose logging.", false),
        new("log-net-log", "network-capture", "warning",
            "Writes a network log that can contain URLs and request metadata.", true),
        new("trace-startup", "trace-capture", "warning",
            "Starts tracing during application startup.", true),
        new("trace-startup-file", "trace-capture", "warning",
            "Writes startup trace data to a file.", true),
        new("crash-dumps-dir", "crash-reporting", "warning",
            "Overrides the crash dump or Crashpad database location.", false),
        new("database", "crash-reporting", "warning",
            "Overrides a Crashpad database location.", false),
        new("disable-breakpad", "crash-reporting", "warning",
            "Disables Chromium crash reporting.", false),
        new("noerrdialogs", "crash-reporting", "warning",
            "Suppresses Windows error dialogs.", false),
        new("remote-debugging-port", "debugging-control-surface", "warning",
            "Exposes the Chrome DevTools Protocol on a TCP port.", true),
        new("remote-debugging-pipe", "debugging-control-surface", "warning",
            "Exposes the Chrome DevTools Protocol over inherited pipes.", true),
        new("no-sandbox", "security", "critical",
            "Disables Chromium sandboxing and is intended only for testing.", false),
        new("disable-web-security", "security", "critical",
            "Disables same-origin web security checks.", false),
        new("ignore-certificate-errors", "security", "critical",
            "Disables TLS certificate error enforcement.", false),
        new("allow-running-insecure-content", "security", "critical",
            "Allows active insecure content in secure pages.", false),
        new("single-process", "diagnostic", "warning",
            "Uses an unsupported single-process diagnostic mode.", false),
    ];

    private readonly IDiagnosticPathInspector _pathInspector;
    private readonly IWindowsErrorReportingInspector _werInspector;

    /// <summary>Creates a provider using Windows filesystem and registry inspection.</summary>
    public DiagnosticArtifactProvider()
        : this(
            new WindowsDiagnosticPathInspector(),
            new WindowsErrorReportingInspector())
    {
    }

    /// <summary>Creates a provider using custom passive inspectors.</summary>
    public DiagnosticArtifactProvider(
        IDiagnosticPathInspector pathInspector,
        IWindowsErrorReportingInspector werInspector)
    {
        ArgumentNullException.ThrowIfNull(pathInspector);
        ArgumentNullException.ThrowIfNull(werInspector);
        _pathInspector = pathInspector;
        _werInspector = werInspector;
    }

    /// <summary>Discovers configured and existing diagnostic artifacts.</summary>
    public DiagnosticArtifactDiscoveryResult Discover(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        bool includeSensitiveValues = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        List<DiscoveryIssue> issues = [];
        CefRuntimeAnalysis cef = CefRuntimeAdapter.Analyze(processes);
        ElectronRuntimeAnalysis electron = ElectronRuntimeAdapter.Analyze(processes);
        WebView2RuntimeAnalysis webView2 = WebView2RuntimeAdapter.Analyze(
            processes,
            CreateEmptyMojoResult(capturedAt));
        Dictionary<int, string> platforms = BuildPlatformMap(
            processes,
            cef,
            electron,
            webView2);
        List<ArtifactCandidate> candidates = [];

        CollectExplicitPathCandidates(processes, platforms, candidates);
        ElectronDiagnosticEvidenceProvider.Collect(
            processes,
            electron,
            candidates);
        CefDiagnosticEvidenceProvider.Collect(processes, cef, candidates);
        BrowserDiagnosticEvidenceProvider.Collect(
            processes,
            platforms,
            candidates);
        CollectWerCandidates(processes, platforms, candidates, issues);
        CollectPackagedDeploymentChannel(electron, candidates);

        DiagnosticArtifact[] artifacts = MaterializeCandidates(
            candidates,
            includeSensitiveValues,
            issues,
            cancellationToken);
        DiagnosticConfigurationFinding[] configuration =
            CollectConfiguration(processes, platforms, includeSensitiveValues);

        return new DiagnosticArtifactDiscoveryResult(
            SchemaVersion,
            capturedAt,
            includeSensitiveValues,
            true,
            artifacts,
            configuration,
            issues);
    }

    private static Dictionary<int, string> BuildPlatformMap(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CefRuntimeAnalysis cef,
        ElectronRuntimeAnalysis electron,
        WebView2RuntimeAnalysis webView2)
    {
        Dictionary<int, string> platforms = [];
        foreach (ProcessSnapshotEntry process in processes.Where(
            process => process.IsLikelyChromium))
        {
            platforms[process.ProcessId] = "Chromium";
        }

        foreach (WebView2ProcessInfo process in webView2.Processes)
        {
            platforms[process.ProcessId] = "WebView2";
        }

        foreach (CefProcessInfo process in cef.Processes)
        {
            platforms[process.ProcessId] = "CEF";
        }

        foreach (ElectronProcessInfo process in electron.Processes)
        {
            platforms[process.ProcessId] = "Electron";
        }

        return platforms;
    }

    private static void CollectExplicitPathCandidates(
        IEnumerable<ProcessSnapshotEntry> processes,
        Dictionary<int, string> platforms,
        ICollection<ArtifactCandidate> candidates)
    {
        foreach (ProcessSnapshotEntry process in processes)
        {
            if (!platforms.TryGetValue(process.ProcessId, out string? platform))
            {
                continue;
            }

            ChromiumCommandLine commandLine =
                ChromiumCommandLine.Parse(process.CommandLine);
            AddSwitchFile("log-file", DiagnosticArtifactKind.Log);
            AddSwitchFile("log-net-log", DiagnosticArtifactKind.NetLog);
            AddSwitchFile("trace-startup-file", DiagnosticArtifactKind.Trace);
            AddSwitchDirectory(
                "crash-dumps-dir",
                DiagnosticArtifactKind.CrashDatabase,
                DumpExtensions,
                DiagnosticArtifactKind.CrashDump);
            AddSwitchDirectory(
                "database",
                DiagnosticArtifactKind.CrashDatabase,
                DumpExtensions,
                DiagnosticArtifactKind.CrashDump);

            void AddSwitchFile(string name, DiagnosticArtifactKind kind)
            {
                string? value = commandLine.GetSwitchValue(name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    candidates.Add(new ArtifactCandidate(
                        kind,
                        platform,
                        $"--{name}",
                        value,
                        false,
                        true,
                        [process.ProcessId],
                        [$"PID {process.ProcessId} configured --{name}."]));
                }
            }

            void AddSwitchDirectory(
                string name,
                DiagnosticArtifactKind kind,
                IReadOnlySet<string> extensions,
                DiagnosticArtifactKind childKind)
            {
                string? value = commandLine.GetSwitchValue(name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    candidates.Add(new ArtifactCandidate(
                        kind,
                        platform,
                        $"--{name}",
                        value,
                        true,
                        true,
                        [process.ProcessId],
                        [$"PID {process.ProcessId} configured --{name}."],
                        extensions,
                        childKind));
                }
            }
        }
    }

    private void CollectWerCandidates(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        Dictionary<int, string> platforms,
        ICollection<ArtifactCandidate> candidates,
        List<DiscoveryIssue> issues)
    {
        string[] imageNames = processes
            .Where(process => platforms.ContainsKey(process.ProcessId))
            .Select(process => process.ImageName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<WerLocalDumpConfiguration> configurations =
            _werInspector.Inspect(imageNames, issues);
        foreach (WerLocalDumpConfiguration configuration in configurations)
        {
            int[] processIds = processes
                .Where(process => configuration.ImageName is null
                    || process.ImageName.Equals(
                        configuration.ImageName,
                        StringComparison.OrdinalIgnoreCase))
                .Where(process => platforms.ContainsKey(process.ProcessId))
                .Select(process => process.ProcessId)
                .ToArray();
            candidates.Add(new ArtifactCandidate(
                DiagnosticArtifactKind.WerDumpDirectory,
                "Windows",
                $"WER LocalDumps ({configuration.Scope})",
                configuration.DumpFolder,
                true,
                true,
                processIds,
                [
                    configuration.DumpType is null
                        ? "WER LocalDumps is configured with the default dump type."
                        : $"WER LocalDumps DumpType is {configuration.DumpType}.",
                ],
                DumpExtensions,
                DiagnosticArtifactKind.CrashDump,
                ChildFileNamePrefixes: new HashSet<string>(
                    configuration.ImageName is null
                        ? imageNames
                        : [configuration.ImageName],
                    StringComparer.OrdinalIgnoreCase)));
        }
    }

    private static void CollectPackagedDeploymentChannel(
        ElectronRuntimeAnalysis electron,
        ICollection<ArtifactCandidate> candidates)
    {
        int[] packagedProcessIds = electron.Processes
            .Where(process => process.PackageIdentity is not null)
            .Select(process => process.ProcessId)
            .Distinct()
            .ToArray();
        if (packagedProcessIds.Length == 0)
        {
            return;
        }

        candidates.Add(new ArtifactCandidate(
            DiagnosticArtifactKind.EventLog,
            "Windows",
            "MSIX deployment diagnostics",
            "Microsoft-Windows-AppxDeploymentServer/Operational",
            false,
            true,
            packagedProcessIds,
            ["Packaged Electron deployment diagnostics are exposed by Windows Event Log."],
            IsFileSystem: false));
    }

    private DiagnosticArtifact[] MaterializeCandidates(
        IEnumerable<ArtifactCandidate> candidates,
        bool includeSensitiveValues,
        List<DiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        List<DiagnosticArtifact> artifacts = [];
        foreach (IGrouping<string, ArtifactCandidate> group in candidates.GroupBy(
            candidate => $"{candidate.Kind}\0{candidate.Location}",
            StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArtifactCandidate candidate = group.First();
            int[] processIds = group
                .SelectMany(item => item.ProcessIds)
                .Distinct()
                .Order()
                .ToArray();
            string[] evidence = group
                .SelectMany(item => item.Evidence)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            DiagnosticPathMetadata metadata;
            if (!candidate.IsFileSystem
                || !Path.IsPathFullyQualified(candidate.Location))
            {
                metadata = new DiagnosticPathMetadata(
                    DiagnosticArtifactStatus.Configured,
                    candidate.IsFileSystem ? candidate.ExpectDirectory : null,
                    null,
                    null,
                    []);
            }
            else
            {
                metadata = _pathInspector.Inspect(
                    candidate.Location,
                    candidate.ExpectDirectory,
                    cancellationToken);
                foreach (DiscoveryIssue issue in metadata.Issues)
                {
                    issues.Add(issue);
                }
            }

            artifacts.Add(CreateArtifact(
                candidate.Kind,
                candidate.Platform,
                candidate.Source,
                candidate.Location,
                metadata,
                candidate.IsPotentiallySensitive,
                processIds,
                evidence,
                includeSensitiveValues));

            if (metadata.Status != DiagnosticArtifactStatus.Present
                || metadata.IsDirectory != true
                || candidate.ChildExtensions is null
                || candidate.ChildKind is null)
            {
                continue;
            }

            IReadOnlyList<string> files;
            try
            {
                files = _pathInspector.EnumerateFiles(
                    candidate.Location,
                    candidate.ChildExtensions,
                    candidate.ChildFileNamePrefixes,
                    MaximumFilesPerDirectory,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                issues.Add(new DiscoveryIssue(
                    "diagnostic-artifact-enumeration",
                    exception.Message));
                continue;
            }

            foreach (string file in files)
            {
                DiagnosticPathMetadata childMetadata = _pathInspector.Inspect(
                    file,
                    false,
                    cancellationToken);
                foreach (DiscoveryIssue issue in childMetadata.Issues)
                {
                    issues.Add(issue);
                }

                artifacts.Add(CreateArtifact(
                    candidate.ChildKind.Value,
                    candidate.Platform,
                    $"{candidate.Source} child",
                    file,
                    childMetadata,
                    true,
                    processIds,
                    [$"Found beneath {candidate.Source}."],
                    includeSensitiveValues));
            }
        }

        return artifacts
            .OrderBy(artifact => artifact.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.Kind)
            .ThenBy(artifact => artifact.Location.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DiagnosticArtifact CreateArtifact(
        DiagnosticArtifactKind kind,
        string platform,
        string source,
        string location,
        DiagnosticPathMetadata metadata,
        bool isPotentiallySensitive,
        IReadOnlyList<int> processIds,
        IReadOnlyList<string> evidence,
        bool includeSensitiveValues)
    {
        return new DiagnosticArtifact(
            kind,
            platform,
            source,
            new SensitiveStringValue(
                includeSensitiveValues ? location : null,
                !includeSensitiveValues,
                kind == DiagnosticArtifactKind.EventLog
                    ? "event-log-channel"
                    : "diagnostic-artifact-path"),
            metadata.Status,
            metadata.IsDirectory,
            metadata.Length,
            metadata.LastWriteTime,
            isPotentiallySensitive,
            processIds,
            evidence);
    }

    private static DiagnosticConfigurationFinding[] CollectConfiguration(
        IEnumerable<ProcessSnapshotEntry> processes,
        Dictionary<int, string> platforms,
        bool includeSensitiveValues)
    {
        List<DiagnosticConfigurationFinding> findings = [];
        foreach (ProcessSnapshotEntry process in processes)
        {
            if (!platforms.TryGetValue(process.ProcessId, out string? platform))
            {
                continue;
            }

            ChromiumCommandLine commandLine =
                ChromiumCommandLine.Parse(process.CommandLine);
            foreach (SwitchDefinition definition in SwitchDefinitions)
            {
                if (!commandLine.HasSwitch(definition.Name))
                {
                    continue;
                }

                string? value = commandLine.GetSwitchValue(definition.Name);
                findings.Add(new DiagnosticConfigurationFinding(
                    new ProcessIdentity(process.ProcessId, process.CreationTime),
                    platform,
                    $"--{definition.Name}",
                    definition.Category,
                    definition.Severity,
                    new SensitiveStringValue(
                        includeSensitiveValues ? value : null,
                        !includeSensitiveValues && value is not null,
                        "command-line-switch-value"),
                    definition.Detail,
                    definition.RequiresExplicitConsent));
            }
        }

        return findings
            .OrderBy(finding => finding.Identity.ProcessId)
            .ThenBy(finding => finding.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MojoPipeInspectionResult CreateEmptyMojoResult(
        DateTimeOffset capturedAt)
    {
        return new MojoPipeInspectionResult(
            capturedAt,
            [],
            new NamedPipeInspectionStatistics(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                TimeSpan.Zero),
            [],
            []);
    }

    private sealed record ArtifactCandidate(
        DiagnosticArtifactKind Kind,
        string Platform,
        string Source,
        string Location,
        bool ExpectDirectory,
        bool IsPotentiallySensitive,
        IReadOnlyList<int> ProcessIds,
        IReadOnlyList<string> Evidence,
        IReadOnlySet<string>? ChildExtensions = null,
        DiagnosticArtifactKind? ChildKind = null,
        bool IsFileSystem = true,
        IReadOnlySet<string>? ChildFileNamePrefixes = null);

    private sealed record SwitchDefinition(
        string Name,
        string Category,
        string Severity,
        string Detail,
        bool RequiresExplicitConsent);

    private static class BrowserDiagnosticEvidenceProvider
    {
        public static void Collect(
            IEnumerable<ProcessSnapshotEntry> processes,
            Dictionary<int, string> platforms,
            ICollection<ArtifactCandidate> candidates)
        {
            foreach (ProcessSnapshotEntry process in processes)
            {
                if (!platforms.TryGetValue(process.ProcessId, out string? platform)
                    || platform is "Electron" or "CEF"
                    || process.ChromiumProcessType is not null
                        && !process.ChromiumProcessType.Equals(
                            "browser",
                            StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ChromiumCommandLine commandLine =
                    ChromiumCommandLine.Parse(process.CommandLine);
                if (!commandLine.HasSwitch("disable-logging")
                    && commandLine.HasSwitch("enable-logging")
                    && !commandLine.HasSwitch("log-file")
                    && !string.IsNullOrWhiteSpace(process.UserDataDirectory))
                {
                    candidates.Add(new ArtifactCandidate(
                        DiagnosticArtifactKind.Log,
                        platform,
                        "default Chromium logging path",
                        Path.Combine(
                            process.UserDataDirectory,
                            "chrome_debug.log"),
                        false,
                        true,
                        [process.ProcessId],
                        ["--enable-logging is present without --log-file."]));
                }

                if (!string.IsNullOrWhiteSpace(process.UserDataDirectory))
                {
                    candidates.Add(new ArtifactCandidate(
                        DiagnosticArtifactKind.CrashDatabase,
                        platform,
                        "default Crashpad path",
                        Path.Combine(process.UserDataDirectory, "Crashpad"),
                        true,
                        true,
                        [process.ProcessId],
                        ["Derived from the observed user-data directory."],
                        DumpExtensions,
                        DiagnosticArtifactKind.CrashDump));
                }
            }
        }
    }

    private static class ElectronDiagnosticEvidenceProvider
    {
        public static void Collect(
            IReadOnlyList<ProcessSnapshotEntry> processes,
            ElectronRuntimeAnalysis analysis,
            ICollection<ArtifactCandidate> candidates)
        {
            Dictionary<int, ProcessSnapshotEntry> snapshots =
                processes.ToDictionary(process => process.ProcessId);
            foreach (ElectronProcessInfo process in analysis.Processes.Where(
                process => process.Role == ElectronProcessRole.Main))
            {
                if (!snapshots.TryGetValue(
                    process.ProcessId,
                    out ProcessSnapshotEntry? snapshot))
                {
                    continue;
                }

                AddDirectory(
                    process.Paths.LogsDirectory,
                    DiagnosticArtifactKind.LogDirectory,
                    "Electron logs path",
                    LogExtensions,
                    DiagnosticArtifactKind.Log);
                AddDirectory(
                    process.Paths.CrashDumpsDirectory,
                    DiagnosticArtifactKind.CrashDatabase,
                    "Electron Crashpad path",
                    DumpExtensions,
                    DiagnosticArtifactKind.CrashDump);

                ChromiumCommandLine commandLine =
                    ChromiumCommandLine.Parse(snapshot.CommandLine);
                string? enableLogging = commandLine.GetSwitchValue("enable-logging");
                if (!commandLine.HasSwitch("disable-logging")
                    && commandLine.HasSwitch("enable-logging")
                    && (enableLogging is null
                        || enableLogging.Equals(
                            "file",
                            StringComparison.OrdinalIgnoreCase))
                    && !commandLine.HasSwitch("log-file")
                    && process.Paths.UserDataDirectory is not null)
                {
                    candidates.Add(new ArtifactCandidate(
                        DiagnosticArtifactKind.Log,
                        "Electron",
                        "default Electron file logging path",
                        Path.Combine(
                            process.Paths.UserDataDirectory.Value,
                            "electron_debug.log"),
                        false,
                        true,
                        [process.ProcessId],
                        ["Electron file logging is enabled without --log-file."]));
                }

                void AddDirectory(
                    ElectronPathObservation? observation,
                    DiagnosticArtifactKind kind,
                    string source,
                    IReadOnlySet<string> extensions,
                    DiagnosticArtifactKind childKind)
                {
                    if (observation is null)
                    {
                        return;
                    }

                    candidates.Add(new ArtifactCandidate(
                        kind,
                        "Electron",
                        source,
                        observation.Value,
                        true,
                        true,
                        [process.ProcessId],
                        [
                            $"Electron path source: {observation.Source}; "
                            + $"confidence: {observation.Confidence}.",
                        ],
                        extensions,
                        childKind));
                }
            }
        }
    }

    private static class CefDiagnosticEvidenceProvider
    {
        public static void Collect(
            IReadOnlyList<ProcessSnapshotEntry> processes,
            CefRuntimeAnalysis analysis,
            ICollection<ArtifactCandidate> candidates)
        {
            Dictionary<int, ProcessSnapshotEntry> snapshots =
                processes.ToDictionary(process => process.ProcessId);
            foreach (CefProcessInfo process in analysis.Processes.Where(
                process => process.Role == CefProcessRole.Browser))
            {
                if (!snapshots.TryGetValue(
                    process.ProcessId,
                    out ProcessSnapshotEntry? snapshot))
                {
                    continue;
                }

                ChromiumCommandLine commandLine =
                    ChromiumCommandLine.Parse(snapshot.CommandLine);
                bool loggingDisabled = commandLine.HasSwitch("disable-logging")
                    || commandLine.GetSwitchValue("log-severity")?.Equals(
                        "disable",
                        StringComparison.OrdinalIgnoreCase) == true;
                if (!loggingDisabled
                    && !commandLine.HasSwitch("log-file")
                    && !string.IsNullOrWhiteSpace(snapshot.ExecutablePath))
                {
                    string? executableDirectory =
                        Path.GetDirectoryName(snapshot.ExecutablePath);
                    if (executableDirectory is not null)
                    {
                        candidates.Add(new ArtifactCandidate(
                            DiagnosticArtifactKind.Log,
                            "CEF",
                            "default CEF debug log",
                            Path.Combine(executableDirectory, "debug.log"),
                            false,
                            true,
                            [process.ProcessId],
                            ["CEF defaults debug.log beside the main executable on Windows."]));
                    }
                }

                AddFile(
                    process.RuntimePaths.CrashReportConfigurationFile,
                    DiagnosticArtifactKind.CrashConfiguration,
                    "CEF crash_reporter.cfg");
                AddDirectory(
                    process.RuntimePaths.CrashReportDirectory,
                    "CEF crash report directory");

                void AddFile(
                    string? path,
                    DiagnosticArtifactKind kind,
                    string source)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        candidates.Add(new ArtifactCandidate(
                            kind,
                            "CEF",
                            source,
                            path,
                            false,
                            true,
                            [process.ProcessId],
                            ["Observed by the CEF runtime adapter."]));
                    }
                }

                void AddDirectory(string? path, string source)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        candidates.Add(new ArtifactCandidate(
                            DiagnosticArtifactKind.CrashDatabase,
                            "CEF",
                            source,
                            path,
                            true,
                            true,
                            [process.ProcessId],
                            ["Observed by the CEF runtime adapter."],
                            DumpExtensions,
                            DiagnosticArtifactKind.CrashDump));
                    }
                }
            }
        }
    }
}

/// <summary>Windows filesystem metadata reader for diagnostic artifacts.</summary>
public sealed class WindowsDiagnosticPathInspector : IDiagnosticPathInspector
{
    /// <inheritdoc />
    public DiagnosticPathMetadata Inspect(
        string path,
        bool expectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (isDirectory)
            {
                DirectoryInfo directory = new(path);
                return new DiagnosticPathMetadata(
                    DiagnosticArtifactStatus.Present,
                    true,
                    null,
                    directory.LastWriteTimeUtc,
                    []);
            }

            FileInfo file = new(path);
            return new DiagnosticPathMetadata(
                DiagnosticArtifactStatus.Present,
                false,
                file.Length,
                file.LastWriteTimeUtc,
                []);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return Missing(expectDirectory);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new DiagnosticPathMetadata(
                DiagnosticArtifactStatus.Inaccessible,
                expectDirectory,
                null,
                null,
                [new DiscoveryIssue("diagnostic-artifact-metadata", exception.Message)]);
        }

        static DiagnosticPathMetadata Missing(bool isDirectory)
        {
            return new DiagnosticPathMetadata(
                DiagnosticArtifactStatus.Missing,
                isDirectory,
                null,
                null,
                []);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(
        string directory,
        IReadOnlySet<string> extensions,
        IReadOnlySet<string>? fileNamePrefixes,
        int maximumFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);

        EnumerationOptions options = new()
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MaxRecursionDepth = 2,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
        };
        List<string> files = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!extensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            string fileName = Path.GetFileName(file);
            if (fileNamePrefixes is not null
                && !fileNamePrefixes.Any(prefix => fileName.StartsWith(
                    $"{prefix}.",
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            files.Add(file);
            if (files.Count >= maximumFiles)
            {
                break;
            }
        }

        return files;
    }
}

/// <summary>Reads Windows Error Reporting LocalDumps registry configuration.</summary>
public sealed class WindowsErrorReportingInspector : IWindowsErrorReportingInspector
{
    private const string LocalDumpsKey =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    /// <inheritdoc />
    public IReadOnlyList<WerLocalDumpConfiguration> Inspect(
        IReadOnlyCollection<string> imageNames,
        ICollection<DiscoveryIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(imageNames);
        ArgumentNullException.ThrowIfNull(issues);

        List<WerLocalDumpConfiguration> configurations = [];
        foreach (RegistryView view in new[]
        {
            RegistryView.Registry64,
            RegistryView.Registry32,
        })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine,
                    view);
                using RegistryKey? localDumps = baseKey.OpenSubKey(LocalDumpsKey);
                if (localDumps is null)
                {
                    continue;
                }

                AddConfiguration(localDumps, $"machine-{view}", null);
                foreach (string imageName in imageNames)
                {
                    using RegistryKey? application =
                        localDumps.OpenSubKey(imageName);
                    if (application is not null)
                    {
                        AddConfiguration(
                            application,
                            $"application-{view}",
                            imageName);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                issues.Add(new DiscoveryIssue(
                    "wer-local-dumps-registry",
                    exception.Message));
            }
        }

        return configurations
            .DistinctBy(configuration =>
                $"{configuration.ImageName}\0{configuration.DumpFolder}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        void AddConfiguration(
            RegistryKey key,
            string scope,
            string? imageName)
        {
            object? folderValue = key.GetValue(
                "DumpFolder",
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            string folder = folderValue as string
                ?? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "CrashDumps");
            folder = Environment.ExpandEnvironmentVariables(folder);
            int? dumpType = key.GetValue("DumpType") switch
            {
                int value => value,
                _ => null,
            };
            configurations.Add(new WerLocalDumpConfiguration(
                scope,
                imageName,
                folder,
                dumpType));
        }
    }
}
