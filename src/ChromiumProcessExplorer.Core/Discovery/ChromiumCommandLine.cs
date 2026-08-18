using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Parses Windows command lines and Chromium-style switches.</summary>
public sealed partial class ChromiumCommandLine
{
    private readonly IReadOnlyDictionary<string, string?> _switches;

    private ChromiumCommandLine(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> switches)
    {
        Arguments = arguments;
        _switches = switches;
    }

    /// <summary>Gets the arguments returned by Windows command-line parsing.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets parsed switch names and their unmodified values.</summary>
    public IReadOnlyDictionary<string, string?> Switches => _switches;

    /// <summary>Parses a raw Windows process command line.</summary>
    public static ChromiumCommandLine Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return new ChromiumCommandLine([], new Dictionary<string, string?>());
        }

        nint argv = NativeMethods.CommandLineToArgvW(commandLine, out int argumentCount);
        if (argv == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            string[] arguments = new string[argumentCount];
            Dictionary<string, string?> switches =
                new(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < argumentCount; index++)
            {
                nint argumentPointer = Marshal.ReadIntPtr(argv, index * nint.Size);
                string argument = Marshal.PtrToStringUni(argumentPointer) ?? string.Empty;
                arguments[index] = argument;

                if (!argument.StartsWith("--", StringComparison.Ordinal)
                    || argument.Length == 2)
                {
                    continue;
                }

                int separator = argument.IndexOf('=');
                string name = separator < 0 ? argument[2..] : argument[2..separator];
                string? value = separator < 0 ? null : argument[(separator + 1)..];
                switches[name] = value;
            }

            return new ChromiumCommandLine(arguments, switches);
        }
        finally
        {
            _ = NativeMethods.LocalFree(argv);
        }
    }

    /// <summary>Gets a switch value, or null when absent or valueless.</summary>
    public string? GetSwitchValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _switches.GetValueOrDefault(name.TrimStart('-'));
    }

    /// <summary>Returns whether the command line contains the named switch.</summary>
    public bool HasSwitch(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _switches.ContainsKey(name.TrimStart('-'));
    }

    private static partial class NativeMethods
    {
        [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint CommandLineToArgvW(
            string commandLine,
            out int argumentCount);

        [LibraryImport("kernel32.dll")]
        internal static partial nint LocalFree(nint memory);
    }
}
