using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ChromiumProcessExplorer.Core.Discovery;
using Microsoft.Win32;

namespace ChromiumProcessExplorer.Gui;

public interface IExternalToolService
{
    void DebugProcess(int processId, string commandTemplate);

    void OpenProcessExplorer(int processId, string commandTemplate);

    Task TerminateProcessTreeAsync(ProcessIdentity identity);

    void DebugFutureLaunches(
        string imageName,
        string? packageFullName,
        string debuggerCommand);

    void LaunchExecutable(
        string executablePath,
        IReadOnlyList<string> arguments);

    void OpenFileSystemPath(string path);

    void OpenRegistryPath(string path);

    void OpenUri(string uri);
}

public sealed class WindowsExternalToolService : IExternalToolService
{
    public void DebugProcess(int processId, string commandTemplate)
    {
        LaunchTemplate(commandTemplate, ("{pid}", processId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
    }

    public void OpenProcessExplorer(int processId, string commandTemplate)
    {
        LaunchTemplate(commandTemplate, ("{pid}", processId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
    }

    public async Task TerminateProcessTreeAsync(ProcessIdentity identity)
    {
        if (identity.CreationTime is null)
        {
            throw new InvalidOperationException(
                "The process creation time is unavailable, so its generation "
                + "cannot be verified before termination.");
        }

        using Process process = Process.GetProcessById(identity.ProcessId);
        DateTimeOffset actualCreationTime =
            new(process.StartTime.ToUniversalTime());
        if (actualCreationTime != identity.CreationTime)
        {
            throw new InvalidOperationException(
                $"PID {identity.ProcessId} now identifies a different process "
                + "generation. Refresh the process list before trying again.");
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void DebugFutureLaunches(
        string imageName,
        string? packageFullName,
        string debuggerCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(debuggerCommand);
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The GUI executable path is unavailable.");
        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        startInfo.ArgumentList.Add(FutureDebugConfigurator.WorkerArgument);
        startInfo.ArgumentList.Add(imageName);
        startInfo.ArgumentList.Add(packageFullName ?? string.Empty);
        startInfo.ArgumentList.Add(debuggerCommand);
        Start(startInfo);
    }

    public void LaunchExecutable(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = true,
        };
        string executableName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(
                Environment.ExpandEnvironmentVariables(argument)
                    .Replace(
                        "{executable}",
                        executableName,
                        StringComparison.OrdinalIgnoreCase));
        }

        Start(startInfo);
    }

    public void OpenFileSystemPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        string folder = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath)
                ?? throw new DirectoryNotFoundException(
                    $"The containing folder for {fullPath} is unavailable.");
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                $"The folder does not exist: {folder}");
        }

        ProcessStartInfo startInfo = new("explorer.exe")
        {
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(folder);
        Start(startInfo);
    }

    public void OpenRegistryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = NormalizeRegistryPath(path);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit",
            writable: true);
        key.SetValue(
            "LastKey",
            $"Computer\\{normalized}",
            RegistryValueKind.String);
        Start(new ProcessStartInfo("regedit.exe")
        {
            UseShellExecute = true,
        });
    }

    public void OpenUri(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Only absolute HTTP or HTTPS URLs can be opened.");
        }

        Start(new ProcessStartInfo(parsed.ToString())
        {
            UseShellExecute = true,
        });
    }

    private static void LaunchTemplate(
        string commandTemplate,
        params (string Placeholder, string Value)[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandTemplate);
        string expanded = commandTemplate.Trim();
        foreach ((string placeholder, string value) in values)
        {
            expanded = expanded.Replace(
                placeholder,
                value,
                StringComparison.OrdinalIgnoreCase);
        }

        (string executable, string arguments) = SplitCommand(expanded);
        Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
        });
    }

    private static (string Executable, string Arguments) SplitCommand(
        string command)
    {
        if (command[0] == '"')
        {
            int closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                throw new FormatException(
                    "The command contains an unmatched quote.");
            }

            return (
                command[1..closingQuote],
                command[(closingQuote + 1)..].TrimStart());
        }

        int separator = command.IndexOf(' ');
        return separator < 0
            ? (command, string.Empty)
            : (command[..separator], command[(separator + 1)..].TrimStart());
    }

    private static string NormalizeRegistryPath(string path)
    {
        string normalized = path.Trim().Trim('"');
        if (normalized.StartsWith(
            "Computer\\",
            StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Computer\\".Length..];
        }

        (string Prefix, string Expanded)[] aliases =
        [
            ("HKLM\\", "HKEY_LOCAL_MACHINE\\"),
            ("HKCU\\", "HKEY_CURRENT_USER\\"),
            ("HKCR\\", "HKEY_CLASSES_ROOT\\"),
            ("HKU\\", "HKEY_USERS\\"),
            ("HKCC\\", "HKEY_CURRENT_CONFIG\\"),
            ("\\Registry\\Machine\\", "HKEY_LOCAL_MACHINE\\"),
            ("\\Registry\\User\\", "HKEY_USERS\\"),
        ];
        foreach ((string prefix, string expanded) in aliases)
        {
            if (normalized.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return expanded + normalized[prefix.Length..];
            }
        }

        return normalized;
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start {startInfo.FileName}: {exception.Message}",
                exception);
        }
    }
}

internal static class FutureDebugConfigurator
{
    public const string WorkerArgument = "--configure-future-debug";

    private const string IfeoPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public static int Run(string[] args)
    {
        if (args.Length != 4
            || !string.Equals(
                args[0],
                WorkerArgument,
                StringComparison.Ordinal))
        {
            return 2;
        }

        try
        {
            Configure(args[1], NullIfEmpty(args[2]), args[3]);
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or Win32Exception)
        {
            MessageBox.Show(
                exception.Message,
                "Future debugging configuration failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static void Configure(
        string imageName,
        string? packageFullName,
        string debuggerCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(debuggerCommand);
        if (!string.IsNullOrWhiteSpace(packageFullName))
        {
            ProcessStartInfo startInfo = new("plmdebug.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/enableDebug");
            startInfo.ArgumentList.Add(packageFullName);
            startInfo.ArgumentList.Add(debuggerCommand);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "PLMDebug did not start.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"PLMDebug exited with code {process.ExitCode}.");
            }

            return;
        }

        string executableName = Path.GetFileName(imageName);
        if (!string.Equals(
                executableName,
                imageName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Future debugging requires an executable file name.",
                nameof(imageName));
        }

        using RegistryKey localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey key = localMachine.CreateSubKey(
            $@"{IfeoPath}\{executableName}",
            writable: true);
        key.SetValue(
            "Debugger",
            debuggerCommand,
            RegistryValueKind.String);
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
