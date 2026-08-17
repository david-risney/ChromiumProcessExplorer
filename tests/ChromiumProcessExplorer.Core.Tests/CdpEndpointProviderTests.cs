using System.Net;
using System.Text;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class CdpEndpointProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ChromiumProcessExplorer.CdpTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task DiscoverAsyncValidatesFixedPort()
    {
        CdpEndpointProvider provider = CreateProvider(
            """
            {
              "Browser": "Chrome/151.0",
              "Protocol-Version": "1.3",
              "webSocketDebuggerUrl": "ws://127.0.0.1:9222/devtools/browser/test"
            }
            """);

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9222")])).Transports);

        Assert.Equal(CdpTransportStatus.Validated, result.Status);
        Assert.Equal(9222, result.Port);
        Assert.Equal("Chrome/151.0", result.Browser);
        Assert.Equal("1.3", result.ProtocolVersion);
    }

    [Fact]
    public async Task DiscoverAsyncResolvesEphemeralPortBreadcrumb()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "DevToolsActivePort"),
            "9333\n/devtools/browser/test\n");
        CdpEndpointProvider provider = CreateProvider(
            """
            {
              "webSocketDebuggerUrl": "ws://localhost:9333/devtools/browser/test"
            }
            """);

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=0", _root)])).Transports);

        Assert.Equal(CdpTransportStatus.Validated, result.Status);
        Assert.Equal(9333, result.Port);
        Assert.Equal("DevToolsActivePort", result.DiscoverySource);
    }

    [Fact]
    public async Task DiscoverAsyncReportsPrivatePipeWithoutOpeningIt()
    {
        CdpEndpointProvider provider = CreateProvider("{}");

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-pipe")])).Transports);

        Assert.Equal(CdpTransportKind.Pipe, result.Kind);
        Assert.Equal(CdpTransportStatus.AlreadyOwned, result.Status);
        Assert.Null(result.VersionEndpoint);
        Assert.Contains("private", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsyncReportsInaccessibleEndpoint()
    {
        HttpClient client = new(new DelegateHandler(
            (_, _) => throw new HttpRequestException("Connection refused.")));
        CdpEndpointProvider provider = new(
            client,
            TimeSpan.FromMilliseconds(100));

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9444")])).Transports);

        Assert.Equal(CdpTransportStatus.Unavailable, result.Status);
        Assert.Contains("Connection refused", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsyncRejectsNonCdpVersionResponse()
    {
        CdpEndpointProvider provider = CreateProvider(
            """{"status":"healthy"}""");

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9555")])).Transports);

        Assert.Equal(CdpTransportStatus.Unavailable, result.Status);
        Assert.Null(result.WebSocketDebuggerUrl);
        Assert.Contains(
            "WebSocket URL",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsyncIgnoresInheritedRendererSwitch()
    {
        CdpEndpointProvider provider = CreateProvider("{}");
        ProcessSnapshotEntry renderer = CreateProcess(
            "--type=renderer --remote-debugging-port=9222") with
        {
            ChromiumProcessType = "renderer",
        };

        CdpDiscoveryResult result = await provider.DiscoverAsync([renderer]);

        Assert.Empty(result.Transports);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CdpEndpointProvider CreateProvider(string response)
    {
        HttpClient client = new(new DelegateHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response,
                    Encoding.UTF8,
                    "application/json"),
            })));
        return new CdpEndpointProvider(client, TimeSpan.FromSeconds(1));
    }

    private static ProcessSnapshotEntry CreateProcess(
        string arguments,
        string? userDataDirectory = null)
    {
        const string executable = @"C:\Apps\Chrome\chrome.exe";
        return new ProcessSnapshotEntry(
            100,
            1,
            DateTimeOffset.UtcNow,
            "chrome.exe",
            executable,
            $"\"{executable}\" {arguments}",
            "browser",
            userDataDirectory,
            true,
            [],
            null);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
