using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class WebView2RuntimeAdapterTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnalyzeRequiresRelationshipEvidenceBeyondModuleClassification()
    {
        ProcessSnapshotEntry host = CreateHost(100);
        ProcessSnapshotEntry browser = CreateBrowser(200, 0);

        WebView2RuntimeAnalysis result = WebView2RuntimeAdapter.Analyze(
            [host, browser],
            CreateInspection());

        Assert.Equal(2, result.Processes.Count);
        Assert.Empty(result.HostAssociations);
        Assert.Contains(
            result.Processes.Single(process => process.ProcessId == 100).Evidence,
            evidence => evidence.Source == "loaded-module");
    }

    [Fact]
    public void AnalyzeDoesNotTreatRuntimeWithUnavailableCommandLineAsBrowser()
    {
        ProcessSnapshotEntry host = CreateHost(100);
        ProcessSnapshotEntry runtime = CreateBrowser(200, 100) with
        {
            CommandLine = null,
            MetadataError = "Access is denied.",
        };

        WebView2RuntimeAnalysis result = WebView2RuntimeAdapter.Analyze(
            [host, runtime],
            CreateInspection());

        Assert.Empty(result.HostAssociations);
        Assert.Equal(
            WebView2ProcessRole.Subprocess,
            result.Processes.Single(process => process.ProcessId == 200).Role);
    }

    [Fact]
    public void AnalyzeAssociatesModuleHostThroughGenerationSafeParent()
    {
        ProcessSnapshotEntry host = CreateHost(100);
        ProcessSnapshotEntry browser = CreateBrowser(200, 100);

        WebView2HostAssociation association = Assert.Single(
            WebView2RuntimeAdapter.Analyze(
                [host, browser],
                CreateInspection()).HostAssociations);

        Assert.Equal((100, 200), (
            association.HostProcessId,
            association.BrowserProcessId));
        Assert.Equal(ProcessRelationshipConfidence.Medium, association.Confidence);
        Assert.False(association.IsAuthoritative);
        Assert.Contains(
            association.Evidence,
            evidence => evidence.Source == "process-snapshot");
    }

    [Fact]
    public void AnalyzeUsesHwndTopologyAndSupportsMultipleHostsPerBrowser()
    {
        ProcessSnapshotEntry firstHost = CreateHost(100);
        ProcessSnapshotEntry secondHost = CreateHost(101);
        ProcessSnapshotEntry browser = CreateBrowser(200, 0);
        WindowSnapshotResult windows = new(
            SnapshotTime,
            [
                CreateWindow(1, 100, "Chrome_WidgetWin_0", firstChild: 2),
                CreateWindow(2, 200, "Chrome_WidgetWin_0", parent: 1),
                CreateWindow(
                    3,
                    101,
                    "Windows.UI.Core.CoreComponentInputSource",
                    crossProcessChild: 4),
                CreateWindow(4, 200, "Chrome_WidgetWin_0", parent: 3),
            ],
            []);

        WebView2RuntimeAnalysis result = WebView2RuntimeAdapter.Analyze(
            [firstHost, secondHost, browser],
            CreateInspection(),
            windows);

        Assert.Equal(2, result.HostAssociations.Count);
        Assert.All(result.HostAssociations, association =>
        {
            Assert.Equal(200, association.BrowserProcessId);
            Assert.Equal(ProcessRelationshipConfidence.High, association.Confidence);
            Assert.True(association.IsAuthoritative);
            Assert.Contains(
                association.Evidence,
                evidence => evidence.Source is "child-window" or "window-property");
        });
    }

    [Fact]
    public void AnalyzeReportsDisagreementBetweenWindowAndParentEvidence()
    {
        ProcessSnapshotEntry host = CreateHost(100);
        ProcessSnapshotEntry parentBrowser = CreateBrowser(200, 100);
        ProcessSnapshotEntry windowBrowser = CreateBrowser(201, 0);
        WindowSnapshotResult windows = new(
            SnapshotTime,
            [
                CreateWindow(
                    1,
                    100,
                    "Chrome_WidgetWin_0",
                    crossProcessChild: 2),
                CreateWindow(2, 201, "Chrome_WidgetWin_0", parent: 1),
            ],
            []);

        WebView2RuntimeAnalysis result = WebView2RuntimeAdapter.Analyze(
            [host, parentBrowser, windowBrowser],
            CreateInspection(),
            windows);

        Assert.Contains(
            result.Issues,
            issue => issue.Stage == "webview2-evidence-disagreement");
        Assert.Equal(2, result.HostAssociations.Count);
    }

    private static ProcessSnapshotEntry CreateHost(int processId)
    {
        return new ProcessSnapshotEntry(
            processId,
            0,
            SnapshotTime,
            $"host-{processId}.exe",
            $@"C:\Apps\host-{processId}.exe",
            null,
            null,
            null,
            false,
            [],
            null)
        {
            LoadedModules =
            [
                $@"C:\Apps\host-{processId}\WebView2Loader.dll",
            ],
        };
    }

    private static ProcessSnapshotEntry CreateBrowser(
        int processId,
        int parentProcessId)
    {
        return new ProcessSnapshotEntry(
            processId,
            parentProcessId,
            SnapshotTime.AddSeconds(1),
            "msedgewebview2.exe",
            @"C:\WebView2\msedgewebview2.exe",
            @"""C:\WebView2\msedgewebview2.exe"" --user-data-dir=C:\Profile",
            "browser",
            @"C:\Profile",
            true,
            ["known executable: msedgewebview2.exe"],
            null);
    }

    private static WindowSnapshotEntry CreateWindow(
        long handle,
        int processId,
        string className,
        long? parent = null,
        long? firstChild = null,
        long? crossProcessChild = null)
    {
        return new WindowSnapshotEntry(
            handle,
            parent,
            firstChild,
            crossProcessChild,
            processId,
            SnapshotTime + (processId >= 200
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.Zero),
            1,
            className,
            true,
            null);
    }

    private static MojoPipeInspectionResult CreateInspection()
    {
        return new MojoPipeInspectionResult(
            SnapshotTime,
            [],
            new NamedPipeInspectionStatistics(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                TimeSpan.Zero),
            [],
            []);
    }
}
