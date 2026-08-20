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
        : this(new MainViewModel(new GuiDiscoveryService(
            Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The GUI executable path is unavailable."))))
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
}
