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
        ElectronRuntimeAnalysis electronRuntime =
            ElectronRuntimeAdapter.Analyze(processes);
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
        AdditionalRuntimeAnalysis additionalRuntime =
            AdditionalRuntimeAdapter.Analyze(
                processes,
                cefRuntime,
                electronRuntime,
                webView2Runtime);
        ProcessGraph graph = ProcessGraphBuilder.Build(
            processes,
            inspection,
            capturedAt,
            cefRuntime,
            webView2Runtime,
            electronRuntime,
            additionalRuntime);
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
            inspection.Issues
                .Concat(webView2Runtime.Issues)
                .Concat(additionalRuntime.Issues)
                .ToArray())
        {
            CefRuntime = cefRuntime,
            WebView2Runtime = webView2Runtime,
            ElectronRuntime = electronRuntime,
            AdditionalRuntime = additionalRuntime,
            Cdp = cdp,
        };
    }

    /// <summary>
    /// Refreshes process discovery by reusing unchanged process generations and
    /// inspecting only newly observed processes.
    /// </summary>
    public async ValueTask<ChromiumDiscoveryResult> DiscoverIncrementalAsync(
        ChromiumDiscoveryResult previous,
        HandleQueryWorkerOptions workerOptions,
        bool includeWindowEvidence,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(workerOptions);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureIncrementalAsync(
                previous.Processes,
                maximumProcessConcurrency,
                cancellationToken);
        Dictionary<int, ProcessSnapshotEntry> previousById =
            previous.Processes.ToDictionary(process => process.ProcessId);
        Dictionary<int, ProcessSnapshotEntry> currentById =
            processes.ToDictionary(process => process.ProcessId);
        ProcessSnapshotEntry[] newProcesses = processes
            .Where(process =>
                !previousById.TryGetValue(
                    process.ProcessId,
                    out ProcessSnapshotEntry? old)
                || old.CreationTime is null
                || process.CreationTime is null
                || old.CreationTime != process.CreationTime)
            .ToArray();
        bool processSetChanged = newProcesses.Length > 0
            || previous.Processes.Any(process =>
                !currentById.TryGetValue(
                    process.ProcessId,
                    out ProcessSnapshotEntry? current)
                || process.CreationTime is null
                || current.CreationTime is null
                || process.CreationTime != current.CreationTime);
        if (!processSetChanged)
        {
            return previous;
        }

        MojoPipeEnumerationResult pipeResult =
            await _mojoPipeEnumerator.EnumerateAsync(cancellationToken);
        MojoPipeInspectionResult newInspection = newProcesses.Length == 0
            ? CreateUninspectedMojoResult(pipeResult, capturedAt)
            : await InspectMojoPipesAsync(
                pipeResult,
                newProcesses,
                workerOptions,
                cancellationToken);
        MojoPipeInspectionResult inspection = MergeMojoInspection(
            previous.MojoPipeInspection,
            newInspection,
            pipeResult,
            previousById,
            currentById);

        WindowSnapshotResult newWindows = includeWindowEvidence
            && newProcesses.Length > 0
            ? await _windowSnapshotProvider.CaptureAsync(
                newProcesses,
                cancellationToken)
            : WindowSnapshotResult.Empty;
        WindowSnapshotResult windows = MergeWindowSnapshot(
            previous.WebView2Runtime.WindowSnapshot,
            newWindows,
            previousById,
            currentById);

        CdpDiscoveryResult newCdp = newProcesses.Length == 0
            ? new CdpDiscoveryResult(capturedAt, [])
            : await _cdpEndpointProvider.DiscoverAsync(
                newProcesses,
                workerOptions,
                cancellationToken);
        CdpDiscoveryResult cdp = MergeCdpDiscovery(
            previous.Cdp,
            newCdp,
            previousById,
            currentById);

        CefRuntimeAnalysis cefRuntime = CefRuntimeAdapter.Analyze(processes);
        ElectronRuntimeAnalysis electronRuntime =
            ElectronRuntimeAdapter.Analyze(processes);
        WebView2RuntimeAnalysis webView2Runtime = WebView2RuntimeAdapter.Analyze(
            processes,
            inspection,
            windows);
        AdditionalRuntimeAnalysis additionalRuntime =
            AdditionalRuntimeAdapter.Analyze(
                processes,
                cefRuntime,
                electronRuntime,
                webView2Runtime);
        ProcessGraph graph = ProcessGraphBuilder.Build(
            processes,
            inspection,
            capturedAt,
            cefRuntime,
            webView2Runtime,
            electronRuntime,
            additionalRuntime);
        return new ChromiumDiscoveryResult(
            capturedAt,
            processes,
            graph,
            graph.CreateProcessTree(),
            inspection,
            inspection.Issues
                .Concat(webView2Runtime.Issues)
                .Concat(additionalRuntime.Issues)
                .ToArray())
        {
            CefRuntime = cefRuntime,
            WebView2Runtime = webView2Runtime,
            ElectronRuntime = electronRuntime,
            AdditionalRuntime = additionalRuntime,
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
        return await DiscoverInstallationsCoreAsync(
            _installationProvider,
            maximumProcessConcurrency,
            cancellationToken);
    }

    /// <summary>
    /// Discovers installations using explicit Windows filesystem options.
    /// </summary>
    public async ValueTask<InstallationDiscoveryResult>
        DiscoverInstallationsWithOptionsAsync(
        WindowsInstallationDiscoveryOptions options,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await DiscoverInstallationsCoreAsync(
            new WindowsInstallationProvider(options),
            maximumProcessConcurrency,
            cancellationToken);
    }

    private async ValueTask<InstallationDiscoveryResult>
        DiscoverInstallationsCoreAsync(
            IInstallationProvider provider,
            int? maximumProcessConcurrency,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        return await provider.DiscoverAsync(
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
    /// Performs opt-in cooperative and CDP renderer/frame enrichment.
    /// </summary>
    public async ValueTask<RendererEnrichmentResult> DiscoverRendererEnrichmentAsync(
        HandleQueryWorkerOptions workerOptions,
        IReadOnlyList<WebView2ExtendedProcessObservation>? webView2 = null,
        bool includeTracing = false,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);

        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        CdpDiscoveryResult cdp = await _cdpEndpointProvider.DiscoverAsync(
            processes,
            workerOptions,
            cancellationToken);
        RendererEnrichmentProvider provider = new();
        return await provider.EnrichAsync(
            processes,
            cdp,
            webView2,
            includeTracing,
            cancellationToken);
    }

    /// <summary>Creates versioned detailed diagnostics for one PID or Chromium processes.</summary>
    public async ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
        int? processId = null,
        bool includeSensitiveValues = false,
        int? maximumProcessConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        CefRuntimeAnalysis cef = CefRuntimeAdapter.Analyze(processes);
        ElectronRuntimeAnalysis electron = ElectronRuntimeAdapter.Analyze(processes);
        WebView2RuntimeAnalysis webView2 = WebView2RuntimeAdapter.Analyze(
            processes,
            new MojoPipeInspectionResult(
                DateTimeOffset.UtcNow,
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
                []));
        AdditionalRuntimeAnalysis additional = AdditionalRuntimeAdapter.Analyze(
            processes,
            cef,
            electron,
            webView2);
        ProcessSnapshotEntry[] selected;
        List<DiscoveryIssue> selectionIssues = [];
        if (processId is int selectedProcessId)
        {
            selected = processes
                .Where(process => process.ProcessId == selectedProcessId)
                .ToArray();
            if (selected.Length == 0)
            {
                selectionIssues.Add(new DiscoveryIssue(
                    "process-selection",
                    "The requested process was not present in the captured snapshot.",
                    selectedProcessId));
            }
        }
        else
        {
            HashSet<int> included = processes
                .Where(process => process.IsLikelyChromium)
                .Select(process => process.ProcessId)
                .Concat(cef.Processes.Select(process => process.ProcessId))
                .Concat(electron.Processes.Select(process => process.ProcessId))
                .Concat(webView2.Processes.Select(process => process.ProcessId))
                .Concat(additional.Processes.Select(process => process.ProcessId))
                .ToHashSet();
            selected = processes
                .Where(process => included.Contains(process.ProcessId))
                .ToArray();
        }

        selected = ApplyRuntimeRoles(
            selected,
            cef,
            electron,
            webView2,
            additional);
        ProcessDetailsResult result = new ProcessDetailsProvider().Create(
            selected,
            includeSensitiveValues);
        return result with
        {
            Issues = result.Issues.Concat(selectionIssues).ToArray(),
        };
    }

    /// <summary>Passively discovers diagnostic artifacts and configuration.</summary>
    public async ValueTask<DiagnosticArtifactDiscoveryResult>
        DiscoverDiagnosticArtifactsAsync(
            bool includeSensitiveValues = false,
            int? maximumProcessConcurrency = null,
            CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProcessSnapshotEntry> processes =
            await _processSnapshotter.CaptureAsync(
                maximumProcessConcurrency,
                cancellationToken);
        return await Task.Run(
            () => new DiagnosticArtifactProvider().Discover(
                processes,
                includeSensitiveValues,
                cancellationToken),
            cancellationToken);
    }

    private static ProcessSnapshotEntry[] ApplyRuntimeRoles(
        IEnumerable<ProcessSnapshotEntry> processes,
        CefRuntimeAnalysis cef,
        ElectronRuntimeAnalysis electron,
        WebView2RuntimeAnalysis webView2,
        AdditionalRuntimeAnalysis additional)
    {
        Dictionary<int, string> roles = [];
        foreach (ElectronProcessInfo process in electron.Processes)
        {
            roles.TryAdd(
                process.ProcessId,
                $"electron-{process.Role.ToString().ToLowerInvariant()}");
        }

        foreach (CefProcessInfo process in cef.Processes)
        {
            roles.TryAdd(
                process.ProcessId,
                $"cef-{process.Role.ToString().ToLowerInvariant()}");
        }

        foreach (WebView2ProcessInfo process in webView2.Processes)
        {
            roles.TryAdd(
                process.ProcessId,
                $"webview2-{process.Role.ToString().ToLowerInvariant()}");
        }

        foreach (AdditionalRuntimeProcessInfo process in additional.Processes)
        {
            roles.TryAdd(
                process.ProcessId,
                $"{process.PlatformId}-"
                    + process.Role.ToString().ToLowerInvariant());
        }

        return processes.Select(process =>
        {
            if (process.ChromiumProcessType is not null
                || !roles.TryGetValue(process.ProcessId, out string? role))
            {
                return process;
            }

            return process with
            {
                ChromiumProcessType = role,
                Evidence = process.Evidence
                    .Append($"Runtime adapter: classified process as {role}.")
                    .ToArray(),
            };
        }).ToArray();
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

    private static MojoPipeInspectionResult MergeMojoInspection(
        MojoPipeInspectionResult previous,
        MojoPipeInspectionResult current,
        MojoPipeEnumerationResult pipeResult,
        Dictionary<int, ProcessSnapshotEntry> previousById,
        Dictionary<int, ProcessSnapshotEntry> currentById)
    {
        Dictionary<string, MojoPipeInfo> previousByName = previous.Pipes
            .ToDictionary(pipe => pipe.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MojoPipeInfo> currentByName = current.Pipes
            .ToDictionary(pipe => pipe.Name, StringComparer.OrdinalIgnoreCase);
        MojoPipeInfo[] pipes = pipeResult.Pipes.Select(pipe =>
        {
            IEnumerable<NamedPipeConnection> retained =
                previousByName.TryGetValue(
                    pipe.Name,
                    out MojoPipeInfo? oldPipe)
                    ? oldPipe.Connections.Where(connection =>
                        IsSurvivingProcess(
                            connection.HandleOwnerProcessId,
                            previousById,
                            currentById)
                        && IsSurvivingProcess(
                            connection.ServerProcessId,
                            previousById,
                            currentById)
                        && IsSurvivingProcess(
                            connection.ClientProcessId,
                            previousById,
                            currentById))
                    : [];
            IEnumerable<NamedPipeConnection> discovered =
                currentByName.TryGetValue(
                    pipe.Name,
                    out MojoPipeInfo? newPipe)
                    ? newPipe.Connections
                    : [];
            return new MojoPipeInfo(
                pipe.Name,
                pipe.ProcessIdHint,
                retained.Concat(discovered).Distinct().ToArray());
        }).ToArray();
        return current with
        {
            Pipes = pipes,
            Statistics = current.Statistics with
            {
                CandidatePipeCount = pipes.Length,
            },
            TimedOutQueries = previous.TimedOutQueries
                .Where(query => IsSurvivingProcess(
                    query.OwnerProcessId,
                    previousById,
                    currentById))
                .Concat(current.TimedOutQueries)
                .Distinct()
                .ToArray(),
            Issues = previous.Issues
                .Where(issue => issue.ProcessId is int processId
                    && IsSurvivingProcess(
                        processId,
                        previousById,
                        currentById))
                .Concat(current.Issues)
                .Distinct()
                .ToArray(),
        };
    }

    private static MojoPipeInspectionResult CreateUninspectedMojoResult(
        MojoPipeEnumerationResult pipeResult,
        DateTimeOffset capturedAt)
    {
        return new MojoPipeInspectionResult(
            capturedAt,
            pipeResult.Pipes
                .Select(pipe => new MojoPipeInfo(
                    pipe.Name,
                    pipe.ProcessIdHint,
                    []))
                .ToArray(),
            new NamedPipeInspectionStatistics(
                pipeResult.Pipes.Count,
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
            pipeResult.Issues);
    }

    private static WindowSnapshotResult MergeWindowSnapshot(
        WindowSnapshotResult previous,
        WindowSnapshotResult current,
        Dictionary<int, ProcessSnapshotEntry> previousById,
        Dictionary<int, ProcessSnapshotEntry> currentById)
    {
        HashSet<int> newProcessIds = currentById
            .Where(pair =>
                !previousById.TryGetValue(
                    pair.Key,
                    out ProcessSnapshotEntry? previousProcess)
                || previousProcess.CreationTime is null
                || pair.Value.CreationTime is null
                || previousProcess.CreationTime != pair.Value.CreationTime)
            .Select(pair => pair.Key)
            .ToHashSet();
        WindowSnapshotEntry[] windows = previous.Windows
            .Where(window => IsSurvivingProcess(
                window.OwnerProcessId,
                previousById,
                currentById))
            .Concat(current.Windows.Where(window =>
                newProcessIds.Contains(window.OwnerProcessId)))
            .DistinctBy(window => window.WindowHandle)
            .ToArray();
        return new WindowSnapshotResult(
            current.CapturedAt,
            windows,
            previous.Issues
                .Where(issue => issue.ProcessId is int processId
                    && IsSurvivingProcess(
                        processId,
                        previousById,
                        currentById))
                .Concat(current.Issues)
                .Distinct()
                .ToArray());
    }

    private static CdpDiscoveryResult MergeCdpDiscovery(
        CdpDiscoveryResult previous,
        CdpDiscoveryResult current,
        Dictionary<int, ProcessSnapshotEntry> previousById,
        Dictionary<int, ProcessSnapshotEntry> currentById)
    {
        CdpTransportInfo[] transports = previous.Transports
            .Where(transport => IsSurvivingProcess(
                transport.ProcessId,
                previousById,
                currentById))
            .Concat(current.Transports)
            .DistinctBy(transport => (
                transport.ProcessId,
                transport.Kind,
                transport.ConfiguredValue))
            .ToArray();
        return new CdpDiscoveryResult(
            current.CapturedAt,
            transports)
        {
            Issues = previous.Issues
                .Where(issue => issue.ProcessId is int processId
                    && IsSurvivingProcess(
                        processId,
                        previousById,
                        currentById))
                .Concat(current.Issues)
                .Distinct()
                .ToArray(),
        };
    }

    private static bool IsSurvivingProcess(
        int? processId,
        Dictionary<int, ProcessSnapshotEntry> previousById,
        Dictionary<int, ProcessSnapshotEntry> currentById)
    {
        if (processId is null)
        {
            return true;
        }

        return previousById.TryGetValue(
                processId.Value,
                out ProcessSnapshotEntry? previous)
            && currentById.TryGetValue(
                processId.Value,
                out ProcessSnapshotEntry? current)
            && previous.CreationTime == current.CreationTime;
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

    /// <summary>Gets Electron-specific process and runtime analysis.</summary>
    public ElectronRuntimeAnalysis ElectronRuntime { get; init; } =
        ElectronRuntimeAnalysis.Empty;

    /// <summary>Gets Qt WebEngine, NW.js, PWA, and generic runtime analysis.</summary>
    public AdditionalRuntimeAnalysis AdditionalRuntime { get; init; } =
        AdditionalRuntimeAnalysis.Empty;

    /// <summary>Gets configured and validated CDP transports.</summary>
    public CdpDiscoveryResult Cdp { get; init; } = new(
        DateTimeOffset.MinValue,
        []);
}
