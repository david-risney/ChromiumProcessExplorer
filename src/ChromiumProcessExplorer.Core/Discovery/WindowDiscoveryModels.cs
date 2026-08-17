namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A native window observed during one optional topology snapshot.</summary>
public sealed record WindowSnapshotEntry(
    long WindowHandle,
    long? ParentWindowHandle,
    long? FirstChildWindowHandle,
    long? CrossProcessChildWindowHandle,
    int OwnerProcessId,
    DateTimeOffset? OwnerProcessCreationTime,
    uint OwnerThreadId,
    string ClassName,
    bool IsVisible,
    string? InspectionError);

/// <summary>Raw native window observations and recoverable collection failures.</summary>
public sealed record WindowSnapshotResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<WindowSnapshotEntry> Windows,
    IReadOnlyList<DiscoveryIssue> Issues)
{
    /// <summary>Gets an empty result used when window collection is disabled.</summary>
    public static WindowSnapshotResult Empty { get; } = new(
        DateTimeOffset.MinValue,
        [],
        []);
}
