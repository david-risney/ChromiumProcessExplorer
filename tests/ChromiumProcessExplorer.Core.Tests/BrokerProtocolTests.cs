using System.IO.Pipes;
using System.Text.Json;
using ChromiumProcessExplorer.Core.Broker;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class BrokerProtocolTests
{
    [Fact]
    public void AuthorizerRequiresSameUserAndLogonSid()
    {
        BrokerCallerIdentity broker = new("S-1-5-21-1", "S-1-5-5-1-2");

        Assert.True(BrokerCallerAuthorizer.IsAuthorized(
            broker,
            new BrokerCallerIdentity("S-1-5-21-1", "S-1-5-5-1-2")));
        Assert.False(BrokerCallerAuthorizer.IsAuthorized(
            broker,
            new BrokerCallerIdentity("S-1-5-21-2", "S-1-5-5-1-2")));
        Assert.False(BrokerCallerAuthorizer.IsAuthorized(
            broker,
            new BrokerCallerIdentity("S-1-5-21-1", "S-1-5-5-9-9")));
    }

    [Fact]
    public async Task CodecRoundTripsRequest()
    {
        BrokerRequest request = CreateRequest(BrokerOperations.Probe);
        await using MemoryStream stream = new();

        await BrokerMessageCodec.WriteAsync(stream, request);
        stream.Position = 0;
        BrokerRequest result =
            await BrokerMessageCodec.ReadAsync<BrokerRequest>(stream);

        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.Operation, result.Operation);
    }

    [Fact]
    public async Task CodecRejectsOversizedAndTruncatedFrames()
    {
        await using MemoryStream oversized = new();
        await oversized.WriteAsync(BitConverter.GetBytes(
            BrokerMessageCodec.MaximumMessageBytes + 1));
        oversized.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await BrokerMessageCodec.ReadAsync<BrokerRequest>(oversized));

        await using MemoryStream truncated = new();
        await truncated.WriteAsync(BitConverter.GetBytes(100));
        await truncated.WriteAsync(new byte[4]);
        truncated.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await BrokerMessageCodec.ReadAsync<BrokerRequest>(truncated));
    }

    [Fact]
    public async Task ClientReturnsBrokerNotRunning()
    {
        ChromiumBrokerClient client = new(
            $"cpe-missing-{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(100));

        BrokerResponse response = await client.SendAsync(BrokerOperations.Probe);

        Assert.False(response.Ok);
        Assert.Equal("broker_not_running", response.Error?.Code);
    }

    [Fact]
    public async Task ExecutorRejectsSensitiveOutputArguments()
    {
        BrokerRequest request = new(
            BrokerMessageCodec.Version,
            Guid.NewGuid(),
            BrokerOperations.ProcessDetails,
            JsonSerializer.SerializeToElement(new
            {
                ProcessId = 4,
                IncludeSensitiveValues = true,
            }));
        ChromiumBrokerOperationExecutor executor = new();

        BrokerResponse response = await executor.ExecuteAsync(
            request,
            isElevated: true);

        Assert.False(response.Ok);
        Assert.Equal("malformed_request", response.Error?.Code);
    }

    [Fact]
    public async Task ServerAcceptsValidRequestAndWritesAudit()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        string auditPath = Path.Combine(
            Path.GetTempPath(),
            $"cpe-audit-{Guid.NewGuid():N}.jsonl");
        using CancellationTokenSource cancellation = new();
        ChromiumBrokerServer server = new(
            new BrokerServerOptions(
                pipeName,
                TimeSpan.FromSeconds(5),
                auditPath),
            new StubExecutor());
        Task serverTask = server.RunAsync(cancellation.Token);
        ChromiumBrokerClient client = new(pipeName, TimeSpan.FromSeconds(5));

        BrokerResponse response = await client.SendAsync(BrokerOperations.Probe);
        cancellation.Cancel();
        await IgnoreCancellationAsync(serverTask);

        Assert.True(response.Ok);
        Assert.Equal("probe", response.Result?.GetProperty("operation").GetString());
        string audit = await File.ReadAllTextAsync(auditPath);
        Assert.Contains(response.RequestId.ToString(), audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", audit, StringComparison.OrdinalIgnoreCase);
        File.Delete(auditPath);
    }

    [Fact]
    public async Task ServerRejectsMalformedOperation()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        string auditPath = Path.Combine(
            Path.GetTempPath(),
            $"cpe-audit-{Guid.NewGuid():N}.jsonl");
        using CancellationTokenSource cancellation = new();
        ChromiumBrokerServer server = new(
            new BrokerServerOptions(
                pipeName,
                TimeSpan.FromSeconds(5),
                auditPath),
            new StubExecutor());
        Task serverTask = server.RunAsync(cancellation.Token);
        await using NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        BrokerRequest request = CreateRequest("run-shell");

        await BrokerMessageCodec.WriteAsync(pipe, request);
        BrokerResponse response =
            await BrokerMessageCodec.ReadAsync<BrokerResponse>(pipe);
        cancellation.Cancel();
        await IgnoreCancellationAsync(serverTask);

        Assert.False(response.Ok);
        Assert.Equal("invalid_operation", response.Error?.Code);
        File.Delete(auditPath);
    }

    [Fact]
    public async Task ClientRejectsStaleResponse()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        Task server = Task.Run(async () =>
        {
            await using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            BrokerRequest request =
                await BrokerMessageCodec.ReadAsync<BrokerRequest>(pipe);
            BrokerResponse stale = new(
                BrokerMessageCodec.Version,
                Guid.NewGuid(),
                true,
                false,
                JsonSerializer.SerializeToElement(new { request.Operation }),
                null);
            await BrokerMessageCodec.WriteAsync(pipe, stale);
        });
        ChromiumBrokerClient client = new(pipeName, TimeSpan.FromSeconds(5));

        BrokerResponse response = await client.SendAsync(BrokerOperations.Probe);
        await server;

        Assert.False(response.Ok);
        Assert.Equal("stale_response", response.Error?.Code);
    }

    [Fact]
    public async Task ClientReturnsTimeoutWhenBrokerDoesNotRespond()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        Task server = RunNonRespondingServerAsync(
            pipeName,
            TimeSpan.FromSeconds(1));
        ChromiumBrokerClient client = new(
            pipeName,
            TimeSpan.FromMilliseconds(100));

        BrokerResponse response = await client.SendAsync(BrokerOperations.Probe);
        await server;

        Assert.False(response.Ok);
        Assert.Equal("broker_timeout", response.Error?.Code);
    }

    [Fact]
    public async Task ClientHonorsCallerCancellation()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        Task server = RunNonRespondingServerAsync(
            pipeName,
            TimeSpan.FromSeconds(1));
        ChromiumBrokerClient client = new(pipeName, TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.SendAsync(
                BrokerOperations.Probe,
                cancellationToken: cancellation.Token));
        await server;
    }

    [Fact]
    public async Task BrokerCanRestartWithoutClientState()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        await RunSingleServerCallAsync(pipeName);
        await RunSingleServerCallAsync(pipeName);
    }

    private static async Task RunSingleServerCallAsync(string pipeName)
    {
        string auditPath = Path.Combine(
            Path.GetTempPath(),
            $"cpe-audit-{Guid.NewGuid():N}.jsonl");
        using CancellationTokenSource cancellation = new();
        ChromiumBrokerServer server = new(
            new BrokerServerOptions(
                pipeName,
                TimeSpan.FromSeconds(5),
                auditPath),
            new StubExecutor());
        Task serverTask = server.RunAsync(cancellation.Token);
        ChromiumBrokerClient client = new(pipeName, TimeSpan.FromSeconds(5));

        BrokerResponse response = await client.SendAsync(BrokerOperations.Probe);
        cancellation.Cancel();
        await IgnoreCancellationAsync(serverTask);

        Assert.True(response.Ok);
        File.Delete(auditPath);
    }

    private static BrokerRequest CreateRequest(string operation)
    {
        return new BrokerRequest(
            BrokerMessageCodec.Version,
            Guid.NewGuid(),
            operation,
            JsonSerializer.SerializeToElement(new { }));
    }

    private static Task RunNonRespondingServerAsync(
        string pipeName,
        TimeSpan delay)
    {
        return Task.Run(async () =>
        {
            await using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            await BrokerMessageCodec.ReadAsync<BrokerRequest>(pipe);
            await Task.Delay(delay);
        });
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class StubExecutor : IBrokerOperationExecutor
    {
        public ValueTask<BrokerResponse> ExecuteAsync(
            BrokerRequest request,
            bool isElevated,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new BrokerResponse(
                BrokerMessageCodec.Version,
                request.RequestId,
                true,
                false,
                JsonSerializer.SerializeToElement(new { operation = request.Operation }),
                null));
        }
    }
}
