namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Coordinates reusable process and Mojo discovery.</summary>
public sealed class ChromiumProcessDiscovery
{
    private readonly IProcessSnapshotProvider _processSnapshotter;
    private readonly IMojoPipeProvider _mojoPipeEnumerator;
    private readonly IInstallationProvider _installationProvider;

    /// <summary>Creates a discovery service using the built-in Windows providers.</summary>
    public ChromiumProcessDiscovery()
        : this(
            new WindowsProcessSnapshotter(),
            new WindowsMojoPipeEnumerator(),
            new WindowsInstallationProvider())
    {
    }

    /// <summary>Creates a discovery service using custom providers.</summary>
    public ChromiumProcessDiscovery(
        IProcessSnapshotProvider processSnapshotter,
        IMojoPipeProvider mojoPipeEnumerator)
        : this(
            processSnapshotter,
            mojoPipeEnumerator,
            new WindowsInstallationProvider())
    {
    }

    /// <summary>Creates a discovery service using custom providers.</summary>
    public ChromiumProcessDiscovery(
        IProcessSnapshotProvider processSnapshotter,
        IMojoPipeProvider mojoPipeEnumerator,
        IInstallationProvider installationProvider)
    {
        ArgumentNullException.ThrowIfNull(processSnapshotter);
        ArgumentNullException.ThrowIfNull(mojoPipeEnumerator);
        ArgumentNullException.ThrowIfNull(installationProvider);

        _processSnapshotter = processSnapshotter;
        _mojoPipeEnumerator = mojoPipeEnumerator;
        _installationProvider = installationProvider;
    }

    /// <summary>
    /// Captures processes and performs endpoint-enriched Mojo discovery.
    /// </summary>
    public async ValueTask<ChromiumDiscoveryResult> DiscoverAsync(
        HandleQueryWorkerOptions workerOptions,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        ValueTask<IReadOnlyList<ProcessSnapshotEntry>> processTask =
            _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        ValueTask<MojoPipeEnumerationResult> pipeTask =
            _mojoPipeEnumerator.EnumerateAsync(cancellationToken);

        IReadOnlyList<ProcessSnapshotEntry> processes = await processTask;
        MojoPipeEnumerationResult pipeResult = await pipeTask;
        ProcessTree tree = ProcessTreeBuilder.Build(processes);
        CefRuntimeAnalysis cefRuntime = CefRuntimeAdapter.Analyze(processes);
        MojoPipeInspectionResult inspection = await InspectMojoPipesAsync(
            pipeResult,
            processes,
            workerOptions,
            cancellationToken);

        return new ChromiumDiscoveryResult(
            capturedAt,
            processes,
            tree,
            inspection,
            inspection.Issues)
        {
            CefRuntime = cefRuntime,
        };
    }

    /// <summary>Enumerates Mojo pipes without performing process discovery.</summary>
    public ValueTask<MojoPipeEnumerationResult> EnumerateMojoPipesAsync(
        CancellationToken cancellationToken = default)
    {
        return _mojoPipeEnumerator.EnumerateAsync(cancellationToken);
    }

    /// <summary>
    /// Discovers installed Chromium browsers, WebView2 runtimes, and
    /// Chromium-based applications.
    /// </summary>
    public async ValueTask<InstallationDiscoveryResult> DiscoverInstallationsAsync(
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        return await _installationProvider.DiscoverAsync(
            processes,
            cancellationToken);
    }

    /// <summary>
    /// Enumerates Mojo pipes and inspects existing foreign handles for endpoint
    /// process information using isolated helper processes.
    /// </summary>
    public async ValueTask<MojoPipeInspectionResult> InspectMojoPipesAsync(
        HandleQueryWorkerOptions workerOptions,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        MojoPipeEnumerationResult pipeResult =
            await _mojoPipeEnumerator.EnumerateAsync(cancellationToken);
        IReadOnlyList<ProcessSnapshotEntry> processes;

        try
        {
            processes = await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            DiscoveryIssue processIssue =
                new("process-snapshot", exception.Message);
            MojoPipeInfo[] pipes = pipeResult.Pipes
                .Select(pipe => new MojoPipeInfo(pipe.Name, pipe.ProcessIdHint, []))
                .ToArray();
            return new MojoPipeInspectionResult(
                capturedAt,
                pipes,
                new NamedPipeInspectionStatistics(
                    pipes.Length,
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
                pipeResult.Issues.Append(processIssue).ToArray());
        }

        return await InspectMojoPipesAsync(
            pipeResult,
            processes,
            workerOptions,
            cancellationToken);
    }

    private static async ValueTask<MojoPipeInspectionResult> InspectMojoPipesAsync(
        MojoPipeEnumerationResult pipeResult,
        IReadOnlyList<ProcessSnapshotEntry> processes,
        HandleQueryWorkerOptions workerOptions,
        CancellationToken cancellationToken)
    {
        MojoPipeInspectionResult inspection =
            await WindowsNamedPipeEndpointInspector.InspectAsync(
                pipeResult.Pipes,
                processes,
                workerOptions,
                cancellationToken);
        return inspection with
        {
            Issues = pipeResult.Issues.Concat(inspection.Issues).ToArray(),
        };
    }
}

/// <summary>A complete discovery snapshot suitable for CLI, GUI, or API use.</summary>
public sealed record ChromiumDiscoveryResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessSnapshotEntry> Processes,
    ProcessTree ProcessTree,
    MojoPipeInspectionResult MojoPipeInspection,
    IReadOnlyList<DiscoveryIssue> Issues)
{
    /// <summary>Gets CEF-specific process and runtime analysis.</summary>
    public CefRuntimeAnalysis CefRuntime { get; init; } = CefRuntimeAnalysis.Empty;
}
