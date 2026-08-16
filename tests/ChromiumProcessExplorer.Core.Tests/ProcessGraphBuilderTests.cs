using System.Text.Json;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ProcessGraphBuilderTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildConnectsMultipleHostsToOneBrowserWithoutDuplicatingNodes()
    {
        ProcessSnapshotEntry browser = CreateProcess(100, 0, SnapshotTime);
        ProcessSnapshotEntry firstHost = CreateProcess(200, 0, SnapshotTime.AddSeconds(1));
        ProcessSnapshotEntry secondHost = CreateProcess(300, 0, SnapshotTime.AddSeconds(2));
        MojoPipeInspectionResult inspection = CreateInspection(
            new MojoPipeInfo(
                "mojo.100.1.1",
                100,
                [
                    CreateConnection(100, 200, 200),
                    CreateConnection(100, 300, 300),
                ]));

        ProcessGraph graph = ProcessGraphBuilder.Build(
            [browser, firstHost, secondHost],
            inspection,
            SnapshotTime);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(3, graph.Nodes.Select(node => node.Identity).Distinct().Count());

        ProcessGraphNode browserNode = Assert.IsType<ProcessGraphNode>(graph.FindNode(100));
        ProcessGraphEdge[] outgoing = graph.GetOutgoingEdges(browserNode.Identity)
            .Where(edge => edge.Type == ProcessRelationshipType.MojoConnection)
            .ToArray();

        Assert.Equal(2, outgoing.Length);
        Assert.Equal([200, 300], outgoing.Select(edge => edge.Target.ProcessId).Order());
        Assert.All(
            outgoing,
            edge => Assert.Equal(
                "mojo-endpoint-inspection",
                edge.Evidence.Source));
    }

    [Fact]
    public void BuildRetainsConflictingOsParentAndMojoEvidence()
    {
        ProcessSnapshotEntry host = CreateProcess(10, 0, SnapshotTime);
        ProcessSnapshotEntry browser = CreateProcess(20, 10, SnapshotTime.AddSeconds(1));
        MojoPipeInspectionResult inspection = CreateInspection(
            new MojoPipeInfo(
                "mojo.20.1.1",
                20,
                [CreateConnection(20, 10, 10)]));

        ProcessGraph graph = ProcessGraphBuilder.Build(
            [host, browser],
            inspection,
            SnapshotTime);

        ProcessGraphEdge osParent = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.OsParent);
        ProcessGraphEdge mojo = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.MojoConnection);

        Assert.Equal((10, 20), (osParent.Source.ProcessId, osParent.Target.ProcessId));
        Assert.Equal((20, 10), (mojo.Source.ProcessId, mojo.Target.ProcessId));
        Assert.Equal(SnapshotTime, osParent.Evidence.ObservedAt);
        Assert.Equal(inspection.CapturedAt, mojo.Evidence.ObservedAt);
        Assert.Equal(ProcessRelationshipConfidence.High, mojo.Evidence.Confidence);
        Assert.Equal("mojo.20.1.1", mojo.Evidence.RawValues["pipeName"]);
        Assert.Equal("connected", mojo.Evidence.RawValues["state"]);

        string json = JsonSerializer.Serialize(graph);

        Assert.Contains("\"Type\":\"OsParent\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Type\":\"MojoConnection\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsEdgesForReusedProcessIds()
    {
        ProcessSnapshotEntry child = CreateProcess(11, 10, SnapshotTime);
        ProcessSnapshotEntry newerParent = CreateProcess(
            10,
            0,
            SnapshotTime.AddSeconds(1));
        ProcessSnapshotEntry reusedClient = CreateProcess(
            12,
            0,
            SnapshotTime,
            "The process ID was reused after the system snapshot was captured.");
        MojoPipeInspectionResult inspection = CreateInspection(
            new MojoPipeInfo(
                "mojo.11.1.1",
                11,
                [CreateConnection(11, 12, 12)]));

        ProcessGraph graph = ProcessGraphBuilder.Build(
            [child, newerParent, reusedClient],
            inspection,
            SnapshotTime);
        ProcessTree tree = graph.CreateProcessTree();

        Assert.Empty(graph.Edges);
        Assert.Null(tree.NodesByProcessId[11].Parent);
        Assert.Equal(3, tree.Roots.Count);
    }

    private static NamedPipeConnection CreateConnection(
        int serverProcessId,
        int clientProcessId,
        int handleOwnerProcessId)
    {
        return new NamedPipeConnection(
            handleOwnerProcessId,
            $"process-{handleOwnerProcessId}.exe",
            serverProcessId,
            $"process-{serverProcessId}.exe",
            clientProcessId,
            $"process-{clientProcessId}.exe",
            "client",
            "connected");
    }

    private static MojoPipeInspectionResult CreateInspection(params MojoPipeInfo[] pipes)
    {
        return new MojoPipeInspectionResult(
            SnapshotTime.AddSeconds(5),
            pipes,
            new NamedPipeInspectionStatistics(
                pipes.Length,
                0,
                0,
                0,
                0,
                0,
                pipes.Sum(pipe => pipe.Connections.Count),
                0,
                0,
                TimeSpan.Zero),
            [],
            []);
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        int parentProcessId,
        DateTimeOffset creationTime,
        string? metadataError = null)
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
            metadataError);
    }
}
