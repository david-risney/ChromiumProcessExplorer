namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A generation-validated process hierarchy.</summary>
public sealed class ProcessTree
{
    private readonly IReadOnlyDictionary<int, ProcessTreeNode> _nodesByProcessId;

    internal ProcessTree(
        IReadOnlyList<ProcessTreeNode> roots,
        IReadOnlyDictionary<int, ProcessTreeNode> nodesByProcessId)
    {
        Roots = roots;
        _nodesByProcessId = nodesByProcessId;
    }

    /// <summary>Gets the root nodes in process ID order.</summary>
    public IReadOnlyList<ProcessTreeNode> Roots { get; }

    /// <summary>Gets all nodes keyed by process ID.</summary>
    public IReadOnlyDictionary<int, ProcessTreeNode> NodesByProcessId => _nodesByProcessId;

    /// <summary>
    /// Creates a tree containing the seed processes, their ancestors, and all
    /// descendants of each seed.
    /// </summary>
    public ProcessTree CreateRelatedView(IEnumerable<int> seedProcessIds)
    {
        HashSet<int> included = [];
        Queue<int> descendants = new();

        foreach (int processId in seedProcessIds)
        {
            if (!_nodesByProcessId.ContainsKey(processId) || !included.Add(processId))
            {
                continue;
            }

            descendants.Enqueue(processId);

            int currentProcessId = processId;
            while (_nodesByProcessId.TryGetValue(currentProcessId, out ProcessTreeNode? current)
                && current.Parent is not null
                && included.Add(current.Parent.Process.ProcessId))
            {
                currentProcessId = current.Parent.Process.ProcessId;
            }
        }

        while (descendants.TryDequeue(out int processId))
        {
            foreach (ProcessTreeNode child in _nodesByProcessId[processId].Children)
            {
                if (included.Add(child.Process.ProcessId))
                {
                    descendants.Enqueue(child.Process.ProcessId);
                }
            }
        }

        return ProcessTreeBuilder.Build(
            included.Select(processId => _nodesByProcessId[processId].Process));
    }

    /// <summary>
    /// Creates a tree containing only the specified processes. Parent links are
    /// retained only when both processes are included.
    /// </summary>
    public ProcessTree CreateFilteredView(IEnumerable<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        return ProcessTreeBuilder.Build(
            processIds
                .Distinct()
                .Where(_nodesByProcessId.ContainsKey)
                .Select(processId => _nodesByProcessId[processId].Process));
    }
}

/// <summary>A process and its generation-validated children.</summary>
public sealed class ProcessTreeNode
{
    internal ProcessTreeNode(ProcessSnapshotEntry process)
    {
        Process = process;
    }

    /// <summary>Gets the process represented by this node.</summary>
    public ProcessSnapshotEntry Process { get; }

    /// <summary>Gets the validated parent node, if present in the snapshot.</summary>
    public ProcessTreeNode? Parent { get; internal set; }

    /// <summary>Gets the validated child nodes.</summary>
    public IReadOnlyList<ProcessTreeNode> Children => MutableChildren;

    internal List<ProcessTreeNode> MutableChildren { get; } = [];
}
