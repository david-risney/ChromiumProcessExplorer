using System.Text.Json;
using ChromiumProcessExplorer.Core.Broker;
using ChromiumProcessExplorer.Core.Discovery;

if (args is [HandleQueryWorker.WorkerArgument])
{
    return await HandleQueryWorker.RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput());
}

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out CliOptions options))
        {
            PrintUsage(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            if (options.Command.StartsWith(
                "broker-",
                StringComparison.Ordinal))
            {
                return await RunBrokerCommandAsync(
                    options,
                    cancellation.Token);
            }

            ChromiumProcessDiscovery discovery = new();
            string workerPath = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The current executable path is unavailable.");
            HandleQueryWorkerOptions workerOptions = new(
                workerPath,
                options.MaximumConcurrency ?? 0);

            if (options.Command == "installations")
            {
                InstallationDiscoveryResult installations =
                    await discovery.DiscoverInstallationsAsync(
                        options.MaximumConcurrency,
                        cancellation.Token);
                WriteInstallations(installations, options.Json);
                return 0;
            }

            if (options.Command == "cdp")
            {
                CdpDiscoveryResult cdp = await discovery.DiscoverCdpAsync(
                    workerOptions,
                    options.MaximumConcurrency,
                    cancellation.Token);
                WriteCdp(cdp, options.Json);
                return 0;
            }

            if (options.Command == "renderer-origins")
            {
                RendererEnrichmentResult enrichment =
                    await discovery.DiscoverRendererEnrichmentAsync(
                        workerOptions,
                        includeTracing: options.IncludeTracing,
                        maximumProcessConcurrency: options.MaximumConcurrency,
                        cancellationToken: cancellation.Token);
                WriteRendererEnrichment(enrichment, options.Json);
                return 0;
            }

            if (options.Command == "process-details")
            {
                ProcessDetailsResult details =
                    await discovery.DiscoverProcessDetailsAsync(
                        options.ProcessId,
                        options.IncludeSensitiveValues,
                        options.MaximumConcurrency,
                        cancellation.Token);
                WriteProcessDetails(details, options.Json);
                return details.Processes.Count == 0 ? 1 : 0;
            }

            if (options.Command == "diagnostics")
            {
                DiagnosticArtifactDiscoveryResult diagnostics =
                    await discovery.DiscoverDiagnosticArtifactsAsync(
                        options.IncludeSensitiveValues,
                        options.MaximumConcurrency,
                        cancellation.Token);
                WriteDiagnostics(diagnostics, options.Json);
                return 0;
            }

            if (options.Command == "mojo-pipes")
            {
                if (options.NamesOnly)
                {
                    MojoPipeEnumerationResult pipeNames =
                        await discovery.EnumerateMojoPipesAsync(cancellation.Token);
                    WritePipeNames(pipeNames, options.Json);
                    return 0;
                }

                MojoPipeInspectionResult pipeInspection =
                    await discovery.InspectMojoPipesAsync(
                        workerOptions,
                        options.MaximumConcurrency,
                        cancellation.Token);
                WritePipes(pipeInspection, options.Json);
                return 0;
            }

            ChromiumDiscoveryResult result = await discovery.DiscoverAsync(
                workerOptions,
                options.MaximumConcurrency,
                options.IncludeWindowEvidence,
                cancellation.Token);

            WriteTree(result, options);

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Discovery cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunBrokerCommandAsync(
        CliOptions options,
        CancellationToken cancellationToken)
    {
        string operation = options.Command switch
        {
            "broker-probe" => BrokerOperations.Probe,
            "broker-process-details" => BrokerOperations.ProcessDetails,
            "broker-installations" => BrokerOperations.Installations,
            "broker-diagnostics" => BrokerOperations.Diagnostics,
            "broker-cdp" => BrokerOperations.Cdp,
            _ => throw new InvalidOperationException(
                "Unknown broker command."),
        };
        object arguments = options.Command switch
        {
            "broker-process-details" => new BrokerProcessDetailsArguments(
                options.ProcessId),
            _ => new { },
        };
        ChromiumBrokerClient client = new(
            BrokerServerOptions.CreateDefault().PipeName,
            TimeSpan.FromSeconds(95));
        BrokerResponse response = await client.SendAsync(
            operation,
            arguments,
            cancellationToken);
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else if (response.Ok)
        {
            Console.WriteLine(
                response.Result is JsonElement result
                    ? JsonSerializer.Serialize(result, JsonOptions)
                    : "Broker request completed.");
        }
        else
        {
            Console.Error.WriteLine(
                $"error: {response.Error?.Code}: {response.Error?.Message}");
            if (response.Error?.RecommendedAction is string action)
            {
                Console.Error.WriteLine($"recommended action: {action}");
            }
        }

        return response.Ok ? 0 : 1;
    }

    private static void WritePipeNames(MojoPipeEnumerationResult result, bool json)
    {
        foreach (DiscoveryIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"warning: {issue.Stage}: {issue.Message}");
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    MojoPipes = result.Pipes,
                    result.Issues,
                },
                JsonOptions));
            return;
        }

        if (result.Pipes.Count == 0)
        {
            Console.WriteLine("No visible Mojo pipes found.");
            return;
        }

        foreach (MojoPipeCandidate pipe in result.Pipes)
        {
            string process = pipe.ProcessIdHint is int processId
                ? $" PID hint {processId}"
                : string.Empty;
            Console.WriteLine($"{pipe.Name}{process}");
        }
    }

    private static void WriteInstallations(
        InstallationDiscoveryResult result,
        bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        foreach (DiscoveryIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"warning: {issue.Stage}: {issue.Message}");
        }

        if (result.Installations.Count == 0)
        {
            Console.WriteLine("No Chromium-related installations found.");
            return;
        }

        foreach (IGrouping<string, ChromiumInstallation> group in result.Installations
            .GroupBy(installation => installation.Kind)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{group.Key}s");
            foreach (ChromiumInstallation installation in group)
            {
                string version = installation.Version is null
                    ? string.Empty
                    : $" {installation.Version}";
                string channel = installation.Channel is null
                    ? string.Empty
                    : $" [{installation.Channel}]";
                Console.WriteLine(
                    $"  {installation.Name}{version}{channel} ({installation.Platform})");
                Console.WriteLine($"    {installation.InstallPath}");
                InstallationMetadata metadata = installation.Metadata;
                Console.WriteLine(
                    $"    install: {metadata.InstallType}; "
                    + $"confidence: {metadata.Confidence}");
                if (metadata.Architecture is not null
                    || metadata.Publisher is not null)
                {
                    Console.WriteLine(
                        $"    architecture: {metadata.Architecture ?? "unknown"}; "
                        + $"publisher: {metadata.Publisher ?? "unknown"}");
                }

                if (metadata.VersionProvenance is not null)
                {
                    Console.WriteLine(
                        $"    version source: {metadata.VersionProvenance}");
                }

                if (metadata.PackageIdentity is not null)
                {
                    Console.WriteLine(
                        $"    package: {metadata.PackageIdentity.PackageFullName}");
                }

                if (metadata.ResourcesPath is not null)
                {
                    Console.WriteLine(
                        $"    resources: {metadata.ResourcesPath}");
                }

                if (metadata.RuntimePath is not null)
                {
                    Console.WriteLine($"    runtime: {metadata.RuntimePath}");
                }

                if (metadata.IsSharedRuntime is bool isSharedRuntime)
                {
                    Console.WriteLine(
                        $"    runtime scope: "
                        + $"{(isSharedRuntime ? "shared" : "app-local")}");
                }

                foreach (InstallationEvidence evidence in installation.Evidence)
                {
                    string process = evidence.ProcessId is int processId
                        ? $" PID {processId}"
                        : string.Empty;
                    Console.WriteLine(
                        $"    evidence: {evidence.Source}{process}: {evidence.Detail}");
                }
            }

            Console.WriteLine();
        }

        InstallationDiscoveryStatistics statistics = result.Statistics;
        Console.WriteLine(
            $"Scanned {statistics.DirectoryCount} directories across "
            + $"{statistics.SearchRootCount} roots in "
            + $"{statistics.Elapsed.TotalMilliseconds:F0} ms; found "
            + $"{statistics.MarkerFileCount} marker files and considered "
            + $"{statistics.RunningProcessCount} running Chromium processes, "
            + $"{statistics.RegistryRecordCount} uninstall records, and "
            + $"{statistics.PackageCount} packages.");
        if (statistics.InaccessibleDirectoryCount > 0
            || statistics.TruncatedDirectoryCount > 0)
        {
            Console.WriteLine(
                $"Coverage note: {statistics.InaccessibleDirectoryCount} directories "
                + "were inaccessible and "
                + $"{statistics.TruncatedDirectoryCount} directories exceeded the "
                + "configured scan depth.");
        }
    }

    private static void WriteCdp(CdpDiscoveryResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        if (result.Transports.Count == 0)
        {
            Console.WriteLine("No configured CDP transports found.");
            return;
        }

        foreach (DiscoveryIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"warning: {issue.Stage}: {issue.Message}");
        }

        foreach (CdpTransportInfo transport in result.Transports)
        {
            string endpoint = transport.Port is int port
                ? $" port {port}"
                : string.Empty;
            Console.WriteLine(
                $"PID {transport.ProcessId}: {transport.Kind} "
                + $"{transport.Status}{endpoint}");
            if (transport.WebSocketDebuggerUrl is not null)
            {
                Console.WriteLine(
                    $"  browser WebSocket: {transport.WebSocketDebuggerUrl}");
            }

            if (transport.Browser is not null)
            {
                Console.WriteLine($"  browser: {transport.Browser}");
            }

            if (transport.ControllerProcessId is int controllerProcessId)
            {
                string image = transport.ControllerImageName is null
                    ? string.Empty
                    : $" {transport.ControllerImageName}";
                Console.WriteLine(
                    $"  existing controller: {controllerProcessId}{image}");
            }

            if (transport.Restriction is not null)
            {
                Console.WriteLine($"  restriction: {transport.Restriction}");
            }

            if (transport.Error is not null)
            {
                Console.WriteLine($"  note: {transport.Error}");
            }
        }
    }

    private static void WriteRendererEnrichment(
        RendererEnrichmentResult result,
        bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        foreach (DiscoveryIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"warning: {issue.Stage}: {issue.Message}");
        }

        foreach (RendererFrameMapping mapping in result.FrameMappings)
        {
            string authority = mapping.IsAuthoritative
                ? "authoritative"
                : "experimental";
            Console.WriteLine(
                $"PID {mapping.Process.ProcessId} frame {mapping.FrameId}: "
                + $"{mapping.Url}");
            Console.WriteLine(
                $"  {mapping.Source}, {mapping.Confidence}, {authority}; "
                + mapping.Lifetime);
        }

        Console.WriteLine(
            $"CDP topology: {result.CdpTargets.Count} targets and "
            + $"{result.CdpProcesses.Count} OS processes.");
        foreach (string limitation in result.Limitations)
        {
            Console.WriteLine($"  limitation: {limitation}");
        }
    }

    private static void WriteProcessDetails(
        ProcessDetailsResult result,
        bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        foreach (DiscoveryIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"warning: {issue.Stage}: {issue.Message}");
        }

        foreach (ProcessDetailEntry process in result.Processes)
        {
            Console.WriteLine(
                $"PID {process.Identity.ProcessId} {process.ImageName}");
            Console.WriteLine(
                $"  identity: {process.Identity.ProcessId}@"
                + $"{process.Identity.CreationTime:O}");
            Console.WriteLine($"  parent PID: {process.ParentProcessId}");
            Console.WriteLine(
                $"  executable: {FormatSensitive(process.ExecutablePath)}");
            Console.WriteLine(
                $"  command line: {FormatSensitive(process.CommandLine)}");
            Console.WriteLine(
                $"  role: {process.ProcessRole ?? "unknown"} "
                + $"[{process.RoleSource}]");
            Console.WriteLine(
                $"  user data: {FormatSensitive(process.UserDataDirectory)}");
            Console.WriteLine(
                $"  architecture: {process.Architecture ?? "unknown"} "
                + $"(native {process.NativeArchitecture ?? "unknown"})");
            Console.WriteLine(
                $"  integrity: {process.IntegrityLevel ?? "unknown"}, "
                + $"elevated: {process.IsElevated?.ToString() ?? "unknown"}");
            if (process.ExecutableVersion is not null)
            {
                Console.WriteLine(
                    $"  version: file {process.ExecutableVersion.FileVersion ?? "unknown"}, "
                    + $"product {process.ExecutableVersion.ProductVersion ?? "unknown"}");
            }

            if (process.PackageFullName is not null)
            {
                Console.WriteLine($"  package: {process.PackageFullName}");
            }

            foreach (ProcessSwitchDetail item in process.Switches)
            {
                Console.WriteLine(
                    item.HasValue
                        ? $"  switch: --{item.Name}={FormatSensitive(item.Value)}"
                        : $"  switch: --{item.Name}");
            }

            foreach (DiscoveryIssue issue in process.Issues)
            {
                Console.Error.WriteLine(
                    $"warning: PID {process.Identity.ProcessId} "
                    + $"{issue.Stage}: {issue.Message}");
            }
        }
    }

    private static string FormatSensitive(SensitiveStringValue value)
    {
        return value.IsRedacted ? "<redacted>" : value.Value ?? "<unavailable>";
    }

    private static void WriteDiagnostics(
        DiagnosticArtifactDiscoveryResult result,
        bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        foreach (DiscoveryIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"warning: {issue.Stage}: {issue.Message}");
        }

        foreach (DiagnosticConfigurationFinding finding in result.Configuration)
        {
            Console.WriteLine(
                $"PID {finding.Identity.ProcessId} [{finding.Platform}] "
                + $"{finding.Name} ({finding.Category}/{finding.Severity})");
            if (finding.Value.Value is not null || finding.Value.IsRedacted)
            {
                Console.WriteLine(
                    $"  value: {FormatSensitive(finding.Value)}");
            }

            Console.WriteLine($"  {finding.Detail}");
            if (finding.RequiresExplicitConsent)
            {
                Console.WriteLine("  capture requires explicit consent");
            }
        }

        foreach (DiagnosticArtifact artifact in result.Artifacts)
        {
            Console.WriteLine(
                $"[{artifact.Platform}] {artifact.Kind}: "
                + $"{FormatSensitive(artifact.Location)} "
                + $"({artifact.Status})");
            Console.WriteLine($"  source: {artifact.Source}");
            if (artifact.Length is long length)
            {
                Console.WriteLine($"  size: {length} bytes");
            }

            if (artifact.LastWriteTime is DateTimeOffset lastWrite)
            {
                Console.WriteLine($"  last write: {lastWrite:O}");
            }

            if (artifact.AssociatedProcessIds.Count > 0)
            {
                Console.WriteLine(
                    "  associated current PIDs: "
                    + string.Join(", ", artifact.AssociatedProcessIds));
            }

            if (artifact.IsPotentiallySensitive)
            {
                Console.WriteLine("  potentially sensitive");
            }
        }
    }

    private static void WritePipes(MojoPipeInspectionResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        if (result.Pipes.Count == 0)
        {
            Console.WriteLine("No visible Mojo pipes found.");
            return;
        }

        var serverGroups = result.Pipes
            .GroupBy(GetServerProcessId)
            .OrderBy(group => group.Key is null)
            .ThenBy(group => group.Key);

        foreach (var group in serverGroups)
        {
            string? serverImageName = group
                .SelectMany(pipe => pipe.Connections)
                .Where(connection => connection.ServerProcessId == group.Key)
                .Select(connection => connection.ServerImageName)
                .FirstOrDefault(imageName => imageName is not null);
            Console.WriteLine(
                group.Key is int serverProcessId
                    ? $"Server {FormatProcess(serverProcessId, serverImageName)}"
                    : "Server unknown");

            foreach (MojoPipeInfo pipe in group)
            {
                string process = pipe.ProcessIdHint is int processId
                    ? $" PID hint {processId}"
                    : string.Empty;
                Console.WriteLine($"  {pipe.Name}{process}");

                if (pipe.Connections.Count == 0)
                {
                    Console.WriteLine("    endpoints unavailable");
                    continue;
                }

                foreach (NamedPipeConnection connection in pipe.Connections)
                {
                    string client = connection.ClientProcessId is null
                        && string.Equals(
                            connection.State,
                            "listening",
                            StringComparison.Ordinal)
                        ? "not connected"
                        : FormatProcess(
                            connection.ClientProcessId,
                            connection.ClientImageName);
                    Console.WriteLine(
                        $"    client {client}"
                        + $" (handle owner {FormatProcess(connection.HandleOwnerProcessId, connection.HandleOwnerImageName)},"
                        + $" {connection.LocalEnd ?? "unknown end"}, {connection.State ?? "unknown state"})");
                }
            }

            Console.WriteLine();
        }

        NamedPipeInspectionStatistics statistics = result.Statistics;
        Console.WriteLine(
            $"Scanned {statistics.QueriedHandleCount} unique file handles in "
            + $"{statistics.Elapsed.TotalMilliseconds:F0} ms; matched "
            + $"{statistics.MatchedMojoHandleCount} Mojo handles; "
            + $"{statistics.TimedOutQueryCount} timed out.");
        WritePipeCoverageNotes(result);
    }

    private static void WritePipeCoverageNotes(MojoPipeInspectionResult result)
    {
        DiscoveryIssue[] accessDeniedIssues = result.Issues
            .Where(issue => issue.Stage == "duplicate-handle"
                && issue.NativeErrorCode == 5)
            .ToArray();
        DiscoveryIssue[] routineTimeoutIssues = result.Issues
            .Where(issue => issue.Stage == "handle-query"
                && issue.Message.Contains(
                    "timed out",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        DiscoveryIssue[] otherIssues = result.Issues
            .Except(accessDeniedIssues)
            .Except(routineTimeoutIssues)
            .ToArray();

        if (result.Statistics.TimedOutQueryCount > 0)
        {
            Console.WriteLine(
                $"Coverage note: {result.Statistics.TimedOutQueryCount} potentially blocking "
                + "handle queries timed out; their helpers were safely replaced.");
            foreach (TimedOutHandleQuery timeout in result.TimedOutQueries)
            {
                Console.WriteLine(
                    $"  {FormatProcess(timeout.OwnerProcessId, timeout.OwnerImageName)}, "
                    + $"handle 0x{timeout.HandleValue:X}, access 0x{timeout.GrantedAccess:X8}, "
                    + $"stage {timeout.QueryStage}, "
                    + $"{timeout.Elapsed.TotalMilliseconds:F0} ms");
            }
        }

        if (accessDeniedIssues.Length > 0)
        {
            int processCount = accessDeniedIssues
                .Where(issue => issue.ProcessId is not null)
                .Select(issue => issue.ProcessId)
                .Distinct()
                .Count();
            Console.WriteLine(
                $"Coverage note: handle duplication was denied for {processCount} "
                + "processes. Running as administrator may expose additional pipes.");
        }

        foreach (IGrouping<(string Stage, string Message), DiscoveryIssue> group
            in otherIssues.GroupBy(issue => (issue.Stage, issue.Message)))
        {
            string count = group.Count() > 1 ? $" ({group.Count()} occurrences)" : string.Empty;
            Console.Error.WriteLine(
                $"warning: {group.Key.Stage}: {group.Key.Message}{count}");
        }
    }

    private static int? GetServerProcessId(MojoPipeInfo pipe)
    {
        return pipe.Connections
            .Select(connection => connection.ServerProcessId)
            .FirstOrDefault(processId => processId is not null);
    }

    private static string FormatProcess(int? processId, string? imageName)
    {
        if (processId is null)
        {
            return "unknown";
        }

        return imageName is null
            ? processId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{processId.Value} {imageName}";
    }

    private static void WriteTree(ChromiumDiscoveryResult result, CliOptions options)
    {
        ProcessTree tree = result.ProcessTree;
        ProcessGraph graph = result.ProcessGraph;
        IReadOnlySet<int> mojoProcessIds =
            result.MojoPipeInspection.GetRelatedProcessIds();
        IReadOnlyDictionary<int, CefProcessInfo> cefProcesses =
            result.CefRuntime.Processes.ToDictionary(process => process.ProcessId);
        IReadOnlyDictionary<int, WebView2ProcessInfo> webView2Processes =
            result.WebView2Runtime.Processes.ToDictionary(
                process => process.ProcessId);
        IReadOnlyDictionary<int, ElectronProcessInfo> electronProcesses =
            result.ElectronRuntime.Processes.ToDictionary(
                process => process.ProcessId);
        if (!options.AllProcesses)
        {
            HashSet<int> seeds = result.Processes
                .Where(process => process.IsLikelyChromium)
                .Select(process => process.ProcessId)
                .Concat(result.CefRuntime.Processes.Select(process => process.ProcessId))
                .Concat(result.CefRuntime.HostAssociations.Select(
                    association => association.HostProcessId))
                .Concat(result.WebView2Runtime.Processes.Select(
                    process => process.ProcessId))
                .Concat(result.WebView2Runtime.HostAssociations.Select(
                    association => association.HostProcessId))
                .Concat(result.ElectronRuntime.Processes.Select(
                    process => process.ProcessId))
                .Concat(result.Cdp.Transports.Select(transport => transport.ProcessId))
                .Concat(mojoProcessIds)
                .ToHashSet();
            tree = tree.CreateFilteredView(seeds);
            graph = graph.CreateFilteredView(seeds);
        }

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    result.CapturedAt,
                    Roots = tree.Roots.Select(ToSerializableNode),
                    result.CefRuntime,
                    result.WebView2Runtime,
                    result.ElectronRuntime,
                    result.Cdp,
                    ProcessGraph = graph,
                    result.MojoPipeInspection,
                    result.Issues,
                },
                JsonOptions));
            return;
        }

        if (tree.Roots.Count == 0)
        {
            Console.WriteLine("No Chromium-related processes found.");
            return;
        }

        foreach (ProcessTreeNode root in tree.Roots)
        {
            WriteNode(
                root,
                string.Empty,
                true,
                mojoProcessIds,
                cefProcesses,
                webView2Processes,
                electronProcesses);
        }

        if (result.CefRuntime.Associations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("CEF process associations");
            foreach (CefProcessAssociation association in result.CefRuntime.Associations)
            {
                string authority = association.IsAuthoritative
                    ? "authoritative"
                    : "inferred";
                Console.WriteLine(
                    $"  browser {association.BrowserProcessId} -> "
                    + $"subprocess {association.SubprocessProcessId}: "
                    + $"{association.Confidence} ({association.Score}/100, {authority})");
            }
        }

        if (result.CefRuntime.HostAssociations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("CEF host associations");
            foreach (CefHostAssociation association in
                result.CefRuntime.HostAssociations)
            {
                string authority = association.IsAuthoritative
                    ? "authoritative"
                    : "inferred";
                Console.WriteLine(
                    $"  host {association.HostProcessId} -> "
                    + $"browser {association.BrowserProcessId}: "
                    + $"{association.Confidence} "
                    + $"({association.Score}/100, {authority})");
            }
        }

        if (result.WebView2Runtime.HostAssociations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("WebView2 host associations");
            foreach (WebView2HostAssociation association in
                result.WebView2Runtime.HostAssociations)
            {
                string authority = association.IsAuthoritative
                    ? "authoritative"
                    : "inferred";
                Console.WriteLine(
                    $"  host {association.HostProcessId} -> "
                    + $"browser {association.BrowserProcessId}: "
                    + $"{association.Confidence} "
                    + $"({association.Score}/100, {authority})");
                foreach (WebView2Evidence evidence in association.Evidence)
                {
                    Console.WriteLine(
                        $"    {evidence.Source}: "
                        + $"{evidence.RawValue ?? evidence.Detail}");
                }
            }
        }

        if (result.ElectronRuntime.Associations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Electron process associations");
            foreach (ElectronProcessAssociation association in
                result.ElectronRuntime.Associations)
            {
                string authority = association.IsAuthoritative
                    ? "authoritative"
                    : "inferred";
                Console.WriteLine(
                    $"  main {association.MainProcessId} -> "
                    + $"child {association.ChildProcessId}: "
                    + $"{association.Confidence} "
                    + $"({association.Score}/100, {authority})");
            }
        }

        foreach (DiscoveryIssue issue in result.WebView2Runtime.Issues)
        {
            Console.Error.WriteLine(
                $"warning: {issue.Stage}: {issue.Message}");
        }

        Console.WriteLine();
        WritePipeCoverageNotes(result.MojoPipeInspection);
    }

    private static object ToSerializableNode(ProcessTreeNode node)
    {
        return new
        {
            node.Process,
            Children = node.Children.Select(ToSerializableNode),
        };
    }

    private static void WriteNode(
        ProcessTreeNode node,
        string prefix,
        bool isLast,
        IReadOnlySet<int> mojoProcessIds,
        IReadOnlyDictionary<int, CefProcessInfo> cefProcesses,
        IReadOnlyDictionary<int, WebView2ProcessInfo> webView2Processes,
        IReadOnlyDictionary<int, ElectronProcessInfo> electronProcesses)
    {
        ProcessSnapshotEntry process = node.Process;
        _ = cefProcesses.TryGetValue(process.ProcessId, out CefProcessInfo? cef);
        _ = webView2Processes.TryGetValue(
            process.ProcessId,
            out WebView2ProcessInfo? webView2);
        _ = electronProcesses.TryGetValue(
            process.ProcessId,
            out ElectronProcessInfo? electron);
        string branch = isLast ? "`- " : "|- ";
        string type = process.ChromiumProcessType is null
            ? string.Empty
            : $" [{process.ChromiumProcessType}]";
        string mojo = mojoProcessIds.Contains(process.ProcessId) ? " [mojo]" : string.Empty;
        string cefBadge = cef is null
            ? string.Empty
            : $" [CEF:{cef.Role}] [{cef.Layout}]";
        string webView2Badge = webView2 is null
            ? string.Empty
            : $" [WebView2:{webView2.Role}]";
        string electronBadge = electron is null
            ? string.Empty
            : $" [Electron:{electron.Role}]";
        string path = process.ExecutablePath is null ? string.Empty : $" {process.ExecutablePath}";

        Console.WriteLine(
            $"{prefix}{branch}{process.ProcessId} {process.ImageName}"
            + $"{type}{cefBadge}{webView2Badge}{electronBadge}{mojo}{path}");

        string childPrefix = prefix + (isLast ? "   " : "|  ");
        if (cef is not null)
        {
            WriteCefDetails(cef, process.CommandLine, childPrefix);
        }

        if (webView2 is not null)
        {
            WriteWebView2Details(webView2, childPrefix);
        }

        if (electron is not null)
        {
            WriteElectronDetails(electron, childPrefix);
        }

        for (int index = 0; index < node.Children.Count; index++)
        {
            WriteNode(
                node.Children[index],
                childPrefix,
                index == node.Children.Count - 1,
                mojoProcessIds,
                cefProcesses,
                webView2Processes,
                electronProcesses);
        }
    }

    private static void WriteElectronDetails(
        ElectronProcessInfo process,
        string prefix)
    {
        if (process.UtilitySubType is not null)
        {
            Console.WriteLine(
                $"{prefix}electron utility subtype: "
                + process.UtilitySubType);
        }

        if (process.WindowType is not null)
        {
            Console.WriteLine(
                $"{prefix}electron window type: {process.WindowType}");
        }

        ElectronRuntimePaths paths = process.Paths;
        WriteElectronPath(prefix, "install", paths.InstallDirectory);
        WriteElectronPath(prefix, "package root", paths.PackageRoot);
        WriteElectronPath(prefix, "resources", paths.ResourcesDirectory);
        WriteElectronPath(prefix, "application", paths.ApplicationPath);
        WriteElectronPath(prefix, "user data", paths.UserDataDirectory);
        WriteElectronPath(prefix, "session data", paths.SessionDataDirectory);
        WriteElectronPath(prefix, "logs", paths.LogsDirectory);
        WriteElectronPath(prefix, "crash dumps", paths.CrashDumpsDirectory);

        if (process.PackageIdentity is not null)
        {
            Console.WriteLine(
                $"{prefix}package: {process.PackageIdentity.PackageFullName}");
        }

        foreach (ElectronEvidence evidence in process.Evidence.Where(
            evidence => evidence.Source is
                "filesystem-marker" or "runtime-executable"
                or "cooperative-electron-api"))
        {
            Console.WriteLine(
                $"{prefix}electron evidence: {evidence.Source}: "
                + $"{evidence.RawValue ?? evidence.Detail}");
        }
    }

    private static void WriteElectronPath(
        string prefix,
        string label,
        ElectronPathObservation? path)
    {
        if (path is not null)
        {
            string exists = path.Exists ? string.Empty : " (not observed on disk)";
            Console.WriteLine(
                $"{prefix}{label}: {path.Value} "
                + $"[{path.Confidence}, {path.Source}]{exists}");
        }
    }

    private static void WriteWebView2Details(
        WebView2ProcessInfo process,
        string prefix)
    {
        foreach (WebView2Evidence evidence in process.Evidence)
        {
            Console.WriteLine(
                $"{prefix}webview2 evidence: {evidence.Source}: "
                + $"{evidence.RawValue ?? evidence.Detail}");
        }

        if (!string.IsNullOrWhiteSpace(process.ModuleInspectionError))
        {
            Console.WriteLine(
                $"{prefix}module inspection error: "
                + process.ModuleInspectionError);
        }
    }

    private static void WriteCefDetails(
        CefProcessInfo cef,
        string? commandLine,
        string prefix)
    {
        if (!string.IsNullOrWhiteSpace(commandLine))
        {
            Console.WriteLine($"{prefix}command: {commandLine}");
        }

        if (cef.UtilityRole is not null)
        {
            Console.WriteLine($"{prefix}utility sandbox: {cef.UtilityRole}");
        }

        if (cef.UtilitySubType is not null)
        {
            Console.WriteLine($"{prefix}utility subtype: {cef.UtilitySubType}");
        }

        if (cef.Wrappers.Count > 0)
        {
            Console.WriteLine($"{prefix}wrapper: {string.Join(", ", cef.Wrappers)}");
        }

        CefRuntimePaths paths = cef.RuntimePaths;
        WriteCefPath(prefix, "user data", paths.UserDataDirectory);
        WriteCefPath(prefix, "log", paths.LogFile);
        WriteCefPath(prefix, "resources", paths.ResourcesDirectory);
        WriteCefPath(prefix, "locales", paths.LocalesDirectory);
        WriteCefPath(prefix, "browser subprocess", paths.BrowserSubprocessPath);
        WriteCefPath(prefix, "crash reports", paths.CrashReportDirectory);
        WriteCefPath(
            prefix,
            "crash reporter configuration",
            paths.CrashReportConfigurationFile);
        WriteCefPath(prefix, "DevTools active port", paths.DevToolsActivePortFile);

        if (cef.RemoteDebuggingPort is not null)
        {
            Console.WriteLine(
                $"{prefix}remote debugging port: {cef.RemoteDebuggingPort}");
        }

        if (cef.RemoteDebuggingPipe)
        {
            Console.WriteLine($"{prefix}remote debugging pipe: enabled");
        }

        foreach (CefSwitchWarning warning in cef.SwitchWarnings)
        {
            Console.WriteLine(
                $"{prefix}warning: {warning.Switch} ({warning.Category}): "
                + warning.Detail);
        }

        foreach (CefEvidence evidence in cef.Evidence.Where(
            evidence => evidence.Source is "filesystem-marker" or "loaded-module"))
        {
            Console.WriteLine(
                $"{prefix}cef evidence: {evidence.Source}: "
                + $"{evidence.Path ?? evidence.Detail}");
        }

        if (!string.IsNullOrWhiteSpace(cef.ModuleInspectionError))
        {
            Console.WriteLine(
                $"{prefix}module inspection error: {cef.ModuleInspectionError}");
        }
    }

    private static void WriteCefPath(
        string prefix,
        string label,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine($"{prefix}{label}: {path}");
        }
    }

    private static bool TryParse(string[] args, out CliOptions options)
    {
        string command = "process-tree";
        bool json = false;
        bool all = false;
        bool namesOnly = false;
        bool includeWindowEvidence = false;
        bool includeTracing = false;
        bool includeSensitiveValues = false;
        int? processId = null;
        bool help = false;
        int? concurrency = null;
        bool commandSeen = false;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "process-tree":
                case "mojo-pipes":
                case "installations":
                case "cdp":
                case "renderer-origins":
                case "process-details":
                case "diagnostics":
                case "broker-probe":
                case "broker-process-details":
                case "broker-installations":
                case "broker-diagnostics":
                case "broker-cdp":
                    if (commandSeen)
                    {
                        options = null!;
                        return false;
                    }

                    command = args[index];
                    commandSeen = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--all":
                    all = true;
                    break;
                case "--names-only":
                    namesOnly = true;
                    break;
                case "--windows":
                    includeWindowEvidence = true;
                    break;
                case "--trace":
                    includeTracing = true;
                    break;
                case "--include-sensitive":
                    includeSensitiveValues = true;
                    break;
                case "--pid":
                    if (++index >= args.Length
                        || !int.TryParse(args[index], out int parsedProcessId)
                        || parsedProcessId <= 0)
                    {
                        options = null!;
                        return false;
                    }

                    processId = parsedProcessId;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--concurrency":
                    if (++index >= args.Length
                        || !int.TryParse(args[index], out int parsedConcurrency)
                        || parsedConcurrency < 1)
                    {
                        options = null!;
                        return false;
                    }

                    concurrency = parsedConcurrency;
                    break;
                default:
                    options = null!;
                    return false;
            }
        }

        if (command.StartsWith("broker-", StringComparison.Ordinal)
            && includeSensitiveValues)
        {
            options = null!;
            return false;
        }

        options = new CliOptions(
            command,
            json,
            all,
            namesOnly,
            includeWindowEvidence,
            includeTracing,
            includeSensitiveValues,
            processId,
            help,
            concurrency);
        return true;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine(
            """
            Chromium Process Explorer

            Usage:
              cpe [process-tree] [--json] [--all] [--windows] [--concurrency N]
              cpe mojo-pipes [--json] [--names-only] [--concurrency N]
              cpe installations [--json] [--concurrency N]
              cpe cdp [--json] [--concurrency N]
              cpe renderer-origins [--json] [--trace] [--concurrency N]
              cpe process-details [--pid PID] [--json] [--include-sensitive] [--concurrency N]
              cpe diagnostics [--json] [--include-sensitive] [--concurrency N]
              cpe broker-probe [--json]
              cpe broker-process-details [--pid PID] [--json]
              cpe broker-installations [--json]
              cpe broker-diagnostics [--json]
              cpe broker-cdp [--json]

            Commands:
              process-tree  Show Chromium-related processes and their process ancestry.
              mojo-pipes    Inspect Mojo pipes and their server/client processes.
              installations Find Chromium browsers, runtimes, and applications.
              cdp           Discover configured and validated CDP transports.
              renderer-origins
                            Opt in to cooperative/CDP renderer-frame enrichment.
              process-details
                            Show detailed diagnostics for one PID or Chromium processes.
              diagnostics   Passively discover logs, dumps, traces, and diagnostic settings.
              broker-*      Invoke the corresponding typed operation through the broker.

            Options:
              --all            Include every process in the process tree.
              --json           Emit structured JSON.
              --names-only     Skip pipe endpoint handle inspection.
              --windows        Add optional HWND topology and WebView2 evidence.
              --trace          Add a bounded, version-sensitive CDP trace.
              --pid PID        Select one process for process-details.
              --include-sensitive
                               Include paths, command lines, and switch values.
              --concurrency N  Bound parallel process metadata queries.
              -h, --help       Show this help.
            """);
    }

    private sealed record CliOptions(
        string Command,
        bool Json,
        bool AllProcesses,
        bool NamesOnly,
        bool IncludeWindowEvidence,
        bool IncludeTracing,
        bool IncludeSensitiveValues,
        int? ProcessId,
        bool ShowHelp,
        int? MaximumConcurrency);

}
