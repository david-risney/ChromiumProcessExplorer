using System.IO;
using System.Text.Json;

namespace ChromiumProcessExplorer.Gui;

public sealed record GuiSettings
{
    public bool AutoRefreshProcesses { get; init; } = true;

    public string DebugCommand { get; init; } = "windbgx.exe -p {pid}";

    public string FutureDebuggerCommand { get; init; } = "windbgx.exe";

    public string ProcessExplorerCommand { get; init; } =
        "procexp.exe /s:{pid}";

    public IReadOnlyList<string> AdditionalInstallationFolders { get; init; } =
        [];
}

public sealed record GuiSettingsLoadResult(
    GuiSettings Settings,
    string? Error);

public interface IGuiSettingsStore
{
    GuiSettingsLoadResult Load();

    void Save(GuiSettings settings);
}

public sealed class JsonGuiSettingsStore : IGuiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public JsonGuiSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ChromiumProcessExplorer",
            "settings.json");
    }

    public GuiSettingsLoadResult Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new GuiSettingsLoadResult(new GuiSettings(), null);
        }

        try
        {
            GuiSettings settings = JsonSerializer.Deserialize<GuiSettings>(
                File.ReadAllText(_settingsPath),
                JsonOptions) ?? new GuiSettings();
            return new GuiSettingsLoadResult(Normalize(settings), null);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                $"Settings could not be loaded: {exception.Message}");
        }
    }

    public void Save(GuiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The settings path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(Normalize(settings), JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private static GuiSettings Normalize(GuiSettings settings)
    {
        return settings with
        {
            DebugCommand = UseDefault(
                settings.DebugCommand,
                "windbgx.exe -p {pid}"),
            FutureDebuggerCommand = UseDefault(
                settings.FutureDebuggerCommand,
                "windbgx.exe"),
            ProcessExplorerCommand = UseDefault(
                settings.ProcessExplorerCommand,
                "procexp.exe /s:{pid}"),
            AdditionalInstallationFolders =
                (settings.AdditionalInstallationFolders ?? [])
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
        };
    }

    private static string UseDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }
}
