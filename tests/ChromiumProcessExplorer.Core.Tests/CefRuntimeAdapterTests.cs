using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class CefRuntimeAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ChromiumProcessExplorer.CefTests.{Guid.NewGuid():N}");

    [Fact]
    public void AnalyzeClassifiesSameExecutableLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        string executable = @"C:\Apps\Sample\sample.exe";
        ProcessSnapshotEntry browser = CreateProcess(
            10,
            1,
            start,
            executable,
            $"\"{executable}\" --user-data-dir=\"C:\\Profiles\\Sample\"",
            null,
            "C:\\Profiles\\Sample") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            11,
            10,
            start.AddSeconds(1),
            executable,
            $"\"{executable}\" --type=renderer "
                + "--user-data-dir=\"C:\\Profiles\\Sample\"",
            "renderer",
            "C:\\Profiles\\Sample") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze([browser, renderer]);

        Assert.Equal(2, result.Processes.Count);
        Assert.All(
            result.Processes,
            process => Assert.Equal(
                CefDeploymentLayout.SameExecutable,
                process.Layout));
        Assert.Equal(
            CefProcessRole.Browser,
            result.Processes.Single(process => process.ProcessId == 10).Role);
        Assert.Equal(
            CefProcessRole.Renderer,
            result.Processes.Single(process => process.ProcessId == 11).Role);
        CefProcessAssociation association = Assert.Single(result.Associations);
        Assert.Equal(CefAssociationConfidence.High, association.Confidence);
        Assert.True(association.IsAuthoritative);
    }

    [Fact]
    public void AnalyzeClassifiesSeparateSubprocessLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        string browserExecutable = @"C:\Apps\Sample\sample.exe";
        string subprocessExecutable = @"C:\Apps\Sample\sample-helper.exe";
        ProcessSnapshotEntry browser = CreateProcess(
            20,
            1,
            start,
            browserExecutable,
            $"\"{browserExecutable}\" "
                + $"--browser-subprocess-path=\"{subprocessExecutable}\"") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            21,
            20,
            start.AddSeconds(1),
            subprocessExecutable,
            $"\"{subprocessExecutable}\" --type=renderer",
            "renderer");

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze([browser, renderer]);

        Assert.Equal(2, result.Processes.Count);
        Assert.All(
            result.Processes,
            process => Assert.Equal(
                CefDeploymentLayout.SeparateSubprocess,
                process.Layout));
        CefProcessAssociation association = Assert.Single(result.Associations);
        Assert.Contains(
            association.Evidence,
            evidence => evidence.Contains(
                "--browser-subprocess-path",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeClassifiesBootstrapLayoutAndUtilityRole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        string executable = @"C:\Apps\Sample\bootstrap.exe";
        ProcessSnapshotEntry browser = CreateProcess(
            30,
            1,
            start,
            executable,
            $"\"{executable}\"") with
        {
            LoadedModules = [@"C:\SharedCEF\libcef.dll"],
        };
        ProcessSnapshotEntry utility = CreateProcess(
            31,
            30,
            start.AddSeconds(1),
            executable,
            $"\"{executable}\" --type=utility "
                + "--service-sandbox-type=network "
                + "--utility-sub-type=network.mojom.NetworkService",
            "utility") with
        {
            LoadedModules = [@"C:\SharedCEF\libcef.dll"],
        };

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze([browser, utility]);

        CefProcessInfo utilityInfo = result.Processes.Single(
            process => process.ProcessId == 31);
        Assert.Equal(CefProcessRole.Utility, utilityInfo.Role);
        Assert.Equal("network", utilityInfo.UtilityRole);
        Assert.Equal(
            "network.mojom.NetworkService",
            utilityInfo.UtilitySubType);
        Assert.All(
            result.Processes,
            process => Assert.Equal(
                CefDeploymentLayout.BootstrapOrDllHosted,
                process.Layout));
    }

    [Fact]
    public void AnalyzeSurfacesExplicitPathsWrappersMarkersAndWarnings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string applicationDirectory = Path.Combine(_root, "app");
        string profileDirectory = Path.Combine(_root, "profile");
        Directory.CreateDirectory(applicationDirectory);
        Directory.CreateDirectory(profileDirectory);
        string executable = Path.Combine(applicationDirectory, "sample.exe");
        string libcef = Path.Combine(applicationDirectory, "libcef.dll");
        string crashConfiguration = Path.Combine(
            applicationDirectory,
            "crash_reporter.cfg");
        string activePort = Path.Combine(profileDirectory, "DevToolsActivePort");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(libcef, string.Empty);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "CefSharp.Core.dll"),
            string.Empty);
        File.WriteAllText(crashConfiguration, string.Empty);
        File.WriteAllText(activePort, "9222");
        ProcessSnapshotEntry process = CreateProcess(
            40,
            1,
            DateTimeOffset.UtcNow,
            executable,
            $"\"{executable}\" --user-data-dir=\"{profileDirectory}\" "
                + "--log-file=\"C:\\Logs\\cef.log\" "
                + "--resources-dir-path=\"C:\\Runtime\\Resources\" "
                + "--locales-dir-path=\"C:\\Runtime\\locales\" "
                + "--crash-dumps-dir=\"C:\\Crash Data\" "
                + "--remote-debugging-port=9222 --remote-debugging-pipe "
                + "--no-sandbox --disable-web-security --single-process",
            null,
            profileDirectory,
            ["framework: CEF4Delphi"]);

        CefProcessInfo result = Assert.Single(
            CefRuntimeAdapter.Analyze([process]).Processes);

        Assert.Equal(profileDirectory, result.RuntimePaths.UserDataDirectory);
        Assert.Equal(@"C:\Logs\cef.log", result.RuntimePaths.LogFile);
        Assert.Equal(
            @"C:\Runtime\Resources",
            result.RuntimePaths.ResourcesDirectory);
        Assert.Equal(@"C:\Runtime\locales", result.RuntimePaths.LocalesDirectory);
        Assert.Equal(@"C:\Crash Data", result.RuntimePaths.CrashReportDirectory);
        Assert.Equal(
            crashConfiguration,
            result.RuntimePaths.CrashReportConfigurationFile);
        Assert.Equal(activePort, result.RuntimePaths.DevToolsActivePortFile);
        Assert.Equal("9222", result.RemoteDebuggingPort);
        Assert.True(result.RemoteDebuggingPipe);
        Assert.Equal(["CEF4Delphi", "CefSharp"], result.Wrappers);
        Assert.Contains(
            result.Evidence,
            evidence => evidence.Source == "filesystem-marker"
                && evidence.Path == libcef);
        Assert.Contains(
            result.Evidence,
            evidence => evidence.Source == "command-line-switch"
                && evidence.Detail == "--no-sandbox");
        Assert.Contains(
            result.SwitchWarnings,
            warning => warning.Switch == "--no-sandbox");
        Assert.Contains(
            result.SwitchWarnings,
            warning => warning.Switch == "--disable-web-security");
        Assert.Contains(
            result.SwitchWarnings,
            warning => warning.Switch == "--single-process");
    }

    [Fact]
    public void AnalyzeKeepsWeakAssociationNonAuthoritative()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessSnapshotEntry browser = CreateProcess(
            50,
            1,
            start,
            @"C:\Apps\Sample\sample.exe",
            "\"C:\\Apps\\Sample\\sample.exe\" "
                + "--user-data-dir=\"C:\\Profiles\\Sample\"",
            null,
            @"C:\Profiles\Sample") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            51,
            2,
            start.AddSeconds(1),
            @"C:\Apps\Sample\unrelated-helper.exe",
            "\"C:\\Apps\\Sample\\unrelated-helper.exe\" --type=renderer "
                + "--user-data-dir=\"C:\\Profiles\\Sample\"",
            "renderer",
            @"C:\Profiles\Sample") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };

        CefProcessAssociation association = Assert.Single(
            CefRuntimeAdapter.Analyze([browser, renderer]).Associations);

        Assert.Equal(CefAssociationConfidence.Low, association.Confidence);
        Assert.False(association.IsAuthoritative);
    }

    [Fact]
    public void AnalyzeDoesNotTreatExternalCrashpadAsSubprocessLayout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        string executable = @"C:\Apps\Sample\sample.exe";
        ProcessSnapshotEntry browser = CreateProcess(
            60,
            1,
            start,
            executable,
            $"\"{executable}\"") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            61,
            60,
            start.AddSeconds(1),
            executable,
            $"\"{executable}\" --type=renderer",
            "renderer") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry crashpad = CreateProcess(
            62,
            60,
            start.AddSeconds(2),
            @"C:\Apps\Sample\crashpad_handler.exe",
            "\"C:\\Apps\\Sample\\crashpad_handler.exe\" "
                + "--type=crashpad-handler",
            "crashpad-handler") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze(
            [browser, renderer, crashpad]);

        Assert.All(
            result.Processes,
            process => Assert.Equal(
                CefDeploymentLayout.SameExecutable,
                process.Layout));
    }

    [Fact]
    public void AnalyzeLeavesCrashpadOnlyLayoutUnknown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessSnapshotEntry browser = CreateProcess(
            65,
            1,
            start,
            @"C:\Apps\Sample\sample.exe",
            "\"C:\\Apps\\Sample\\sample.exe\"") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry crashpad = CreateProcess(
            66,
            65,
            start.AddSeconds(1),
            @"C:\Apps\Sample\crashpad_handler.exe",
            "\"C:\\Apps\\Sample\\crashpad_handler.exe\" "
                + "--type=crashpad-handler",
            "crashpad-handler") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze([browser, crashpad]);

        Assert.All(
            result.Processes,
            process => Assert.Equal(
                CefDeploymentLayout.Unknown,
                process.Layout));
    }

    [Fact]
    public void AnalyzeOmitsUnassociatedProcessWithoutCefEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessSnapshotEntry renderer = CreateProcess(
            70,
            1,
            DateTimeOffset.UtcNow,
            @"C:\Other\chrome.exe",
            "\"C:\\Other\\chrome.exe\" --type=renderer",
            "renderer",
            evidence: ["--type command-line switch"]);

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze([renderer]);

        Assert.Empty(result.Processes);
        Assert.Empty(result.Associations);
    }

    [Fact]
    public void AnalyzeDoesNotTreatGenericChromiumFilesAsCefAnchor()
    {
        string applicationDirectory = Path.Combine(_root, "generic-chromium");
        Directory.CreateDirectory(applicationDirectory);
        string executable = Path.Combine(applicationDirectory, "generic.exe");
        File.WriteAllText(executable, string.Empty);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "resources.pak"),
            string.Empty);
        File.WriteAllText(
            Path.Combine(applicationDirectory, "icudtl.dat"),
            string.Empty);
        ProcessSnapshotEntry process = CreateProcess(
            80,
            1,
            DateTimeOffset.UtcNow,
            executable,
            string.Empty);

        CefRuntimeAnalysis result = CefRuntimeAdapter.Analyze([process]);

        Assert.Empty(result.Processes);
    }

    [Fact]
    public void AnalyzeDoesNotAuthorizeParentWithoutCreationTimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string executable = @"C:\Apps\Sample\sample.exe";
        ProcessSnapshotEntry browser = CreateProcess(
            90,
            1,
            null,
            executable,
            $"\"{executable}\"") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            91,
            90,
            null,
            executable,
            $"\"{executable}\" --type=renderer",
            "renderer") with
        {
            LoadedModules = [@"C:\Apps\Sample\libcef.dll"],
        };

        CefProcessAssociation association = Assert.Single(
            CefRuntimeAdapter.Analyze([browser, renderer]).Associations);

        Assert.False(association.IsAuthoritative);
        Assert.DoesNotContain(
            association.Evidence,
            evidence => evidence.Contains(
                "Generation-safe",
                StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        int parentProcessId,
        DateTimeOffset? creationTime,
        string executablePath,
        string commandLine,
        string? processType = null,
        string? userDataDirectory = null,
        IReadOnlyList<string>? evidence = null)
    {
        return new ProcessSnapshotEntry(
            processId,
            parentProcessId,
            creationTime,
            Path.GetFileName(executablePath),
            executablePath,
            commandLine,
            processType,
            userDataDirectory,
            true,
            evidence ?? [],
            null);
    }
}
