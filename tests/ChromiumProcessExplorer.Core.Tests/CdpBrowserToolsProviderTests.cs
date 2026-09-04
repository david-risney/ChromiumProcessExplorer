using System.Net;
using System.Text;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class CdpBrowserToolsProviderTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Edg/140.0", "browser.exe", "edge")]
    [InlineData(null, "msedge.exe", "edge")]
    [InlineData(null, "msedgewebview2.exe", "edge")]
    [InlineData("Chrome/140.0", "chrome.exe", "chrome")]
    [InlineData("HeadlessChrome/140.0", "app.exe", "chrome")]
    public void ResolveInternalPageSchemeUsesBrowserProduct(
        string? browser,
        string imageName,
        string expected)
    {
        Assert.Equal(
            expected,
            CdpBrowserToolsProvider.ResolveInternalPageScheme(
                browser,
                imageName));
    }

    [Fact]
    public void BuildRendererProcessIdMapScopesIdsToBrowserTree()
    {
        ProcessSnapshotEntry browser = CreateProcess(100, 0, "chrome.exe", null);
        ProcessSnapshotEntry renderer = CreateProcess(
            101,
            100,
            "chrome.exe",
            "--type=renderer --renderer-client-id=7");
        ProcessSnapshotEntry nested = CreateProcess(
            102,
            101,
            "chrome.exe",
            "--type=renderer --renderer-client-id=8");
        ProcessSnapshotEntry otherBrowser = CreateProcess(
            200,
            0,
            "chrome.exe",
            null);
        ProcessSnapshotEntry otherRenderer = CreateProcess(
            201,
            200,
            "chrome.exe",
            "--type=renderer --renderer-client-id=7");

        IReadOnlyDictionary<int, ProcessIdentity> result =
            CdpBrowserToolsProvider.BuildRendererProcessIdMap(
                browser.ProcessId,
                [browser, renderer, nested, otherBrowser, otherRenderer]);

        Assert.Equal(101, result[7].ProcessId);
        Assert.Equal(102, result[8].ProcessId);
    }

    [Fact]
    public async Task DiscoverTargetsResolvesRemoteFrontendUrl()
    {
        HttpClient httpClient = new(new DelegateHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "http://127.0.0.1:9222/json/list"),
                Content = new StringContent(
                    """
                    [
                      {
                        "id": "page-1",
                        "type": "page",
                        "title": "Example",
                        "url": "https://example.test/",
                        "devtoolsFrontendUrl": "/devtools/inspector.html?ws=127.0.0.1:9222/devtools/page/page-1",
                        "webSocketDebuggerUrl": "ws://127.0.0.1:9222/devtools/page/page-1"
                      }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json"),
            })));
        CdpBrowserToolsProvider provider = new(
            httpClient,
            new StubSessionClient(),
            TimeSpan.FromSeconds(1));

        CdpInspectableTarget target = Assert.Single(
            (await provider.DiscoverTargetsAsync(CreateTransport())).Targets);

        Assert.Equal("page-1", target.TargetId);
        Assert.Equal(
            "http://127.0.0.1:9222/devtools/inspector.html?ws=127.0.0.1:9222/devtools/page/page-1",
            target.DevToolsFrontendUrl);
    }

    [Fact]
    public async Task DiscoverTargetsReplacesHostedFrontendWithLoopbackFrontend()
    {
        HttpClient httpClient = new(new DelegateHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "http://127.0.0.1:9222/json/list"),
                Content = new StringContent(
                    """
                    [
                      {
                        "id": "page-1",
                        "type": "page",
                        "title": "Example",
                        "url": "https://example.test/",
                        "devtoolsFrontendUrl": "https://chrome-devtools-frontend.appspot.com/serve_rev/@revision/inspector.html?ws=127.0.0.1:9222/devtools/page/page-1",
                        "webSocketDebuggerUrl": "ws://127.0.0.1:9222/devtools/page/page-1"
                      }
                    ]
                    """,
                    Encoding.UTF8,
                    "application/json"),
            })));
        CdpBrowserToolsProvider provider = new(
            httpClient,
            new StubSessionClient(),
            TimeSpan.FromSeconds(1));

        CdpInspectableTarget target = Assert.Single(
            (await provider.DiscoverTargetsAsync(CreateTransport())).Targets);

        Assert.Equal(
            "http://127.0.0.1:9222/devtools/inspector.html?ws=127.0.0.1:9222/devtools/page/page-1",
            target.DevToolsFrontendUrl);
    }

    [Fact]
    public async Task CaptureProcessInternalsUsesEdgeSchemeAndMapsWindowsPid()
    {
        StubSessionClient client = new()
        {
            Capture = (_, pageUrl, _) => ValueTask.FromResult(
                new CdpRawProcessInternalsSnapshot(
                    SnapshotTime,
                    pageUrl,
                    [
                        new CdpRawProcessInternalsFrame(
                            "Example",
                            0,
                            7,
                            12,
                            3,
                            "Active",
                            "https://example.test/",
                            4,
                            5,
                            6,
                            "https://example.test/",
                            null),
                    ])),
        };
        CdpBrowserToolsProvider provider = new(
            new HttpClient(new DelegateHandler(
                (_, _) => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)))),
            client,
            TimeSpan.FromSeconds(1));
        ProcessSnapshotEntry browser = CreateProcess(
            100,
            0,
            "msedge.exe",
            null);
        ProcessSnapshotEntry renderer = CreateProcess(
            101,
            100,
            "msedge.exe",
            "--type=renderer --renderer-client-id=7");

        CdpProcessInternalsResult result =
            await provider.CaptureProcessInternalsAsync(
                CreateTransport("Edg/140.0"),
                browser.ImageName,
                [browser, renderer]);

        Assert.Equal(
            "edge://process-internals/",
            Assert.Single(client.RequestedPages));
        Assert.Equal(101, Assert.Single(result.Frames).Process?.ProcessId);
        Assert.Empty(result.Issues);
    }

    private static CdpTransportInfo CreateTransport(
        string browser = "Chrome/140.0")
    {
        return new CdpTransportInfo(
            100,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Validated,
            "9222",
            9222,
            "command-line",
            "http://127.0.0.1:9222/json/version",
            "ws://127.0.0.1:9222/devtools/browser/test",
            browser,
            "1.3",
            null,
            []);
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        int parentProcessId,
        string imageName,
        string? commandLine)
    {
        return new ProcessSnapshotEntry(
            processId,
            parentProcessId,
            SnapshotTime,
            imageName,
            $@"C:\Apps\{imageName}",
            commandLine,
            null,
            null,
            true,
            [],
            null);
    }

    private sealed class StubSessionClient : ICdpBrowserToolsSessionClient
    {
        public Func<Uri, string, CancellationToken, ValueTask>
            Open
        { get; init; } =
            (_, _, _) => ValueTask.CompletedTask;

        public Func<
            Uri,
            string,
            CancellationToken,
            ValueTask<CdpRawProcessInternalsSnapshot>>
            Capture
        { get; init; } =
            (_, pageUrl, _) => ValueTask.FromResult(
                new CdpRawProcessInternalsSnapshot(
                    SnapshotTime,
                    pageUrl,
                    []));

        public List<string> RequestedPages { get; } = [];

        public ValueTask OpenDevToolsAsync(
            Uri browserWebSocketUrl,
            string targetId,
            CancellationToken cancellationToken)
        {
            return Open(browserWebSocketUrl, targetId, cancellationToken);
        }

        public ValueTask<CdpRawProcessInternalsSnapshot>
            CaptureProcessInternalsAsync(
                Uri browserWebSocketUrl,
                string pageUrl,
                CancellationToken cancellationToken)
        {
            RequestedPages.Add(pageUrl);
            return Capture(
                browserWebSocketUrl,
                pageUrl,
                cancellationToken);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
