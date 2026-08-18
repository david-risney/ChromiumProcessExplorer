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

    [Fact]
    public void BuildAddsCefSubprocessAndHostEdges()
    {
        ProcessSnapshotEntry host = CreateProcess(20, 0, SnapshotTime);
        ProcessSnapshotEntry browser = CreateProcess(
            21,
            20,
            SnapshotTime.AddSeconds(1));
        ProcessSnapshotEntry renderer = CreateProcess(
            22,
            21,
            SnapshotTime.AddSeconds(2));
        CefRuntimeAnalysis cef = new(
            [],
            [
                new CefProcessAssociation(
                    21,
                    22,
                    85,
                    CefAssociationConfidence.High,
                    true,
                    ["Generation-safe parent process relationship."]),
            ],
            [
                new CefHostAssociation(
                    20,
                    21,
                    100,
                    CefAssociationConfidence.High,
                    true,
                    ["The browser command line references the host."]),
            ]);

        ProcessGraph graph = ProcessGraphBuilder.Build(
            [host, browser, renderer],
            CreateInspection(),
            SnapshotTime,
            cef);

        ProcessGraphEdge subprocess = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.ChromiumSubprocess);
        ProcessGraphEdge embedded = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.EmbeddedBy);

        Assert.Equal((21, 22), (
            subprocess.Source.ProcessId,
            subprocess.Target.ProcessId));
        Assert.Equal((20, 21), (
            embedded.Source.ProcessId,
            embedded.Target.ProcessId));
        Assert.Equal("cef-runtime-adapter", embedded.Evidence.Source);
        Assert.Equal("True", embedded.Evidence.RawValues["isAuthoritative"]);
    }

    [Fact]
    public void BuildAddsWebView2HostEdgeWithoutChangingOsParentTree()
    {
        ProcessSnapshotEntry host = CreateProcess(20, 0, SnapshotTime);
        ProcessSnapshotEntry browser = CreateProcess(21, 0, SnapshotTime.AddSeconds(1));
        WebView2RuntimeAnalysis webView2 = new(
            [],
            [
                new WebView2HostAssociation(
                    20,
                    21,
                    90,
                    ProcessRelationshipConfidence.High,
                    true,
                    [new WebView2Evidence("window-property", "Observed HWND link.")]),
            ],
            WindowSnapshotResult.Empty,
            []);

        ProcessGraph graph = ProcessGraphBuilder.Build(
            [host, browser],
            CreateInspection(),
            SnapshotTime,
            webView2Runtime: webView2);

        Assert.Equal(2, graph.Edges.Count);
        ProcessGraphEdge embedded = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.EmbeddedBy);
        ProcessGraphEdge window = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.CrossProcessWindow);
        Assert.Equal(ProcessRelationshipType.EmbeddedBy, embedded.Type);
        Assert.Equal((20, 21), (
            embedded.Source.ProcessId,
            embedded.Target.ProcessId));
        Assert.Equal("webview2-runtime-adapter", embedded.Evidence.Source);
        Assert.Equal("windows-window-snapshot", window.Evidence.Source);
        Assert.Equal(2, graph.CreateProcessTree().Roots.Count);
    }

    [Fact]
    public void BuildAddsElectronSubprocessEdge()
    {
        ProcessSnapshotEntry main = CreateProcess(20, 0, SnapshotTime);
        ProcessSnapshotEntry renderer = CreateProcess(
            21,
            20,
            SnapshotTime.AddSeconds(1));
        ElectronRuntimeAnalysis electron = new(
            [],
            [
                new ElectronProcessAssociation(
                    20,
                    21,
                    100,
                    ProcessRelationshipConfidence.High,
                    true,
                    [new ElectronEvidence("process-snapshot", "Validated parent.")]),
            ],
            []);

        ProcessGraph graph = ProcessGraphBuilder.Build(
            [main, renderer],
            CreateInspection(),
            SnapshotTime,
            electronRuntime: electron);

        ProcessGraphEdge electronEdge = Assert.Single(
            graph.Edges,
            edge => edge.Type == ProcessRelationshipType.ChromiumSubprocess);
        Assert.Equal((20, 21), (
            electronEdge.Source.ProcessId,
            electronEdge.Target.ProcessId));
        Assert.Equal("electron-runtime-adapter", electronEdge.Evidence.Source);
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
