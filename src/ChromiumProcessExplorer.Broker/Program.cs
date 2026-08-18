using ChromiumProcessExplorer.Core.Broker;
using ChromiumProcessExplorer.Core.Discovery;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("cpe-broker requires Windows.");
    return 1;
}

if (args is [HandleQueryWorker.WorkerArgument])
{
    return await HandleQueryWorker.RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput());
}

bool allowUnelevated = args.Contains(
    "--allow-unelevated",
    StringComparer.Ordinal);
string? unknownArgument = args.FirstOrDefault(argument =>
    !argument.Equals("--allow-unelevated", StringComparison.Ordinal));
if (unknownArgument is not null)
{
    Console.Error.WriteLine($"Unknown argument: {unknownArgument}");
    return 2;
}

if (!ChromiumBrokerServer.IsCurrentProcessElevated() && !allowUnelevated)
{
    Console.Error.WriteLine(
        "Administrator rights are required. Start cpe-broker with "
        + "PowerShell Start-Process -Verb RunAs.");
    return 5;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

BrokerServerOptions options = BrokerServerOptions.CreateDefault();
ChromiumBrokerServer server = new(
    options,
    new ChromiumBrokerOperationExecutor());
Console.Error.WriteLine(
    $"cpe-broker listening on {options.PipeName}; "
    + $"elevated={ChromiumBrokerServer.IsCurrentProcessElevated()}.");
try
{
    await server.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
