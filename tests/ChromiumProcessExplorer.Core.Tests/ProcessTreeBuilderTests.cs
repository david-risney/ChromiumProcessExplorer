using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ProcessTreeBuilderTests
{
    [Fact]
    public void BuildConnectsParentCreatedBeforeChild()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessSnapshotEntry parent = CreateProcess(10, 1, start);
        ProcessSnapshotEntry child = CreateProcess(11, 10, start.AddSeconds(1));

        ProcessTree tree = ProcessTreeBuilder.Build([parent, child]);

        Assert.Same(tree.NodesByProcessId[10], tree.NodesByProcessId[11].Parent);
        Assert.Single(tree.NodesByProcessId[10].Children);
    }

    [Fact]
    public void BuildRejectsReusedParentPid()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessSnapshotEntry child = CreateProcess(11, 10, start);
        ProcessSnapshotEntry reusedParent = CreateProcess(10, 1, start.AddSeconds(1));

        ProcessTree tree = ProcessTreeBuilder.Build([child, reusedParent]);

        Assert.Null(tree.NodesByProcessId[11].Parent);
        Assert.Equal(2, tree.Roots.Count);
    }

    [Fact]
    public void CreateFilteredViewOmitsUnselectedAncestors()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessTree tree = ProcessTreeBuilder.Build(
        [
            CreateProcess(1, 0, start),
            CreateProcess(2, 1, start.AddSeconds(1)),
            CreateProcess(3, 2, start.AddSeconds(2)),
            CreateProcess(4, 3, start.AddSeconds(3)),
        ]);

        ProcessTree filtered = tree.CreateFilteredView([3, 4]);

        Assert.Equal([3, 4], filtered.NodesByProcessId.Keys.Order());
        Assert.Single(filtered.Roots);
        Assert.Equal(3, filtered.Roots[0].Process.ProcessId);
        Assert.Equal(4, Assert.Single(filtered.Roots[0].Children).Process.ProcessId);
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        int parentProcessId,
        DateTimeOffset creationTime)
    {
        return new ProcessSnapshotEntry(
            processId,
            parentProcessId,
            creationTime,
            $"process-{processId}.exe",
            null,
            null,
            null,
            null,
            false,
            [],
            null);
    }
}
