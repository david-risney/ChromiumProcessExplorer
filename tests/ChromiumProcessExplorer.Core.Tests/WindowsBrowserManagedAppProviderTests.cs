using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class WindowsBrowserManagedAppProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ChromiumProcessExplorer.Pwa.{Guid.NewGuid():N}");

    [Fact]
    public void DiscoverFindsProfileWebApplication()
    {
        string appId = "abcdefghijklmnopabcdefghijklmnop";
        string profilePath = Path.Combine(_root, "Default");
        string appPath = Path.Combine(
            profilePath,
            "Web Applications",
            $"_crx_{appId}");
        Directory.CreateDirectory(appPath);
        WindowsBrowserManagedAppProvider provider = new(
            [("edge", "msedge.exe", _root)],
            [],
            includeRegistrations: false);
        List<DiscoveryIssue> issues = [];

        BrowserManagedAppInstallation app = Assert.Single(
            provider.Discover(issues));

        Assert.Empty(issues);
        Assert.Equal(appId, app.AppId);
        Assert.Equal("edge", app.BrowserPlatform);
        Assert.Equal("Default", app.ProfileName);
        Assert.Equal(profilePath, app.ProfilePath);
        Assert.Equal(appPath, app.InstallPath);
        Assert.Contains(
            app.Evidence,
            evidence => evidence.Source
                == "browser-profile-web-application");
    }

    [Fact]
    public void DiscoverKeepsSameAppInstalledInMultipleProfilesSeparate()
    {
        string appId = "abcdefghijklmnopabcdefghijklmnop";
        foreach (string profileName in new[] { "Default", "Profile 1" })
        {
            Directory.CreateDirectory(Path.Combine(
                _root,
                profileName,
                "Web Applications",
                $"_crx_{appId}"));
        }

        WindowsBrowserManagedAppProvider provider = new(
            [("edge", "msedge.exe", _root)],
            [],
            includeRegistrations: false);

        BrowserManagedAppInstallation[] apps =
            provider.Discover([]).ToArray();

        Assert.Equal(2, apps.Length);
        Assert.Equal(
            ["Default", "Profile 1"],
            apps.Select(app => app.ProfileName!).Order().ToArray());
    }

    [Theory]
    [InlineData(
        @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
        "--profile-directory=\"Profile 2\" --app-id=abcdefghijklmnopabcdefghijklmnop",
        "chrome",
        "Profile 2")]
    [InlineData(
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        "--app-id abcdefghijklmnopabcdefghijklmnop --profile-directory=Default",
        "edge",
        "Default")]
    public void TryParseCommandReadsBrowserAppIdentity(
        string target,
        string arguments,
        string expectedPlatform,
        string expectedProfile)
    {
        bool parsed = WindowsBrowserManagedAppProvider.TryParseCommand(
            target,
            arguments,
            out string platform,
            out string appId,
            out string? profileName);

        Assert.True(parsed);
        Assert.Equal(expectedPlatform, platform);
        Assert.Equal("abcdefghijklmnopabcdefghijklmnop", appId);
        Assert.Equal(expectedProfile, profileName);
    }

    [Fact]
    public void TryParseCommandRejectsNonBrowserTargets()
    {
        Assert.False(WindowsBrowserManagedAppProvider.TryParseCommand(
            @"C:\Apps\sample.exe",
            "--app-id=abcdefghijklmnopabcdefghijklmnop",
            out _,
            out _,
            out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
