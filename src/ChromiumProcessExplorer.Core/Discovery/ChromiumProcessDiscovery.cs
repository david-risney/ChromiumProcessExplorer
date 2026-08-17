namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Coordinates reusable process and Mojo discovery.</summary>
public sealed class ChromiumProcessDiscovery
{
    private readonly IProcessSnapshotProvider _processSnapshotter;
    private readonly IMojoPipeProvider _mojoPipeEnumerator;
    private readonly IInstallationProvider _installationProvider;
    private readonly ICdpEndpointProvider _cdpEndpointProvider;
    private readonly IWindowSnapshotProvider _windowSnapshotProvider;

    /// <summary>Creates a discovery service using the built-in Windows providers.</summary>
    public ChromiumProcessDiscovery()
        : this(
            new WindowsProcessSnapshotter(),
            new WindowsMojoPipeEnumerator(),
            new WindowsInstallationProvider(),
            new CdpEndpointProvider(),
            new WindowsWindowSnapshotProvider())
    {
    }

    /// <summary>Creates a discovery service using custom providers.</summary>
    public ChromiumProcessDiscovery(
        IProcessSnapshotProvider processSnapshotter,
        IMojoPipeProvider mojoPipeEnumerator)
        : this(
            processSnapshotter,
            mojoPipeEnumerator,
            new WindowsInstallationProvider(),
            new CdpEndpointProvider(),
            new WindowsWindowSnapshotProvider())
    {
    }

    /// <summary>Creates a discovery service using custom providers.</summary>
    public ChromiumProcessDiscovery(
        IProcessSnapshotProvider processSnapshotter,
        IMojoPipeProvider mojoPipeEnumerator,
        IInstallationProvider installationProvider)
        : this(
            processSnapshotter,
            mojoPipeEnumerator,
            installationProvider,
            new CdpEndpointProvider(),
            new WindowsWindowSnapshotProvider())
    {
    }

    /// <summary>Creates a discovery service using custom providers.</summary>
    public ChromiumProcessDiscovery(
        IProcessSnapshotProvider processSnapshotter,
        IMojoPipeProvider mojoPipeEnumerator,
        IInstallationProvider installationProvider,
        ICdpEndpointProvider cdpEndpointProvider)
        : this(
            processSnapshotter,
            mojoPipeEnumerator,
            installationProvider,
            cdpEndpointProvider,
            new WindowsWindowSnapshotProvider())
    {
    }

    /// <summary>Creates a discovery service using custom providers.</summary>
    public ChromiumProcessDiscovery(
        IProcessSnapshotProvider processSnapshotter,
        IMojoPipeProvider mojoPipeEnumerator,
        IInstallationProvider installationProvider,
        ICdpEndpointProvider cdpEndpointProvider,
        IWindowSnapshotProvider windowSnapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(processSnapshotter);
        ArgumentNullException.ThrowIfNull(mojoPipeEnumerator);
        ArgumentNullException.ThrowIfNull(installationProvider);
        ArgumentNullException.ThrowIfNull(cdpEndpointProvider);
        ArgumentNullException.ThrowIfNull(windowSnapshotProvider);

        _processSnapshotter = processSnapshotter;
        _mojoPipeEnumerator = mojoPipeEnumerator;
        _installationProvider = installationProvider;
        _cdpEndpointProvider = cdpEndpointProvider;
        _windowSnapshotProvider = windowSnapshotProvider;
    }

    /// <summary>
    /// Captures processes and performs endpoint-enriched Mojo discovery.
    /// </summary>
    public async ValueTask<ChromiumDiscoveryResult> DiscoverAsync(
        HandleQueryWorkerOptions workerOptions,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        return await DiscoverAsync(
            workerOptions,
            maximumProcessConcurrency,
            false,
            cancellationToken);
    }

    /// <summary>
    /// Captures processes and optionally includes native window topology.
    /// </summary>
    public async ValueTask<ChromiumDiscoveryResult> DiscoverAsync(
        HandleQueryWorkerOptions workerOptions,
        int? maximumProcessConcurrency,
        bool includeWindowEvidence,
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
        CefRuntimeAnalysis cefRuntime = CefRuntimeAdapter.Analyze(processes);
        MojoPipeInspectionResult inspection = await InspectMojoPipesAsync(
            pipeResult,
            processes,
            workerOptions,
            cancellationToken);
        WindowSnapshotResult windowSnapshot = includeWindowEvidence
            ? await _windowSnapshotProvider.CaptureAsync(
                processes,
                cancellationToken)
            : WindowSnapshotResult.Empty;
        WebView2RuntimeAnalysis webView2Runtime = WebView2RuntimeAdapter.Analyze(
            processes,
            inspection,
            windowSnapshot);
        ProcessGraph graph = ProcessGraphBuilder.Build(
            processes,
            inspection,
            capturedAt,
            cefRuntime,
            webView2Runtime);
        ProcessTree tree = graph.CreateProcessTree();
        CdpDiscoveryResult cdp = await _cdpEndpointProvider.DiscoverAsync(
            processes,
            workerOptions,
            cancellationToken);

        return new ChromiumDiscoveryResult(
            capturedAt,
            processes,
            graph,
            tree,
            inspection,
            inspection.Issues.Concat(webView2Runtime.Issues).ToArray())
        {
            CefRuntime = cefRuntime,
            WebView2Runtime = webView2Runtime,
            Cdp = cdp,
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

    /// <summary>Discovers configured and validated CDP transports.</summary>
    public async ValueTask<CdpDiscoveryResult> DiscoverCdpAsync(
        HandleQueryWorkerOptions workerOptions,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);

        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        return await _cdpEndpointProvider.DiscoverAsync(
            processes,
            workerOptions,
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
    ProcessGraph ProcessGraph,
    ProcessTree ProcessTree,
    MojoPipeInspectionResult MojoPipeInspection,
    IReadOnlyList<DiscoveryIssue> Issues)
{
    /// <summary>Gets CEF-specific process and runtime analysis.</summary>
    public CefRuntimeAnalysis CefRuntime { get; init; } = CefRuntimeAnalysis.Empty;

    /// <summary>Gets WebView2-specific process and host analysis.</summary>
    public WebView2RuntimeAnalysis WebView2Runtime { get; init; } =
        WebView2RuntimeAnalysis.Empty;

    /// <summary>Gets configured and validated CDP transports.</summary>
    public CdpDiscoveryResult Cdp { get; init; } = new(
        DateTimeOffset.MinValue,
        []);
}
