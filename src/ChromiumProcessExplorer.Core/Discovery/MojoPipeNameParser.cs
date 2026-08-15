using System.Text.RegularExpressions;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Parses implementation-dependent hints from Chromium Mojo pipe names.</summary>
public static partial class MojoPipeNameParser
{
    [GeneratedRegex(
        @"^(?:\(LOCAL\))?mojo\.(?:[^.]*_)?(?<pid>\d+)\.\d+\.\d+(?:\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MojoPipeRegex();

    /// <summary>
    /// Returns a candidate when the final path component resembles a Mojo pipe.
    /// The PID is only a hint and must be validated against process creation time.
    /// </summary>
    public static bool TryParse(string path, out MojoPipeCandidate? candidate)
    {
        ArgumentNullException.ThrowIfNull(path);

        string name = path[(path.LastIndexOf('\\') + 1)..];
        Match match = MojoPipeRegex().Match(name);
        if (!match.Success)
        {
            candidate = null;
            return false;
        }

        int? processIdHint = int.TryParse(
            match.Groups["pid"].Value,
            out int processId)
            ? processId
            : null;

        candidate = new MojoPipeCandidate(name, processIdHint);
        return true;
    }
}
