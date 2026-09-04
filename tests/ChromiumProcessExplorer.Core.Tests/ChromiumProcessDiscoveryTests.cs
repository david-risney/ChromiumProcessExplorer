using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ChromiumProcessDiscoveryTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IncrementalDiscoveryReturnsPreviousResultWhenProcessesMatch()
    {
        ProcessSnapshotEntry existing = CreateProcess(100);
        ChromiumDiscoveryResult previous = CreateResult([existing]);
        StubSnapshotProvider snapshots = new([existing]);
        StubMojoPipeProvider pipes = new();
        ChromiumProcessDiscovery discovery = CreateDiscovery(
            snapshots,
            pipes,
            new StubCdpEndpointProvider(),
            new StubWindowSnapshotProvider());

        ChromiumDiscoveryResult result =
            await discovery.DiscoverIncrementalAsync(
                previous,
                new HandleQueryWorkerOptions("unused.exe"),
                includeWindowEvidence: true);

        Assert.Same(previous, result);
        Assert.Equal(0, pipes.CallCount);
    }

    [Fact]
    public async Task IncrementalDiscoveryInspectsOnlyNewProcessGenerations()
    {
        ProcessSnapshotEntry existing = CreateProcess(100);
        ProcessSnapshotEntry added = CreateProcess(200);
        ChromiumDiscoveryResult previous = CreateResult([existing]);
        StubSnapshotProvider snapshots = new([existing, added]);
        StubCdpEndpointProvider cdp = new();
        StubWindowSnapshotProvider windows = new();
        ChromiumProcessDiscovery discovery = CreateDiscovery(
            snapshots,
            new StubMojoPipeProvider(),
            cdp,
            windows);

        ChromiumDiscoveryResult result =
            await discovery.DiscoverIncrementalAsync(
                previous,
                new HandleQueryWorkerOptions("unused.exe"),
                includeWindowEvidence: true);

        Assert.Same(
            existing,
            Assert.Single(
                result.Processes,
                process => process.ProcessId == existing.ProcessId));
        Assert.Equal([added.ProcessId], cdp.ProcessIds);
        Assert.Equal([added.ProcessId], windows.ProcessIds);
    }

    [Fact]
    public async Task IncrementalDiscoveryDoesNotInspectWhenProcessesOnlyExit()
    {
        ProcessSnapshotEntry existing = CreateProcess(100);
        ChromiumDiscoveryResult previous = CreateResult([existing]);
        StubMojoPipeProvider pipes = new();
        StubCdpEndpointProvider cdp = new();
        StubWindowSnapshotProvider windows = new();
        ChromiumProcessDiscovery discovery = CreateDiscovery(
            new StubSnapshotProvider([]),
            pipes,
            cdp,
            windows);

        ChromiumDiscoveryResult result =
            await discovery.DiscoverIncrementalAsync(
                previous,
                new HandleQueryWorkerOptions("unused.exe"),
                includeWindowEvidence: true);

        Assert.Empty(result.Processes);
        Assert.Equal(1, pipes.CallCount);
        Assert.Equal(0, cdp.CallCount);
        Assert.Equal(0, windows.CallCount);
    }

    private static ChromiumProcessDiscovery CreateDiscovery(
        IProcessSnapshotProvider snapshots,
        IMojoPipeProvider pipes,
        ICdpEndpointProvider cdp,
        IWindowSnapshotProvider windows)
    {
        return new ChromiumProcessDiscovery(
            snapshots,
            pipes,
            new StubInstallationProvider(),
            cdp,
            windows);
    }

    private static ChromiumDiscoveryResult CreateResult(
        IReadOnlyList<ProcessSnapshotEntry> processes)
    {
        ProcessGraph graph = new(processes, []);
        return new ChromiumDiscoveryResult(
            SnapshotTime,
            processes,
            graph,
            graph.CreateProcessTree(),
            EmptyInspection(),
            []);
    }

    private static MojoPipeInspectionResult EmptyInspection()
    {
        return new MojoPipeInspectionResult(
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
            []);
    }

    private static ProcessSnapshotEntry CreateProcess(int processId)
    {
        return new ProcessSnapshotEntry(
            processId,
            0,
            SnapshotTime.AddSeconds(processId),
            $"process-{processId}.exe",
            $@"C:\Apps\process-{processId}.exe",
            $@"""C:\Apps\process-{processId}.exe""",
            "browser",
            null,
            true,
            [],
            null);
    }

    private sealed class StubSnapshotProvider(
        IReadOnlyList<ProcessSnapshotEntry> processes)
        : IProcessSnapshotProvider
    {
        public ValueTask<IReadOnlyList<ProcessSnapshotEntry>> CaptureAsync(
            int? maximumConcurrency = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Incremental discovery must not request a full snapshot.");
        }

        public ValueTask<IReadOnlyList<ProcessSnapshotEntry>>
            CaptureIncrementalAsync(
            IReadOnlyList<ProcessSnapshotEntry> previousProcesses,
            int? maximumConcurrency = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(processes);
        }
    }

    private sealed class StubMojoPipeProvider : IMojoPipeProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<MojoPipeEnumerationResult> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                new MojoPipeEnumerationResult([], []));
        }
    }

    private sealed class StubInstallationProvider : IInstallationProvider
    {
        public ValueTask<InstallationDiscoveryResult> DiscoverAsync(
            IReadOnlyList<ProcessSnapshotEntry> runningProcesses,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Process discovery must not scan installations.");
        }
    }

    private sealed class StubCdpEndpointProvider : ICdpEndpointProvider
    {
        public int[] ProcessIds { get; private set; } = [];

        public int CallCount { get; private set; }

        public ValueTask<CdpDiscoveryResult> DiscoverAsync(
            IReadOnlyList<ProcessSnapshotEntry> processes,
            HandleQueryWorkerOptions? workerOptions = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ProcessIds = processes.Select(process => process.ProcessId).ToArray();
            return ValueTask.FromResult(
                new CdpDiscoveryResult(SnapshotTime, []));
        }
    }

    private sealed class StubWindowSnapshotProvider : IWindowSnapshotProvider
    {
        public int[] ProcessIds { get; private set; } = [];

        public int CallCount { get; private set; }

        public ValueTask<WindowSnapshotResult> CaptureAsync(
            IReadOnlyList<ProcessSnapshotEntry> processes,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ProcessIds = processes.Select(process => process.ProcessId).ToArray();
            return ValueTask.FromResult(WindowSnapshotResult.Empty);
        }
    }
}
