using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Identifies one generation of a process in a system snapshot.</summary>
public readonly record struct ProcessIdentity(
    int ProcessId,
    DateTimeOffset? CreationTime);

/// <summary>Classifies a relationship between two process nodes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessRelationshipType>))]
public enum ProcessRelationshipType
{
    /// <summary>A validated operating-system parent relationship.</summary>
    OsParent,

    /// <summary>An inferred Chromium browser-to-subprocess relationship.</summary>
    ChromiumSubprocess,

    /// <summary>An observed Mojo endpoint connection.</summary>
    MojoConnection,

    /// <summary>A Chromium process embedded by a host process.</summary>
    EmbeddedBy,

    /// <summary>A process that owns a window.</summary>
    OwnsWindow,

    /// <summary>A cross-process window topology relationship.</summary>
    CrossProcessWindow,

    /// <summary>Processes that use the same profile.</summary>
    SharesProfile,
}

/// <summary>Describes the confidence assigned to relationship evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessRelationshipConfidence>))]
public enum ProcessRelationshipConfidence
{
    /// <summary>Weak or incomplete evidence.</summary>
    Low,

    /// <summary>Corroborated but indirect evidence.</summary>
    Medium,

    /// <summary>Directly observed or generation-validated evidence.</summary>
    High,
}

/// <summary>Evidence supporting one process relationship observation.</summary>
public sealed class ProcessRelationshipEvidence
{
    /// <summary>Creates an immutable evidence snapshot.</summary>
    public ProcessRelationshipEvidence(
        string source,
        ProcessRelationshipConfidence confidence,
        DateTimeOffset observedAt,
        IReadOnlyDictionary<string, string?> rawValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(rawValues);

        Source = source;
        Confidence = confidence;
        ObservedAt = observedAt;
        RawValues = new ReadOnlyDictionary<string, string?>(
            rawValues.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }

    /// <summary>Gets the component that produced the evidence.</summary>
    public string Source { get; }

    /// <summary>Gets the confidence assigned to the evidence.</summary>
    public ProcessRelationshipConfidence Confidence { get; }

    /// <summary>Gets when the evidence was observed.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Gets the unmodified values supporting the relationship.</summary>
    public IReadOnlyDictionary<string, string?> RawValues { get; }
}

/// <summary>A process represented once in a process graph.</summary>
public sealed record ProcessGraphNode(
    ProcessIdentity Identity,
    ProcessSnapshotEntry Process);

/// <summary>A typed, directed relationship between process generations.</summary>
public sealed record ProcessGraphEdge(
    ProcessIdentity Source,
    ProcessIdentity Target,
    ProcessRelationshipType Type,
    ProcessRelationshipEvidence Evidence);

/// <summary>A typed many-to-many graph of process generations.</summary>
public sealed class ProcessGraph
{
    private readonly IReadOnlyDictionary<ProcessIdentity, ProcessGraphNode> _nodesByIdentity;
    private readonly IReadOnlyDictionary<int, ProcessGraphNode> _nodesByProcessId;
    private readonly IReadOnlyDictionary<ProcessIdentity, IReadOnlyList<ProcessGraphEdge>>
        _incomingEdges;
    private readonly IReadOnlyDictionary<ProcessIdentity, IReadOnlyList<ProcessGraphEdge>>
        _outgoingEdges;

    /// <summary>Creates a graph and validates that every edge references a node.</summary>
    public ProcessGraph(
        IEnumerable<ProcessSnapshotEntry> processes,
        IEnumerable<ProcessGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(edges);

        ProcessGraphNode[] nodes = processes
            .OrderBy(process => process.ProcessId)
            .Select(process => new ProcessGraphNode(
                new ProcessIdentity(process.ProcessId, process.CreationTime),
                process))
            .ToArray();
        IGrouping<int, ProcessGraphNode>? duplicateProcessId = nodes
            .GroupBy(node => node.Process.ProcessId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProcessId is not null)
        {
            throw new ArgumentException(
                $"Process ID {duplicateProcessId.Key} occurs more than once in the snapshot.",
                nameof(processes));
        }

        Dictionary<ProcessIdentity, ProcessGraphNode> nodesByIdentity =
            nodes.ToDictionary(node => node.Identity);
        Dictionary<int, ProcessGraphNode> nodesByProcessId =
            nodes.ToDictionary(node => node.Process.ProcessId);
        ProcessGraphEdge[] edgeArray = edges.ToArray();

        if (edgeArray.Any(edge => !nodesByIdentity.ContainsKey(edge.Source)
            || !nodesByIdentity.ContainsKey(edge.Target)))
        {
            throw new ArgumentException(
                "Every process graph edge must reference nodes in the graph.",
                nameof(edges));
        }

        Nodes = nodes;
        Edges = edgeArray;
        _nodesByIdentity = nodesByIdentity;
        _nodesByProcessId = nodesByProcessId;
        _incomingEdges = CreateEdgeIndex(edgeArray, edge => edge.Target);
        _outgoingEdges = CreateEdgeIndex(edgeArray, edge => edge.Source);
    }

    /// <summary>Gets graph nodes in process ID order.</summary>
    public IReadOnlyList<ProcessGraphNode> Nodes { get; }

    /// <summary>Gets all typed edges.</summary>
    public IReadOnlyList<ProcessGraphEdge> Edges { get; }

    /// <summary>Finds a node by its process generation identity.</summary>
    public ProcessGraphNode? FindNode(ProcessIdentity identity)
    {
        return _nodesByIdentity.GetValueOrDefault(identity);
    }

    /// <summary>Finds the one node with a process ID in this snapshot.</summary>
    public ProcessGraphNode? FindNode(int processId)
    {
        return _nodesByProcessId.GetValueOrDefault(processId);
    }

    /// <summary>Gets all edges entering a process generation.</summary>
    public IReadOnlyList<ProcessGraphEdge> GetIncomingEdges(ProcessIdentity identity)
    {
        return _incomingEdges.GetValueOrDefault(identity) ?? [];
    }

    /// <summary>Gets all edges leaving a process generation.</summary>
    public IReadOnlyList<ProcessGraphEdge> GetOutgoingEdges(ProcessIdentity identity)
    {
        return _outgoingEdges.GetValueOrDefault(identity) ?? [];
    }

    /// <summary>Creates a graph containing only the specified process IDs.</summary>
    public ProcessGraph CreateFilteredView(IEnumerable<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        HashSet<int> included = processIds.ToHashSet();
        ProcessGraphNode[] nodes = Nodes
            .Where(node => included.Contains(node.Process.ProcessId))
            .ToArray();
        HashSet<ProcessIdentity> identities = nodes
            .Select(node => node.Identity)
            .ToHashSet();

        return new ProcessGraph(
            nodes.Select(node => node.Process),
            Edges.Where(edge => identities.Contains(edge.Source)
                && identities.Contains(edge.Target)));
    }

    /// <summary>Derives the strict, generation-validated OS-parent view.</summary>
    public ProcessTree CreateProcessTree()
    {
        return ProcessTreeBuilder.Build(Nodes.Select(node => node.Process));
    }

    private static Dictionary<ProcessIdentity, IReadOnlyList<ProcessGraphEdge>> CreateEdgeIndex(
        IEnumerable<ProcessGraphEdge> edges,
        Func<ProcessGraphEdge, ProcessIdentity> identitySelector)
    {
        return edges
            .GroupBy(identitySelector)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ProcessGraphEdge>)group.ToArray());
    }
}
