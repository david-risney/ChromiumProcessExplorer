using System.Diagnostics;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class ProcessDetailsProviderTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 18, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateParsesQuotedCommandLineAndUnknownSwitch()
    {
        ProcessSnapshotEntry process = CreateProcess() with
        {
            CommandLine =
                @"""C:\Program Files\App\app.exe"" --type=renderer "
                + @"--future-option=""value with spaces""",
            ChromiumProcessType = "renderer",
        };
        ProcessDetailsProvider provider = new(new StubInspector());

        ProcessDetailEntry result = Assert.Single(
            provider.Create([process], includeSensitiveValues: true).Processes);

        Assert.Equal("observed-command-line", result.RoleSource);
        Assert.Equal(
            "value with spaces",
            result.Switches.Single(item => item.Name == "future-option").Value.Value);
        Assert.Equal(
            @"""C:\Program Files\App\app.exe"" --type=renderer "
                + @"--future-option=""value with spaces""",
            result.CommandLine.Value);
    }

    [Fact]
    public void CreateRedactsSensitiveValuesWithStableShape()
    {
        ProcessSnapshotEntry process = CreateProcess();
        ProcessDetailsProvider provider = new(new StubInspector());

        ProcessDetailEntry result = Assert.Single(
            provider.Create([process], includeSensitiveValues: false).Processes);

        Assert.True(result.ExecutablePath.IsRedacted);
        Assert.Null(result.ExecutablePath.Value);
        Assert.True(result.CommandLine.IsRedacted);
        Assert.All(result.LoadedModules, module => Assert.True(module.IsRedacted));
    }

    [Fact]
    public void CreatePreservesInaccessibleAndExitedErrors()
    {
        ProcessSnapshotEntry process = CreateProcess() with
        {
            MetadataError = "The process exited.",
            ModuleInspectionError = "Access is denied.",
        };
        ProcessDetailsProvider provider = new(new StubInspector
        {
            Details = new ProcessPlatformDetails(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [new DiscoveryIssue("process-details-open", "Access is denied.", 100)]),
        });

        ProcessDetailEntry result = Assert.Single(
            provider.Create([process], includeSensitiveValues: false).Processes);

        Assert.Contains(result.Issues, issue => issue.Stage == "process-metadata");
        Assert.Contains(result.Issues, issue => issue.Stage == "loaded-modules");
        Assert.Contains(result.Issues, issue => issue.Stage == "process-details-open");
    }

    [Fact]
    public void CreateRejectsPlatformDataAfterPidReuse()
    {
        ProcessSnapshotEntry process = CreateProcess();
        ProcessDetailsProvider provider = new(new StubInspector
        {
            Details = new ProcessPlatformDetails(
                SnapshotTime.AddSeconds(1),
                "x64",
                "x64",
                "Medium",
                false,
                "Package",
                new ProcessExecutableVersion(
                    "1.0",
                    "1.0",
                    "App",
                    "Company",
                    "app.exe"),
                []),
        });

        ProcessDetailEntry result = Assert.Single(
            provider.Create([process], includeSensitiveValues: true).Processes);

        Assert.Null(result.Architecture);
        Assert.Null(result.ExecutableVersion);
        Assert.Contains(
            result.Issues,
            issue => issue.Stage == "process-identity");
    }

    [Fact]
    public void CreateIncludesArchitectureIntegrityAndVersion()
    {
        ProcessDetailsProvider provider = new(new StubInspector
        {
            Details = new ProcessPlatformDetails(
                SnapshotTime,
                "x86",
                "x64",
                "Medium",
                false,
                "Contoso.App_1.0.0.0_x86__publisher",
                new ProcessExecutableVersion(
                    "1.2.3.4",
                    "1.2.3",
                    "Contoso App",
                    "Contoso",
                    "app.exe"),
                []),
        });

        ProcessDetailEntry result = Assert.Single(
            provider.Create([CreateProcess()], true).Processes);

        Assert.Equal("x86", result.Architecture);
        Assert.Equal("x64", result.NativeArchitecture);
        Assert.Equal("Medium", result.IntegrityLevel);
        Assert.False(result.IsElevated);
        Assert.Equal("1.2.3.4", result.ExecutableVersion?.FileVersion);
        Assert.Equal(
            "Contoso.App_1.0.0.0_x86__publisher",
            result.PackageFullName);
    }

    [Fact]
    public void CreateIdentifiesRuntimeAdapterRoleSource()
    {
        ProcessSnapshotEntry process = CreateProcess() with
        {
            CommandLine = @"""C:\Program Files\App\app.exe""",
            ChromiumProcessType = "electron-main",
            Evidence = ["Runtime adapter: classified process as electron-main."],
        };
        ProcessDetailsProvider provider = new(new StubInspector());

        ProcessDetailEntry result = Assert.Single(provider.Create([process], false).Processes);

        Assert.Equal("electron-main", result.ProcessRole);
        Assert.Equal("inferred-runtime-adapter", result.RoleSource);
    }

    [Fact]
    public void WindowsInspectorExtractsCurrentProcessArchitectureAndVersion()
    {
        using Process current = Process.GetCurrentProcess();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("No current executable path.");
        ProcessSnapshotEntry snapshot = new(
            current.Id,
            0,
            new DateTimeOffset(current.StartTime),
            Path.GetFileName(executablePath),
            executablePath,
            Environment.CommandLine,
            null,
            null,
            false,
            [],
            null);

        ProcessPlatformDetails result =
            new WindowsProcessDetailsPlatformInspector().Inspect(snapshot);

        Assert.NotNull(result.Architecture);
        Assert.NotNull(result.NativeArchitecture);
        Assert.NotNull(result.IntegrityLevel);
        Assert.NotNull(result.ExecutableVersion?.FileVersion);
        Assert.Equal(snapshot.CreationTime, result.ReopenedCreationTime);
    }

    private static ProcessSnapshotEntry CreateProcess()
    {
        return new ProcessSnapshotEntry(
            100,
            1,
            SnapshotTime,
            "app.exe",
            @"C:\Program Files\App\app.exe",
            @"""C:\Program Files\App\app.exe"" --type=renderer --flag=value",
            "renderer",
            @"C:\Profiles\App",
            true,
            ["--type command-line switch"],
            null)
        {
            LoadedModules = [@"C:\Program Files\App\module.dll"],
        };
    }

    private sealed class StubInspector : IProcessDetailsPlatformInspector
    {
        public ProcessPlatformDetails Details { get; init; } = new(
            SnapshotTime,
            "x64",
            "x64",
            "Medium",
            false,
            null,
            null,
            []);

        public ProcessPlatformDetails Inspect(ProcessSnapshotEntry process)
        {
            return Details;
        }
    }
}
