using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Discovers browser-managed apps from profiles, shortcuts, and registrations.</summary>
public sealed partial class WindowsBrowserManagedAppProvider :
    IBrowserManagedAppProvider
{
    private const int MaximumShortcutCount = 20_000;
    private const int MaximumRegistryKeyCount = 50_000;

    private static readonly BrowserProfileSpec[] DefaultBrowserProfiles =
    [
        new(
            "chrome",
            "chrome.exe",
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Google",
                "Chrome",
                "User Data")),
        new(
            "edge",
            "msedge.exe",
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Edge",
                "User Data")),
        new(
            "brave",
            "brave.exe",
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "BraveSoftware",
                "Brave-Browser",
                "User Data")),
        new(
            "chromium",
            "chromium.exe",
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Chromium",
                "User Data")),
    ];

    private static readonly string[] DefaultShortcutRoots =
    [
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs"),
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    ];

    private readonly IReadOnlyList<BrowserProfileSpec> _browserProfiles;
    private readonly IReadOnlyList<string> _shortcutRoots;
    private readonly bool _includeRegistrations;

    /// <summary>Creates a provider using current-user browser and shell locations.</summary>
    public WindowsBrowserManagedAppProvider()
        : this(
            DefaultBrowserProfiles.Select(profile => (
                profile.Platform,
                profile.ExecutableName,
                profile.UserDataRoot)).ToArray(),
            DefaultShortcutRoots,
            includeRegistrations: true)
    {
    }

    internal WindowsBrowserManagedAppProvider(
        IReadOnlyList<(
            string Platform,
            string ExecutableName,
            string UserDataRoot)> browserProfiles,
        IReadOnlyList<string> shortcutRoots,
        bool includeRegistrations)
    {
        ArgumentNullException.ThrowIfNull(browserProfiles);
        ArgumentNullException.ThrowIfNull(shortcutRoots);
        _browserProfiles = browserProfiles
            .Select(profile => new BrowserProfileSpec(
                profile.Platform,
                profile.ExecutableName,
                profile.UserDataRoot))
            .ToArray();
        _shortcutRoots = shortcutRoots;
        _includeRegistrations = includeRegistrations;
    }

    /// <inheritdoc />
    public IReadOnlyList<BrowserManagedAppInstallation> Discover(
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Dictionary<string, AppBuilder> apps =
            new(StringComparer.OrdinalIgnoreCase);
        DiscoverProfiles(apps, issues, cancellationToken);
        DiscoverShortcuts(apps, issues, cancellationToken);
        if (_includeRegistrations)
        {
            DiscoverRegistrations(apps, issues, cancellationToken);
        }

        return apps.Values
            .Select(builder => builder.Build())
            .OrderBy(app => app.BrowserPlatform, StringComparer.Ordinal)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.AppId, StringComparer.Ordinal)
            .ToArray();
    }

    private void DiscoverProfiles(
        IDictionary<string, AppBuilder> apps,
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (BrowserProfileSpec browser in _browserProfiles)
        {
            if (!Directory.Exists(browser.UserDataRoot))
            {
                continue;
            }

            try
            {
                foreach (string profilePath in Directory.EnumerateDirectories(
                    browser.UserDataRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string profileName = Path.GetFileName(profilePath);
                    if (profileName != "Default"
                        && !profileName.StartsWith(
                            "Profile ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string webApplications = Path.Combine(
                        profilePath,
                        "Web Applications");
                    if (!Directory.Exists(webApplications))
                    {
                        continue;
                    }

                    foreach (string appPath in Directory.EnumerateDirectories(
                        webApplications,
                        "_crx_*",
                        SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string directoryName = Path.GetFileName(appPath);
                        string appId = directoryName["_crx_".Length..];
                        if (!IsValidAppId(appId))
                        {
                            continue;
                        }

                        AppBuilder builder = GetOrCreate(
                            apps,
                            browser.Platform,
                            appId,
                            profileName);
                        builder.SetBrowser(
                            FindBrowserExecutable(browser),
                            profileName,
                            profilePath,
                            appPath);
                        builder.AddEvidence(new InstallationEvidence(
                            "browser-profile-web-application",
                            $"Found app {appId} in browser profile {profileName}.",
                            appPath));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                issues.Add(new DiscoveryIssue(
                    "browser-pwa-profile",
                    $"{browser.UserDataRoot}: {exception.Message}"));
            }
        }
    }

    private void DiscoverShortcuts(
        IDictionary<string, AppBuilder> apps,
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        int inspected = 0;
        foreach (string root in _shortcutRoots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string shortcutPath in Directory.EnumerateFiles(
                    root,
                    "*.lnk",
                    SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++inspected > MaximumShortcutCount)
                    {
                        issues.Add(new DiscoveryIssue(
                            "browser-pwa-shortcut",
                            $"Shortcut scan stopped after {MaximumShortcutCount} files."));
                        return;
                    }

                    if (!TryReadShortcut(
                        shortcutPath,
                        out string target,
                        out string arguments))
                    {
                        continue;
                    }

                    AddCommandEvidence(
                        apps,
                        target,
                        arguments,
                        Path.GetFileNameWithoutExtension(shortcutPath),
                        new InstallationEvidence(
                            "browser-app-shortcut",
                            "Found a browser app shortcut.",
                            shortcutPath));
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or COMException)
            {
                issues.Add(new DiscoveryIssue(
                    "browser-pwa-shortcut",
                    $"{root}: {exception.Message}"));
            }
        }
    }

    private static void DiscoverRegistrations(
        IDictionary<string, AppBuilder> apps,
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes");
            if (classes is null)
            {
                return;
            }

            Queue<(string Path, int Depth)> pending = new();
            foreach (string name in classes.GetSubKeyNames())
            {
                pending.Enqueue((name, 0));
            }

            int inspected = 0;
            while (pending.TryDequeue(out (string Path, int Depth) current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > MaximumRegistryKeyCount)
                {
                    issues.Add(new DiscoveryIssue(
                        "browser-pwa-registration",
                        $"Registration scan stopped after {MaximumRegistryKeyCount} keys."));
                    return;
                }

                using RegistryKey? key = classes.OpenSubKey(current.Path);
                if (key is null)
                {
                    continue;
                }

                if (current.Path.EndsWith(
                    @"\shell\open\command",
                    StringComparison.OrdinalIgnoreCase)
                    && key.GetValue(null) is string command)
                {
                    AddCommandEvidence(
                        apps,
                        ExtractExecutable(command),
                        command,
                        current.Path.Split('\\')[0],
                        new InstallationEvidence(
                            "browser-app-registration",
                            "Found a file or protocol registration for a browser app.",
                            $@"HKCU\Software\Classes\{current.Path}"));
                }

                if (current.Depth >= 4)
                {
                    continue;
                }

                foreach (string child in key.GetSubKeyNames())
                {
                    pending.Enqueue(($@"{current.Path}\{child}", current.Depth + 1));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            issues.Add(new DiscoveryIssue(
                "browser-pwa-registration",
                exception.Message));
        }
    }

    private static void AddCommandEvidence(
        IDictionary<string, AppBuilder> apps,
        string target,
        string arguments,
        string name,
        InstallationEvidence evidence)
    {
        if (!TryParseCommand(
            target,
            arguments,
            out string platform,
            out string appId,
            out string? profileName))
        {
            return;
        }

        AppBuilder builder = GetOrCreate(
            apps,
            platform,
            appId,
            profileName);
        builder.SetName(name);
        builder.SetBrowser(target, null, null, evidence.Path);
        if (profileName is not null)
        {
            builder.SetProfileName(profileName);
        }

        builder.AddEvidence(evidence);
    }

    internal static bool TryParseCommand(
        string target,
        string arguments,
        out string platform,
        out string appId,
        out string? profileName)
    {
        Match match = AppIdRegex().Match(arguments);
        string? classifiedPlatform = ClassifyBrowser(target);
        if (!match.Success || classifiedPlatform is null)
        {
            platform = string.Empty;
            appId = string.Empty;
            profileName = null;
            return false;
        }

        platform = classifiedPlatform;
        appId = match.Groups[1].Value;
        Match profile = ProfileRegex().Match(arguments);
        profileName = !profile.Success
            ? null
            : profile.Groups[1].Success
                ? profile.Groups[1].Value
                : profile.Groups[2].Value;
        return true;
    }

    private static AppBuilder GetOrCreate(
        IDictionary<string, AppBuilder> apps,
        string browserPlatform,
        string appId,
        string? profileIdentity = null)
    {
        string key = $"{browserPlatform}:{appId}:{profileIdentity ?? "<unspecified>"}";
        if (!apps.TryGetValue(key, out AppBuilder? builder))
        {
            builder = new AppBuilder(browserPlatform, appId);
            apps.Add(key, builder);
        }

        return builder;
    }

    private static string? FindBrowserExecutable(BrowserProfileSpec browser)
    {
        string[] candidateRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        string[] relativeDirectories = browser.Platform switch
        {
            "chrome" => [@"Google\Chrome\Application"],
            "edge" => [@"Microsoft\Edge\Application"],
            "brave" => [@"BraveSoftware\Brave-Browser\Application"],
            "chromium" => [@"Chromium\Application"],
            _ => [],
        };
        return candidateRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => relativeDirectories.Select(relative =>
                Path.Combine(root, relative, browser.ExecutableName)))
            .FirstOrDefault(File.Exists);
    }

    private static bool TryReadShortcut(
        string shortcutPath,
        out string target,
        out string arguments)
    {
        Type shellLinkType = Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"),
            throwOnError: true)!;
        IShellLinkW link = (IShellLinkW)Activator.CreateInstance(
            shellLinkType)!;
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            StringBuilder targetBuffer = new(32768);
            link.GetPath(
                targetBuffer,
                targetBuffer.Capacity,
                0,
                0);
            StringBuilder argumentBuffer = new(32768);
            link.GetArguments(argumentBuffer, argumentBuffer.Capacity);
            target = targetBuffer.ToString();
            arguments = argumentBuffer.ToString();
            return !string.IsNullOrWhiteSpace(target)
                && !string.IsNullOrWhiteSpace(arguments);
        }
        catch (COMException)
        {
            target = string.Empty;
            arguments = string.Empty;
            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string ExtractExecutable(string command)
    {
        string trimmed = command.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : string.Empty;
        }

        int separator = trimmed.IndexOf(' ');
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static string? ClassifyBrowser(string executablePath)
    {
        return Path.GetFileName(executablePath).ToLowerInvariant() switch
        {
            "chrome.exe" or "chrome_proxy.exe" => "chrome",
            "msedge.exe" or "msedge_proxy.exe" => "edge",
            "brave.exe" or "brave_proxy.exe" => "brave",
            "chromium.exe" => "chromium",
            _ => null,
        };
    }

    private static bool IsValidAppId(string appId)
    {
        return appId.Length == 32
            && appId.All(character => character is >= 'a' and <= 'p');
    }

    [GeneratedRegex(
        @"(?:^|\s)--app-id(?:=|\s+)([a-p]{32})(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(
        "(?:^|\\s)--profile-directory(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileRegex();

    private sealed record BrowserProfileSpec(
        string Platform,
        string ExecutableName,
        string UserDataRoot);

    private sealed class AppBuilder(string browserPlatform, string appId)
    {
        private readonly List<InstallationEvidence> _evidence = [];
        private string? _name;
        private string? _browserExecutablePath;
        private string? _profileName;
        private string? _profilePath;
        private string? _installPath;

        public void SetName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _name = name;
            }
        }

        public void SetBrowser(
            string? executablePath,
            string? profileName,
            string? profilePath,
            string? installPath)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                _browserExecutablePath = executablePath;
            }

            _profileName ??= profileName;
            _profilePath ??= profilePath;
            if (!string.IsNullOrWhiteSpace(installPath)
                && Path.IsPathFullyQualified(installPath))
            {
                _installPath ??= installPath;
            }
        }

        public void SetProfileName(string profileName)
        {
            _profileName ??= profileName;
        }

        public void AddEvidence(InstallationEvidence evidence)
        {
            if (!_evidence.Contains(evidence))
            {
                _evidence.Add(evidence);
            }
        }

        public BrowserManagedAppInstallation Build()
        {
            return new BrowserManagedAppInstallation(
                appId,
                _name ?? $"Browser app {appId}",
                browserPlatform,
                _browserExecutablePath,
                _profileName,
                _profilePath,
                _installPath ?? _profilePath ?? string.Empty,
                _evidence.ToArray());
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumPath,
            nint findData,
            uint flags);

        void GetIdList(out nint itemIdList);

        void SetIdList(nint itemIdList);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int maximumName);

        void SetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maximumPath);

        void SetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maximumPath);
    }
}
