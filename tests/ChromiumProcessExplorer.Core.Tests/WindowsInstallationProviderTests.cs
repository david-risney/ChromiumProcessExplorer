using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class WindowsInstallationProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ChromiumProcessExplorer.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task DiscoverClassifiesElectronApplicationFromAppAsar()
    {
        string applicationPath = Path.Combine(_root, "ElectronApp");
        string resourcesPath = Path.Combine(applicationPath, "resources");
        Directory.CreateDirectory(resourcesPath);
        File.WriteAllText(Path.Combine(resourcesPath, "app.asar"), string.Empty);
        File.WriteAllText(Path.Combine(applicationPath, "sample.exe"), string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        ChromiumInstallation installation = Assert.Single(result.Installations);
        Assert.Equal("Application", installation.Kind);
        Assert.Equal("Electron", installation.Platform);
        Assert.Equal("Portable", installation.Metadata.InstallType);
        Assert.False(installation.Metadata.IsSharedRuntime);
        Assert.Equal(applicationPath, installation.InstallPath);
        Assert.Contains(
            installation.Evidence,
            evidence => evidence.Source == "filesystem-marker"
                && evidence.Path?.EndsWith("app.asar", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DiscoverClassifiesCefAndWebView2Markers()
    {
        string cefPath = Path.Combine(_root, "CefApp");
        string webViewPath = Path.Combine(_root, "WebViewApp");
        Directory.CreateDirectory(cefPath);
        Directory.CreateDirectory(webViewPath);
        File.WriteAllText(Path.Combine(cefPath, "libcef.dll"), string.Empty);
        File.WriteAllText(Path.Combine(webViewPath, "WebView2Loader.dll"), string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Contains(
            result.Installations,
            installation => installation.InstallPath == cefPath
                && installation.Platform == "CEF");
        Assert.Contains(
            result.Installations,
            installation => installation.InstallPath == webViewPath
                && installation.Platform == "WebView2");
        Assert.Equal(2, result.Statistics.MarkerFileCount);
    }

    [Fact]
    public async Task DiscoverDoesNotUseCreatedumpAsApplicationExecutable()
    {
        string applicationPath = Path.Combine(_root, "Tempo");
        Directory.CreateDirectory(applicationPath);
        File.WriteAllText(
            Path.Combine(applicationPath, "WebView2Loader.dll"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(applicationPath, "createdump.exe"),
            string.Empty);
        string executablePath = Path.Combine(applicationPath, "Tempo.exe");
        File.WriteAllText(executablePath, string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal(executablePath, installation.ExecutablePath);
        Assert.NotEqual("Microsoft® .NET", installation.Name);
    }

    [Fact]
    public async Task DiscoverFindsCefApplicationAboveBrowserSubprocess()
    {
        string applicationPath = Path.Combine(_root, "Razer Central");
        string frameworkPath = Path.Combine(
            applicationPath,
            "Framework",
            "Host");
        Directory.CreateDirectory(frameworkPath);
        File.WriteAllText(
            Path.Combine(frameworkPath, "libcef.dll"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(frameworkPath, "CefSharp.BrowserSubprocess.exe"),
            string.Empty);
        string executablePath = Path.Combine(
            applicationPath,
            "Razer Central.exe");
        File.WriteAllText(executablePath, string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal("CEF", installation.Platform);
        Assert.Equal(applicationPath, installation.InstallPath);
        Assert.Equal(executablePath, installation.ExecutablePath);
        Assert.NotEqual("CefSharp", installation.Name);
    }

    [Fact]
    public void PackageExecutableUsesManifestInsteadOfCreatedump()
    {
        Directory.CreateDirectory(_root);
        string executablePath = Path.Combine(_root, "ActualApp.exe");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(
            Path.Combine(_root, "createdump.exe"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(_root, "AppxManifest.xml"),
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="App" Executable="ActualApp.exe" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """);

        Assert.Equal(
            executablePath,
            InstallationExecutableSelector.FindPackageExecutable(
                _root,
                maximumDepth: 3));
    }

    [Fact]
    public async Task DiscoverUsesNormalizedQtAndNwJsPlatformIds()
    {
        string qtPath = Path.Combine(_root, "QtApp");
        string nwPath = Path.Combine(_root, "NwApp");
        Directory.CreateDirectory(qtPath);
        Directory.CreateDirectory(nwPath);
        File.WriteAllText(Path.Combine(qtPath, "sample.exe"), string.Empty);
        File.WriteAllText(
            Path.Combine(qtPath, "Qt6WebEngineCore.dll"),
            string.Empty);
        File.WriteAllText(Path.Combine(nwPath, "sample.exe"), string.Empty);
        File.WriteAllText(Path.Combine(nwPath, "nw.dll"), string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Contains(
            result.Installations,
            installation => installation.InstallPath == qtPath
                && installation.Platform == RuntimePlatformIds.QtWebEngine);
        Assert.Contains(
            result.Installations,
            installation => installation.InstallPath == nwPath
                && installation.Platform == RuntimePlatformIds.Nwjs);
    }

    [Fact]
    public async Task DiscoverClassifiesChromiumSourceOutputAsBrowser()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".gn"), string.Empty);
        string outputPath = Path.Combine(_root, "out", "Debug");
        Directory.CreateDirectory(outputPath);
        string executablePath = Path.Combine(outputPath, "chrome.exe");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(
            Path.Combine(outputPath, "chrome.dll"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(outputPath, "chrome_elf.dll"),
            string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal("Chromium Source Build (Debug)", installation.Name);
        Assert.Equal("Browser", installation.Kind);
        Assert.Equal("Chromium", installation.Platform);
        Assert.Equal("Source", installation.Channel);
        Assert.Equal("Source", installation.Metadata.InstallType);
        Assert.Equal(executablePath, installation.ExecutablePath);
        Assert.Contains(
            installation.Evidence,
            evidence => evidence.Source == "chromium-source-build");
    }

    [Fact]
    public async Task DiscoverAddsRunningProcessInstallationOutsideSearchRoots()
    {
        Directory.CreateDirectory(_root);
        string executablePath = Path.Combine(_root, "custom-browser.exe");
        File.WriteAllText(executablePath, string.Empty);
        ProcessSnapshotEntry process = new(
            123,
            1,
            DateTimeOffset.UtcNow,
            "custom-browser.exe",
            executablePath,
            "\"custom-browser.exe\" --type=renderer",
            "renderer",
            null,
            true,
            ["--type command-line switch"],
            null);
        WindowsInstallationProvider provider = new(
            new WindowsInstallationDiscoveryOptions(
                [],
                includeKnownLocations: false));

        InstallationDiscoveryResult result = await provider.DiscoverAsync([process]);

        ChromiumInstallation installation = Assert.Single(result.Installations);
        Assert.Equal(_root, installation.InstallPath);
        Assert.Equal("Application", installation.Kind);
        Assert.Contains(
            installation.Evidence,
            evidence => evidence.Source == "running-process"
                && evidence.ProcessId == 123);
        Assert.Equal(1, result.Statistics.RunningProcessCount);
    }

    [Fact]
    public async Task DiscoverAppendsAdditionalSearchRoots()
    {
        string applicationPath = Path.Combine(_root, "AdditionalCefApp");
        Directory.CreateDirectory(applicationPath);
        File.WriteAllText(
            Path.Combine(applicationPath, "libcef.dll"),
            string.Empty);
        WindowsInstallationProvider provider = new(
            new WindowsInstallationDiscoveryOptions(
                [],
                includeKnownLocations: false)
            {
                AdditionalSearchRoots = [_root],
            });

        InstallationDiscoveryResult result =
            await provider.DiscoverAsync([]);

        Assert.Contains(
            result.Installations,
            installation => installation.InstallPath == applicationPath
                && installation.Platform == "CEF");
        Assert.Equal(1, result.Statistics.SearchRootCount);
    }

    [Fact]
    public async Task DiscoverCombinesMarkerAndRunningProcessEvidence()
    {
        Directory.CreateDirectory(_root);
        string executablePath = Path.Combine(_root, "electron.exe");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(Path.Combine(_root, "app.asar"), string.Empty);
        ProcessSnapshotEntry process = new(
            456,
            1,
            DateTimeOffset.UtcNow,
            "electron.exe",
            executablePath,
            "\"electron.exe\"",
            "browser",
            null,
            true,
            ["known executable: electron.exe"],
            null);
        WindowsInstallationProvider provider = CreateProvider();

        InstallationDiscoveryResult result = await provider.DiscoverAsync([process]);

        ChromiumInstallation installation = Assert.Single(result.Installations);
        Assert.Equal("Electron", installation.Platform);
        Assert.Contains(
            installation.Evidence,
            evidence => evidence.Source == "filesystem-marker");
        Assert.Contains(
            installation.Evidence,
            evidence => evidence.Source == "running-process");
    }

    [Fact]
    public async Task DiscoverClassifiesRunningRenamedElectronExecutable()
    {
        string applicationPath = Path.Combine(_root, "RenamedElectron");
        string resourcesPath = Path.Combine(applicationPath, "resources");
        Directory.CreateDirectory(resourcesPath);
        File.WriteAllText(Path.Combine(resourcesPath, "app.asar"), string.Empty);
        string executablePath = Path.Combine(applicationPath, "sample.exe");
        File.WriteAllText(executablePath, string.Empty);
        ProcessSnapshotEntry process = new(
            789,
            1,
            DateTimeOffset.UtcNow,
            "sample.exe",
            executablePath,
            $"\"{executablePath}\" --type=renderer",
            "renderer",
            null,
            true,
            ["--type command-line switch"],
            null);
        WindowsInstallationProvider provider = new(
            new WindowsInstallationDiscoveryOptions(
                [],
                includeKnownLocations: false));

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([process])).Installations);

        Assert.Equal("Electron", installation.Platform);
        Assert.Contains(
            installation.Evidence,
            evidence => evidence.Source == "electron-packaged-layout"
                && evidence.Path?.EndsWith(
                    Path.Combine("resources", "app.asar"),
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task DiscoverMergesMsiRegistryAndFilesystemEvidence()
    {
        string applicationPath = Path.Combine(_root, "Chrome");
        Directory.CreateDirectory(applicationPath);
        string executablePath = Path.Combine(applicationPath, "chrome.exe");
        WritePortableExecutable(executablePath, 0x8664);
        File.WriteAllText(Path.Combine(applicationPath, "chrome_elf.dll"), string.Empty);
        InstalledProgramRecord record = new(
            "Google Chrome Beta",
            "151.0.1",
            "Google LLC",
            applicationPath,
            executablePath,
            @"C:\InstallerCache",
            "msiexec.exe /x {PRODUCT}",
            true,
            "Machine",
            "Registry64",
            @"HKLM\Uninstall\Chrome");
        WindowsInstallationProvider provider = CreateProvider(
            [record],
            []);

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal("Chrome", installation.Platform);
        Assert.Equal("MSI", installation.Metadata.InstallType);
        Assert.Equal("x64", installation.Metadata.Architecture);
        Assert.Equal("Google LLC", installation.Metadata.Publisher);
        Assert.Equal("Beta", installation.Channel);
        Assert.Equal("151.0.1", installation.Version);
        Assert.Equal("Uninstall registry", installation.Metadata.VersionProvenance);
        Assert.Contains(
            installation.Evidence,
            item => item.Source == "uninstall-registry");
        Assert.Contains(
            installation.Evidence,
            item => item.Source == "filesystem-marker");
    }

    [Fact]
    public async Task DiscoverClassifiesSquirrelAndNsisApplications()
    {
        string electronPath = Path.Combine(_root, "Slack");
        string cefPath = Path.Combine(_root, "CefApp");
        Directory.CreateDirectory(Path.Combine(electronPath, "resources"));
        Directory.CreateDirectory(cefPath);
        File.WriteAllText(
            Path.Combine(electronPath, "resources", "app.asar"),
            string.Empty);
        string electronExe = Path.Combine(electronPath, "Slack.exe");
        string cefExe = Path.Combine(cefPath, "CefApp.exe");
        File.WriteAllText(electronExe, string.Empty);
        File.WriteAllText(cefExe, string.Empty);
        File.WriteAllText(Path.Combine(cefPath, "libcef.dll"), string.Empty);
        InstalledProgramRecord[] records =
        [
            new(
                "Slack",
                "4.0",
                "Slack Technologies",
                electronPath,
                electronExe,
                null,
                $"\"{Path.Combine(electronPath, "Update.exe")}\" --uninstall",
                false,
                "User",
                "Registry64",
                @"HKCU\Uninstall\Slack"),
            new(
                "CEF Sample",
                "1.0",
                "Contoso",
                cefPath,
                cefExe,
                null,
                $"\"{Path.Combine(cefPath, "unins000.exe")}\"",
                false,
                "Machine",
                "Registry64",
                @"HKLM\Uninstall\CefSample"),
        ];
        WindowsInstallationProvider provider = CreateProvider(records, []);

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Contains(
            result.Installations,
            item => item.Platform == "Electron"
                && item.Metadata.InstallType == "Squirrel"
                && item.Channel is null);
        Assert.Contains(
            result.Installations,
            item => item.Platform == "CEF"
                && item.Metadata.InstallType == "NSIS");
    }

    [Fact]
    public async Task DiscoverDoesNotTreatChromeRemoteDesktopAsBrowser()
    {
        string applicationPath = Path.Combine(_root, "RemoteDesktop");
        Directory.CreateDirectory(applicationPath);
        string executablePath = Path.Combine(applicationPath, "remoting_host.exe");
        File.WriteAllText(executablePath, string.Empty);
        InstalledProgramRecord record = new(
            "Google Chrome Remote Desktop Host",
            "1.0",
            "Google LLC",
            applicationPath,
            executablePath,
            null,
            "uninstall.exe",
            false,
            "Machine",
            "Registry64",
            @"HKLM\Uninstall\ChromeRemoteDesktop");
        WindowsInstallationProvider provider = CreateProvider([record], []);

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Empty(result.Installations);
    }

    [Fact]
    public async Task DiscoverAddsMsixPackageIdentity()
    {
        string packagePath = Path.Combine(
            _root,
            "Contoso.App_1.2.3.4_x64__publisher");
        Directory.CreateDirectory(packagePath);
        InstallationPackageIdentity identity = new(
            Path.GetFileName(packagePath),
            "Contoso.App_publisher",
            "Contoso.App",
            "1.2.3.4",
            "x64",
            "publisher");
        WindowsPackageInstallation package = new(
            "Contoso Electron App",
            "Electron",
            packagePath,
            null,
            identity,
            "Contoso",
            Path.Combine(packagePath, "resources"),
            null,
            false,
            [new InstallationEvidence("windows-package", "Test package.", packagePath)]);
        WindowsInstallationProvider provider = CreateProvider([], [package]);

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal("MSIX/AppX", installation.Metadata.InstallType);
        Assert.Equal(identity, installation.Metadata.PackageIdentity);
        Assert.Equal("x64", installation.Metadata.Architecture);
        Assert.Equal("Package identity", installation.Metadata.VersionProvenance);
        Assert.False(installation.Metadata.IsSharedRuntime);
    }

    [Fact]
    public async Task DiscoverNormalizesDuplicateNestedRuntimeMarkers()
    {
        string applicationPath = Path.Combine(_root, "NestedApp");
        string firstNative = Path.Combine(
            applicationPath,
            "runtimes",
            "win-x64",
            "native");
        string secondNative = Path.Combine(
            applicationPath,
            "runtimes",
            "win-x86",
            "native");
        Directory.CreateDirectory(firstNative);
        Directory.CreateDirectory(secondNative);
        File.WriteAllText(Path.Combine(applicationPath, "app.exe"), string.Empty);
        File.WriteAllText(Path.Combine(firstNative, "WebView2Loader.dll"), string.Empty);
        File.WriteAllText(Path.Combine(secondNative, "WebView2Loader.dll"), string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal(applicationPath, installation.InstallPath);
        Assert.Equal("WebView2", installation.Platform);
        Assert.Equal(
            2,
            installation.Evidence.Count(item => item.Source == "filesystem-marker"));
    }

    [Fact]
    public async Task DiscoverIgnoresSdkMarkerWithoutApplicationExecutable()
    {
        string nativePath = Path.Combine(_root, "sdk", "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativePath);
        File.WriteAllText(Path.Combine(nativePath, "libcef.dll"), string.Empty);
        WindowsInstallationProvider provider = CreateProvider();

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Empty(result.Installations);
        Assert.Equal(1, result.Statistics.MarkerFileCount);
    }

    [Fact]
    public async Task DiscoverAddsBrowserManagedAppsAsSharedRuntimeInstallations()
    {
        string browserPath = Path.Combine(_root, "Browser", "msedge.exe");
        string appPath = Path.Combine(
            _root,
            "Profile",
            "Web Applications",
            "_crx_abcdefghijklmnopabcdefghijklmnop");
        Directory.CreateDirectory(Path.GetDirectoryName(browserPath)!);
        Directory.CreateDirectory(appPath);
        File.WriteAllText(browserPath, string.Empty);
        BrowserManagedAppInstallation app = new(
            "abcdefghijklmnopabcdefghijklmnop",
            "Sample PWA",
            "edge",
            browserPath,
            "Default",
            Path.GetDirectoryName(Path.GetDirectoryName(appPath)),
            appPath,
            [
                new InstallationEvidence(
                    "browser-profile-web-application",
                    "Found app in browser profile.",
                    appPath),
            ]);
        WindowsInstallationProvider provider = new(
            new WindowsInstallationDiscoveryOptions(
                [],
                includeKnownLocations: false)
            {
                IncludeBrowserManagedApps = true,
            },
            new StubInstalledProgramProvider([]),
            new StubPackageProvider([]),
            new StubBrowserManagedAppProvider([app]));

        ChromiumInstallation installation = Assert.Single(
            (await provider.DiscoverAsync([])).Installations);

        Assert.Equal("BrowserApp", installation.Kind);
        Assert.Equal(RuntimePlatformIds.BrowserPwa, installation.Platform);
        Assert.Equal(browserPath, installation.ExecutablePath);
        Assert.True(installation.Metadata.IsSharedRuntime);
        Assert.Equal("edge", installation.Metadata.BrowserPlatform);
        Assert.Equal(
            "abcdefghijklmnopabcdefghijklmnop",
            installation.Metadata.ApplicationId);
        Assert.Equal("Default", installation.Metadata.BrowserProfileName);
    }

    [Fact]
    public async Task DiscoverHonorsMaximumDirectoryCount()
    {
        Directory.CreateDirectory(Path.Combine(_root, "one", "two", "three"));
        WindowsInstallationDiscoveryOptions options = new(
            [_root],
            includeKnownLocations: false)
        {
            MaximumDirectories = 2,
        };
        WindowsInstallationProvider provider = new(options);

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Equal(2, result.Statistics.DirectoryCount);
        Assert.True(result.Statistics.TruncatedDirectoryCount > 0);
    }

    [Fact]
    public async Task ParallelSearchRootsHaveIndependentDirectoryLimits()
    {
        string[] roots = Enumerable.Range(0, 3)
            .Select(index => Path.Combine(_root, $"root-{index}"))
            .ToArray();
        foreach (string root in roots)
        {
            Directory.CreateDirectory(Path.Combine(root, "one", "two"));
        }

        WindowsInstallationProvider provider = new(
            new WindowsInstallationDiscoveryOptions(
                roots,
                includeKnownLocations: false)
            {
                MaximumConcurrency = 3,
                MaximumDirectories = 2,
            });

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Equal(6, result.Statistics.DirectoryCount);
        Assert.True(result.Statistics.TruncatedDirectoryCount > 0);
    }

    [Fact]
    public async Task PriorityPathsAreScannedBeforeUnrelatedDirectories()
    {
        string unrelated = Path.Combine(_root, "aaa-unrelated", "nested");
        string priority = Path.Combine(_root, "zzz-priority", "application");
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(priority);
        File.WriteAllText(
            Path.Combine(priority, "libcef.dll"),
            string.Empty);
        WindowsInstallationProvider provider = new(
            new WindowsInstallationDiscoveryOptions(
                [_root],
                includeKnownLocations: false)
            {
                MaximumDirectories = 3,
                PrioritySearchPaths = [priority],
            });

        InstallationDiscoveryResult result = await provider.DiscoverAsync([]);

        Assert.Contains(
            result.Installations,
            installation => installation.InstallPath == priority
                && installation.Platform == "CEF");
        Assert.Equal(3, result.Statistics.DirectoryCount);
        Assert.True(result.Statistics.TruncatedDirectoryCount > 0);
    }

    [Fact]
    public void InstallationConcurrencyDefaultsToProcessorCount()
    {
        Assert.Equal(
            Math.Max(1, Environment.ProcessorCount),
            new WindowsInstallationDiscoveryOptions().MaximumConcurrency);
    }

    [Fact]
    public async Task IndependentMetadataSourcesRunConcurrently()
    {
        using Barrier barrier = new(3);
        WindowsInstallationDiscoveryOptions options = new(
            [_root],
            includeKnownLocations: false)
        {
            IncludeRegistry = true,
            IncludePackages = true,
            IncludeBrowserManagedApps = true,
        };
        WindowsInstallationProvider provider = new(
            options,
            new CoordinatedInstalledProgramProvider(barrier),
            new CoordinatedPackageProvider(barrier),
            new CoordinatedBrowserManagedAppProvider(barrier));

        InstallationDiscoveryResult result = await provider
            .DiscoverAsync([])
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(result.Issues);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WindowsInstallationProvider CreateProvider()
    {
        return new WindowsInstallationProvider(
            new WindowsInstallationDiscoveryOptions(
                [_root],
                includeKnownLocations: false));
    }

    private WindowsInstallationProvider CreateProvider(
        IReadOnlyList<InstalledProgramRecord> records,
        IReadOnlyList<WindowsPackageInstallation> packages)
    {
        WindowsInstallationDiscoveryOptions options = new(
            [_root],
            includeKnownLocations: false)
        {
            IncludeRegistry = true,
            IncludePackages = true,
        };
        return new WindowsInstallationProvider(
            options,
            new StubInstalledProgramProvider(records),
            new StubPackageProvider(packages));
    }

    private static void WritePortableExecutable(string path, ushort machine)
    {
        byte[] bytes = new byte[256];
        BitConverter.GetBytes((ushort)0x5A4D).CopyTo(bytes, 0);
        BitConverter.GetBytes(128).CopyTo(bytes, 0x3C);
        BitConverter.GetBytes(0x00004550u).CopyTo(bytes, 128);
        BitConverter.GetBytes(machine).CopyTo(bytes, 132);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class StubInstalledProgramProvider(
        IReadOnlyList<InstalledProgramRecord> records)
        : IInstalledProgramProvider
    {
        public IReadOnlyList<InstalledProgramRecord> Discover(
            ICollection<DiscoveryIssue> issues,
            CancellationToken cancellationToken = default)
        {
            return records;
        }
    }

    private sealed class StubPackageProvider(
        IReadOnlyList<WindowsPackageInstallation> packages)
        : IWindowsPackageInstallationProvider
    {
        public IReadOnlyList<WindowsPackageInstallation> Discover(
            IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
            ICollection<DiscoveryIssue> issues,
            CancellationToken cancellationToken = default)
        {
            return packages;
        }
    }

    private sealed class StubBrowserManagedAppProvider(
        IReadOnlyList<BrowserManagedAppInstallation> apps)
        : IBrowserManagedAppProvider
    {
        public IReadOnlyList<BrowserManagedAppInstallation> Discover(
            ICollection<DiscoveryIssue> issues,
            CancellationToken cancellationToken = default)
        {
            return apps;
        }
    }

    private sealed class CoordinatedInstalledProgramProvider(Barrier barrier)
        : IInstalledProgramProvider
    {
        public IReadOnlyList<InstalledProgramRecord> Discover(
            ICollection<DiscoveryIssue> issues,
            CancellationToken cancellationToken = default)
        {
            Signal(barrier, cancellationToken);
            return [];
        }
    }

    private sealed class CoordinatedPackageProvider(Barrier barrier)
        : IWindowsPackageInstallationProvider
    {
        public IReadOnlyList<WindowsPackageInstallation> Discover(
            IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
            ICollection<DiscoveryIssue> issues,
            CancellationToken cancellationToken = default)
        {
            Signal(barrier, cancellationToken);
            return [];
        }
    }

    private sealed class CoordinatedBrowserManagedAppProvider(Barrier barrier)
        : IBrowserManagedAppProvider
    {
        public IReadOnlyList<BrowserManagedAppInstallation> Discover(
            ICollection<DiscoveryIssue> issues,
            CancellationToken cancellationToken = default)
        {
            Signal(barrier, cancellationToken);
            return [];
        }
    }

    private static void Signal(
        Barrier barrier,
        CancellationToken cancellationToken)
    {
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(3), cancellationToken))
        {
            throw new TimeoutException(
                "Installation metadata providers did not run concurrently.");
        }
    }
}
