namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Describes a recoverable problem encountered during discovery.</summary>
public sealed record DiscoveryIssue(
    string Stage,
    string Message,
    int? ProcessId = null,
    int? NativeErrorCode = null);
