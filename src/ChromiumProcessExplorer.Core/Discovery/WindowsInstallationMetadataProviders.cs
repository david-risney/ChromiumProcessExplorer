using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Reads installed-program registrations from Windows uninstall keys.</summary>
public sealed class WindowsInstalledProgramProvider : IInstalledProgramProvider
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <inheritdoc />
    public IReadOnlyList<InstalledProgramRecord> Discover(
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        List<InstalledProgramRecord> records = [];
        foreach (RegistryHive hive in new[]
        {
            RegistryHive.LocalMachine,
            RegistryHive.CurrentUser,
        })
        {
            foreach (RegistryView view in new[]
            {
                RegistryView.Registry64,
                RegistryView.Registry32,
            })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallKey);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (string subKeyName in uninstall.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using RegistryKey? application =
                                uninstall.OpenSubKey(subKeyName);
                            string? displayName =
                                application?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName))
                            {
                                continue;
                            }

                            records.Add(new InstalledProgramRecord(
                                displayName,
                                application?.GetValue("DisplayVersion") as string,
                                application?.GetValue("Publisher") as string,
                                application?.GetValue("InstallLocation") as string,
                                ParseDisplayIcon(
                                    application?.GetValue("DisplayIcon") as string),
                                application?.GetValue("InstallSource") as string,
                                application?.GetValue("UninstallString") as string,
                                application?.GetValue("WindowsInstaller") is int value
                                    && value != 0,
                                hive == RegistryHive.LocalMachine
                                    ? "Machine"
                                    : "User",
                                view.ToString(),
                                $@"{hive}\{UninstallKey}\{subKeyName}"));
                        }
                        catch (Exception exception) when (
                            exception is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException)
                        {
                            issues.Add(new DiscoveryIssue(
                                "installation-registry-record",
                                exception.Message));
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
                {
                    issues.Add(new DiscoveryIssue(
                        "installation-registry",
                        $"{hive}/{view}: {exception.Message}"));
                }
            }
        }

        return records
            .DistinctBy(
                record =>
                    $"{record.RegistryView}\0{record.RegistryPath}\0"
                    + $"{record.InstallLocation}\0{record.DisplayVersion}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ParseDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string path = value.Trim().Trim('"');
        int index = path.LastIndexOf(',');
        if (index > 2 && int.TryParse(path[(index + 1)..], out _))
        {
            path = path[..index].Trim().Trim('"');
        }

        return Environment.ExpandEnvironmentVariables(path);
    }
}

/// <summary>Discovers accessible Chromium-related WindowsApps packages.</summary>
public sealed partial class WindowsPackageInstallationProvider
    : IWindowsPackageInstallationProvider
{
    /// <inheritdoc />
    public IReadOnlyList<WindowsPackageInstallation> Discover(
        IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
        ICollection<DiscoveryIssue> issues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runningProcesses);
        ArgumentNullException.ThrowIfNull(issues);
        Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase);
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        string windowsApps = Path.Combine(programFiles, "WindowsApps");
        try
        {
            if (Directory.Exists(windowsApps))
            {
                foreach (string directory in Directory.EnumerateDirectories(
                    windowsApps,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    roots.TryAdd(directory, "WindowsApps scan");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            issues.Add(new DiscoveryIssue(
                "installation-package-scan",
                $"{windowsApps}: {exception.Message}"));
        }

        foreach (ProcessSnapshotEntry process in runningProcesses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetPackageRoot(process.ExecutablePath) is string packageRoot)
            {
                roots.TryAdd(packageRoot, $"running process PID {process.ProcessId}");
            }
        }

        List<WindowsPackageInstallation> packages = [];
        foreach ((string root, string source) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseIdentity(root, out InstallationPackageIdentity? identity)
                || identity is null)
            {
                continue;
            }

            PackageLayout? layout;
            try
            {
                bool allowDeepInspection = source.StartsWith(
                        "running process",
                        StringComparison.Ordinal)
                    || IsKnownChromiumPackageName(identity.Name);
                layout = InspectLayout(root, allowDeepInspection);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                issues.Add(new DiscoveryIssue(
                    "installation-package-layout",
                    $"{root}: {exception.Message}"));
                continue;
            }

            if (layout is null)
            {
                continue;
            }

            string displayName = layout.ExecutablePath is null
                ? identity.Name
                : GetProductName(layout.ExecutablePath) ?? identity.Name;
            packages.Add(new WindowsPackageInstallation(
                displayName,
                layout.Platform,
                root,
                layout.ExecutablePath,
                identity,
                layout.ExecutablePath is null
                    ? null
                    : GetPublisher(layout.ExecutablePath),
                layout.ResourcesPath,
                layout.RuntimePath,
                layout.IsSharedRuntime,
                [
                    new InstallationEvidence(
                        "windows-package",
                        $"Discovered package from {source}.",
                        root),
                ]));
        }

        return packages
            .DistinctBy(
                package => package.Identity.PackageFullName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PackageLayout? InspectLayout(
        string root,
        bool allowDeepInspection)
    {
        string resources = Path.Combine(root, "resources");
        string appAsar = Path.Combine(resources, "app.asar");
        if (File.Exists(appAsar))
        {
            string? executable = FindPreferredExecutable(root);
            return new PackageLayout(
                "Electron",
                executable,
                resources,
                executable,
                false);
        }

        string directCef = Path.Combine(root, "libcef.dll");
        if (File.Exists(directCef))
        {
            return new PackageLayout(
                "CEF",
                FindPreferredExecutable(root),
                root,
                directCef,
                false);
        }

        if (!allowDeepInspection)
        {
            return null;
        }

        string? nestedArchive = FindFile(root, "app.asar", maximumDepth: 3);
        if (nestedArchive is not null)
        {
            string nestedResources = Path.GetDirectoryName(nestedArchive)!;
            string applicationRoot =
                Directory.GetParent(nestedResources)?.FullName ?? root;
            string? executable = FindPreferredExecutable(applicationRoot)
                ?? FindExecutable(root, maximumDepth: 3);
            return new PackageLayout(
                "Electron",
                executable,
                nestedResources,
                executable,
                false);
        }

        foreach ((string fileName, string platform, bool shared) in new[]
        {
            ("msedgewebview2.exe", "WebView2", true),
            ("msedge.exe", "Edge", true),
            ("chrome.exe", "Chrome", true),
            ("brave.exe", "Brave", true),
        })
        {
            string? executable = FindFile(root, fileName, maximumDepth: 2);
            if (executable is not null)
            {
                return new PackageLayout(
                    platform,
                    executable,
                    Path.GetDirectoryName(executable),
                    executable,
                    shared);
            }
        }

        string? cef = FindFile(root, "libcef.dll", maximumDepth: 3);
        if (cef is not null)
        {
            return new PackageLayout(
                "CEF",
                FindPreferredExecutable(root),
                Path.GetDirectoryName(cef),
                cef,
                false);
        }

        return null;
    }

    private static bool IsKnownChromiumPackageName(string name)
    {
        return name.Contains("Edge", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Chromium", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WebView", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Electron", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CEF", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Brave", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindFile(
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

            foreach (string child in Directory.EnumerateDirectories(current.Path))
            {
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
        }

        return null;
    }

    private static string? FindPreferredExecutable(string root)
    {
        return Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path).Contains(
                "update",
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? FindExecutable(string root, int maximumDepth)
    {
        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));
        while (pending.TryDequeue(out (string Path, int Depth) current))
        {
            string? executable = FindPreferredExecutable(current.Path);
            if (executable is not null)
            {
                return executable;
            }

            if (current.Depth >= maximumDepth)
            {
                continue;
            }

            foreach (string child in Directory.EnumerateDirectories(current.Path))
            {
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
        }

        return null;
    }

    private static string? TryGetPackageRoot(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        string windowsApps = Path.Combine(programFiles, "WindowsApps");
        if (!executablePath.StartsWith(
            windowsApps + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relative = Path.GetRelativePath(windowsApps, executablePath);
        string? firstSegment = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment is null ? null : Path.Combine(windowsApps, firstSegment);
    }

    private static bool TryParseIdentity(
        string root,
        out InstallationPackageIdentity? identity)
    {
        Match match = PackageFolderRegex().Match(Path.GetFileName(root));
        if (!match.Success)
        {
            identity = null;
            return false;
        }

        string name = match.Groups["name"].Value;
        string version = match.Groups["version"].Value;
        string architecture = match.Groups["architecture"].Value;
        string publisherId = match.Groups["publisher"].Value;
        identity = new InstallationPackageIdentity(
            Path.GetFileName(root),
            $"{name}_{publisherId}",
            name,
            version,
            architecture,
            publisherId);
        return true;
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

    private static string? GetPublisher(string executablePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).CompanyName;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record PackageLayout(
        string Platform,
        string? ExecutablePath,
        string? ResourcesPath,
        string? RuntimePath,
        bool? IsSharedRuntime);

    [GeneratedRegex(
        @"^(?<name>.+)_(?<version>\d+\.\d+\.\d+\.\d+)_(?<architecture>x64|x86|arm|arm64|neutral)_(?<resource>[^_]*)_(?<publisher>[^_]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackageFolderRegex();
}
