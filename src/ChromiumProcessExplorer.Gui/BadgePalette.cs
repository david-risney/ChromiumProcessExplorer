using System.Windows.Media;

namespace ChromiumProcessExplorer.Gui;

public static class BadgePalette
{
    public static Brush PlatformBackground { get; } =
        CreateBrush(0xEE, 0xE8, 0xFA);

    public static Brush PlatformForeground { get; } =
        CreateBrush(0x5B, 0x37, 0x8D);

    public static Brush GetPlatformBackground(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        return PlatformBackground;
    }

    public static Brush GetPlatformForeground(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        return PlatformForeground;
    }

    private static Brush PrimaryBackground { get; } =
        CreateBrush(0xE5, 0xF1, 0xFB);

    private static Brush PrimaryForeground { get; } =
        CreateBrush(0x1F, 0x5D, 0x8F);

    private static Brush RendererBackground { get; } =
        CreateBrush(0xE4, 0xF4, 0xE8);

    private static Brush RendererForeground { get; } =
        CreateBrush(0x1F, 0x6B, 0x3A);

    private static Brush GpuBackground { get; } =
        CreateBrush(0xED, 0xE7, 0xF6);

    private static Brush GpuForeground { get; } =
        CreateBrush(0x65, 0x3A, 0x8E);

    private static Brush ServiceBackground { get; } =
        CreateBrush(0xFF, 0xF0, 0xD6);

    private static Brush ServiceForeground { get; } =
        CreateBrush(0x8A, 0x55, 0x00);

    private static Brush WorkerBackground { get; } =
        CreateBrush(0xDF, 0xF3, 0xF4);

    private static Brush WorkerForeground { get; } =
        CreateBrush(0x1D, 0x68, 0x6D);

    private static Brush DiagnosticBackground { get; } =
        CreateBrush(0xFA, 0xE4, 0xE4);

    private static Brush DiagnosticForeground { get; } =
        CreateBrush(0x9A, 0x2D, 0x2D);

    private static Brush OtherBackground { get; } =
        CreateBrush(0xEB, 0xED, 0xF0);

    private static Brush OtherForeground { get; } =
        CreateBrush(0x4F, 0x58, 0x64);

    public static Brush GetRoleBackground(string role)
    {
        return GetRoleCategory(role) switch
        {
            RoleCategory.Primary => PrimaryBackground,
            RoleCategory.Renderer => RendererBackground,
            RoleCategory.Gpu => GpuBackground,
            RoleCategory.Service => ServiceBackground,
            RoleCategory.Worker => WorkerBackground,
            RoleCategory.Diagnostic => DiagnosticBackground,
            _ => OtherBackground,
        };
    }

    public static Brush GetRoleForeground(string role)
    {
        return GetRoleCategory(role) switch
        {
            RoleCategory.Primary => PrimaryForeground,
            RoleCategory.Renderer => RendererForeground,
            RoleCategory.Gpu => GpuForeground,
            RoleCategory.Service => ServiceForeground,
            RoleCategory.Worker => WorkerForeground,
            RoleCategory.Diagnostic => DiagnosticForeground,
            _ => OtherForeground,
        };
    }

    private static RoleCategory GetRoleCategory(string role)
    {
        if (role.Contains("browser", StringComparison.OrdinalIgnoreCase)
            || role.Contains("main", StringComparison.OrdinalIgnoreCase)
            || role.Contains("host", StringComparison.OrdinalIgnoreCase))
        {
            return RoleCategory.Primary;
        }

        if (role.Contains("renderer", StringComparison.OrdinalIgnoreCase))
        {
            return RoleCategory.Renderer;
        }

        if (role.Contains("gpu", StringComparison.OrdinalIgnoreCase))
        {
            return RoleCategory.Gpu;
        }

        if (role.Contains("worker", StringComparison.OrdinalIgnoreCase))
        {
            return RoleCategory.Worker;
        }

        if (role.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || role.Contains("devtools", StringComparison.OrdinalIgnoreCase))
        {
            return RoleCategory.Diagnostic;
        }

        if (role.Contains("utility", StringComparison.OrdinalIgnoreCase)
            || role.Contains("service", StringComparison.OrdinalIgnoreCase)
            || role.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || role.Contains("network", StringComparison.OrdinalIgnoreCase)
            || role.Contains("storage", StringComparison.OrdinalIgnoreCase))
        {
            return RoleCategory.Service;
        }

        return RoleCategory.Other;
    }

    private static SolidColorBrush CreateBrush(
        byte red,
        byte green,
        byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private enum RoleCategory
    {
        Other,
        Primary,
        Renderer,
        Gpu,
        Service,
        Worker,
        Diagnostic,
    }
}
