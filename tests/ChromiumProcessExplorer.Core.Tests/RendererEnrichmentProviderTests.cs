using System.Text.Json;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class RendererEnrichmentProviderTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 18, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PassiveModeNeverClaimsRendererOrigins()
    {
        RendererEnrichmentProvider provider = new(new StubCdpClient());

        RendererEnrichmentResult result = await provider.EnrichAsync(
            [CreateProcess(100)],
            new CdpDiscoveryResult(SnapshotTime, []));

        Assert.Empty(result.FrameMappings);
        Assert.Contains(
            result.Limitations,
            limitation => limitation.Contains(
                "Passive process inspection",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebView2CooperativeDataMapsMainAndCrossSiteFrames()
    {
        ProcessSnapshotEntry renderer = CreateProcess(200);
        RendererEnrichmentProvider provider = new(new StubCdpClient());
        WebView2ExtendedProcessObservation observation = new(
            new ProcessIdentity(renderer.ProcessId, renderer.CreationTime),
            "Renderer",
            [
                new("main", "https://app.example/page", true),
                new("cross-site", "https://login.contoso.test/frame", false),
            ],
            SnapshotTime);

        RendererEnrichmentResult result = await provider.EnrichAsync(
            [renderer],
            new CdpDiscoveryResult(SnapshotTime, []),
            [observation]);

        Assert.Equal(2, result.FrameMappings.Count);
        Assert.All(result.FrameMappings, mapping =>
        {
            Assert.Equal(
                RendererObservationSource.WebView2Cooperative,
                mapping.Source);
            Assert.Equal(ProcessRelationshipConfidence.High, mapping.Confidence);
            Assert.True(mapping.IsAuthoritative);
        });
        Assert.Contains(
            result.FrameMappings,
            mapping => mapping.IsMainFrame
                && mapping.Origin == "https://app.example");
        Assert.Contains(
            result.FrameMappings,
            mapping => !mapping.IsMainFrame
                && mapping.Origin == "https://login.contoso.test");
    }

    [Fact]
    public async Task WebView2RejectsMismatchedProcessGeneration()
    {
        ProcessSnapshotEntry renderer = CreateProcess(200);
        RendererEnrichmentProvider provider = new(new StubCdpClient());
        WebView2ExtendedProcessObservation observation = new(
            new ProcessIdentity(
                renderer.ProcessId,
                renderer.CreationTime?.AddSeconds(1)),
            "Renderer",
            [new("main", "https://example.test", true)],
            SnapshotTime);

        RendererEnrichmentResult result = await provider.EnrichAsync(
            [renderer],
            new CdpDiscoveryResult(SnapshotTime, []),
            [observation]);

        Assert.Empty(result.FrameMappings);
        Assert.Contains(
            result.Issues,
            issue => issue.Stage == "webview2-renderer-enrichment");
    }

    [Fact]
    public async Task CdpTopologyAndProcessesRemainUnjoined()
    {
        StubCdpClient client = new()
        {
            Snapshot = new CdpRendererSessionSnapshot(
                SnapshotTime,
                [
                    new CdpProtocolTarget(
                        "page-1",
                        "page",
                        "Example",
                        "https://example.test/",
                        null,
                        null,
                        "context-1"),
                ],
                [new CdpProtocolProcess(300, "renderer", 1.25)],
                [],
                []),
        };
        RendererEnrichmentProvider provider = new(client);

        RendererEnrichmentResult result = await provider.EnrichAsync(
            [CreateProcess(100), CreateProcess(300)],
            CreateValidatedCdp());

        Assert.Single(result.CdpTargets);
        Assert.Single(result.CdpProcesses);
        Assert.Empty(result.FrameMappings);
        Assert.Contains(
            result.Limitations,
            limitation => limitation.Contains(
                "target-to-PID join",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TraceParserProducesNonAuthoritativeMapping()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "name": "TracingStartedInBrowser",
              "pid": 100,
              "args": {
                "data": {
                  "frames": [
                    {
                      "frame": "frame-1",
                      "url": "https://example.test/path",
                      "processId": 300,
                      "isMainFrame": true
                    }
                  ]
                }
              }
            }
            """);
        ProcessSnapshotEntry renderer = CreateProcess(300);

        RendererFrameMapping mapping = Assert.Single(
            RendererEnrichmentProvider.ParseTracingMappings(
                [document.RootElement.Clone()],
                new Dictionary<int, ProcessSnapshotEntry>
                {
                    [300] = renderer,
                },
                SnapshotTime));

        Assert.Equal(RendererObservationSource.CdpTracing, mapping.Source);
        Assert.Equal(ProcessRelationshipConfidence.Medium, mapping.Confidence);
        Assert.False(mapping.IsAuthoritative);
        Assert.Equal("https://example.test", mapping.Origin);
    }

    private static CdpDiscoveryResult CreateValidatedCdp()
    {
        return new CdpDiscoveryResult(
            SnapshotTime,
            [
                new CdpTransportInfo(
                    100,
                    CdpTransportKind.Tcp,
                    CdpTransportStatus.Validated,
                    "9222",
                    9222,
                    "command-line",
                    "http://127.0.0.1:9222/json/version",
                    "ws://127.0.0.1:9222/devtools/browser/test",
                    "Chrome/151",
                    "1.3",
                    null,
                    []),
            ]);
    }

    private static ProcessSnapshotEntry CreateProcess(int processId)
    {
        return new ProcessSnapshotEntry(
            processId,
            1,
            SnapshotTime.AddMilliseconds(processId),
            processId == 100 ? "chrome.exe" : "chrome.exe",
            @"C:\Chrome\chrome.exe",
            processId == 100
                ? @"""C:\Chrome\chrome.exe"" --remote-debugging-port=9222"
                : @"""C:\Chrome\chrome.exe"" --type=renderer",
            processId == 100 ? "browser" : "renderer",
            null,
            true,
            [],
            null);
    }

    private sealed class StubCdpClient : ICdpRendererSessionClient
    {
        public CdpRendererSessionSnapshot Snapshot { get; init; } = new(
            SnapshotTime,
            [],
            [],
            [],
            []);

        public ValueTask<CdpRendererSessionSnapshot> CaptureAsync(
            Uri webSocketDebuggerUrl,
            bool includeTracing,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Snapshot);
        }
    }
}
