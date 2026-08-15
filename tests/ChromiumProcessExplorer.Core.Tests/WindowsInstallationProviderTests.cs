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
}
