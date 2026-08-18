using System.Text.Json;
using ChromiumProcessExplorer.Core.Broker;
using ChromiumProcessExplorer.Mcp;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task ListsOnlyTypedReadOnlyTools()
    {
        StringWriter output = new();
        McpServer server = new(
            new StubBrokerClient(),
            new StringReader(
                """
                {"jsonrpc":"2.0","id":1,"method":"tools/list"}

                """),
            output,
            new StringWriter());

        await server.RunAsync();

        using JsonDocument response = JsonDocument.Parse(output.ToString());
        JsonElement tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");
        string[] names = tools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "cpe_probe",
                "cpe_process_details",
                "cpe_installations",
                "cpe_diagnostics",
                "cpe_cdp",
            ],
            names);
        Assert.DoesNotContain(names, name => name.Contains(
            "shell",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MapsProcessDetailsToolToRedactedBrokerOperation()
    {
        StubBrokerClient client = new();
        StringWriter output = new();
        McpServer server = new(
            client,
            new StringReader(
                """
                {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"cpe_process_details","arguments":{"pid":123}}}

                """),
            output,
            new StringWriter());

        await server.RunAsync();

        Assert.Equal(BrokerOperations.ProcessDetails, client.Operation);
        BrokerProcessDetailsArguments arguments =
            Assert.IsType<BrokerProcessDetailsArguments>(client.Arguments);
        Assert.Equal(123, arguments.ProcessId);
        using JsonDocument response = JsonDocument.Parse(output.ToString());
        Assert.False(response.RootElement
            .GetProperty("result")
            .GetProperty("isError")
            .GetBoolean());
    }

    [Fact]
    public async Task RejectsInvalidPidWithoutCallingBroker()
    {
        StubBrokerClient client = new();
        StringWriter output = new();
        McpServer server = new(
            client,
            new StringReader(
                """
                {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"cpe_process_details","arguments":{"pid":0}}}

                """),
            output,
            new StringWriter());

        await server.RunAsync();

        Assert.Null(client.Operation);
        using JsonDocument response = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            -32602,
            response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":[]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"cpe_probe","arguments":[]}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"cpe_probe","arguments":{"path":"C:\\"}}}""")]
    public async Task RejectsMalformedOrUnknownToolArguments(string request)
    {
        StubBrokerClient client = new();
        StringWriter output = new();
        McpServer server = new(
            client,
            new StringReader(request + Environment.NewLine),
            output,
            new StringWriter());

        await server.RunAsync();

        Assert.Null(client.Operation);
        using JsonDocument response = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            -32602,
            response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task DoesNotRespondToNotifications()
    {
        StringWriter output = new();
        McpServer server = new(
            new StubBrokerClient(),
            new StringReader(
                """
                {"jsonrpc":"2.0","method":"notifications/initialized"}

                """),
            output,
            new StringWriter());

        await server.RunAsync();

        Assert.Equal(string.Empty, output.ToString());
    }

    private sealed class StubBrokerClient : IChromiumBrokerClient
    {
        public string? Operation { get; private set; }

        public object? Arguments { get; private set; }

        public ValueTask<BrokerResponse> SendAsync(
            string operation,
            object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            Operation = operation;
            Arguments = arguments;
            return ValueTask.FromResult(new BrokerResponse(
                BrokerMessageCodec.Version,
                Guid.NewGuid(),
                true,
                false,
                JsonSerializer.SerializeToElement(new { ok = true }),
                null));
        }
    }
}
