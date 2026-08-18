using System.Text.Json;
using ChromiumProcessExplorer.Core.Broker;
using ChromiumProcessExplorer.Core.Discovery;
using ChromiumProcessExplorer.Gui;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class GuiViewModelTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 18, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshDisplaysLogicalParentsWithoutDuplicatingNodesAndRetainsStaleRows()
    {
        ProcessSnapshotEntry browser = CreateProcess(100, "browser.exe", true);
        ProcessSnapshotEntry firstHost = CreateProcess(
            200,
            "host-one.exe",
            false) with
        {
            ParentProcessId = 100,
        };
        ProcessSnapshotEntry secondHost = CreateProcess(300, "host-two.exe", false);
        ProcessGraph graph = new(
            [browser, firstHost, secondHost],
            [
                CreateEdge(browser, firstHost),
                CreateEdge(browser, secondHost),
            ]);
        Queue<ChromiumDiscoveryResult> results = new(
            [
                CreateDiscoveryResult(
                    [browser, firstHost, secondHost],
                    graph),
                CreateDiscoveryResult([], new ProcessGraph([], [])),
            ]);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(results.Dequeue()),
        };
        MainViewModel viewModel = new(discovery);

        await viewModel.RefreshProcessesAsync();

        Assert.Equal(3, viewModel.Processes.Count);
        Assert.Equal(
            3,
            viewModel.Processes.Select(process => process.Identity).Distinct().Count());
        Assert.Equal(2, viewModel.Relationships.Count(
            relationship => relationship.Relationship == "MojoConnection"));
        Assert.All(
            viewModel.Relationships,
            relationship => Assert.Equal(
                "Logical/evidence",
                relationship.EdgeClass));
        using JsonDocument export = JsonDocument.Parse(
            viewModel.CreateJsonExport());
        Assert.Equal(
            3,
            export.RootElement
                .GetProperty("Processes")
                .GetProperty("ProcessGraph")
                .GetProperty("Nodes")
                .GetArrayLength());

        await viewModel.RefreshProcessesAsync();

        Assert.Equal(3, viewModel.Processes.Count);
        Assert.All(viewModel.Processes, process => Assert.True(process.IsStale));
    }

    [Fact]
    public async Task CancellationPreservesResponsiveState()
    {
        TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        MainViewModel viewModel = new(discovery);

        Task refresh = viewModel.RefreshProcessesAsync();
        await started.Task;
        Assert.True(viewModel.IsBusy);

        viewModel.Cancel();
        await refresh;

        Assert.False(viewModel.IsBusy);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectionLoadsRedactedProcessDetails()
    {
        ProcessSnapshotEntry process = CreateProcess(123, "sample.exe", true);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
            DiscoverDetails = (processId, _) =>
            {
                Assert.Equal(123, processId);
                return ValueTask.FromResult(CreateDetails(process));
            },
        };
        MainViewModel viewModel = new(discovery);
        await viewModel.RefreshProcessesAsync();
        viewModel.SelectedProcess = Assert.Single(viewModel.Processes);

        await viewModel.LoadSelectedProcessDetailsAsync();

        using JsonDocument details = JsonDocument.Parse(
            viewModel.SelectedProcessDetails);
        Assert.False(details.RootElement
            .GetProperty("IncludesSensitiveValues")
            .GetBoolean());
        Assert.Equal(
            123,
            details.RootElement
                .GetProperty("Processes")[0]
                .GetProperty("Identity")
                .GetProperty("ProcessId")
                .GetInt32());
    }

    [Fact]
    public async Task RefreshFailureIsVisibleWithoutThrowing()
    {
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => throw new InvalidOperationException(
                "Synthetic discovery failure."),
        };
        MainViewModel viewModel = new(discovery);

        await viewModel.RefreshProcessesAsync();

        IssueRow issue = Assert.Single(viewModel.Issues);
        Assert.Equal("gui", issue.Source);
        Assert.Contains("Synthetic discovery failure", issue.Message);
        Assert.Contains("failed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static ChromiumDiscoveryResult CreateDiscoveryResult(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        ProcessGraph graph)
    {
        return new ChromiumDiscoveryResult(
            SnapshotTime,
            processes,
            graph,
            graph.CreateProcessTree(),
            new MojoPipeInspectionResult(
                SnapshotTime,
                [],
                new NamedPipeInspectionStatistics(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                [],
                []),
            []);
    }

    private static ProcessDetailsResult CreateDetails(
        ProcessSnapshotEntry process)
    {
        SensitiveStringValue redacted = new(
            null,
            true,
            "test");
        return new ProcessDetailsResult(
            "1.0",
            SnapshotTime,
            false,
            [
                new ProcessDetailEntry(
                    new ProcessIdentity(process.ProcessId, process.CreationTime),
                    process.ParentProcessId,
                    process.ImageName,
                    redacted,
                    redacted,
                    [],
                    "browser",
                    "test",
                    redacted,
                    null,
                    "x64",
                    "x64",
                    "Medium",
                    false,
                    null,
                    [],
                    [],
                    []),
            ],
            []);
    }

    private static ProcessGraphEdge CreateEdge(
        ProcessSnapshotEntry source,
        ProcessSnapshotEntry target)
    {
        return new ProcessGraphEdge(
            new ProcessIdentity(source.ProcessId, source.CreationTime),
            new ProcessIdentity(target.ProcessId, target.CreationTime),
            ProcessRelationshipType.MojoConnection,
            new ProcessRelationshipEvidence(
                "test",
                ProcessRelationshipConfidence.High,
                SnapshotTime,
                new Dictionary<string, string?>
                {
                    ["pipeName"] = "mojo.test",
                }));
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        string imageName,
        bool isLikelyChromium)
    {
        return new ProcessSnapshotEntry(
            processId,
            0,
            SnapshotTime.AddSeconds(processId),
            imageName,
            null,
            null,
            isLikelyChromium ? "browser" : null,
            null,
            isLikelyChromium,
            [],
            null);
    }

    private sealed class StubGuiDiscoveryService : IGuiDiscoveryService
    {
        public Func<CancellationToken, ValueTask<ChromiumDiscoveryResult>>
            DiscoverProcesses
        { get; init; } =
            _ => ValueTask.FromResult(
                CreateDiscoveryResult([], new ProcessGraph([], [])));

        public Func<int, CancellationToken, ValueTask<ProcessDetailsResult>>
            DiscoverDetails
        { get; init; } =
            (_, _) => ValueTask.FromResult(
                new ProcessDetailsResult("1.0", SnapshotTime, false, [], []));

        public ValueTask<ChromiumDiscoveryResult> DiscoverProcessesAsync(
            CancellationToken cancellationToken)
        {
            return DiscoverProcesses(cancellationToken);
        }

        public ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            return DiscoverDetails(processId, cancellationToken);
        }

        public ValueTask<InstallationDiscoveryResult> DiscoverInstallationsAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new InstallationDiscoveryResult(
                SnapshotTime,
                [],
                new InstallationDiscoveryStatistics(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                []));
        }

        public ValueTask<BrokerResponse> ProbeBrokerAsync(
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new BrokerResponse(
                BrokerMessageCodec.Version,
                Guid.NewGuid(),
                false,
                true,
                null,
                new BrokerError(
                    "broker_not_running",
                    "The broker is not running.")));
        }
    }
}
