using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ChromiumProcessExplorer.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
        : this(CreateViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await Task.WhenAll(
                _viewModel.RefreshProcessesAsync(),
                _viewModel.RefreshInstallationsAsync());
            _viewModel.StartAutoRefresh();
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshProcessesAsync();
    }

    private async void RefreshInstallations_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshInstallationsAsync();
    }

    private void CancelProcessRefresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelProcessRefresh();
    }

    private void CancelInstallationScan_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelInstallationScan();
    }

    private void ToggleProcessExpansion_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.ToggleProcessExpansion();
    }

    private void DismissNotice_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.DismissNotice(
            ((FrameworkElement)sender).Tag as ContextIssueViewModel);
    }

    private void CopyProcess_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext
            is ProcessTreeItemViewModel process)
        {
            SetClipboardText(MainViewModel.GetProcessLineText(process));
        }
    }

    private void DebugProcess_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DebugProcess(
            ((FrameworkElement)sender).DataContext
                as ProcessTreeItemViewModel);
    }

    private async void DebugFutureProcess_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _viewModel.DebugFutureLaunchesAsync(
            ((FrameworkElement)sender).DataContext
                as ProcessTreeItemViewModel);
    }

    private void OpenProcessExplorer_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.OpenProcessExplorer(
            ((FrameworkElement)sender).DataContext
                as ProcessTreeItemViewModel);
    }

    private async void KillProcessTree_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext
            is not ProcessTreeItemViewModel process)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            $"Terminate {process.ImageName} ({process.ProcessId}) and all of "
                + "its descendant processes?",
            "Kill process tree",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes)
        {
            await _viewModel.KillProcessTreeAsync(process);
        }
    }

    private async void CopyProcessDetails_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext
            is ProcessTreeItemViewModel process)
        {
            SetClipboardText(
                await _viewModel.GetProcessDetailsTextAsync(process));
        }
    }

    private void CopyInstallation_Click(object sender, RoutedEventArgs e)
    {
        if ((((FrameworkElement)sender).Tag
                ?? ((FrameworkElement)sender).DataContext)
            is InstallationItemViewModel installation)
        {
            SetClipboardText(
                MainViewModel.GetInstallationLineText(installation));
        }
    }

    private void CopyInstallationDetails_Click(
        object sender,
        RoutedEventArgs e)
    {
        if ((((FrameworkElement)sender).Tag
                ?? ((FrameworkElement)sender).DataContext)
            is InstallationItemViewModel installation)
        {
            SetClipboardText(
                MainViewModel.GetInstallationDetailsText(installation));
        }
    }

    private void DebugFutureInstallation_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.DebugFutureLaunches(
            (((FrameworkElement)sender).Tag
                ?? ((FrameworkElement)sender).DataContext)
                as InstallationItemViewModel);
    }

    private void ProcessContextMenu_Opened(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu
            && contextMenu.DataContext is ProcessTreeItemViewModel process)
        {
            PopulateTemplateMenu(
                contextMenu,
                process,
                _viewModel.GetFavoriteApplicableTemplates(process));
        }
    }

    private void InstallationContextMenu_Opened(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is ContextMenu contextMenu
            && contextMenu.PlacementTarget is DataGrid dataGrid
            && dataGrid.SelectedItem is InstallationItemViewModel installation)
        {
            PopulateTemplateMenu(
                contextMenu,
                installation,
                _viewModel.GetFavoriteApplicableTemplates(installation));
        }
    }

    private void LaunchWithTemplate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag
            is not TemplateLaunchRequest request)
        {
            return;
        }

        if (request.Target is ProcessTreeItemViewModel process)
        {
            _viewModel.LaunchWithTemplate(process, request.Template);
        }
        else if (request.Target is InstallationItemViewModel installation)
        {
            _viewModel.LaunchWithTemplate(installation, request.Template);
        }
    }

    private void AddCommandLineTemplate_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.AddCommandLineTemplate();
    }

    private void RemoveCommandLineTemplate_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.RemoveSelectedCommandLineTemplate();
    }

    private void AddCommandLineSuggestion_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.AddSelectedCommandLineSuggestion();
    }

    private void CommandLineSuggestion_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        _viewModel.AddSelectedCommandLineSuggestion();
    }

    private void CommandLineAddParts_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        UpdateCommandLineSuggestionContext();
    }

    private void CommandLineAddParts_SelectionChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (CommandLineAddPartsTextBox.IsKeyboardFocusWithin)
        {
            UpdateCommandLineSuggestionContext();
        }
    }

    private void CommandLineAddParts_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ScheduleCommandLineSuggestionVisibilityUpdate();
    }

    private void CommandLineSuggestionPanel_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ScheduleCommandLineSuggestionVisibilityUpdate();
    }

    private void ScheduleCommandLineSuggestionVisibilityUpdate()
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool visible = CommandLineAddPartsTextBox.IsKeyboardFocusWithin
                || CommandLineSuggestionPanel.IsKeyboardFocusWithin;
            _viewModel.SetCommandLineSuggestionContext(
                GetCurrentCommandLineAddPart(),
                visible);
        });
    }

    private void RunCommandLineTemplate_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.RunCommandLineTarget(
            ((Button)sender).CommandParameter
                as CommandLineRunTargetViewModel);
    }

    private void UpdateCommandLineSuggestionContext()
    {
        _viewModel.SetCommandLineSuggestionContext(
            GetCurrentCommandLineAddPart(),
            isVisible: true);
    }

    private string GetCurrentCommandLineAddPart()
    {
        string text = CommandLineAddPartsTextBox.Text;
        int caret = Math.Clamp(
            CommandLineAddPartsTextBox.CaretIndex,
            0,
            text.Length);
        int start = caret == 0
            ? 0
            : text.LastIndexOf('\n', caret - 1) + 1;
        int end = text.IndexOf('\n', caret);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].Trim('\r').Trim();
    }

    private void Installations_Sorting(
        object sender,
        DataGridSortingEventArgs e)
    {
        if (e.Column.SortMemberPath is not ("Version" or "Channel"))
        {
            if (CollectionViewSource.GetDefaultView(
                    ((DataGrid)sender).ItemsSource)
                is ListCollectionView defaultView)
            {
                defaultView.CustomSort = null;
            }

            return;
        }

        e.Handled = true;
        ListCollectionView view = (ListCollectionView)
            CollectionViewSource.GetDefaultView(
                ((DataGrid)sender).ItemsSource);
        ListSortDirection direction =
            e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        foreach (DataGridColumn column in ((DataGrid)sender).Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = direction;
        view.CustomSort = new InstallationComparer(
            e.Column.SortMemberPath,
            direction);
    }

    private void Installations_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null and not DataGridRow)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private async void ProcessTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        await _viewModel.SelectProcessAsync(
            e.NewValue as ProcessTreeItemViewModel);
    }

    private async void DevTools_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        await _viewModel.SelectDevToolsAsync(
            ((DataGrid)sender).SelectedItem as DevToolsItemViewModel);
    }

    private async void RefreshDevTools_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _viewModel.RefreshProcessesAsync();
    }

    private async void OpenDevTools_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _viewModel.OpenSelectedDevToolsAsync();
    }

    private void OpenRemoteDevTools_Click(
        object sender,
        RoutedEventArgs e)
    {
        _viewModel.OpenSelectedRemoteDevTools();
    }

    private async void ExtractProcessInternals_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _viewModel.ExtractProcessInternalsAsync();
    }

    private void OpenDetailTarget_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not DetailOpenTarget target)
        {
            return;
        }

        TabItem? tab = MainTabControl.SelectedItem as TabItem;
        _viewModel.OpenDetailTarget(
            target,
            string.Equals(
                tab?.Header as string,
                "Installs",
                StringComparison.Ordinal));
    }

    private void SetClipboardText(string text)
    {
        ExternalException? failure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException exception)
            {
                failure = exception;
                Thread.Sleep(75);
            }
        }

        MessageBox.Show(
            this,
            $"The clipboard is currently unavailable: {failure?.Message}",
            "Copy failed",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void PopulateTemplateMenu(
        ContextMenu contextMenu,
        object target,
        IReadOnlyList<CommandLineTemplateViewModel> templates)
    {
        MenuItem? submenu = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                "command-templates",
                StringComparison.Ordinal));
        if (submenu is null)
        {
            return;
        }

        submenu.Items.Clear();
        submenu.IsEnabled = templates.Count > 0;
        if (templates.Count == 0)
        {
            submenu.Items.Add(new MenuItem
            {
                Header = "No applicable templates",
                IsEnabled = false,
            });
            return;
        }

        foreach (CommandLineTemplateViewModel template in templates)
        {
            MenuItem item = new()
            {
                Header = template.Name,
                Tag = new TemplateLaunchRequest(target, template),
            };
            item.Click += LaunchWithTemplate_Click;
            submenu.Items.Add(item);
        }
    }

    private sealed class InstallationComparer(
        string property,
        ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            InstallationItemViewModel? left = x as InstallationItemViewModel;
            InstallationItemViewModel? right = y as InstallationItemViewModel;
            int comparison = property == "Version"
                ? CompareVersions(left?.Version, right?.Version)
                : GetChannelRank(left?.Channel).CompareTo(
                    GetChannelRank(right?.Channel));
            if (comparison == 0)
            {
                comparison = StringComparer.OrdinalIgnoreCase.Compare(
                    left?.Name,
                    right?.Name);
            }

            return direction == ListSortDirection.Ascending
                ? comparison
                : -comparison;
        }

        private static int CompareVersions(string? left, string? right)
        {
            int[] leftParts = ParseVersion(left);
            int[] rightParts = ParseVersion(right);
            int count = Math.Max(leftParts.Length, rightParts.Length);
            for (int index = 0; index < count; index++)
            {
                int leftPart = index < leftParts.Length ? leftParts[index] : 0;
                int rightPart = index < rightParts.Length ? rightParts[index] : 0;
                int comparison = leftPart.CompareTo(rightPart);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static int[] ParseVersion(string? version)
        {
            return (version ?? string.Empty)
                .Split(['.', ',', '+', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out int value)
                    ? value
                    : (int?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
        }

        private static int GetChannelRank(string? channel)
        {
            return channel?.Trim().ToUpperInvariant() switch
            {
                "STABLE" => 0,
                "BETA" => 1,
                "DEV" => 2,
                "CANARY" => 3,
                "INTERNAL" => 4,
                "FIXEDAPP" => 5,
                _ => 6,
            };
        }
    }

    private static MainViewModel CreateViewModel()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The GUI executable path is unavailable.");
        JsonGuiSettingsStore settingsStore = new();
        GuiSettingsLoadResult loadResult = settingsStore.Load();
        return new MainViewModel(
            new GuiDiscoveryService(executablePath),
            settings: loadResult.Settings,
            settingsStore: settingsStore,
            settingsLoadError: loadResult.Error);
    }

    private sealed record TemplateLaunchRequest(
        object Target,
        CommandLineTemplateViewModel Template);
}
