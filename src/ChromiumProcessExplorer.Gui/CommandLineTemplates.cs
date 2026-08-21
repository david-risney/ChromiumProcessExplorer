using System.Text.RegularExpressions;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Gui;

public sealed record CommandLineRemovalSettings(
    string Pattern,
    bool IsRegex);

public sealed record CommandLineRunTargetViewModel(
    string Name,
    string Source,
    string ExecutablePath,
    string? CommandLine,
    ProcessTreeItemViewModel? Process,
    InstallationItemViewModel? Installation)
{
    public string SelectionKey => Process is null
        ? $"install|{Installation?.InstallPath}"
        : $"process|{Process.Identity}";
}

public sealed record CommandLineTemplateSettings
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "New template";

    public string ApplicableExecutableRegex { get; init; } = ".*";

    public bool IsFavorite { get; init; } = true;

    public IReadOnlyList<string> AddParts { get; init; } = [];

    public IReadOnlyList<CommandLineRemovalSettings> RemoveParts { get; init; } =
        [];

    public static CommandLineTemplateSettings CreateRemoteDebugging()
    {
        return new CommandLineTemplateSettings
        {
            Id = "remote-debugging",
            Name = "Enable remote debugging",
            ApplicableExecutableRegex = ".*",
            IsFavorite = true,
            AddParts =
            [
                "--remote-debugging-port=9222",
                "--user-data-dir=%LOCALAPPDATA%\\ChromiumProcessExplorer\\RemoteDebugging\\{executable}",
            ],
        };
    }
}

public sealed class CommandLineTemplateViewModel : ObservableObject
{
    private static readonly TimeSpan RegexTimeout =
        TimeSpan.FromMilliseconds(100);

    private readonly Action _changed;
    private string _name;
    private string _applicableExecutableRegex;
    private string _addPartsText;
    private string _removePartsText;
    private bool _isFavorite;

    public CommandLineTemplateViewModel(
        CommandLineTemplateSettings settings,
        Action changed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(changed);
        Id = settings.Id;
        _name = settings.Name;
        _applicableExecutableRegex = settings.ApplicableExecutableRegex;
        _isFavorite = settings.IsFavorite;
        _addPartsText = string.Join(Environment.NewLine, settings.AddParts);
        _removePartsText = string.Join(
            Environment.NewLine,
            settings.RemoveParts.Select(part =>
                part.IsRegex ? $"regex:{part.Pattern}" : part.Pattern));
        _changed = changed;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetTemplateField(ref _name, value);
    }

    public string ApplicableExecutableRegex
    {
        get => _applicableExecutableRegex;
        set => SetTemplateField(ref _applicableExecutableRegex, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetField(ref _isFavorite, value))
            {
                _changed();
            }
        }
    }

    public string AddPartsText
    {
        get => _addPartsText;
        set => SetTemplateField(ref _addPartsText, value);
    }

    public string RemovePartsText
    {
        get => _removePartsText;
        set => SetTemplateField(ref _removePartsText, value);
    }

    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "Name is required.";
            }

            try
            {
                _ = CreateExecutableRegex();
                foreach (CommandLineRemovalSettings removal in GetRemoveParts()
                    .Where(removal => removal.IsRegex))
                {
                    _ = new Regex(
                        removal.Pattern,
                        RegexOptions.IgnoreCase
                            | RegexOptions.CultureInvariant,
                        RegexTimeout);
                }
            }
            catch (ArgumentException exception)
            {
                return exception.Message;
            }

            return null;
        }
    }

    public bool IsValid => ValidationError is null;

    public bool AppliesTo(string executableName)
    {
        if (!IsValid || string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        try
        {
            return CreateExecutableRegex().IsMatch(executableName);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public CommandLineTemplateSettings ToSettings()
    {
        return new CommandLineTemplateSettings
        {
            Id = Id,
            Name = Name.Trim(),
            ApplicableExecutableRegex =
                ApplicableExecutableRegex.Trim(),
            IsFavorite = IsFavorite,
            AddParts = GetLines(AddPartsText),
            RemoveParts = GetRemoveParts(),
        };
    }

    private Regex CreateExecutableRegex()
    {
        return new Regex(
            string.IsNullOrWhiteSpace(ApplicableExecutableRegex)
                ? ".*"
                : ApplicableExecutableRegex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    private CommandLineRemovalSettings[] GetRemoveParts()
    {
        return GetLines(RemovePartsText)
            .Select(line => line.StartsWith(
                "regex:",
                StringComparison.OrdinalIgnoreCase)
                ? new CommandLineRemovalSettings(line[6..], true)
                : new CommandLineRemovalSettings(line, false))
            .ToArray();
    }

    private static string[] GetLines(string value)
    {
        return value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private void SetTemplateField(ref string field, string value)
    {
        if (!SetField(ref field, value))
        {
            return;
        }

        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(IsValid));
        _changed();
    }
}

public static class CommandLineTemplateTransformer
{
    private static readonly TimeSpan RegexTimeout =
        TimeSpan.FromMilliseconds(100);
    private static readonly HashSet<string> CommaSeparatedSwitches =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "disable-blink-features",
            "disable-features",
            "enable-blink-features",
            "enable-features",
            "feature-flags",
        };

    public static IReadOnlyList<string> Apply(
        string? commandLine,
        CommandLineTemplateSettings template)
    {
        ArgumentNullException.ThrowIfNull(template);
        ChromiumCommandLine parsed = ChromiumCommandLine.Parse(commandLine);
        string[] originalArguments = parsed.Arguments.Skip(1).ToArray();
        int terminatorIndex = Array.FindIndex(
            originalArguments,
            argument => string.Equals(
                argument,
                "--",
                StringComparison.Ordinal));
        List<string> switchArguments = (terminatorIndex < 0
                ? originalArguments
                : originalArguments[..terminatorIndex])
            .ToList();
        IReadOnlyList<string> positionalArguments = terminatorIndex < 0
            ? []
            : originalArguments[terminatorIndex..];
        switchArguments.RemoveAll(argument =>
            ShouldRemove(argument, template.RemoveParts));
        switchArguments.AddRange(template.AddParts.Where(part =>
            !string.IsNullOrWhiteSpace(part)));
        return MergeValuedSwitches(switchArguments)
            .Concat(positionalArguments)
            .ToArray();
    }

    private static bool ShouldRemove(
        string argument,
        IReadOnlyList<CommandLineRemovalSettings> removals)
    {
        foreach (CommandLineRemovalSettings removal in removals)
        {
            if (removal.IsRegex)
            {
                if (Regex.IsMatch(
                    argument,
                    removal.Pattern,
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant,
                    RegexTimeout))
                {
                    return true;
                }

                continue;
            }

            if (MatchesLiteral(argument, removal.Pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesLiteral(string argument, string removal)
    {
        if (string.Equals(
                argument,
                removal,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !removal.Contains('=')
            && removal.StartsWith("--", StringComparison.Ordinal)
            && argument.StartsWith(
                removal + "=",
                StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> MergeValuedSwitches(
        IReadOnlyList<string> arguments)
    {
        List<string> result = [];
        Dictionary<string, int> switchIndexes =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string argument in arguments)
        {
            if (!TrySplitValuedSwitch(
                    argument,
                    out string? name,
                    out string? value))
            {
                result.Add(argument);
                continue;
            }

            if (!switchIndexes.TryGetValue(name, out int index))
            {
                switchIndexes[name] = result.Count;
                result.Add(argument);
                continue;
            }

            if (CommaSeparatedSwitches.Contains(name))
            {
                string existingValue = result[index][(name.Length + 3)..];
                string merged = string.Join(
                    ",",
                    existingValue.Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries
                                | StringSplitOptions.TrimEntries)
                        .Concat(value.Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries
                                | StringSplitOptions.TrimEntries))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                result[index] = $"--{name}={merged}";
            }
            else
            {
                result[index] = argument;
            }
        }

        return result;
    }

    private static bool TrySplitValuedSwitch(
        string argument,
        out string name,
        out string value)
    {
        name = string.Empty;
        value = string.Empty;
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        int separator = argument.IndexOf('=');
        if (separator <= 2 || separator == argument.Length - 1)
        {
            return false;
        }

        name = argument[2..separator];
        value = argument[(separator + 1)..];
        return true;
    }
}
