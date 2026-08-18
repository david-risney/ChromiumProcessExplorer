using ChromiumProcessExplorer.Core.Broker;
using ChromiumProcessExplorer.Mcp;

ChromiumBrokerClient client = new(
    BrokerServerOptions.CreateDefault().PipeName,
    TimeSpan.FromSeconds(95));
McpServer server = new(client, Console.In, Console.Out, Console.Error);
await server.RunAsync();
