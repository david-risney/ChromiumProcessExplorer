using System.Runtime.InteropServices;
using System.Windows;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Gui;

internal static class Program
{
    internal const string AppUserModelId =
        "DavidRisney.ChromiumProcessExplorer";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [HandleQueryWorker.WorkerArgument])
        {
            return HandleQueryWorker.RunAsync(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput())
                .GetAwaiter()
                .GetResult();
        }

        if (args.Length > 0
            && string.Equals(
                args[0],
                FutureDebugConfigurator.WorkerArgument,
                StringComparison.Ordinal))
        {
            return FutureDebugConfigurator.Run(args);
        }

        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        Marshal.ThrowExceptionForHR(
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId));

        Application application = new()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };
        MainWindow window = new();
        application.MainWindow = window;
        window.Show();
        application.Run();
        return 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        string appId);
}
