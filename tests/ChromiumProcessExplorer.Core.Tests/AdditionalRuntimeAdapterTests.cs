using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class AdditionalRuntimeAdapterTests : IDisposable
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ChromiumProcessExplorer.Additional.{Guid.NewGuid():N}");

    [Fact]
    public void AnalyzeClassifiesQtHostAndHelper()
    {
        string hostPath = CreateFile("QtApp", "sample.exe");
        string helperPath = CreateFile("QtApp", "QtWebEngineProcess.exe");
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(hostPath)!, "qtwebengine_resources.pak"),
            string.Empty);
        ProcessSnapshotEntry host = CreateProcess(100, 0, hostPath, null) with
        {
            LoadedModules = [Path.Combine(_root, "Qt6WebEngineCore.dll")],
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            101,
            100,
            helperPath,
            "--type=renderer");

        AdditionalRuntimeAnalysis result =
            AdditionalRuntimeAdapter.Analyze([host, renderer]);

        Assert.Equal(2, result.Processes.Count);
        Assert.Equal(
            AdditionalRuntimeProcessRole.Host,
            result.Processes.Single(process => process.ProcessId == 100).Role);
        Assert.Equal(
            AdditionalRuntimeProcessRole.Renderer,
            result.Processes.Single(process => process.ProcessId == 101).Role);
        Assert.Equal(
            ProcessRelationshipConfidence.High,
            result.Processes.Single(process => process.ProcessId == 100).Confidence);
        Assert.Equal(100, Assert.Single(result.Associations).Score);
    }

    [Fact]
    public void AnalyzeClassifiesRenamedNwJsLayout()
    {
        string executable = CreateFile("NwApp", "sample.exe");
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(executable)!, "package.nw"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(executable)!, "nw.dll"),
            string.Empty);
        ProcessSnapshotEntry process = CreateProcess(
            200,
            0,
            executable,
            null) with
        {
            LoadedModules = [Path.Combine(_root, "nw.dll")],
        };

        AdditionalRuntimeProcessInfo result = Assert.Single(
            AdditionalRuntimeAdapter.Analyze([process]).Processes);

        Assert.Equal(RuntimePlatformIds.Nwjs, result.PlatformId);
        Assert.Equal(AdditionalRuntimeProcessRole.Browser, result.Role);
        Assert.Equal(ProcessRelationshipConfidence.High, result.Confidence);
    }

    [Fact]
    public void AnalyzePropagatesBrowserAppIdentityToSubprocesses()
    {
        string executable = CreateFile("Browser", "msedge.exe");
        ProcessSnapshotEntry browser = CreateProcess(
            300,
            0,
            executable,
            "--app-id=abcdefghijklmnopabcdefghijklmnop");
        ProcessSnapshotEntry renderer = CreateProcess(
            301,
            300,
            executable,
            "--type=renderer --user-data-dir=C:\\Profiles\\Pwa");
        ProcessSnapshotEntry utility = CreateProcess(
            302,
            301,
            executable,
            "--type=utility");

        AdditionalRuntimeAnalysis result =
            AdditionalRuntimeAdapter.Analyze([browser, renderer, utility]);

        Assert.Equal(3, result.Processes.Count);
        Assert.All(
            result.Processes,
            process => Assert.Equal(
                RuntimePlatformIds.BrowserPwa,
                process.PlatformId));
        Assert.Equal(2, result.Associations.Count);
        Assert.Contains(
            result.Processes.Single(process => process.ProcessId == 302).Evidence,
            evidence => evidence.Source == "process-ancestry");
    }

    [Fact]
    public void AnalyzeRequiresCorroborationForGenericChromium()
    {
        ProcessSnapshotEntry weak = CreateProcess(
            400,
            0,
            CreateFile("Generic", "weak.exe"),
            "--type=renderer");
        ProcessSnapshotEntry corroborated = CreateProcess(
            401,
            0,
            CreateFile("Generic", "strong.exe"),
            "--type=renderer --user-data-dir=C:\\Profiles\\Strong");

        AdditionalRuntimeAnalysis result =
            AdditionalRuntimeAdapter.Analyze([weak, corroborated]);

        AdditionalRuntimeProcessInfo info = Assert.Single(result.Processes);
        Assert.Equal(401, info.ProcessId);
        Assert.Equal(RuntimePlatformIds.ChromiumGeneric, info.PlatformId);
    }

    [Theory]
    [InlineData("sciter.dll", "nonchromium.sciter")]
    [InlineData("Ultralight.dll", "nonchromium.ultralight")]
    public void AnalyzeExplicitlyExcludesKnownNonChromiumEngines(
        string moduleName,
        string platformId)
    {
        ProcessSnapshotEntry process = CreateProcess(
            500,
            0,
            CreateFile("Excluded", "sample.exe"),
            "--type=renderer --user-data-dir=C:\\Profiles\\Excluded") with
        {
            LoadedModules = [Path.Combine(_root, moduleName)],
        };

        AdditionalRuntimeAnalysis result =
            AdditionalRuntimeAdapter.Analyze([process]);

        Assert.Empty(result.Processes);
        Assert.Equal(platformId, Assert.Single(result.Exclusions).PlatformId);
    }

    [Fact]
    public void AnalyzeDoesNotOverrideWebView2Classification()
    {
        ProcessSnapshotEntry process = CreateProcess(
            600,
            0,
            CreateFile("Tauri", "sample.exe"),
            "--type=renderer --user-data-dir=C:\\Profiles\\Tauri") with
        {
            LoadedModules = [Path.Combine(_root, "chrome_elf.dll")],
        };
        WebView2RuntimeAnalysis webView2 = new(
            [new WebView2ProcessInfo(600, WebView2ProcessRole.Subprocess, [], null)],
            [],
            WindowSnapshotResult.Empty,
            []);

        AdditionalRuntimeAnalysis result = AdditionalRuntimeAdapter.Analyze(
            [process],
            webView2: webView2);

        Assert.Empty(result.Processes);
    }

    [Fact]
    public void AnalyzeRejectsAppSwitchOnUnknownExecutable()
    {
        ProcessSnapshotEntry process = CreateProcess(
            700,
            0,
            CreateFile("Unknown", "sample.exe"),
            "--app=https://example.test");

        Assert.Empty(AdditionalRuntimeAdapter.Analyze([process]).Processes);
    }

    [Fact]
    public void AnalyzeRejectsReusedParentPidAssociation()
    {
        string executable = CreateFile("Reused", "nw.exe");
        ProcessSnapshotEntry child = CreateProcess(
            801,
            800,
            executable,
            "--type=renderer --user-data-dir=C:\\Profiles\\Reused") with
        {
            CreationTime = SnapshotTime,
        };
        ProcessSnapshotEntry staleParent = CreateProcess(
            800,
            0,
            executable,
            "--user-data-dir=C:\\Profiles\\Reused") with
        {
            CreationTime = SnapshotTime.AddMinutes(1),
        };

        AdditionalRuntimeAnalysis result =
            AdditionalRuntimeAdapter.Analyze([staleParent, child]);

        Assert.Empty(result.Associations);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateFile(string directoryName, string fileName)
    {
        string directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        int parentProcessId,
        string executablePath,
        string? arguments)
    {
        return new ProcessSnapshotEntry(
            processId,
            parentProcessId,
            SnapshotTime.AddMilliseconds(processId),
            Path.GetFileName(executablePath),
            executablePath,
            arguments is null
                ? $"\"{executablePath}\""
                : $"\"{executablePath}\" {arguments}",
            null,
            null,
            true,
            [],
            null);
    }
}
