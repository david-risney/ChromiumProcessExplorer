using System.Xml.Linq;

namespace ChromiumProcessExplorer.Core.Discovery;

internal static class InstallationExecutableSelector
{
    private static readonly string[] HelperExecutableNames =
    [
        "createdump.exe",
        "crashpad_handler.exe",
        "CefSharp.BrowserSubprocess.exe",
        "QtWebEngineProcess.exe",
        "msedgewebview2.exe",
    ];

    public static bool IsHelperExecutable(string path)
    {
        string name = Path.GetFileName(path);
        return HelperExecutableNames.Contains(
                name,
                StringComparer.OrdinalIgnoreCase)
            || name.Contains("helper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("notification", StringComparison.OrdinalIgnoreCase)
            || name.Contains("subprocess", StringComparison.OrdinalIgnoreCase)
            || name.Contains("updater", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("update.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGenericRuntimeProductName(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            || name.Equals("Microsoft® .NET", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Microsoft .NET", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".NET", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CefSharp", StringComparison.OrdinalIgnoreCase)
            || name.Contains(
                "Chromium Embedded Framework",
                StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindPreferredExecutable(string directory)
    {
        return Directory.EnumerateFiles(
                directory,
                "*.exe",
                SearchOption.TopDirectoryOnly)
            .Where(path => !IsHelperExecutable(path))
            .OrderBy(path => GetNamePriority(directory, path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static string? FindPackageExecutable(
        string root,
        int maximumDepth)
    {
        string? manifestExecutable = FindManifestExecutable(root);
        return manifestExecutable
            ?? FindExecutable(root, maximumDepth);
    }

    public static string? FindExecutable(string root, int maximumDepth)
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

            foreach (string child in Directory.EnumerateDirectories(current.Path)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Enqueue((child, current.Depth + 1));
                }
            }
        }

        return null;
    }

    private static string? FindManifestExecutable(string root)
    {
        string manifestPath = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            XDocument manifest = XDocument.Load(
                manifestPath,
                LoadOptions.None);
            return manifest.Descendants()
                .Where(element => element.Name.LocalName == "Application")
                .Select(element => element.Attributes()
                    .FirstOrDefault(attribute =>
                        attribute.Name.LocalName == "Executable")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(
                    Path.Combine(
                        root,
                        value!.Replace(
                            Path.AltDirectorySeparatorChar,
                            Path.DirectorySeparatorChar))))
                .FirstOrDefault(path =>
                    File.Exists(path)
                    && !IsHelperExecutable(path));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static int GetNamePriority(string directory, string path)
    {
        string directoryName = new DirectoryInfo(directory).Name;
        string executableName = Path.GetFileNameWithoutExtension(path);
        return executableName.Equals(
            directoryName,
            StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1;
    }
}
