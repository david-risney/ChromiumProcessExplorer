namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Enumerates visible Windows named pipes that resemble Mojo pipes.</summary>
public sealed class WindowsMojoPipeEnumerator : IMojoPipeProvider
{
    private const string PipeDirectory = @"\\.\pipe\";

    /// <summary>Enumerates Mojo pipe names without opening or connecting to them.</summary>
    public ValueTask<MojoPipeEnumerationResult> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        List<MojoPipeCandidate> pipes = [];
        List<DiscoveryIssue> issues = [];

        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(PipeDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MojoPipeNameParser.TryParse(path, out MojoPipeCandidate? candidate)
                    && candidate is not null)
                {
                    pipes.Add(candidate);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            issues.Add(new DiscoveryIssue("mojo-pipes", exception.Message));
        }

        return ValueTask.FromResult(
            new MojoPipeEnumerationResult(
                pipes.OrderBy(pipe => pipe.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                issues));
    }
}
