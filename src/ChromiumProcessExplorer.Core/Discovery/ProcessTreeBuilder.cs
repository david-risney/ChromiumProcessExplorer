namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Builds process trees while guarding against stale parent PIDs.</summary>
public static class ProcessTreeBuilder
{
    /// <summary>Builds a process tree from one consistent snapshot.</summary>
    public static ProcessTree Build(IEnumerable<ProcessSnapshotEntry> processes)
    {
        Dictionary<int, ProcessTreeNode> nodes = processes
            .OrderBy(process => process.ProcessId)
            .ToDictionary(process => process.ProcessId, process => new ProcessTreeNode(process));

        foreach (ProcessTreeNode node in nodes.Values)
        {
            if (node.Process.ParentProcessId <= 0
                || node.Process.ParentProcessId == node.Process.ProcessId
                || node.Process.IsProcessIdReused
                || !nodes.TryGetValue(node.Process.ParentProcessId, out ProcessTreeNode? parent)
                || parent.Process.IsProcessIdReused
                || !IsValidGeneration(parent.Process, node.Process))
            {
                continue;
            }

            node.Parent = parent;
            parent.MutableChildren.Add(node);
        }

        foreach (ProcessTreeNode node in nodes.Values)
        {
            node.MutableChildren.Sort(
                static (left, right) => left.Process.ProcessId.CompareTo(right.Process.ProcessId));
        }

        ProcessTreeNode[] roots = nodes.Values
            .Where(node => node.Parent is null)
            .OrderBy(node => node.Process.ProcessId)
            .ToArray();

        return new ProcessTree(roots, nodes);
    }

    private static bool IsValidGeneration(
        ProcessSnapshotEntry parent,
        ProcessSnapshotEntry child)
    {
        return parent.CreationTime is null
            || child.CreationTime is null
            || parent.CreationTime <= child.CreationTime;
    }
}
