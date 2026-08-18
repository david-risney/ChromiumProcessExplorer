using System.Reflection;
using System.Text.RegularExpressions;

namespace ChromiumProcessExplorer.Core;

/// <summary>Version metadata embedded in Chromium Process Explorer assemblies.</summary>
public static class ProductVersion
{
    private static readonly Assembly ProductAssembly =
        typeof(ProductVersion).Assembly;

    /// <summary>Gets the release version supplied to the build.</summary>
    public static string Version { get; } =
        GetMetadata("ProductVersion")
        ?? GetInformationalVersion().Split('+', 2)[0];

    /// <summary>Gets the complete informational version.</summary>
    public static string InformationalVersion { get; } =
        GetInformationalVersion();

    /// <summary>Gets source revision metadata embedded by the build, when present.</summary>
    public static string? SourceRevision { get; } =
        GetSourceRevision();

    private static string? GetMetadata(string key)
    {
        return ProductAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value;
    }

    private static string? GetSourceRevision()
    {
        string candidate = GetInformationalVersion()
            .Split('+', 2)
            .Last()
            .Split('.')
            .Last();
        return Regex.IsMatch(
            candidate,
            "^[0-9a-f]{7,64}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                ? candidate
                : null;
    }

    private static string GetInformationalVersion()
    {
        return ProductAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? ProductAssembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
