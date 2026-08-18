using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChromiumProcessExplorer.Core.Broker;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Gui;

public sealed record ProcessRow(
    ProcessIdentity Identity,
    int ParentProcessId,
    string ImageName,
    string? Role,
    string Evidence,
    bool IsStale)
{
    public int ProcessId => Identity.ProcessId;

    public string State => IsStale ? "Exited or stale" : "Current";

    public string DisplayName => $"{ImageName} ({ProcessId})";
}

public sealed record RelationshipRow(
    int SourceProcessId,
    string Source,
    int TargetProcessId,
    string Target,
    string Relationship,
    string EdgeClass,
    string Confidence,
    string Evidence);

public sealed record ProcessTreeRow(
    int ProcessId,
    string DisplayName,
    ObservableCollection<ProcessTreeRow> Children);

public sealed record MojoRow(
    string PipeName,
    int? ServerProcessId,
    string? Server,
    int? ClientProcessId,
    string? Client,
    string? State);

public sealed record CdpRow(
    int ProcessId,
    string Kind,
    string Status,
    int? Port,
    string? Browser,
    string? Restriction,
    string? Error);

public sealed record InstallationRow(
    string Name,
    string Platform,
    string Kind,
    string? Version,
    string? Channel,
    string InstallType,
    string Confidence,
    string InstallPath);

public sealed record IssueRow(
    string Source,
    string Message,
    int? ProcessId);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IGuiDiscoveryService _discovery;
    private CancellationTokenSource? _operationCancellation;
    private int _operationActive;
    private ChromiumDiscoveryResult? _processResult;
    private InstallationDiscoveryResult? _installationResult;
    private ProcessDetailsResult? _detailsResult;
    private BrokerResponse? _brokerResponse;
    private ProcessRow? _selectedProcess;
    private string _selectedProcessDetails = "Select a process to view details.";
    private string _jsonText = "{}";
    private string _status = "Ready";
    private string _brokerStatus = "Not checked";
    private bool _isBusy;

    public MainViewModel(IGuiDiscoveryService discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    public ObservableCollection<ProcessTreeRow> ProcessTree { get; } = [];

    public ObservableCollection<RelationshipRow> Relationships { get; } = [];

    public ObservableCollection<MojoRow> MojoPipes { get; } = [];

    public ObservableCollection<CdpRow> CdpTransports { get; } = [];

    public ObservableCollection<InstallationRow> Installations { get; } = [];

    public ObservableCollection<IssueRow> Issues { get; } = [];

    public ProcessRow? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
            {
                SelectedProcessDetails = value is null
                    ? "Select a process to view details."
                    : JsonSerializer.Serialize(value, JsonOptions);
            }
        }
    }

    public string SelectedProcessDetails
    {
        get => _selectedProcessDetails;
        private set => SetField(ref _selectedProcessDetails, value);
    }

    public string JsonText
    {
        get => _jsonText;
        private set => SetField(ref _jsonText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string BrokerStatus
    {
        get => _brokerStatus;
        private set => SetField(ref _brokerStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public async Task RefreshProcessesAsync()
    {
        await RunOperationAsync(
            "Refreshing process graph",
            async cancellationToken =>
            {
                ChromiumDiscoveryResult result =
                    await _discovery.DiscoverProcessesAsync(cancellationToken);
                ApplyProcessResult(result);
                await ProbeBrokerCoreAsync(cancellationToken);
                Status = $"Captured {Processes.Count(row => !row.IsStale)} "
                    + $"current processes and {Relationships.Count} relationships.";
            });
    }

    public async Task RefreshInstallationsAsync()
    {
        await RunOperationAsync(
            "Scanning installations",
            async cancellationToken =>
            {
                InstallationDiscoveryResult result =
                    await _discovery.DiscoverInstallationsAsync(cancellationToken);
                _installationResult = result;
                Replace(
                    Installations,
                    result.Installations.Select(installation => new InstallationRow(
                        installation.Name,
                        installation.Platform,
                        installation.Kind,
                        installation.Version,
                        installation.Channel,
                        installation.Metadata.InstallType,
                        installation.Metadata.Confidence,
                        installation.InstallPath)));
                AddIssues("installations", result.Issues);
                UpdateJson();
                Status = $"Found {Installations.Count} installations in "
                    + $"{result.Statistics.Elapsed.TotalSeconds:F1} seconds.";
            });
    }

    public async Task LoadSelectedProcessDetailsAsync()
    {
        ProcessRow? selected = SelectedProcess;
        if (selected is null)
        {
            return;
        }

        if (selected.IsStale)
        {
            SelectedProcessDetails = JsonSerializer.Serialize(
                new
                {
                    selected,
                    Note = "The process is no longer in the current snapshot.",
                },
                JsonOptions);
            return;
        }

        await RunOperationAsync(
            $"Loading PID {selected.ProcessId}",
            async cancellationToken =>
            {
                ProcessDetailsResult result =
                    await _discovery.DiscoverProcessDetailsAsync(
                        selected.ProcessId,
                        cancellationToken);
                _detailsResult = result;
                AddIssues("process-details", result.Issues);
                SelectedProcessDetails = JsonSerializer.Serialize(
                    result,
                    JsonOptions);
                UpdateJson();
                Status = result.Processes.Count == 0
                    ? $"PID {selected.ProcessId} exited before details were captured."
                    : $"Loaded details for PID {selected.ProcessId}.";
            });
    }

    public async Task ProbeBrokerAsync()
    {
        await RunOperationAsync(
            "Checking broker",
            async cancellationToken =>
            {
                await ProbeBrokerCoreAsync(cancellationToken);
                Status = "Broker status updated.";
            });
    }

    public void Cancel()
    {
        _operationCancellation?.Cancel();
    }

    public string CreateJsonExport()
    {
        UpdateJson();
        return JsonText;
    }

    private async Task ProbeBrokerCoreAsync(CancellationToken cancellationToken)
    {
        _brokerResponse = await _discovery.ProbeBrokerAsync(cancellationToken);
        BrokerStatus = _brokerResponse.Ok
            ? _brokerResponse.Partial
                ? "Running (unelevated or partial)"
                : "Running elevated"
            : $"{_brokerResponse.Error?.Code}: {_brokerResponse.Error?.Message}";
        UpdateJson();
    }

    private void ApplyProcessResult(ChromiumDiscoveryResult result)
    {
        Dictionary<ProcessIdentity, ProcessRow> previous = Processes
            .ToDictionary(row => row.Identity);
        HashSet<int> relevantProcessIds = result.ProcessGraph.Edges
            .Where(edge => edge.Type != ProcessRelationshipType.OsParent)
            .SelectMany(edge => new[]
            {
                edge.Source.ProcessId,
                edge.Target.ProcessId,
            })
            .Concat(result.Processes
                .Where(process => process.IsLikelyChromium)
                .Select(process => process.ProcessId))
            .Concat(result.CefRuntime.Processes.Select(process => process.ProcessId))
            .Concat(result.ElectronRuntime.Processes.Select(process => process.ProcessId))
            .Concat(result.WebView2Runtime.Processes.Select(process => process.ProcessId))
            .ToHashSet();
        ProcessGraph graph = result.ProcessGraph.CreateFilteredView(
            relevantProcessIds);
        ProcessRow[] currentRows = graph.Nodes
            .Select(node => CreateProcessRow(node.Process, isStale: false))
            .ToArray();
        HashSet<ProcessIdentity> currentIdentities = currentRows
            .Select(row => row.Identity)
            .ToHashSet();
        ProcessRow[] staleRows = previous.Values
            .Where(row => !currentIdentities.Contains(row.Identity))
            .Select(row => row with { IsStale = true })
            .ToArray();
        Replace(
            Processes,
            currentRows.Concat(staleRows)
                .OrderBy(row => row.ProcessId)
                .ThenBy(row => row.IsStale));

        Dictionary<int, string> names = graph.Nodes.ToDictionary(
            node => node.Process.ProcessId,
            node => node.Process.ImageName);
        Replace(
            Relationships,
            graph.Edges.Select(edge => new RelationshipRow(
                edge.Source.ProcessId,
                names.GetValueOrDefault(edge.Source.ProcessId, "unknown"),
                edge.Target.ProcessId,
                names.GetValueOrDefault(edge.Target.ProcessId, "unknown"),
                edge.Type.ToString(),
                edge.Type == ProcessRelationshipType.OsParent
                    ? "OS parent"
                    : "Logical/evidence",
                edge.Evidence.Confidence.ToString(),
                FormatEvidence(edge.Evidence))));

        ProcessTree relatedTree = result.ProcessTree.CreateRelatedView(
            relevantProcessIds);
        Replace(
            ProcessTree,
            relatedTree.Roots.Select(CreateTreeRow));

        Replace(
            MojoPipes,
            result.MojoPipeInspection.Pipes.SelectMany(CreateMojoRows));
        Replace(
            CdpTransports,
            result.Cdp.Transports.Select(transport => new CdpRow(
                transport.ProcessId,
                transport.Kind.ToString(),
                transport.Status.ToString(),
                transport.Port,
                transport.Browser,
                transport.Restriction,
                transport.Error)));
        AddIssues("process-discovery", result.Issues);
        AddIssues("mojo", result.MojoPipeInspection.Issues);
        AddIssues("cdp", result.Cdp.Issues);
        foreach (TimedOutHandleQuery timeout
            in result.MojoPipeInspection.TimedOutQueries)
        {
            Issues.Add(new IssueRow(
                "mojo-timeout",
                $"Handle 0x{timeout.HandleValue:X} timed out during "
                    + $"{timeout.QueryStage} after "
                    + $"{timeout.Elapsed.TotalMilliseconds:F0} ms.",
                timeout.OwnerProcessId));
        }

        _processResult = result;
        UpdateJson();
    }

    private async Task RunOperationAsync(
        string activity,
        Func<CancellationToken, Task> operation)
    {
        if (Interlocked.CompareExchange(
            ref _operationActive,
            1,
            0) != 0)
        {
            Status = "Another operation is already running.";
            return;
        }

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        IsBusy = true;
        Status = activity;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = $"{activity} cancelled. Previous results were preserved.";
        }
        catch (Exception exception)
        {
            Issues.Add(new IssueRow("gui", exception.Message, null));
            Status = $"{activity} failed: {exception.Message}";
        }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
            Volatile.Write(ref _operationActive, 0);
        }
    }

    private void UpdateJson()
    {
        JsonText = JsonSerializer.Serialize(
            new
            {
                Processes = _processResult is null
                    ? null
                    : new
                    {
                        _processResult.CapturedAt,
                        _processResult.Processes,
                        ProcessGraph = new
                        {
                            _processResult.ProcessGraph.Nodes,
                            _processResult.ProcessGraph.Edges,
                        },
                        _processResult.MojoPipeInspection,
                        _processResult.Cdp,
                        _processResult.CefRuntime,
                        _processResult.WebView2Runtime,
                        _processResult.ElectronRuntime,
                        _processResult.AdditionalRuntime,
                        _processResult.Issues,
                    },
                ProcessDetails = _detailsResult,
                Installations = _installationResult,
                Broker = _brokerResponse,
                DisplayedIssues = Issues,
            },
            JsonOptions);
    }

    private void AddIssues(
        string source,
        IEnumerable<DiscoveryIssue> issues)
    {
        foreach (DiscoveryIssue issue in issues)
        {
            Issues.Add(new IssueRow(
                $"{source}:{issue.Stage}",
                issue.Message,
                issue.ProcessId));
        }
    }

    private static ProcessRow CreateProcessRow(
        ProcessSnapshotEntry process,
        bool isStale)
    {
        return new ProcessRow(
            new ProcessIdentity(process.ProcessId, process.CreationTime),
            process.ParentProcessId,
            process.ImageName,
            process.ChromiumProcessType,
            string.Join("; ", process.Evidence),
            isStale);
    }

    private static ProcessTreeRow CreateTreeRow(ProcessTreeNode node)
    {
        return new ProcessTreeRow(
            node.Process.ProcessId,
            $"{node.Process.ImageName} ({node.Process.ProcessId})"
                + (node.Process.ChromiumProcessType is null
                    ? string.Empty
                    : $" - {node.Process.ChromiumProcessType}"),
            new ObservableCollection<ProcessTreeRow>(
                node.Children.Select(CreateTreeRow)));
    }

    private static IEnumerable<MojoRow> CreateMojoRows(MojoPipeInfo pipe)
    {
        if (pipe.Connections.Count == 0)
        {
            return
            [
                new MojoRow(
                    pipe.Name,
                    null,
                    null,
                    null,
                    null,
                    "Endpoint not resolved"),
            ];
        }

        return pipe.Connections.Select(connection => new MojoRow(
            pipe.Name,
            connection.ServerProcessId,
            connection.ServerImageName,
            connection.ClientProcessId,
            connection.ClientImageName,
            connection.State));
    }

    private static string FormatEvidence(ProcessRelationshipEvidence evidence)
    {
        string raw = string.Join(
            ", ",
            evidence.RawValues.Select(pair => $"{pair.Key}={pair.Value}"));
        return string.IsNullOrEmpty(raw)
            ? evidence.Source
            : $"{evidence.Source}: {raw}";
    }

    private static void Replace<T>(
        ObservableCollection<T> collection,
        IEnumerable<T> items)
    {
        collection.Clear();
        foreach (T item in items)
        {
            collection.Add(item);
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
