using System.IO;

namespace ChromiumProcessExplorer.Gui;

public enum DetailOpenTargetKind
{
    FileSystem,
    Registry,
}

public sealed record DetailOpenTarget(
    DetailOpenTargetKind Kind,
    string Value)
{
    public string Emoji => Kind == DetailOpenTargetKind.Registry
        ? "🔑"
        : "📂";

    public string ToolTip => Kind == DetailOpenTargetKind.Registry
        ? "Open this registry key"
        : "Open the containing folder";

    public static DetailOpenTarget? Detect(string? value)
    {
        string? candidate = NormalizeValue(value);
        if (candidate is null)
        {
            return null;
        }

        if (IsRegistryPath(candidate))
        {
            return new DetailOpenTarget(
                DetailOpenTargetKind.Registry,
                candidate);
        }

        try
        {
            return Path.IsPathFullyQualified(candidate)
                ? new DetailOpenTarget(
                    DetailOpenTargetKind.FileSystem,
                    candidate)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static DetailOpenTarget? FileSystem(string? value)
    {
        string? candidate = NormalizeValue(value);
        return candidate is null
            ? null
            : new DetailOpenTarget(
                DetailOpenTargetKind.FileSystem,
                candidate);
    }

    private static string? NormalizeValue(string? value)
    {
        string? candidate = value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(candidate)
            ? null
            : candidate;
    }

    private static bool IsRegistryPath(string value)
    {
        string candidate = value.StartsWith(
            "Computer\\",
            StringComparison.OrdinalIgnoreCase)
            ? value["Computer\\".Length..]
            : value;
        return candidate.StartsWith(
                "HKEY_LOCAL_MACHINE\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKEY_CURRENT_USER\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKEY_CLASSES_ROOT\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKEY_USERS\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKEY_CURRENT_CONFIG\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKLM\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKCU\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKCR\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKU\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "HKCC\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "\\Registry\\Machine\\",
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                "\\Registry\\User\\",
                StringComparison.OrdinalIgnoreCase);
    }
}
