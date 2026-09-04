using System.IO;
using System.Text;

namespace ChromiumProcessExplorer.Gui;

public sealed record CommandLineHistoryResult(
    IReadOnlyList<string> CommandLines,
    IReadOnlyList<string> Issues);

public interface ICommandLineHistoryProvider
{
    CommandLineHistoryResult Read();
}

public sealed class PsReadLineCommandHistoryProvider
    : ICommandLineHistoryProvider
{
    private const int MaximumLinesPerFile = 10_000;

    public CommandLineHistoryResult Read()
    {
        string applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        string historyDirectory = Path.Combine(
            applicationData,
            "Microsoft",
            "Windows",
            "PowerShell",
            "PSReadLine");
        if (!Directory.Exists(historyDirectory))
        {
            return new CommandLineHistoryResult([], []);
        }

        List<string> commandLines = [];
        List<string> issues = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(
                historyDirectory,
                "*_history.txt",
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    commandLines.AddRange(
                        File.ReadLines(path)
                            .TakeLast(MaximumLinesPerFile));
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException)
                {
                    issues.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            issues.Add(exception.Message);
        }

        return new CommandLineHistoryResult(commandLines, issues);
    }
}

internal static class CommandLineHistoryMatcher
{
    public static IReadOnlyList<(string Argument, string Executable)>
        ExtractArguments(
        IEnumerable<string> commandLines,
        IEnumerable<string?> executablePaths)
    {
        HashSet<string> executableNames = executablePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path =>
            {
                string fileName = Path.GetFileName(path!);
                return new[]
                {
                    fileName,
                    Path.GetFileNameWithoutExtension(fileName),
                };
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (executableNames.Count == 0)
        {
            return [];
        }

        List<(string Argument, string Executable)> results = [];
        foreach (string commandLine in commandLines)
        {
            string[] tokens = Tokenize(commandLine);
            int executableIndex = tokens.Length > 1 && tokens[0] == "&"
                ? 1
                : 0;
            if (tokens.Length <= executableIndex)
            {
                continue;
            }

            string executable = Path.GetFileName(tokens[executableIndex]);
            string executableWithoutExtension =
                Path.GetFileNameWithoutExtension(executable);
            if (!executableNames.Contains(executable)
                && !executableNames.Contains(executableWithoutExtension))
            {
                continue;
            }

            results.AddRange(tokens
                .Skip(executableIndex + 1)
                .Where(argument => argument.StartsWith('-')
                    || argument.StartsWith('/'))
                .Select(argument => (argument, executable)));
        }

        return results;
    }

    private static string[] Tokenize(string commandLine)
    {
        List<string> tokens = [];
        StringBuilder token = new();
        char? quote = null;
        bool escaped = false;
        foreach (char character in commandLine)
        {
            if (escaped)
            {
                token.Append(character);
                escaped = false;
                continue;
            }

            if (character == '`')
            {
                escaped = true;
                continue;
            }

            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    token.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                AddToken();
            }
            else
            {
                token.Append(character);
            }
        }

        AddToken();
        return tokens.ToArray();

        void AddToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            tokens.Add(token.ToString());
            token.Clear();
        }
    }
}
