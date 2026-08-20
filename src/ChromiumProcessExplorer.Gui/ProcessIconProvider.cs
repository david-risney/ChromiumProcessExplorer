using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace ChromiumProcessExplorer.Gui;

public interface IProcessIconProvider
{
    ValueTask<ImageSource?> GetIconAsync(
        string? executablePath,
        CancellationToken cancellationToken);
}

public sealed class WindowsProcessIconProvider : IProcessIconProvider
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    private static readonly ImageSource FallbackIcon = CreateFallbackIcon();

    private readonly ConcurrentDictionary<string, Task<ImageSource>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<ImageSource?> GetIconAsync(
        string? executablePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath))
        {
            return FallbackIcon;
        }

        string path = executablePath;
        Task<ImageSource> task = _cache.GetOrAdd(
            path,
            static value => Task.Run<ImageSource>(
                () => LoadExtractedIcon(value)
                    ?? LoadPackagedAppIcon(value)
                    ?? LoadShellIcon(value)
                    ?? FallbackIcon));
        return await task.WaitAsync(cancellationToken);
    }

    private static BitmapImage? LoadPackagedAppIcon(string executablePath)
    {
        if (!executablePath.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps"
                    + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !executablePath.Contains(
                $"{Path.DirectorySeparatorChar}SystemApps"
                    + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string? directory = Path.GetDirectoryName(executablePath);
            for (int depth = 0; depth < 6 && directory is not null; depth++)
            {
                string manifestPath = Path.Combine(
                    directory,
                    "AppxManifest.xml");
                if (File.Exists(manifestPath))
                {
                    return LoadManifestLogo(directory, manifestPath);
                }

                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or SecurityException
            or XmlException)
        {
            return null;
        }

        return null;
    }

    private static BitmapImage? LoadManifestLogo(
        string packageRoot,
        string manifestPath)
    {
        XDocument manifest = XDocument.Load(
            manifestPath,
            LoadOptions.None);
        string? relativeLogo = manifest.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is
                "Square44x44Logo" or "Square30x30Logo" or "Logo")
            .Select(attribute => attribute.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (relativeLogo is null)
        {
            return null;
        }

        string logoPath = Path.Combine(
            packageRoot,
            relativeLogo.Replace(
                '/',
                Path.DirectorySeparatorChar));
        string? resolvedLogoPath = ResolveLogoVariant(logoPath);
        if (resolvedLogoPath is null)
        {
            return null;
        }

        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 32;
        image.UriSource = new Uri(resolvedLogoPath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? ResolveLogoVariant(string logoPath)
    {
        if (File.Exists(logoPath))
        {
            return logoPath;
        }

        string? directory = Path.GetDirectoryName(logoPath);
        string fileName = Path.GetFileNameWithoutExtension(logoPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(
                directory,
                $"{fileName}.*{Path.GetExtension(logoPath)}",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path.Contains(
                "targetsize-32",
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => path.Contains(
                "scale-200",
                StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static BitmapSource? LoadShellIcon(string executablePath)
    {
        try
        {
            ShFileInfo fileInfo = default;
            if (SHGetFileInfo(
                executablePath,
                0,
                ref fileInfo,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiSmallIcon) == 0
                || fileInfo.IconHandle == 0)
            {
                return null;
            }

            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    fileInfo.IconHandle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(20, 20));
                source.Freeze();
                return source;
            }
            finally
            {
                _ = DestroyIcon(fileInfo.IconHandle);
            }
        }

        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapSource? LoadExtractedIcon(string executablePath)
    {
        try
        {
            nint[] smallIcons = new nint[1];
            uint extracted = ExtractIconEx(
                executablePath,
                0,
                null,
                smallIcons,
                1);
            if (extracted == 0 || smallIcons[0] == 0)
            {
                return null;
            }

            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    smallIcons[0],
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(20, 20));
                source.Freeze();
                return source;
            }
            finally
            {
                _ = DestroyIcon(smallIcons[0]);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DrawingImage CreateFallbackIcon()
    {
        GeometryDrawing drawing = new(
            new SolidColorBrush(Color.FromRgb(92, 105, 120)),
            null,
            Geometry.Parse(
                "M2,2 H14 V14 H2 Z M5,5 H11 V7 H5 Z M5,9 H11 V11 H5 Z"));
        drawing.Freeze();
        DrawingImage image = new(drawing);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        nint[]? largeIcons,
        [Out] nint[]? smallIcons,
        uint iconCount);
#pragma warning restore SYSLIB1054
}
