using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ElectronRuntimeAdapterTests : IDisposable
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 18, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ChromiumProcessExplorer.Electron.{Guid.NewGuid():N}");

    [Fact]
    public void AnalyzeDetectsRenamedPackageAndMultipleWindowRoles()
    {
        string executable = CreatePackagedApplication("Sample.exe");
        ProcessSnapshotEntry main = CreateProcess(100, 0, executable, null);
        ProcessSnapshotEntry firstRenderer = CreateProcess(
            101,
            100,
            executable,
            "--type=renderer --window-type=main");
        ProcessSnapshotEntry secondRenderer = CreateProcess(
            102,
            100,
            executable,
            "--type=renderer --window-type=secondary");
        ProcessSnapshotEntry devTools = CreateProcess(
            103,
            100,
            executable,
            "--type=renderer --window-type=devtools");

        ElectronRuntimeAnalysis result = ElectronRuntimeAdapter.Analyze(
            [main, firstRenderer, secondRenderer, devTools]);

        Assert.Equal(4, result.Processes.Count);
        Assert.Equal(3, result.Associations.Count);
        Assert.Equal(
            ElectronProcessRole.Main,
            result.Processes.Single(process => process.ProcessId == 100).Role);
        Assert.Equal(
            2,
            result.Processes.Count(process =>
                process.Role == ElectronProcessRole.Renderer));
        Assert.Equal(
            ElectronProcessRole.DevTools,
            result.Processes.Single(process => process.ProcessId == 103).Role);
        Assert.All(result.Associations, association =>
        {
            Assert.Equal(100, association.MainProcessId);
            Assert.True(association.IsAuthoritative);
        });
    }

    [Fact]
    public void AnalyzeDoesNotTreatWindowsPackageIdentityAsElectronEvidence()
    {
        string packageDirectory = Path.Combine(
            _root,
            "WindowsApps",
            "Contoso.NotElectron_1.0.0.0_x64__publisher",
            "app");
        Directory.CreateDirectory(packageDirectory);
        string executable = Path.Combine(packageDirectory, "native.exe");
        File.WriteAllText(executable, string.Empty);
        ProcessSnapshotEntry process = CreateProcess(100, 0, executable, null);

        ElectronRuntimeAnalysis result = ElectronRuntimeAdapter.Analyze([process]);

        Assert.Empty(result.Processes);
        Assert.Empty(result.Associations);
    }

    [Fact]
    public void AnalyzePreservesUnknownUtilitySubtype()
    {
        string executable = CreatePackagedApplication("UtilitySample.exe");
        ProcessSnapshotEntry main = CreateProcess(100, 0, executable, null);
        ProcessSnapshotEntry utility = CreateProcess(
            101,
            100,
            executable,
            "--type=utility --utility-sub-type=future.mojom.NewService");

        ElectronProcessInfo result = ElectronRuntimeAdapter.Analyze(
            [main, utility]).Processes.Single(process => process.ProcessId == 101);

        Assert.Equal(ElectronProcessRole.Utility, result.Role);
        Assert.Equal("future.mojom.NewService", result.UtilitySubType);
    }

    [Fact]
    public void AnalyzeUsesCooperativeWorkerAndServiceWorkerRoles()
    {
        string executable = CreatePackagedApplication("WorkerSample.exe");
        ProcessSnapshotEntry main = CreateProcess(100, 0, executable, null);
        ProcessSnapshotEntry worker = CreateProcess(
            101,
            100,
            executable,
            "--type=renderer");
        ProcessSnapshotEntry serviceWorker = CreateProcess(
            102,
            100,
            executable,
            "--type=renderer");
        ElectronCooperativeProcessInfo[] cooperative =
        [
            new(
                new ProcessIdentity(101, worker.CreationTime),
                ElectronProcessRole.Worker,
                "app.getAppMetrics()",
                ServiceName: "dedicated-worker"),
            new(
                new ProcessIdentity(102, serviceWorker.CreationTime),
                ElectronProcessRole.ServiceWorker,
                "webContents.getAllWebContents()",
                WebContentsType: "service-worker"),
        ];

        ElectronRuntimeAnalysis result = ElectronRuntimeAdapter.Analyze(
            [main, worker, serviceWorker],
            cooperative);

        Assert.Equal(
            ElectronProcessRole.Worker,
            result.Processes.Single(process => process.ProcessId == 101).Role);
        ElectronProcessInfo serviceWorkerResult = result.Processes.Single(
            process => process.ProcessId == 102);
        Assert.Equal(ElectronProcessRole.ServiceWorker, serviceWorkerResult.Role);
        Assert.True(serviceWorkerResult.HasCooperativeEvidence);
        Assert.Contains(
            serviceWorkerResult.Evidence,
            evidence => evidence.Source == "cooperative-electron-api");
    }

    [Fact]
    public void AnalyzeDoesNotClassifyElectronRunAsNodeHelperAsMain()
    {
        string executable = CreatePackagedApplication("NodeSample.exe");
        ProcessSnapshotEntry main = CreateProcess(100, 0, executable, null);
        ProcessSnapshotEntry nodeHelper = CreateProcess(
            101,
            100,
            executable,
            "worker.js");

        ElectronRuntimeAnalysis result = ElectronRuntimeAdapter.Analyze(
            [main, nodeHelper]);

        Assert.Single(
            result.Processes,
            process => process.Role == ElectronProcessRole.Main);
        Assert.Equal(
            ElectronProcessRole.NodeHelper,
            result.Processes.Single(process => process.ProcessId == 101).Role);
    }

    [Fact]
    public void AnalyzeSeparatesPackageAndRuntimePaths()
    {
        string packageRoot = Path.Combine(
            _root,
            "WindowsApps",
            "Contoso.Sample_1.2.3.4_x64__publisher");
        string executable = CreatePackagedApplication(
            "Sample.exe",
            Path.Combine(packageRoot, "app"),
            looseApplication: true);
        ProcessSnapshotEntry main = CreateProcess(100, 0, executable, null);
        ProcessSnapshotEntry renderer = CreateProcess(
            101,
            100,
            executable,
            @"--type=renderer --user-data-dir=""C:\Profiles\Sample""");

        ElectronProcessInfo result = ElectronRuntimeAdapter.Analyze(
            [main, renderer]).Processes.Single(process => process.ProcessId == 100);

        Assert.Equal(packageRoot, result.Paths.PackageRoot?.Value);
        Assert.EndsWith(
            Path.Combine("resources", "app"),
            result.Paths.ApplicationPath?.Value,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Profiles\Sample", result.Paths.UserDataDirectory?.Value);
        Assert.Equal("associated-electron-children", result.Paths.UserDataDirectory?.Source);
        Assert.Equal("Contoso.Sample_1.2.3.4_x64__publisher",
            result.PackageIdentity?.PackageFullName);
        Assert.Equal("Sample Product", result.PackageName);
        Assert.Equal("9.8.7", result.PackageVersion);
    }

    [Fact]
    public void AnalyzeRejectsPidGenerationMismatchAsAuthoritative()
    {
        string executable = CreatePackagedApplication("Reused.exe");
        ProcessSnapshotEntry main = CreateProcess(100, 0, executable, null) with
        {
            CreationTime = SnapshotTime.AddSeconds(10),
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            101,
            100,
            executable,
            "--type=renderer") with
        {
            CreationTime = SnapshotTime,
        };

        ElectronProcessAssociation association = Assert.Single(
            ElectronRuntimeAdapter.Analyze([main, renderer]).Associations);

        Assert.False(association.IsAuthoritative);
        Assert.Equal(ProcessRelationshipConfidence.Medium, association.Confidence);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreatePackagedApplication(
        string executableName,
        string? directory = null,
        bool looseApplication = false)
    {
        directory ??= Path.Combine(_root, Path.GetFileNameWithoutExtension(
            executableName));
        string resources = Path.Combine(directory, "resources");
        Directory.CreateDirectory(resources);
        if (looseApplication)
        {
            string application = Path.Combine(resources, "app");
            Directory.CreateDirectory(application);
            File.WriteAllText(
                Path.Combine(application, "package.json"),
                """{"name":"sample","productName":"Sample Product","version":"9.8.7"}""");
        }
        else
        {
            File.WriteAllText(Path.Combine(resources, "app.asar"), string.Empty);
        }

        string executable = Path.Combine(directory, executableName);
        File.WriteAllText(executable, string.Empty);
        return executable;
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        int parentProcessId,
        string executablePath,
        string? arguments)
    {
        string commandLine = $"\"{executablePath}\"";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            commandLine += " " + arguments;
        }

        return new ProcessSnapshotEntry(
            processId,
            parentProcessId,
            SnapshotTime.AddMilliseconds(processId),
            Path.GetFileName(executablePath),
            executablePath,
            commandLine,
            null,
            null,
            true,
            [],
            null);
    }
}
