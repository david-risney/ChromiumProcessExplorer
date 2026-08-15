namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A named pipe whose name indicates Chromium Mojo infrastructure.</summary>
public sealed record MojoPipeCandidate(string Name, int? ProcessIdHint);

/// <summary>The result of enumerating Mojo pipe names.</summary>
public sealed record MojoPipeEnumerationResult(
    IReadOnlyList<MojoPipeCandidate> Pipes,
    IReadOnlyList<DiscoveryIssue> Issues);
