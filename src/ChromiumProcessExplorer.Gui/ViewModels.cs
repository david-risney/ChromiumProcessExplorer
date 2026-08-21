using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using ChromiumProcessExplorer.Core;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Gui;

public sealed class ProcessTreeItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isStale;

    public ProcessTreeItemViewModel(
        string branchKey,
        ProcessPresentationDescriptor descriptor,
        bool isReference,
        bool isStale,
        IEnumerable<ProcessTreeItemViewModel> children)
    {
        BranchKey = branchKey;
        Descriptor = descriptor;
        IsReference = isReference;
        _isStale = isStale;
        Children = new ObservableCollection<ProcessTreeItemViewModel>(children);
    }

    public string BranchKey { get; }

    public ProcessPresentationDescriptor Descriptor { get; }

    public ProcessIdentity Identity => Descriptor.Identity;

    public int ProcessId => Identity.ProcessId;

    public string ImageName => Descriptor.Process.ImageName;

    public string? ExecutablePath => Descriptor.Process.ExecutablePath;

    public string? CommandLine => Descriptor.Process.CommandLine;

    public string Platform => Descriptor.Platform;

    public string Role => Descriptor.Role;

    public Brush PlatformBadgeBackground =>
        BadgePalette.GetPlatformBackground(Platform);

    public Brush PlatformBadgeForeground =>
        BadgePalette.GetPlatformForeground(Platform);

    public Brush RoleBadgeBackground => BadgePalette.GetRoleBackground(Role);

    public Brush RoleBadgeForeground => BadgePalette.GetRoleForeground(Role);

    public bool IsHost => Descriptor.IsHost;

    public bool HasWarning => Descriptor.HasWarning;

    public bool IsReference { get; }

    public string ReferenceLabel => IsReference ? "Same process" : string.Empty;

    public bool IsStale
    {
        get => _isStale;
        set
        {
            if (SetField(ref _isStale, value))
            {
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public string StateLabel => IsStale ? "Exited" : string.Empty;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public ObservableCollection<ProcessTreeItemViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public ProcessTreeItemViewModel CloneForRetention(
        IReadOnlySet<ProcessIdentity> currentIdentities)
    {
        ProcessTreeItemViewModel clone = new(
            BranchKey,
            Descriptor,
            IsReference,
            !currentIdentities.Contains(Identity),
            Children.Select(child =>
                child.CloneForRetention(currentIdentities)))
        {
            Icon = Icon,
            IsExpanded = IsExpanded,
            IsSelected = IsSelected,
        };
        return clone;
    }
}

public sealed record PropertyRow(string Label, string? Value);

public sealed record RelationshipDetailRow(
    string Direction,
    string Process,
    string Relationship,
    string Confidence,
    string Evidence);

public sealed record SwitchDetailRow(
    string Name,
    string? Value);

public sealed record PathDetailRow(
    string Kind,
    string? Value,
    string? Source,
    string? Confidence);

public sealed record DiagnosticDetailRow(
    string Kind,
    string Status,
    string? Location,
    string Detail);

public sealed record EvidenceDetailRow(
    string Source,
    string Detail,
    string? Confidence);

public sealed record ContextIssueViewModel(
    string Source,
    string Message);

public sealed class ProcessInspectorViewModel
{
    public required ProcessIdentity Identity { get; init; }

    public required string ImageName { get; init; }

    public required string Platform { get; init; }

    public required string Role { get; init; }

    public required bool IsStale { get; init; }

    public required bool IsLoadingDiagnostics { get; init; }

    public required ImageSource? Icon { get; init; }

    public required string? CommandLine { get; init; }

    public required string? PackageFullName { get; init; }

    public required bool PackageIdentityKnown { get; init; }

    public required IReadOnlyList<PropertyRow> Summary { get; init; }

    public required IReadOnlyList<RelationshipDetailRow> Relationships { get; init; }

    public required IReadOnlyList<PropertyRow> Runtime { get; init; }

    public required IReadOnlyList<PropertyRow> Executable { get; init; }

    public required IReadOnlyList<SwitchDetailRow> Switches { get; init; }

    public required IReadOnlyList<PathDetailRow> Paths { get; init; }

    public required IReadOnlyList<DiagnosticDetailRow> Diagnostics { get; init; }

    public required IReadOnlyList<EvidenceDetailRow> Evidence { get; init; }

    public required IReadOnlyList<ContextIssueViewModel> Issues { get; init; }
}

public sealed class InstallationItemViewModel : ObservableObject
{
    private ImageSource? _icon;

    public InstallationItemViewModel(ChromiumInstallation installation)
    {
        Installation = installation;
    }

    public ChromiumInstallation Installation { get; }

    public string Name => Installation.Name;

    public string Platform => Installation.Platform;

    public string Kind => Installation.Kind;

    public string? Version => Installation.Version;

    public string? Channel => Installation.Channel
        ?? (Installation.Kind == "Runtime"
            && Installation.Metadata.IsSharedRuntime == false
                ? "FixedApp"
                : null);

    public string InstallPath => Installation.InstallPath;

    public string? ExecutablePath => Installation.ExecutablePath;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public string RuntimeScope => Installation.Metadata.IsSharedRuntime switch
    {
        true => "Shared runtime",
        false => "App-local runtime",
        _ => "Runtime scope unknown",
    };

    public IReadOnlyList<PropertyRow> Details =>
    [
        new("Name", Installation.Name),
        new("Platform", Installation.Platform),
        new("Kind", Installation.Kind),
        new("Version", Installation.Version),
        new("Channel", Channel),
        new("Install path", Installation.InstallPath),
        new("Executable", Installation.ExecutablePath),
        new("Architecture", Installation.Metadata.Architecture),
        new("Publisher", Installation.Metadata.Publisher),
        new("Install type", Installation.Metadata.InstallType),
        new("Install source", Installation.Metadata.InstallSource),
        new("Resources", Installation.Metadata.ResourcesPath),
        new("Runtime", Installation.Metadata.RuntimePath),
        new("Runtime scope", RuntimeScope),
        new("Confidence", Installation.Metadata.Confidence),
        new("Application ID", Installation.Metadata.ApplicationId),
        new("Browser", Installation.Metadata.BrowserPlatform),
        new("Profile", Installation.Metadata.BrowserProfileName),
    ];

    public IReadOnlyList<EvidenceDetailRow> Evidence =>
        Installation.Evidence.Select(item => new EvidenceDetailRow(
            item.Source,
            item.Detail,
            null)).ToArray();
}

public sealed class DevToolsItemViewModel
{
    public DevToolsItemViewModel(CdpTransportInfo transport)
    {
        Transport = transport;
    }

    public CdpTransportInfo Transport { get; }

    public int ProcessId => Transport.ProcessId;

    public string Availability => Transport.Status switch
    {
        CdpTransportStatus.Validated => "DevTools available",
        CdpTransportStatus.AlreadyOwned => "Private pipe already in use",
        CdpTransportStatus.Unavailable => "Configured but unavailable",
        CdpTransportStatus.Discovered => "Endpoint discovered",
        _ => "Configured",
    };

    public string TransportLabel => Transport.Kind == CdpTransportKind.Tcp
        ? Transport.Port is int port
            ? $"TCP port {port}"
            : "TCP"
        : "Private pipe";

    public string? Browser => Transport.Browser;

    public string? Error => Transport.Error ?? Transport.Restriction;

    public IReadOnlyList<PropertyRow> Details =>
    [
        new("Process ID", ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture)),
        new("Availability", Availability),
        new("Transport", TransportLabel),
        new("Browser", Transport.Browser),
        new("Protocol", Transport.ProtocolVersion),
        new("Version endpoint", Transport.VersionEndpoint),
        new("WebSocket endpoint", Transport.WebSocketDebuggerUrl),
        new("Controller", Transport.ControllerProcessId is int controller
            ? $"{Transport.ControllerImageName ?? "process"} ({controller})"
            : null),
        new("Restriction", Transport.Restriction),
        new("Error", Transport.Error),
    ];
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGuiDiscoveryService _discovery;
    private readonly IProcessIconProvider _iconProvider;
    private readonly IExternalToolService _externalTools;
    private readonly IGuiSettingsStore? _settingsStore;
    private readonly string _productName = "Chromium Process Explorer";
    private readonly string _productVersion =
        ChromiumProcessExplorer.Core.ProductVersion.Version;
    private readonly Dictionary<ProcessIdentity, ProcessInspectorViewModel>
        _inspectorCache = [];
    private readonly HashSet<string> _dismissedProcessNotices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dismissedInstallationNotices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _autoRefreshInterval;
    private CancellationTokenSource? _processRefreshCancellation;
    private CancellationTokenSource? _installationRefreshCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private CancellationTokenSource? _installationFilterCancellation;
    private CancellationTokenSource? _autoRefreshCancellation;
    private Task? _autoRefreshTask;
    private Task? _installationFilterTask;
    private ChromiumDiscoveryResult? _processResult;
    private DiagnosticArtifactDiscoveryResult? _diagnosticsResult;
    private Task<DiagnosticArtifactDiscoveryResult>? _diagnosticsTask;
    private ProcessTreeItemViewModel? _selectedProcess;
    private ProcessInspectorViewModel? _processInspector;
    private InstallationItemViewModel? _selectedInstallation;
    private DevToolsItemViewModel? _selectedDevTools;
    private CommandLineTemplateViewModel? _selectedCommandLineTemplate;
    private string _processFilter = string.Empty;
    private string _installationFilter = string.Empty;
    private string? _mojoPipeFingerprint;
    private string _status = "Ready";
    private string _installationStatus = "Not scanned";
    private bool _autoRefreshProcesses = true;
    private bool _areAllProcessNodesExpanded;
    private bool _isRefreshingProcesses;
    private bool _isScanningInstallations;
    private bool _isLoadingSelection;
    private string _debugCommand;
    private string _futureDebuggerCommand;
    private string _processExplorerCommand;
    private string _additionalInstallationFoldersText;
    private string _settingsStatus;

    public MainViewModel(
        IGuiDiscoveryService discovery,
        IProcessIconProvider? iconProvider = null,
        TimeSpan? autoRefreshInterval = null,
        GuiSettings? settings = null,
        IGuiSettingsStore? settingsStore = null,
        IExternalToolService? externalTools = null,
        string? settingsLoadError = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _discovery = discovery;
        _iconProvider = iconProvider ?? new WindowsProcessIconProvider();
        _externalTools = externalTools ?? new WindowsExternalToolService();
        _settingsStore = settingsStore;
        GuiSettings initialSettings = settings ?? new GuiSettings();
        _autoRefreshProcesses = initialSettings.AutoRefreshProcesses;
        _debugCommand = initialSettings.DebugCommand;
        _futureDebuggerCommand = initialSettings.FutureDebuggerCommand;
        _processExplorerCommand = initialSettings.ProcessExplorerCommand;
        _additionalInstallationFoldersText = string.Join(
            Environment.NewLine,
            initialSettings.AdditionalInstallationFolders);
        _settingsStatus = settingsLoadError ?? "Settings are saved automatically.";
        foreach (CommandLineTemplateSettings template in
            initialSettings.CommandLineTemplates)
        {
            CommandLineTemplates.Add(
                new CommandLineTemplateViewModel(template, SaveSettings));
        }

        SelectedCommandLineTemplate = CommandLineTemplates.FirstOrDefault();
        _autoRefreshInterval = autoRefreshInterval ?? TimeSpan.FromSeconds(2);
        if (_autoRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(autoRefreshInterval),
                "The auto-refresh interval must be positive.");
        }
    }

    public ObservableCollection<ProcessTreeItemViewModel> ProcessRoots { get; } = [];

    public ObservableCollection<ProcessTreeItemViewModel> FilteredProcessRoots { get; } = [];

    public ObservableCollection<InstallationItemViewModel> Installations { get; } = [];

    public ObservableCollection<InstallationItemViewModel>
        FilteredInstallations
    { get; } = [];

    public ObservableCollection<DevToolsItemViewModel> DevTools { get; } = [];

    public ObservableCollection<ContextIssueViewModel> ProcessNotices { get; } = [];

    public ObservableCollection<ContextIssueViewModel> InstallationNotices { get; } = [];

    public ObservableCollection<ContextIssueViewModel> DevToolsNotices { get; } = [];

    public ObservableCollection<CommandLineTemplateViewModel>
        CommandLineTemplates
    { get; } = [];

    public ProcessTreeItemViewModel? SelectedProcess
    {
        get => _selectedProcess;
        private set
        {
            if (ReferenceEquals(_selectedProcess, value))
            {
                return;
            }

            if (_selectedProcess is not null)
            {
                _selectedProcess.IsSelected = false;
            }

            if (SetField(ref _selectedProcess, value)
                && value is not null)
            {
                value.IsSelected = true;
            }
        }
    }

    public ProcessInspectorViewModel? ProcessInspector
    {
        get => _processInspector;
        private set => SetField(ref _processInspector, value);
    }

    public InstallationItemViewModel? SelectedInstallation
    {
        get => _selectedInstallation;
        set => SetField(ref _selectedInstallation, value);
    }

    public DevToolsItemViewModel? SelectedDevTools
    {
        get => _selectedDevTools;
        set => SetField(ref _selectedDevTools, value);
    }

    public CommandLineTemplateViewModel? SelectedCommandLineTemplate
    {
        get => _selectedCommandLineTemplate;
        set => SetField(ref _selectedCommandLineTemplate, value);
    }

    public string ProcessFilter
    {
        get => _processFilter;
        set
        {
            if (SetField(ref _processFilter, value))
            {
                UpdateFilteredProcessRoots();
            }
        }
    }

    public string InstallationFilter
    {
        get => _installationFilter;
        set
        {
            if (SetField(ref _installationFilter, value))
            {
                ScheduleInstallationFilter();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsBusy => IsRefreshingProcesses || IsScanningInstallations;

    public string InstallationStatus
    {
        get => _installationStatus;
        private set => SetField(ref _installationStatus, value);
    }

    public bool IsRefreshingProcesses
    {
        get => _isRefreshingProcesses;
        private set
        {
            if (SetField(ref _isRefreshingProcesses, value))
            {
                OnPropertyChanged(nameof(IsProcessActivityBusy));
            }
        }
    }

    public bool IsScanningInstallations
    {
        get => _isScanningInstallations;
        private set => SetField(ref _isScanningInstallations, value);
    }

    public bool IsLoadingSelection
    {
        get => _isLoadingSelection;
        private set
        {
            if (SetField(ref _isLoadingSelection, value))
            {
                OnPropertyChanged(nameof(IsProcessActivityBusy));
            }
        }
    }

    public bool IsProcessActivityBusy =>
        IsRefreshingProcesses || IsLoadingSelection;

    public bool AutoRefreshProcesses
    {
        get => _autoRefreshProcesses;
        set
        {
            if (SetField(ref _autoRefreshProcesses, value))
            {
                SaveSettings();
            }
        }
    }

    public string ProductName => _productName;

    public string ProductVersion => _productVersion;

    public string DebugCommand
    {
        get => _debugCommand;
        set
        {
            if (SetField(ref _debugCommand, value))
            {
                SaveSettings();
            }
        }
    }

    public string FutureDebuggerCommand
    {
        get => _futureDebuggerCommand;
        set
        {
            if (SetField(ref _futureDebuggerCommand, value))
            {
                SaveSettings();
            }
        }
    }

    public string ProcessExplorerCommand
    {
        get => _processExplorerCommand;
        set
        {
            if (SetField(ref _processExplorerCommand, value))
            {
                SaveSettings();
            }
        }
    }

    public string AdditionalInstallationFoldersText
    {
        get => _additionalInstallationFoldersText;
        set
        {
            if (SetField(ref _additionalInstallationFoldersText, value))
            {
                SaveSettings();
            }
        }
    }

    public string SettingsStatus
    {
        get => _settingsStatus;
        private set => SetField(ref _settingsStatus, value);
    }

    public string ProcessExpansionButtonText =>
        AreAllProcessNodesExpanded ? "Collapse all" : "Expand all";

    public bool AreAllProcessNodesExpanded
    {
        get => _areAllProcessNodesExpanded;
        private set
        {
            if (SetField(ref _areAllProcessNodesExpanded, value))
            {
                OnPropertyChanged(nameof(ProcessExpansionButtonText));
            }
        }
    }

    public async Task RefreshProcessesAsync()
    {
        if (IsRefreshingProcesses)
        {
            return;
        }

        await RunRefreshAsync(
            "Refreshing Chromium processes",
            RefreshTarget.Processes,
            async cancellationToken =>
            {
                ChromiumDiscoveryResult result =
                    await _discovery.DiscoverProcessesAsync(cancellationToken);
                await ApplyProcessResultAsync(result, cancellationToken);
                Status = $"Found {result.ProcessGraph.Nodes.Count} captured processes; "
                    + $"{Flatten(ProcessRoots).Count(item => !item.IsStale)} "
                    + "Chromium and associated-host entries are displayed.";
            });
    }

    public async Task RefreshInstallationsAsync()
    {
        if (IsScanningInstallations)
        {
            return;
        }

        await RunRefreshAsync(
            "Scanning Chromium installations",
            RefreshTarget.Installations,
            async cancellationToken =>
            {
                _installationFilterCancellation?.Cancel();
                string? selectedPath = SelectedInstallation?.InstallPath;
                InstallationDiscoveryResult result =
                    await _discovery.DiscoverInstallationsAsync(
                        GetAdditionalInstallationFolders(),
                        cancellationToken);
                Replace(
                    Installations,
                    result.Installations
                        .Select(installation => new InstallationItemViewModel(
                            installation))
                        .OrderBy(item => GetInstallationOrder(item.Kind))
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
                await PopulateInstallationIconsAsync(
                    Installations,
                    cancellationToken);
                ApplyInstallationFilter(InstallationFilter);
                Replace(
                    InstallationNotices,
                    result.Issues
                        .Select(issue => new ContextIssueViewModel(
                            issue.Stage,
                            issue.Message))
                        .Where(issue => !_dismissedInstallationNotices.Contains(
                            issue.Message))
                        .DistinctBy(
                            issue => issue.Message,
                            StringComparer.OrdinalIgnoreCase));
                SelectedInstallation = selectedPath is null
                    ? null
                    : FilteredInstallations.FirstOrDefault(item =>
                        string.Equals(
                            item.InstallPath,
                            selectedPath,
                            StringComparison.OrdinalIgnoreCase));
                InstallationStatus = $"Found {Installations.Count} installations in "
                    + $"{result.Statistics.Elapsed.TotalSeconds:F1} seconds.";
            });
    }

    public async Task SelectProcessAsync(ProcessTreeItemViewModel? process)
    {
        if (process is not null
            && SelectedProcess?.Identity == process.Identity
            && ProcessInspector?.Identity == process.Identity)
        {
            SelectedProcess = process;
            return;
        }

        SelectedProcess = process;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        if (process is null)
        {
            ProcessInspector = null;
            Status = "Select a process to inspect it.";
            return;
        }

        ProcessInspector = BuildInspector(
            process,
            detail: null,
            diagnostics: _diagnosticsResult,
            isLoadingDiagnostics: !process.IsStale);
        if (process.IsStale)
        {
            if (_inspectorCache.TryGetValue(
                process.Identity,
                out ProcessInspectorViewModel? cached))
            {
                ProcessInspector = CreateStaleInspector(process, cached);
            }

            Status = $"PID {process.ProcessId} exited; showing retained snapshot data.";
            return;
        }

        CancellationTokenSource cancellation = new();
        _selectionCancellation = cancellation;
        IsLoadingSelection = true;
        Status = $"Loading details for PID {process.ProcessId}.";
        try
        {
            ProcessDetailsResult details =
                await _discovery.DiscoverProcessDetailsAsync(
                    process.ProcessId,
                    cancellation.Token);
            if (cancellation.IsCancellationRequested
                || SelectedProcess?.Identity != process.Identity)
            {
                return;
            }

            ProcessDetailEntry? detail = details.Processes.SingleOrDefault(
                item => item.Identity == process.Identity);
            if (detail is null)
            {
                ProcessInspector = BuildInspector(
                    process,
                    null,
                    _diagnosticsResult,
                    isLoadingDiagnostics: false,
                    additionalIssue: "The process exited or its PID was reused before details were captured.");
                Status = $"PID {process.ProcessId} exited or was reused.";
                return;
            }

            ProcessInspector = BuildInspector(
                process,
                detail,
                _diagnosticsResult,
                isLoadingDiagnostics: _diagnosticsResult is null);
            _inspectorCache[process.Identity] = ProcessInspector;
            Status = $"Loading diagnostics for PID {process.ProcessId}.";
            DiagnosticArtifactDiscoveryResult diagnostics =
                await GetDiagnosticsAsync(cancellation.Token);
            if (SelectedProcess?.Identity != process.Identity)
            {
                return;
            }

            ProcessInspector = BuildInspector(
                process,
                detail,
                diagnostics,
                isLoadingDiagnostics: false);
            _inspectorCache[process.Identity] = ProcessInspector;
            Status = $"Loaded details for PID {process.ProcessId}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (SelectedProcess?.Identity != process.Identity)
            {
                return;
            }

            ProcessInspector = BuildInspector(
                process,
                null,
                _diagnosticsResult,
                isLoadingDiagnostics: false,
                additionalIssue: exception.Message);
            Status = $"Unable to load PID {process.ProcessId}: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_selectionCancellation, cancellation))
            {
                IsLoadingSelection = false;
            }
        }

    }

    public async Task SelectDevToolsAsync(DevToolsItemViewModel? item)
    {
        SelectedDevTools = item;
        if (item is null)
        {
            return;
        }

        ProcessTreeItemViewModel? process = Flatten(ProcessRoots)
            .FirstOrDefault(candidate =>
                candidate.ProcessId == item.ProcessId
                && !candidate.IsStale);
        if (process is not null)
        {
            await SelectProcessAsync(process);
        }
    }

    public void Cancel()
    {
        CancelProcessRefresh();
        CancelInstallationScan();
        StopAutoRefresh();
        _selectionCancellation?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        _installationFilterCancellation?.Cancel();
        _installationFilterCancellation?.Dispose();
        _installationFilterCancellation = null;
        GC.SuppressFinalize(this);
    }

    public void CancelProcessRefresh()
    {
        _processRefreshCancellation?.Cancel();
    }

    public void CancelInstallationScan()
    {
        _installationRefreshCancellation?.Cancel();
    }

    public void StartAutoRefresh()
    {
        if (_autoRefreshTask is not null)
        {
            return;
        }

        _autoRefreshCancellation = new CancellationTokenSource();
        _autoRefreshTask = WatchMojoPipesAsync(
            _autoRefreshCancellation.Token);
    }

    public void StopAutoRefresh()
    {
        _autoRefreshCancellation?.Cancel();
        _autoRefreshCancellation?.Dispose();
        _autoRefreshCancellation = null;
        _autoRefreshTask = null;
    }

    public void ToggleProcessExpansion()
    {
        bool expand = !AreAllProcessNodesExpanded;
        SetExpanded(FilteredProcessRoots, expand);
        AreAllProcessNodesExpanded = expand;
    }

    public void DismissNotice(ContextIssueViewModel? notice)
    {
        if (notice is null)
        {
            return;
        }

        if (ProcessNotices.Remove(notice))
        {
            _dismissedProcessNotices.Add(notice.Message);
        }

        if (InstallationNotices.Remove(notice))
        {
            _dismissedInstallationNotices.Add(notice.Message);
        }
    }

    public void DismissProcessNotice(ContextIssueViewModel? notice)
    {
        DismissNotice(notice);
    }

    public static string GetProcessLineText(ProcessTreeItemViewModel process)
    {
        return $"{process.ImageName} ({process.ProcessId})  "
            + $"{process.Platform}  {process.Role}";
    }

    public async Task<string> GetProcessDetailsTextAsync(
        ProcessTreeItemViewModel process)
    {
        await SelectProcessAsync(process);
        ProcessInspectorViewModel? inspector = ProcessInspector;
        if (inspector is null || inspector.Identity != process.Identity)
        {
            return GetProcessLineText(process);
        }

        StringBuilder text = new();
        text.AppendLine(GetProcessLineText(process));
        AppendRows(text, "Summary", inspector.Summary);
        AppendRows(text, "Runtime", inspector.Runtime);
        AppendRows(text, "Executable", inspector.Executable);
        if (!string.IsNullOrWhiteSpace(inspector.CommandLine))
        {
            text.AppendLine().AppendLine("Command line")
                .AppendLine(inspector.CommandLine);
        }

        AppendRows(
            text,
            "Switches",
            inspector.Switches.Select(item =>
                new PropertyRow(item.Name, item.Value)));
        AppendRows(
            text,
            "Paths",
            inspector.Paths.Select(item =>
                new PropertyRow(
                    item.Kind,
                    string.Join(
                        " | ",
                        new[] { item.Value, item.Source, item.Confidence }
                            .Where(value => !string.IsNullOrWhiteSpace(value))))));
        AppendRows(
            text,
            "Diagnostics",
            inspector.Diagnostics.Select(item =>
                new PropertyRow(
                    item.Kind,
                    $"{item.Status} | {item.Location} | {item.Detail}")));
        AppendRows(
            text,
            "Relationships",
            inspector.Relationships.Select(item =>
                new PropertyRow(
                    $"{item.Direction} {item.Process}",
                    $"{item.Relationship} | {item.Confidence} | {item.Evidence}")));
        AppendRows(
            text,
            "Evidence",
            inspector.Evidence.Select(item =>
                new PropertyRow(
                    item.Source,
                    $"{item.Detail} | {item.Confidence}")));
        AppendRows(
            text,
            "Issues",
            inspector.Issues.Select(item =>
                new PropertyRow(item.Source, item.Message)));
        return text.ToString().TrimEnd();
    }

    public static string GetInstallationLineText(
        InstallationItemViewModel installation)
    {
        return string.Join(
            "  ",
            new[]
            {
                installation.Name,
                installation.Platform,
                installation.Kind,
                installation.Version,
                installation.Channel,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public static string GetInstallationDetailsText(
        InstallationItemViewModel installation)
    {
        StringBuilder text = new();
        text.AppendLine(GetInstallationLineText(installation));
        AppendRows(text, "Details", installation.Details);
        AppendRows(
            text,
            "Evidence",
            installation.Evidence.Select(item =>
                new PropertyRow(item.Source, item.Detail)));
        return text.ToString().TrimEnd();
    }

    public void DebugProcess(ProcessTreeItemViewModel? process)
    {
        if (process is null || process.IsStale)
        {
            AddProcessActionIssue(
                "Select a running process before starting a debugger.");
            return;
        }

        RunProcessAction(
            () => _externalTools.DebugProcess(
                process.ProcessId,
                DebugCommand),
            $"Started debugger for PID {process.ProcessId}.");
    }

    public void OpenProcessExplorer(ProcessTreeItemViewModel? process)
    {
        if (process is null || process.IsStale)
        {
            AddProcessActionIssue(
                "Select a running process before opening Process Explorer.");
            return;
        }

        RunProcessAction(
            () => _externalTools.OpenProcessExplorer(
                process.ProcessId,
                ProcessExplorerCommand),
            $"Opened Process Explorer for PID {process.ProcessId}.");
    }

    public async Task DebugFutureLaunchesAsync(
        ProcessTreeItemViewModel? process)
    {
        if (process is null)
        {
            AddProcessActionIssue(
                "Select a process before configuring future debugging.");
            return;
        }

        await SelectProcessAsync(process);
        string? packageFullName = ProcessInspector?.Identity == process.Identity
            ? ProcessInspector.PackageFullName
            : null;
        if (ProcessInspector?.Identity != process.Identity
            || !ProcessInspector.PackageIdentityKnown)
        {
            AddProcessActionIssue(
                "Package identity could not be determined; future debugging was not changed.");
            return;
        }

        RunProcessAction(
            () => _externalTools.DebugFutureLaunches(
                process.ImageName,
                packageFullName,
                FutureDebuggerCommand),
            packageFullName is null
                ? $"Started elevated future-debug setup for {process.ImageName}."
                : $"Started elevated packaged-app debug setup for {packageFullName}.");
    }

    public void DebugFutureLaunches(
        InstallationItemViewModel? installation)
    {
        if (installation is null)
        {
            AddInstallationActionIssue(
                "Select an install before configuring future debugging.");
            return;
        }

        string? imageName = installation.Installation.ExecutablePath is string path
            ? Path.GetFileName(path)
            : null;
        if (string.IsNullOrWhiteSpace(imageName))
        {
            AddInstallationActionIssue(
                "The selected install has no executable to configure.");
            return;
        }

        try
        {
            _externalTools.DebugFutureLaunches(
                imageName,
                installation.Installation.Metadata.PackageIdentity
                    ?.PackageFullName,
                FutureDebuggerCommand);
            InstallationStatus = "Started elevated future-debug setup for "
                + $"{installation.Name}.";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or FormatException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            AddInstallationActionIssue(exception.Message);
        }
    }

    public void AddCommandLineTemplate()
    {
        CommandLineTemplateViewModel template = new(
            new CommandLineTemplateSettings(),
            SaveSettings);
        CommandLineTemplates.Add(template);
        SelectedCommandLineTemplate = template;
        SaveSettings();
    }

    public void RemoveSelectedCommandLineTemplate()
    {
        if (SelectedCommandLineTemplate is null)
        {
            return;
        }

        int index = CommandLineTemplates.IndexOf(
            SelectedCommandLineTemplate);
        CommandLineTemplates.Remove(SelectedCommandLineTemplate);
        SelectedCommandLineTemplate = CommandLineTemplates.Count == 0
            ? null
            : CommandLineTemplates[Math.Min(
                index,
                CommandLineTemplates.Count - 1)];
        SaveSettings();
    }

    public IReadOnlyList<CommandLineTemplateViewModel>
        GetApplicableTemplates(ProcessTreeItemViewModel process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.IsStale
            || process.IsHost
            || !string.Equals(
                process.Role,
                "Browser",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                process.Platform,
                "WebView2",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(process.ExecutablePath)
            || string.IsNullOrWhiteSpace(process.CommandLine))
        {
            return [];
        }

        return GetApplicableTemplates(process.ImageName);
    }

    public IReadOnlyList<CommandLineTemplateViewModel>
        GetApplicableTemplates(InstallationItemViewModel installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (string.Equals(
                installation.Platform,
                "WebView2",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            return [];
        }

        return GetApplicableTemplates(
            Path.GetFileName(installation.ExecutablePath));
    }

    public void LaunchWithTemplate(
        ProcessTreeItemViewModel process,
        CommandLineTemplateViewModel template)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(template);
        if (!GetApplicableTemplates(process).Contains(template))
        {
            AddProcessActionIssue(
                "The selected template does not apply to this process.");
            return;
        }

        LaunchWithTemplate(
            process.ExecutablePath!,
            process.CommandLine,
            template,
            AddProcessActionIssue,
            message => Status = message);
    }

    public void LaunchWithTemplate(
        InstallationItemViewModel installation,
        CommandLineTemplateViewModel template)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(template);
        if (!GetApplicableTemplates(installation).Contains(template))
        {
            AddInstallationActionIssue(
                "The selected template does not apply to this install.");
            return;
        }

        LaunchWithTemplate(
            installation.ExecutablePath!,
            null,
            template,
            AddInstallationActionIssue,
            message => InstallationStatus = message);
    }

    private string[] GetAdditionalInstallationFolders()
    {
        return AdditionalInstallationFoldersText
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SaveSettings()
    {
        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            _settingsStore.Save(new GuiSettings
            {
                AutoRefreshProcesses = AutoRefreshProcesses,
                DebugCommand = DebugCommand,
                FutureDebuggerCommand = FutureDebuggerCommand,
                ProcessExplorerCommand = ProcessExplorerCommand,
                AdditionalInstallationFolders =
                    GetAdditionalInstallationFolders(),
                CommandLineTemplates = CommandLineTemplates
                    .Select(template => template.ToSettings())
                    .ToArray(),
            });
            SettingsStatus = "Settings saved.";
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            SettingsStatus = $"Settings could not be saved: {exception.Message}";
        }
    }

    private void RunProcessAction(Action action, string successStatus)
    {
        try
        {
            action();
            Status = successStatus;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or FormatException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            AddProcessActionIssue(exception.Message);
        }
    }

    private void AddProcessActionIssue(string message)
    {
        if (!ProcessNotices.Any(notice =>
            string.Equals(
                notice.Message,
                message,
                StringComparison.OrdinalIgnoreCase)))
        {
            ProcessNotices.Add(new ContextIssueViewModel(
                "external-tool",
                message));
        }

        Status = message;
    }

    private void AddInstallationActionIssue(string message)
    {
        if (!InstallationNotices.Any(notice =>
            string.Equals(
                notice.Message,
                message,
                StringComparison.OrdinalIgnoreCase)))
        {
            InstallationNotices.Add(new ContextIssueViewModel(
                "external-tool",
                message));
        }

        InstallationStatus = message;
    }

    private CommandLineTemplateViewModel[]
        GetApplicableTemplates(string executableName)
    {
        return CommandLineTemplates
            .Where(template => template.AppliesTo(executableName))
            .ToArray();
    }

    private void LaunchWithTemplate(
        string executablePath,
        string? commandLine,
        CommandLineTemplateViewModel template,
        Action<string> reportError,
        Action<string> reportSuccess)
    {
        if (!template.IsValid)
        {
            reportError(template.ValidationError ?? "The template is invalid.");
            return;
        }

        try
        {
            IReadOnlyList<string> arguments =
                CommandLineTemplateTransformer.Apply(
                    commandLine,
                    template.ToSettings());
            _externalTools.LaunchExecutable(executablePath, arguments);
            reportSuccess(
                $"Launched {Path.GetFileName(executablePath)} with "
                + $"template \"{template.Name}\".");
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or FormatException
            or InvalidOperationException
            or IOException
            or System.Security.SecurityException
            or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            reportError(exception.Message);
        }
    }

    private async Task ApplyProcessResultAsync(
        ChromiumDiscoveryResult result,
        CancellationToken cancellationToken)
    {
        ProcessIdentity? selectedIdentity = SelectedProcess?.Identity;
        ProcessTreeItemViewModel[] previousRoots = ProcessRoots.ToArray();
        ProcessPresentationTree presentation =
            ProcessPresentationTreeBuilder.Build(result);
        List<ProcessTreeItemViewModel> currentRoots = presentation.Roots
            .Select(CreateTreeItem)
            .ToList();
        IReadOnlySet<ProcessIdentity> currentIdentities =
            presentation.Processes.Keys.ToHashSet();
        MergeNewlyExited(previousRoots, currentRoots, currentIdentities);
        Replace(ProcessRoots, currentRoots);
        _processResult = result;
        _mojoPipeFingerprint = CreateMojoFingerprint(
            result.MojoPipeInspection.Pipes.Select(pipe => pipe.Name));
        _diagnosticsResult = null;
        _diagnosticsTask = null;
        await PopulateIconsAsync(ProcessRoots, cancellationToken);
        UpdateFilteredProcessRoots();

        IReadOnlyList<DiscoveryIssue> issues =
            ProcessPresentationTreeBuilder.CollectIssues(result);
        Replace(
            ProcessNotices,
            issues.Where(issue => issue.ProcessId is null)
                .Select(issue => new ContextIssueViewModel(
                    issue.Stage,
                    issue.Message))
                .Where(issue => !_dismissedProcessNotices.Contains(
                    issue.Message))
                .DistinctBy(
                    issue => issue.Message,
                    StringComparer.OrdinalIgnoreCase));
        Replace(
            DevTools,
            result.Cdp.Transports
                .Select(transport => new DevToolsItemViewModel(transport))
                .OrderBy(item => item.ProcessId));
        Replace(
            DevToolsNotices,
            result.Cdp.Issues.Select(issue => new ContextIssueViewModel(
                issue.Stage,
                issue.Message)));
        SelectedDevTools = DevTools.FirstOrDefault();

        if (selectedIdentity is not null)
        {
            ProcessTreeItemViewModel? replacement = Flatten(FilteredProcessRoots)
                .FirstOrDefault(item =>
                    item.Identity == selectedIdentity);
            SelectedProcess = replacement;
            if (replacement is null)
            {
                ProcessInspector = null;
            }
            else if (replacement.IsStale
                && _inspectorCache.TryGetValue(
                    replacement.Identity,
                    out ProcessInspectorViewModel? cached))
            {
                ProcessInspector = CreateStaleInspector(
                    replacement,
                    cached);
            }
        }

        ProcessTreeItemViewModel CreateTreeItem(ProcessPresentationBranch branch)
        {
            return new ProcessTreeItemViewModel(
                branch.BranchKey,
                branch.Process,
                branch.IsReference,
                isStale: false,
                branch.Children.Select(CreateTreeItem));
        }
    }

    private async Task PopulateIconsAsync(
        IEnumerable<ProcessTreeItemViewModel> roots,
        CancellationToken cancellationToken)
    {
        IGrouping<ProcessIdentity, ProcessTreeItemViewModel>[] groups =
            Flatten(roots)
                .GroupBy(item => item.Identity)
                .ToArray();
        await Task.WhenAll(groups.Select(async group =>
        {
            ProcessTreeItemViewModel first = group.First();
            ImageSource? icon = await _iconProvider.GetIconAsync(
                first.Descriptor.Process.ExecutablePath,
                cancellationToken);
            foreach (ProcessTreeItemViewModel item in group)
            {
                item.Icon = icon;
            }
        }));
    }

    private static ProcessInspectorViewModel CreateStaleInspector(
        ProcessTreeItemViewModel item,
        ProcessInspectorViewModel source)
    {
        return new ProcessInspectorViewModel
        {
            Identity = source.Identity,
            ImageName = source.ImageName,
            Platform = source.Platform,
            Role = source.Role,
            IsStale = true,
            IsLoadingDiagnostics = false,
            Icon = item.Icon ?? source.Icon,
            CommandLine = source.CommandLine,
            PackageFullName = source.PackageFullName,
            PackageIdentityKnown = source.PackageIdentityKnown,
            Summary = source.Summary
                .Select(row => row.Label == "State"
                    ? row with { Value = "Exited" }
                    : row)
                .ToArray(),
            Relationships = source.Relationships,
            Runtime = source.Runtime,
            Executable = source.Executable,
            Switches = source.Switches,
            Paths = source.Paths,
            Diagnostics = source.Diagnostics,
            Evidence = source.Evidence,
            Issues = source.Issues,
        };
    }

    private void UpdateFilteredProcessRoots()
    {
        string filter = ProcessFilter.Trim();
        ProcessIdentity? selectedIdentity = SelectedProcess?.Identity;
        Replace(
            FilteredProcessRoots,
            ProcessRoots
                .Select(root => Filter(root, filter))
                .Where(root => root is not null)
                .Select(root => root!));
        if (selectedIdentity is not null)
        {
            SelectedProcess = Flatten(FilteredProcessRoots)
                .FirstOrDefault(item => item.Identity == selectedIdentity);
        }

        ProcessTreeItemViewModel? Filter(
            ProcessTreeItemViewModel item,
            string filter)
        {
            ProcessTreeItemViewModel[] children = item.Children
                .Select(child => Filter(child, filter))
                .Where(child => child is not null)
                .Select(child => child!)
                .ToArray();
            bool matches = filter.Length == 0
                || item.ImageName.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase)
                || item.Platform.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase)
                || item.Role.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase)
                || item.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                    .Contains(filter, StringComparison.OrdinalIgnoreCase);
            if (!matches && children.Length == 0)
            {
                return null;
            }

            return new ProcessTreeItemViewModel(
                item.BranchKey,
                item.Descriptor,
                item.IsReference,
                item.IsStale,
                children)
            {
                Icon = item.Icon,
                IsExpanded = filter.Length > 0 && children.Length > 0,
                IsSelected = item.Identity == selectedIdentity,
            };
        }

        AreAllProcessNodesExpanded =
            filter.Length > 0 && FilteredProcessRoots.Count > 0;
    }

    private void ScheduleInstallationFilter()
    {
        _installationFilterCancellation?.Cancel();
        _installationFilterCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _installationFilterCancellation = cancellation;
        string filter = InstallationFilter;
        InstallationItemViewModel[] installations = Installations.ToArray();
        _installationFilterTask = ApplyAsync();

        async Task ApplyAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(175), cancellation.Token);
                InstallationItemViewModel[] matches = await Task.Run(
                    () => FilterInstallations(installations, filter),
                    cancellation.Token);
                if (!cancellation.IsCancellationRequested
                    && string.Equals(
                        filter,
                        InstallationFilter,
                        StringComparison.Ordinal))
                {
                    string? selectedPath = SelectedInstallation?.InstallPath;
                    Replace(FilteredInstallations, matches);
                    SelectedInstallation = selectedPath is null
                        ? null
                        : FilteredInstallations.FirstOrDefault(item =>
                            string.Equals(
                                item.InstallPath,
                                selectedPath,
                                StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void ApplyInstallationFilter(string filter)
    {
        string? selectedPath = SelectedInstallation?.InstallPath;
        InstallationItemViewModel[] matches =
            FilterInstallations(Installations.ToArray(), filter);
        Replace(
            FilteredInstallations,
            matches);
        SelectedInstallation = selectedPath is null
            ? null
            : FilteredInstallations.FirstOrDefault(item =>
                string.Equals(
                    item.InstallPath,
                    selectedPath,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static InstallationItemViewModel[] FilterInstallations(
        IReadOnlyList<InstallationItemViewModel> installations,
        string filterValue)
    {
        string filter = filterValue.Trim();
        return installations.Where(item =>
            filter.Length == 0
            || item.Name.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase)
            || item.Platform.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase)
            || item.Kind.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase)
            || item.InstallPath.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase)
            || (item.Version?.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Channel?.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
    }

    private async Task PopulateInstallationIconsAsync(
        IEnumerable<InstallationItemViewModel> installations,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(installations.Select(async installation =>
        {
            installation.Icon = await _iconProvider.GetIconAsync(
                installation.Installation.ExecutablePath
                    ?? installation.InstallPath,
                cancellationToken);
        }));
    }

    private static void AppendRows(
        StringBuilder text,
        string heading,
        IEnumerable<PropertyRow> rows)
    {
        PropertyRow[] values = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Value))
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        text.AppendLine().AppendLine(heading);
        foreach (PropertyRow row in values)
        {
            text.Append(row.Label).Append(": ").AppendLine(row.Value);
        }
    }

    private async Task WatchMojoPipesAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(_autoRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!AutoRefreshProcesses || IsProcessActivityBusy)
                {
                    continue;
                }

                MojoPipeEnumerationResult result =
                    await _discovery.EnumerateMojoPipesAsync(cancellationToken);
                string fingerprint = CreateMojoFingerprint(
                    result.Pipes.Select(pipe => pipe.Name));
                if (_mojoPipeFingerprint is null)
                {
                    _mojoPipeFingerprint = fingerprint;
                    continue;
                }

                if (string.Equals(
                    fingerprint,
                    _mojoPipeFingerprint,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                _mojoPipeFingerprint = fingerprint;
                Status = "Process changes detected; refreshing.";
                await RefreshProcessesAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AddRefreshFailure(
                RefreshTarget.Processes,
                $"Automatic process refresh stopped: {exception.Message}");
        }
    }

    private static string CreateMojoFingerprint(IEnumerable<string> names)
    {
        return string.Join(
            '\n',
            names.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    private static void SetExpanded(
        IEnumerable<ProcessTreeItemViewModel> roots,
        bool isExpanded)
    {
        foreach (ProcessTreeItemViewModel item in Flatten(roots))
        {
            item.IsExpanded = isExpanded;
        }
    }

    private async Task<DiagnosticArtifactDiscoveryResult> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        if (_diagnosticsResult is not null)
        {
            return _diagnosticsResult;
        }

        _diagnosticsTask ??=
            _discovery.DiscoverDiagnosticsAsync(CancellationToken.None).AsTask();
        _diagnosticsResult = await _diagnosticsTask.WaitAsync(cancellationToken);
        return _diagnosticsResult;
    }

    private ProcessInspectorViewModel BuildInspector(
        ProcessTreeItemViewModel item,
        ProcessDetailEntry? detail,
        DiagnosticArtifactDiscoveryResult? diagnostics,
        bool isLoadingDiagnostics,
        string? additionalIssue = null)
    {
        ProcessSnapshotEntry snapshot = item.Descriptor.Process;
        List<PropertyRow> runtime =
        [
            new("Platform", item.Platform),
            new("Role", item.Role),
            new("Role source", detail?.RoleSource),
        ];
        AddRuntimeDetails(runtime, item.ProcessId);

        List<PathDetailRow> paths = [];
        AddPath(paths, "Executable", detail?.ExecutablePath.Value);
        AddPath(paths, "User data", detail?.UserDataDirectory.Value);
        AddRuntimePaths(paths, item.ProcessId);

        List<RelationshipDetailRow> relationships = [];
        List<EvidenceDetailRow> evidence = snapshot.Evidence
            .Select(value => new EvidenceDetailRow(
                "process-snapshot",
                value,
                null))
            .ToList();
        if (_processResult is not null)
        {
            foreach (ProcessGraphEdge edge in _processResult.ProcessGraph.Edges.Where(
                edge => edge.Source == item.Identity
                    || edge.Target == item.Identity))
            {
                bool outgoing = edge.Source == item.Identity;
                ProcessIdentity otherIdentity = outgoing
                    ? edge.Target
                    : edge.Source;
                ProcessGraphNode? other =
                    _processResult.ProcessGraph.FindNode(otherIdentity);
                relationships.Add(new RelationshipDetailRow(
                    outgoing ? "To" : "From",
                    other is null
                        ? otherIdentity.ProcessId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : $"{other.Process.ImageName} ({otherIdentity.ProcessId})",
                    edge.Type.ToString(),
                    edge.Evidence.Confidence.ToString(),
                    FormatRelationshipEvidence(edge.Evidence)));
                evidence.Add(new EvidenceDetailRow(
                    edge.Evidence.Source,
                    $"{edge.Type}: {FormatRelationshipEvidence(edge.Evidence)}",
                    edge.Evidence.Confidence.ToString()));
            }
        }

        List<DiagnosticDetailRow> diagnosticRows = [];
        if (_processResult is not null)
        {
            foreach (CdpTransportInfo transport in _processResult.Cdp.Transports.Where(
                transport => transport.ProcessId == item.ProcessId))
            {
                DevToolsItemViewModel devTools = new(transport);
                diagnosticRows.Add(new DiagnosticDetailRow(
                    "DevTools",
                    devTools.Availability,
                    transport.WebSocketDebuggerUrl ?? transport.VersionEndpoint,
                    transport.Error
                        ?? transport.Restriction
                        ?? devTools.TransportLabel));
            }
        }

        if (diagnostics is not null)
        {
            diagnosticRows.AddRange(diagnostics.Artifacts
                .Where(artifact => artifact.AssociatedProcessIds.Contains(
                    item.ProcessId))
                .Select(artifact => new DiagnosticDetailRow(
                    artifact.Kind.ToString(),
                    artifact.Status.ToString(),
                    artifact.Location.Value,
                    string.Join("; ", artifact.Evidence))));
            diagnosticRows.AddRange(diagnostics.Configuration
                .Where(configuration =>
                    configuration.Identity == item.Identity)
                .Select(configuration => new DiagnosticDetailRow(
                    configuration.Category,
                    configuration.Severity,
                    configuration.Value.Value,
                    configuration.Detail)));
        }

        List<ContextIssueViewModel> issues = [];
        if (_processResult is not null)
        {
            issues.AddRange(ProcessPresentationTreeBuilder
                .CollectIssues(_processResult)
                .Where(issue => issue.ProcessId == item.ProcessId)
                .Select(issue => new ContextIssueViewModel(
                    issue.Stage,
                    issue.Message)));
        }

        if (detail is not null)
        {
            issues.AddRange(detail.Issues.Select(issue =>
                new ContextIssueViewModel(issue.Stage, issue.Message)));
        }

        if (diagnostics is not null)
        {
            issues.AddRange(diagnostics.Issues
                .Where(issue => issue.ProcessId is null
                    || issue.ProcessId == item.ProcessId)
                .Select(issue => new ContextIssueViewModel(
                    issue.Stage,
                    issue.Message)));
        }

        if (additionalIssue is not null)
        {
            issues.Add(new ContextIssueViewModel(
                "process-selection",
                additionalIssue));
        }

        return new ProcessInspectorViewModel
        {
            Identity = item.Identity,
            ImageName = item.ImageName,
            Platform = item.Platform,
            Role = item.Role,
            IsStale = item.IsStale,
            IsLoadingDiagnostics = isLoadingDiagnostics,
            Icon = item.Icon,
            CommandLine = detail?.CommandLine.Value
                ?? snapshot.CommandLine,
            PackageFullName = detail?.PackageFullName,
            PackageIdentityKnown = detail is not null,
            Summary =
            [
                new("Process ID", item.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
                new("Created", item.Identity.CreationTime?.ToString("O")),
                new("State", item.IsStale ? "Exited" : "Running"),
                new("Image", item.ImageName),
                new("Parent PID", snapshot.ParentProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            ],
            Relationships = relationships,
            Runtime = runtime,
            Executable =
            [
                new("Path", detail?.ExecutablePath.Value
                    ?? snapshot.ExecutablePath),
                new("File version", detail?.ExecutableVersion?.FileVersion),
                new("Product version", detail?.ExecutableVersion?.ProductVersion),
                new("Product", detail?.ExecutableVersion?.ProductName),
                new("Company", detail?.ExecutableVersion?.CompanyName),
                new("Architecture", detail?.Architecture),
                new("Native architecture", detail?.NativeArchitecture),
                new("Integrity", detail?.IntegrityLevel),
                new("Elevated", FormatBoolean(detail?.IsElevated)),
                new("Package", detail?.PackageFullName),
            ],
            Switches = detail?.Switches.Select(processSwitch =>
                new SwitchDetailRow(
                    $"--{processSwitch.Name}",
                    processSwitch.Value.Value)).ToArray() ?? [],
            Paths = paths,
            Diagnostics = diagnosticRows,
            Evidence = evidence,
            Issues = issues
                .Distinct()
                .ToArray(),
        };
    }

    private void AddRuntimeDetails(
        List<PropertyRow> rows,
        int processId)
    {
        if (_processResult is null)
        {
            return;
        }

        CefProcessInfo? cef = _processResult.CefRuntime.Processes
            .FirstOrDefault(process => process.ProcessId == processId);
        if (cef is not null)
        {
            rows.Add(new("CEF layout", cef.Layout.ToString()));
            rows.Add(new("CEF raw type", cef.RawProcessType));
            rows.Add(new("CEF utility", cef.UtilitySubType ?? cef.UtilityRole));
            rows.Add(new("CEF wrappers", string.Join(", ", cef.Wrappers)));
        }

        ElectronProcessInfo? electron = _processResult.ElectronRuntime.Processes
            .FirstOrDefault(process => process.ProcessId == processId);
        if (electron is not null)
        {
            rows.Add(new("Electron process type", electron.RawProcessType));
            rows.Add(new("Electron utility", electron.UtilitySubType));
            rows.Add(new("Electron window type", electron.WindowType));
            rows.Add(new("Application", electron.PackageName));
            rows.Add(new("Application version", electron.PackageVersion));
        }

        AdditionalRuntimeProcessInfo? additional =
            _processResult.AdditionalRuntime.Processes
                .FirstOrDefault(process => process.ProcessId == processId);
        if (additional is not null)
        {
            rows.Add(new("Classification confidence",
                additional.Confidence.ToString()));
            rows.Add(new("Annotations",
                string.Join(", ", additional.Annotations)));
        }
    }

    private void AddRuntimePaths(
        ICollection<PathDetailRow> paths,
        int processId)
    {
        if (_processResult is null)
        {
            return;
        }

        CefProcessInfo? cef = _processResult.CefRuntime.Processes
            .FirstOrDefault(process => process.ProcessId == processId);
        if (cef is not null)
        {
            AddPath(paths, "CEF user data", cef.RuntimePaths.UserDataDirectory);
            AddPath(paths, "CEF log", cef.RuntimePaths.LogFile);
            AddPath(paths, "CEF resources", cef.RuntimePaths.ResourcesDirectory);
            AddPath(paths, "CEF locales", cef.RuntimePaths.LocalesDirectory);
            AddPath(paths, "CEF subprocess", cef.RuntimePaths.BrowserSubprocessPath);
            AddPath(paths, "CEF crash reports", cef.RuntimePaths.CrashReportDirectory);
        }

        ElectronProcessInfo? electron = _processResult.ElectronRuntime.Processes
            .FirstOrDefault(process => process.ProcessId == processId);
        if (electron is not null)
        {
            AddElectronPath(paths, "Install", electron.Paths.InstallDirectory);
            AddElectronPath(paths, "Package root", electron.Paths.PackageRoot);
            AddElectronPath(paths, "Resources", electron.Paths.ResourcesDirectory);
            AddElectronPath(paths, "Application", electron.Paths.ApplicationPath);
            AddElectronPath(paths, "User data", electron.Paths.UserDataDirectory);
            AddElectronPath(paths, "Session data", electron.Paths.SessionDataDirectory);
            AddElectronPath(paths, "Logs", electron.Paths.LogsDirectory);
            AddElectronPath(paths, "Crash dumps", electron.Paths.CrashDumpsDirectory);
            AddElectronPath(paths, "Temporary data", electron.Paths.TempDirectory);
        }
    }

    private static void AddElectronPath(
        ICollection<PathDetailRow> paths,
        string kind,
        ElectronPathObservation? path)
    {
        if (path is not null)
        {
            paths.Add(new PathDetailRow(
                kind,
                path.Value,
                path.Source,
                path.Confidence.ToString()));
        }
    }

    private static void AddPath(
        ICollection<PathDetailRow> paths,
        string kind,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            paths.Add(new PathDetailRow(kind, value, null, null));
        }
    }

    private static string FormatRelationshipEvidence(
        ProcessRelationshipEvidence evidence)
    {
        string values = string.Join(
            ", ",
            evidence.RawValues
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{pair.Key}: {pair.Value}"));
        return string.IsNullOrWhiteSpace(values)
            ? evidence.Source
            : $"{evidence.Source}; {values}";
    }

    private static string? FormatBoolean(bool? value)
    {
        return value switch
        {
            true => "Yes",
            false => "No",
            null => null,
        };
    }

    private static void MergeNewlyExited(
        IReadOnlyList<ProcessTreeItemViewModel> previousRoots,
        List<ProcessTreeItemViewModel> currentRoots,
        IReadOnlySet<ProcessIdentity> currentIdentities)
    {
        Dictionary<string, ProcessTreeItemViewModel> currentByBranch =
            Flatten(currentRoots).ToDictionary(item => item.BranchKey);
        foreach (ProcessTreeItemViewModel previousRoot in previousRoots)
        {
            Merge(previousRoot, null);
        }

        void Merge(
            ProcessTreeItemViewModel previous,
            ProcessTreeItemViewModel? currentParent)
        {
            if (previous.IsStale)
            {
                return;
            }

            if (!currentIdentities.Contains(previous.Identity))
            {
                ProcessTreeItemViewModel retained =
                    previous.CloneForRetention(currentIdentities);
                if (currentParent is null)
                {
                    currentRoots.Add(retained);
                }
                else
                {
                    currentParent.Children.Add(retained);
                }

                return;
            }

            ProcessTreeItemViewModel? current =
                currentByBranch.GetValueOrDefault(previous.BranchKey)
                ?? Flatten(currentRoots).FirstOrDefault(item =>
                    item.Identity == previous.Identity);
            foreach (ProcessTreeItemViewModel child in previous.Children)
            {
                Merge(child, current);
            }
        }
    }

    private async Task RunRefreshAsync(
        string activity,
        RefreshTarget target,
        Func<CancellationToken, Task> operation)
    {
        using CancellationTokenSource cancellation = new();
        SetRefreshCancellation(target, cancellation);
        SetRefreshState(target, true);
        SetRefreshStatus(target, activity);
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetRefreshStatus(
                target,
                $"{activity} cancelled. Previous results were preserved.");
        }
        catch (Exception exception)
        {
            AddRefreshFailure(target, exception.Message);
            SetRefreshStatus(target, $"{activity} failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(
                GetRefreshCancellation(target),
                cancellation))
            {
                SetRefreshCancellation(target, null);
                SetRefreshState(target, false);
            }
        }
    }

    private CancellationTokenSource? GetRefreshCancellation(
        RefreshTarget target)
    {
        return target == RefreshTarget.Processes
            ? _processRefreshCancellation
            : _installationRefreshCancellation;
    }

    private void SetRefreshCancellation(
        RefreshTarget target,
        CancellationTokenSource? cancellation)
    {
        if (target == RefreshTarget.Processes)
        {
            _processRefreshCancellation = cancellation;
        }
        else
        {
            _installationRefreshCancellation = cancellation;
        }
    }

    private void AddRefreshFailure(
        RefreshTarget target,
        string message)
    {
        ObservableCollection<ContextIssueViewModel> notices =
            target == RefreshTarget.Processes
                ? ProcessNotices
                : InstallationNotices;
        if (target == RefreshTarget.Processes
            && _dismissedProcessNotices.Contains(message))
        {
            return;
        }

        if (target == RefreshTarget.Installations
            && _dismissedInstallationNotices.Contains(message))
        {
            return;
        }

        if (notices.Any(notice =>
            string.Equals(
                notice.Message,
                message,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        notices.Add(new ContextIssueViewModel("gui", message));
    }

    private void SetRefreshState(
        RefreshTarget target,
        bool isRefreshing)
    {
        if (target == RefreshTarget.Processes)
        {
            IsRefreshingProcesses = isRefreshing;
        }
        else
        {
            IsScanningInstallations = isRefreshing;
        }

        OnPropertyChanged(nameof(IsBusy));
    }

    private void SetRefreshStatus(
        RefreshTarget target,
        string value)
    {
        if (target == RefreshTarget.Processes)
        {
            Status = value;
        }
        else
        {
            InstallationStatus = value;
        }
    }

    private static int GetInstallationOrder(string kind)
    {
        return kind switch
        {
            "Browser" => 0,
            "Runtime" => 1,
            "BrowserApp" => 2,
            _ => 3,
        };
    }

    private static IEnumerable<ProcessTreeItemViewModel> Flatten(
        IEnumerable<ProcessTreeItemViewModel> roots)
    {
        foreach (ProcessTreeItemViewModel root in roots)
        {
            yield return root;
            foreach (ProcessTreeItemViewModel child in Flatten(root.Children))
            {
                yield return child;
            }
        }
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

    private enum RefreshTarget
    {
        Processes,
        Installations,
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
