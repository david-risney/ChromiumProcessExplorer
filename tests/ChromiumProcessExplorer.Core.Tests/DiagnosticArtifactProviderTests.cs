using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class DiagnosticArtifactProviderTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DiscoverPreservesExplicitLogPathAndRedactsIt()
    {
        ProcessSnapshotEntry process = CreateProcess(
            "chrome.exe",
            @"""C:\Chrome\chrome.exe"" --log-file=C:\Logs\chrome.log");
        FakePathInspector pathInspector = new();
        DiagnosticArtifactProvider provider = CreateProvider(pathInspector);

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: false);

        DiagnosticArtifact artifact = Assert.Single(
            result.Artifacts,
            item => item.Source == "--log-file");
        Assert.Equal(DiagnosticArtifactKind.Log, artifact.Kind);
        Assert.True(artifact.Location.IsRedacted);
        Assert.Equal(DiagnosticArtifactStatus.Missing, artifact.Status);
        Assert.True(artifact.IsPotentiallySensitive);
    }

    [Fact]
    public void DiscoverUsesElectronDefaultLogAndCrashPaths()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, "app.exe");
        string resources = Path.Combine(temporary.Path, "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "app.asar"), string.Empty);
        string userData = Path.Combine(temporary.Path, "User Data");
        ProcessSnapshotEntry process = CreateProcess(
            "app.exe",
            $"\"{executable}\" --enable-logging=file --user-data-dir=\"{userData}\"")
            with
        {
            ExecutablePath = executable,
            UserDataDirectory = userData,
        };
        DiagnosticArtifactProvider provider = CreateProvider(new FakePathInspector());

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: true);

        Assert.Contains(
            result.Artifacts,
            item => item.Platform == "Electron"
                && item.Location.Value == Path.Combine(
                    userData,
                    "electron_debug.log"));
        Assert.Contains(
            result.Artifacts,
            item => item.Platform == "Electron"
                && item.Kind == DiagnosticArtifactKind.CrashDatabase
                && item.Location.Value == Path.Combine(userData, "Crashpad"));
    }

    [Fact]
    public void DiscoverDoesNotTreatElectronStderrLoggingAsFileLogging()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, "app.exe");
        string resources = Path.Combine(temporary.Path, "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "app.asar"), string.Empty);
        string userData = Path.Combine(temporary.Path, "User Data");
        ProcessSnapshotEntry process = CreateProcess(
            "app.exe",
            $"\"{executable}\" --enable-logging --user-data-dir=\"{userData}\"")
            with
        {
            ExecutablePath = executable,
            UserDataDirectory = userData,
        };
        DiagnosticArtifactProvider provider = CreateProvider(new FakePathInspector());

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: true);

        Assert.DoesNotContain(
            result.Artifacts,
            item => item.Source == "default Electron file logging path");
    }

    [Fact]
    public void DiscoverUsesCefDefaultDebugLog()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, "cefclient.exe");
        ProcessSnapshotEntry process = CreateProcess(
            "cefclient.exe",
            $"\"{executable}\"") with
        {
            ExecutablePath = executable,
        };
        DiagnosticArtifactProvider provider = CreateProvider(new FakePathInspector());

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: true);

        Assert.Contains(
            result.Artifacts,
            item => item.Platform == "CEF"
                && item.Source == "default CEF debug log"
                && item.Location.Value == Path.Combine(
                    temporary.Path,
                    "debug.log"));
        Assert.Contains(
            result.Artifacts,
            item => item.Platform == "CEF"
                && item.Source == "default CEF crash configuration"
                && item.Status == DiagnosticArtifactStatus.Missing
                && item.Location.Value == Path.Combine(
                    temporary.Path,
                    "crash_reporter.cfg"));
    }

    [Fact]
    public void DiscoverDoesNotInferDefaultLogWhenLoggingIsDisabled()
    {
        string userData = @"C:\Profiles\Chrome";
        ProcessSnapshotEntry process = CreateProcess(
            "chrome.exe",
            @"""C:\Chrome\chrome.exe"" --enable-logging --disable-logging "
            + $@"--user-data-dir=""{userData}""") with
        {
            UserDataDirectory = userData,
            ChromiumProcessType = null,
        };
        DiagnosticArtifactProvider provider = CreateProvider(new FakePathInspector());

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: true);

        Assert.DoesNotContain(
            result.Artifacts,
            item => item.Source == "default Chromium logging path");
        Assert.Contains(
            result.Configuration,
            item => item.Name == "--disable-logging");
    }

    [Fact]
    public void DiscoverUsesDefaultLogForInferredBrowserRole()
    {
        string userData = @"C:\Profiles\Chrome";
        ProcessSnapshotEntry process = CreateProcess(
            "chrome.exe",
            @"""C:\Chrome\chrome.exe"" --enable-logging") with
        {
            UserDataDirectory = userData,
            ChromiumProcessType = "browser",
        };
        DiagnosticArtifactProvider provider = CreateProvider(new FakePathInspector());

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: true);

        Assert.Contains(
            result.Artifacts,
            item => item.Source == "default Chromium logging path"
                && item.Location.Value == Path.Combine(
                    userData,
                    "chrome_debug.log"));
    }

    [Fact]
    public void DiscoverSurfacesAccessDeniedAsPartialResult()
    {
        ProcessSnapshotEntry process = CreateProcess(
            "chrome.exe",
            @"""C:\Chrome\chrome.exe"" --log-file=C:\Denied\chrome.log");
        FakePathInspector pathInspector = new()
        {
            Result = new DiagnosticPathMetadata(
                DiagnosticArtifactStatus.Inaccessible,
                false,
                null,
                null,
                [new DiscoveryIssue("diagnostic-artifact-metadata", "Access denied.")]),
        };
        DiagnosticArtifactProvider provider = CreateProvider(pathInspector);

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: true);

        Assert.Contains(
            result.Artifacts,
            item => item.Source == "--log-file"
                && item.Status == DiagnosticArtifactStatus.Inaccessible);
        Assert.Contains(
            result.Issues,
            issue => issue.Message == "Access denied.");
    }

    [Fact]
    public void DiscoverFlagsRiskySwitchesAndSensitiveCaptures()
    {
        ProcessSnapshotEntry process = CreateProcess(
            "chrome.exe",
            @"""C:\Chrome\chrome.exe"" --no-sandbox "
            + @"--log-net-log=C:\Logs\net.json");
        DiagnosticArtifactProvider provider = CreateProvider(new FakePathInspector());

        DiagnosticArtifactDiscoveryResult result = provider.Discover(
            [process],
            includeSensitiveValues: false);

        Assert.Contains(
            result.Configuration,
            item => item.Name == "--no-sandbox"
                && item.Severity == "critical");
        Assert.Contains(
            result.Configuration,
            item => item.Name == "--log-net-log"
                && item.RequiresExplicitConsent
                && item.Value.IsRedacted);
    }

    [Fact]
    public void WindowsPathInspectorFiltersWerFilesByImageName()
    {
        using TemporaryDirectory temporary = new();
        File.WriteAllText(
            Path.Combine(temporary.Path, "chrome.exe.100.dmp"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(temporary.Path, "unrelated.exe.200.dmp"),
            string.Empty);
        WindowsDiagnosticPathInspector inspector = new();

        IReadOnlyList<string> files = inspector.EnumerateFiles(
            temporary.Path,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dmp" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe" },
            10);

        Assert.Single(files);
        Assert.EndsWith("chrome.exe.100.dmp", files[0], StringComparison.Ordinal);
    }

    private static DiagnosticArtifactProvider CreateProvider(
        IDiagnosticPathInspector pathInspector)
    {
        return new DiagnosticArtifactProvider(
            pathInspector,
            new EmptyWerInspector());
    }

    private static ProcessSnapshotEntry CreateProcess(
        string imageName,
        string commandLine)
    {
        return new ProcessSnapshotEntry(
            100,
            1,
            SnapshotTime,
            imageName,
            Path.Combine(@"C:\Apps", imageName),
            commandLine,
            null,
            null,
            true,
            [],
            null);
    }

    private sealed class FakePathInspector : IDiagnosticPathInspector
    {
        public DiagnosticPathMetadata Result { get; init; } = new(
            DiagnosticArtifactStatus.Missing,
            false,
            null,
            null,
            []);

        public DiagnosticPathMetadata Inspect(
            string path,
            bool expectDirectory,
            CancellationToken cancellationToken = default)
        {
            return Result with { IsDirectory = expectDirectory };
        }

        public IReadOnlyList<string> EnumerateFiles(
            string directory,
            IReadOnlySet<string> extensions,
            IReadOnlySet<string>? fileNamePrefixes,
            int maximumFiles,
            CancellationToken cancellationToken = default)
        {
            return [];
        }
    }

    private sealed class EmptyWerInspector : IWindowsErrorReportingInspector
    {
        public IReadOnlyList<WerLocalDumpConfiguration> Inspect(
            IReadOnlyCollection<string> imageNames,
            ICollection<DiscoveryIssue> issues)
        {
            return [];
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cpe-diagnostics-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
