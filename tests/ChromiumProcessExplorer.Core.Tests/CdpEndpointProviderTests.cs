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
        Assert.Equal(CdpTransportStatus.Configured, result.Status);
        Assert.Null(result.VersionEndpoint);
        Assert.Null(result.ControllerProcessId);
    }

    [Fact]
    public async Task DiscoverAsyncReportsInaccessibleEndpoint()
    {
        HttpClient client = new(new DelegateHandler(
            (_, _) => throw new HttpRequestException("Connection refused.")));
        CdpEndpointProvider provider = new(
            client,
            new StubListenerOwnerResolver([100]),
            new StubRestrictionDetector(null),
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

    [Fact]
    public async Task DiscoverAsyncIgnoresUnrelatedUntypedProcess()
    {
        CdpEndpointProvider provider = CreateProvider("{}");
        ProcessSnapshotEntry unrelated = CreateProcess(
            "--remote-debugging-port=9222") with
        {
            ImageName = "server.exe",
            ExecutablePath = @"C:\Apps\Server\server.exe",
            IsLikelyChromium = false,
        };

        CdpDiscoveryResult result = await provider.DiscoverAsync([unrelated]);

        Assert.Empty(result.Transports);
    }

    [Fact]
    public async Task DiscoverAsyncRejectsRedirectedVersionResponse()
    {
        HttpClient client = new(new DelegateHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://example.com/json/version"),
                Content = new StringContent("{}"),
            })));
        CdpEndpointProvider provider = new(
            client,
            new StubListenerOwnerResolver([100]),
            new StubRestrictionDetector(null));

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9222")])).Transports);

        Assert.Equal(CdpTransportStatus.Unavailable, result.Status);
        Assert.Contains("redirected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsyncBoundsActivePortFile()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "DevToolsActivePort"),
            new string('1', 5000));
        CdpEndpointProvider provider = CreateProvider("{}");

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=0", _root)])).Transports);

        Assert.Equal(CdpTransportStatus.Unavailable, result.Status);
        Assert.Contains("4 KiB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsyncRejectsPortOwnedByAnotherProcess()
    {
        bool requested = false;
        HttpClient client = new(new DelegateHandler(
            (_, _) =>
            {
                requested = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));
        CdpEndpointProvider provider = new(
            client,
            new StubListenerOwnerResolver([999]),
            new StubRestrictionDetector(null));

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9222")])).Transports);

        Assert.Equal(CdpTransportStatus.Unavailable, result.Status);
        Assert.Contains("PID(s) 999", result.Error, StringComparison.Ordinal);
        Assert.False(requested);
    }

    [Fact]
    public async Task DiscoverAsyncReportsDiscoveredWhenOwnershipCannotBeRead()
    {
        CdpEndpointProvider provider = CreateProvider(
            "{}",
            new CdpListenerOwnerResult([], "Access denied."));

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9222")])).Transports);

        Assert.Equal(CdpTransportStatus.Discovered, result.Status);
        Assert.Contains("Access denied", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsyncReportsChromeDefaultProfileRestriction()
    {
        const string restriction = "Chrome 136 default profile restriction.";
        CdpEndpointProvider provider = CreateProvider(
            "{}",
            restriction: restriction);

        CdpTransportInfo result = Assert.Single((await provider.DiscoverAsync(
            [CreateProcess("--remote-debugging-port=9222")])).Transports);

        Assert.Equal(CdpTransportStatus.Unavailable, result.Status);
        Assert.Equal(restriction, result.Restriction);
    }

    [Fact]
    public void CorrelatePipeTransportRequiresBothDirectionsToParentController()
    {
        ProcessSnapshotEntry browser = CreateProcess("--remote-debugging-pipe");
        ProcessSnapshotEntry controller = new(
            1,
            0,
            browser.CreationTime?.AddSeconds(-1),
            "node.exe",
            @"C:\Node\node.exe",
            "\"C:\\Node\\node.exe\"",
            null,
            null,
            false,
            [],
            null);
        CdpTransportInfo configured = new(
            browser.ProcessId,
            CdpTransportKind.Pipe,
            CdpTransportStatus.Configured,
            null,
            null,
            "command-line",
            null,
            null,
            null,
            null,
            null,
            []);
        ProcessPipeInspectionResult inspection = new(
            [
                new ProcessPipeHandleInfo(
                    100,
                    10,
                    @"\Device\NamedPipe\playwright.in",
                    1,
                    100,
                    "client",
                    "connected",
                    null),
                new ProcessPipeHandleInfo(
                    100,
                    11,
                    @"\Device\NamedPipe\playwright.out",
                    100,
                    1,
                    "server",
                    "connected",
                    null),
            ],
            [],
            []);

        CdpTransportInfo result = CdpEndpointProvider.CorrelatePipeTransport(
            configured,
            browser,
            ChromiumCommandLine.Parse(browser.CommandLine),
            inspection,
            new Dictionary<int, ProcessSnapshotEntry>
            {
                [1] = controller,
                [100] = browser,
            });

        Assert.Equal(CdpTransportStatus.AlreadyOwned, result.Status);
        Assert.Equal(1, result.ControllerProcessId);
        Assert.Equal(2, result.PipeConnections.Count);
    }

    [Fact]
    public void ChromeRestrictionRequiresVersion136AndDefaultProfile()
    {
        ProcessSnapshotEntry process = CreateProcess(
            "--remote-debugging-port=9222");
        ChromiumCommandLine commandLine =
            ChromiumCommandLine.Parse(process.CommandLine);
        const string defaultDirectory = @"C:\Users\Test\AppData\Local\Google\Chrome\User Data";

        string? restricted =
            ChromeRemoteDebuggingRestrictionDetector.GetRestrictionForVersion(
                process,
                commandLine,
                "Google Chrome",
                136,
                defaultDirectory);
        string? older =
            ChromeRemoteDebuggingRestrictionDetector.GetRestrictionForVersion(
                process,
                commandLine,
                "Google Chrome",
                135,
                defaultDirectory);
        ProcessSnapshotEntry customProfileProcess = CreateProcess(
            "--remote-debugging-port=9222 "
                + "--user-data-dir=C:\\Profiles\\ChromeTest");
        string? customProfile =
            ChromeRemoteDebuggingRestrictionDetector.GetRestrictionForVersion(
                customProfileProcess,
                ChromiumCommandLine.Parse(customProfileProcess.CommandLine),
                "Google Chrome",
                136,
                defaultDirectory);

        Assert.NotNull(restricted);
        Assert.Null(older);
        Assert.Null(customProfile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CdpEndpointProvider CreateProvider(
        string response,
        CdpListenerOwnerResult? owners = null,
        string? restriction = null)
    {
        HttpClient client = new(new DelegateHandler(
            (request, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        response,
                        Encoding.UTF8,
                        "application/json"),
                })));
        return new CdpEndpointProvider(
            client,
            new StubListenerOwnerResolver(
                owners ?? new CdpListenerOwnerResult([100], null)),
            new StubRestrictionDetector(restriction),
            TimeSpan.FromSeconds(1));
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

    private sealed class StubListenerOwnerResolver(CdpListenerOwnerResult result)
        : ICdpListenerOwnerResolver
    {
        public StubListenerOwnerResolver(IReadOnlyList<int> processIds)
            : this(new CdpListenerOwnerResult(processIds, null))
        {
        }

        public CdpListenerOwnerResult Resolve(int port)
        {
            return result;
        }
    }

    private sealed class StubRestrictionDetector(string? restriction)
        : IChromeRemoteDebuggingRestrictionDetector
    {
        public string? GetRestriction(
            ProcessSnapshotEntry process,
            ChromiumCommandLine commandLine)
        {
            return restriction;
        }
    }
}
