using System.IO;
using System.Windows;
using Microsoft.Win32;

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
        Loaded += async (_, _) => await _viewModel.RefreshProcessesAsync();
        Closed += (_, _) => _viewModel.Cancel();
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshProcessesAsync();
    }

    private async void RefreshInstallations_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshInstallationsAsync();
    }

    private async void ProbeBroker_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ProbeBrokerAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Cancel();
    }

    private async void ProcessSelection_Changed(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        await _viewModel.LoadSelectedProcessDetailsAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            DefaultExt = ".json",
            FileName = $"chromium-process-explorer-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                dialog.FileName,
                _viewModel.CreateJsonExport());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Export failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
