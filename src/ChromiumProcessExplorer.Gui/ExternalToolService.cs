using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ChromiumProcessExplorer.Gui;

public interface IExternalToolService
{
    void DebugProcess(int processId, string commandTemplate);

    void OpenProcessExplorer(int processId, string commandTemplate);

    void DebugFutureLaunches(
        string imageName,
        string? packageFullName,
        string debuggerCommand);
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
