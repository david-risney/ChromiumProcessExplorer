using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChromiumProcessExplorer.Core.Discovery;
using ChromiumProcessExplorer.Gui;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class GuiViewModelTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(2026, 8, 18, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PresentationTreeExcludesUnrelatedProcessesAndDuplicatesLogicalReferences()
    {
        ProcessSnapshotEntry browser = CreateProcess(
            100,
            "browser.exe",
            true);
        ProcessSnapshotEntry firstHost = CreateProcess(
            200,
            "host-one.exe",
            false);
        ProcessSnapshotEntry secondHost = CreateProcess(
            300,
            "host-two.exe",
            false);
        ProcessSnapshotEntry unrelated = CreateProcess(
            400,
            "unrelated.exe",
            false);
        ProcessGraph graph = new(
            [browser, firstHost, secondHost, unrelated],
            [
                CreateEdge(
                    firstHost,
                    browser,
                    ProcessRelationshipType.EmbeddedBy),
                CreateEdge(
                    secondHost,
                    browser,
                    ProcessRelationshipType.EmbeddedBy),
                CreateEdge(
                    unrelated,
                    browser,
                    ProcessRelationshipType.MojoConnection),
            ]);

        ProcessPresentationTree tree = ProcessPresentationTreeBuilder.Build(
            CreateDiscoveryResult(
                [browser, firstHost, secondHost, unrelated],
                graph));
        ProcessPresentationBranch[] branches = Flatten(tree.Roots).ToArray();

        Assert.DoesNotContain(
            branches,
            branch => branch.Process.Identity.ProcessId == unrelated.ProcessId);
        ProcessPresentationBranch[] browserBranches = branches
            .Where(branch => branch.Process.Identity.ProcessId == browser.ProcessId)
            .ToArray();
        Assert.Equal(2, browserBranches.Length);
        Assert.Single(browserBranches, branch => branch.IsReference);
        Assert.All(
            tree.Roots,
            root => Assert.True(root.Process.IsHost));
    }

    [Fact]
    public void PresentationTreeNormalizesPlatformAndUtilityBadges()
    {
        ProcessSnapshotEntry browser = CreateProcess(
            100,
            "chrome.exe",
            true);
        ProcessSnapshotEntry utility = CreateProcess(
            101,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "utility",
            CommandLine = "chrome.exe --type=utility "
                + "--utility-sub-type=network.mojom.NetworkService",
        };
        ProcessGraph graph = new(
            [browser, utility],
            [
                CreateEdge(
                    browser,
                    utility,
                    ProcessRelationshipType.ChromiumSubprocess),
            ]);

        ProcessPresentationBranch[] branches = Flatten(
            ProcessPresentationTreeBuilder.Build(
                CreateDiscoveryResult([browser, utility], graph)).Roots)
            .ToArray();

        Assert.Contains(
            branches,
            branch => branch.Process.Identity.ProcessId == browser.ProcessId
                && branch.Process.Platform == "Chrome"
                && branch.Process.Role == "Browser");
        Assert.Contains(
            branches,
            branch => branch.Process.Identity.ProcessId == utility.ProcessId
                && branch.Process.Platform == "Chrome"
                && branch.Process.Role == "Network service");
    }

    [Fact]
    public async Task RefreshRetainsExitedGenerationOnceThenRemovesIt()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        Queue<ChromiumDiscoveryResult> results = new(
        [
            CreateDiscoveryResult(
                [process],
                new ProcessGraph([process], [])),
            CreateDiscoveryResult([], new ProcessGraph([], [])),
            CreateDiscoveryResult([], new ProcessGraph([], [])),
        ]);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(results.Dequeue()),
        };
        MainViewModel viewModel = new(discovery, new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        Assert.False(Assert.Single(viewModel.ProcessRoots).IsStale);

        await viewModel.RefreshProcessesAsync();
        Assert.True(Assert.Single(viewModel.ProcessRoots).IsStale);

        await viewModel.RefreshProcessesAsync();
        Assert.Empty(viewModel.ProcessRoots);
    }

    [Fact]
    public async Task SelectionBuildsStructuredInspectorWithSensitiveValues()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true) with
        {
            ExecutablePath = @"C:\Apps\sample.exe",
            CommandLine = @"""C:\Apps\sample.exe"" --user-data-dir=C:\Profile",
        };
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
            DiscoverDetails = (processId, _) =>
            {
                Assert.Equal(123, processId);
                return ValueTask.FromResult(CreateDetails(process));
            },
        };
        MainViewModel viewModel = new(discovery, new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        viewModel.ProcessFilter = "renderer";
        Assert.Empty(viewModel.FilteredProcessRoots);
        viewModel.ProcessFilter = "sample";
        Assert.Single(viewModel.FilteredProcessRoots);

        await viewModel.SelectProcessAsync(Assert.Single(viewModel.ProcessRoots));

        ProcessInspectorViewModel inspector =
            Assert.IsType<ProcessInspectorViewModel>(viewModel.ProcessInspector);
        Assert.Equal(process.Identity(), inspector.Identity);
        Assert.Contains(@"C:\Apps\sample.exe", inspector.CommandLine);
        Assert.Contains(
            inspector.Switches,
            item => item.Name == "--user-data-dir"
                && item.Value == @"C:\Profile");
        Assert.Contains(
            inspector.Paths,
            item => item.Kind == "User data"
                && item.Value == @"C:\Profile");
        Assert.Contains(
            inspector.Executable,
            item => item.Label == "Architecture" && item.Value == "x64");
    }

    [Fact]
    public async Task RefreshPreservesSelectedProcessGeneration()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        ChromiumDiscoveryResult result = CreateDiscoveryResult(
            [process],
            new ProcessGraph([process], []));
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(result),
            DiscoverDetails = (_, _) =>
                ValueTask.FromResult(CreateDetails(process)),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectProcessAsync(
            Assert.Single(viewModel.FilteredProcessRoots));

        await viewModel.RefreshProcessesAsync();

        Assert.Equal(process.Identity(), viewModel.SelectedProcess?.Identity);
        Assert.Equal(process.Identity(), viewModel.ProcessInspector?.Identity);
        Assert.True(viewModel.SelectedProcess?.IsSelected);
    }

    [Fact]
    public async Task SelectionReportsProgressWhileDetailsAreLoading()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
            DiscoverDetails = async (_, cancellationToken) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return CreateDetails(process);
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();

        Task selection = viewModel.SelectProcessAsync(
            Assert.Single(viewModel.ProcessRoots));
        await started.Task;

        Assert.True(viewModel.IsLoadingSelection);
        Assert.True(viewModel.IsProcessActivityBusy);
        Assert.Equal("Loading details for PID 123.", viewModel.Status);

        release.SetResult();
        await selection;

        Assert.False(viewModel.IsLoadingSelection);
        Assert.False(viewModel.IsProcessActivityBusy);
        Assert.Equal("Loaded details for PID 123.", viewModel.Status);
    }

    [Fact]
    public async Task WindowsIconProviderReturnsCachedFallback()
    {
        WindowsProcessIconProvider provider = new();

        ImageSource? first = await provider.GetIconAsync(null, CancellationToken.None);
        ImageSource? second = await provider.GetIconAsync(
            "not-a-fully-qualified-path",
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
    }

    [Fact]
    public async Task WindowsIconProviderLoadsPackagedManifestLogo()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"cpe-icon-{Guid.NewGuid():N}");
        string packageRoot = Path.Combine(root, "SystemApps", "Test.Package");
        string assets = Path.Combine(packageRoot, "Assets");
        Directory.CreateDirectory(assets);
        string executablePath = Path.Combine(packageRoot, "SearchHost.exe");
        string logoPath = Path.Combine(assets, "Logo.targetsize-32.png");
        await File.WriteAllTextAsync(executablePath, string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "AppxManifest.xml"),
            """
            <Package>
              <Applications>
                <Application>
                  <VisualElements Square44x44Logo="Assets\Logo.png" />
                </Application>
              </Applications>
            </Package>
            """);
        await File.WriteAllBytesAsync(
            logoPath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC"
                + "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        try
        {
            WindowsProcessIconProvider provider = new();

            ImageSource? icon = await provider.GetIconAsync(
                executablePath,
                CancellationToken.None);

            Assert.IsType<BitmapImage>(icon);
            Assert.True(icon.IsFrozen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessNoticesAreDeduplicatedDismissedAndStayDismissed()
    {
        DiscoveryIssue first = new(
            "handle-query",
            "8 handle queries timed out.");
        DiscoveryIssue duplicate = new(
            "mojo-handle-query",
            "8 handle queries timed out.");
        ChromiumDiscoveryResult result = CreateDiscoveryResult(
            [],
            new ProcessGraph([], []),
            [first, duplicate]);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(result),
        };
        MainViewModel viewModel = new(discovery, new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        ContextIssueViewModel notice = Assert.Single(viewModel.ProcessNotices);

        viewModel.DismissProcessNotice(notice);
        Assert.Empty(viewModel.ProcessNotices);
        await viewModel.RefreshProcessesAsync();

        Assert.Empty(viewModel.ProcessNotices);
    }

    [Fact]
    public async Task AutoRefreshUsesMojoNameChangesAsLightweightTrigger()
    {
        ChromiumDiscoveryResult result = CreateDiscoveryResult(
            [],
            new ProcessGraph([], []));
        int processRefreshes = 0;
        int pipeEnumerations = 0;
        TaskCompletionSource refreshed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ =>
            {
                processRefreshes++;
                if (processRefreshes == 2)
                {
                    refreshed.SetResult();
                }

                return ValueTask.FromResult(result);
            },
            EnumerateMojoPipes = _ =>
            {
                pipeEnumerations++;
                return ValueTask.FromResult(
                    new MojoPipeEnumerationResult(
                        pipeEnumerations == 1
                            ? [new MojoPipeCandidate("mojo.changed", 123)]
                            : [],
                        []));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();

        viewModel.StartAutoRefresh();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.StopAutoRefresh();

        Assert.Equal(2, processRefreshes);
    }

    [Fact]
    public async Task FilteringExpandsParentsAndToggleCollapsesTree()
    {
        ProcessSnapshotEntry browser = CreateProcess(
            100,
            "chrome.exe",
            true);
        ProcessSnapshotEntry renderer = CreateProcess(
            101,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        ProcessGraph graph = new(
            [browser, renderer],
            [
                CreateEdge(
                    browser,
                    renderer,
                    ProcessRelationshipType.ChromiumSubprocess),
            ]);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult([browser, renderer], graph)),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();

        viewModel.ProcessFilter = "renderer";

        ProcessTreeItemViewModel root =
            Assert.Single(viewModel.FilteredProcessRoots);
        Assert.True(root.IsExpanded);
        Assert.Equal("Collapse all", viewModel.ProcessExpansionButtonText);

        viewModel.ToggleProcessExpansion();

        Assert.All(
            FlattenTree(viewModel.FilteredProcessRoots),
            item => Assert.False(item.IsExpanded));
        Assert.Equal("Expand all", viewModel.ProcessExpansionButtonText);
    }

    [Fact]
    public async Task InstallationFilterMatchesMetadataAndPath()
    {
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverInstallations = _ => ValueTask.FromResult(
                new InstallationDiscoveryResult(
                    SnapshotTime,
                    [
                        CreateInstallation(
                            "Microsoft Edge",
                            "Edge",
                            @"C:\Program Files\Edge"),
                        CreateInstallation(
                            "Razer Central",
                            "CEF",
                            @"C:\Program Files\Razer"),
                    ],
                    new InstallationDiscoveryStatistics(
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    [])),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshInstallationsAsync();

        viewModel.InstallationFilter = "razer";
        await Task.Delay(300);

        InstallationItemViewModel installation =
            Assert.Single(viewModel.FilteredInstallations);
        Assert.Equal("Razer Central", installation.Name);
    }

    [Fact]
    public async Task InstallationFilterPreservesMatchingSelection()
    {
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverInstallations = _ => ValueTask.FromResult(
                new InstallationDiscoveryResult(
                    SnapshotTime,
                    [
                        CreateInstallation(
                            "Microsoft Edge",
                            "Edge",
                            @"C:\Program Files\Edge"),
                        CreateInstallation(
                            "Razer Central",
                            "CEF",
                            @"C:\Program Files\Razer"),
                    ],
                    new InstallationDiscoveryStatistics(
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    [])),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshInstallationsAsync();
        viewModel.SelectedInstallation = viewModel.Installations[1];

        viewModel.InstallationFilter = "razer";
        await Task.Delay(300);

        Assert.Equal(
            @"C:\Program Files\Razer",
            viewModel.SelectedInstallation?.InstallPath);
    }

    [Fact]
    public void FixedRuntimeUsesFixedAppChannelAndReadableCopyText()
    {
        InstallationItemViewModel installation = new(
            CreateInstallation(
                "Embedded Chromium",
                "CEF",
                @"C:\Apps\Sample",
                kind: "Runtime",
                version: "128.0.6613.120+commit",
                isSharedRuntime: false));

        Assert.Equal("FixedApp", installation.Channel);
        Assert.Contains(
            "128.0.6613.120+commit",
            MainViewModel.GetInstallationLineText(installation));
        Assert.Contains(
            "Runtime scope: App-local runtime",
            MainViewModel.GetInstallationDetailsText(installation));
    }

    [Fact]
    public async Task InstallationNoticesStayDismissedAcrossRefresh()
    {
        DiscoveryIssue issue = new(
            "filesystem",
            "Access to a Chromium install folder was denied.");
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverInstallations = _ => ValueTask.FromResult(
                new InstallationDiscoveryResult(
                    SnapshotTime,
                    [],
                    new InstallationDiscoveryStatistics(
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                    [issue])),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshInstallationsAsync();
        ContextIssueViewModel notice =
            Assert.Single(viewModel.InstallationNotices);
        viewModel.DismissNotice(notice);
        await viewModel.RefreshInstallationsAsync();

        Assert.Empty(viewModel.InstallationNotices);
    }

    [Fact]
    public async Task StaleSelectionUsesCachedDetailsWithoutQueryingReusedPid()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        Queue<ChromiumDiscoveryResult> results = new(
        [
            CreateDiscoveryResult(
                [process],
                new ProcessGraph([process], [])),
            CreateDiscoveryResult([], new ProcessGraph([], [])),
        ]);
        int detailQueries = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(results.Dequeue()),
            DiscoverDetails = (_, _) =>
            {
                detailQueries++;
                return ValueTask.FromResult(CreateDetails(process));
            },
        };
        MainViewModel viewModel = new(discovery, new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectProcessAsync(Assert.Single(viewModel.ProcessRoots));

        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel stale = Assert.Single(viewModel.ProcessRoots);
        await viewModel.SelectProcessAsync(stale);

        Assert.Equal(1, detailQueries);
        Assert.True(viewModel.ProcessInspector?.IsStale);
        Assert.Contains(
            viewModel.ProcessInspector!.Summary,
            row => row.Label == "State" && row.Value == "Exited");
    }

    [Fact]
    public async Task CancellationAndRefreshFailureRemainVisible()
    {
        TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService cancellingDiscovery = new()
        {
            DiscoverProcesses = async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        MainViewModel cancelling = new(
            cancellingDiscovery,
            new StubIconProvider());

        Task refresh = cancelling.RefreshProcessesAsync();
        await started.Task;
        Assert.True(cancelling.IsRefreshingProcesses);
        cancelling.CancelProcessRefresh();
        await refresh;

        Assert.False(cancelling.IsBusy);
        Assert.False(cancelling.IsRefreshingProcesses);
        Assert.Contains(
            "cancelled",
            cancelling.Status,
            StringComparison.OrdinalIgnoreCase);

        StubGuiDiscoveryService failingDiscovery = new()
        {
            DiscoverProcesses = _ => throw new InvalidOperationException(
                "Synthetic discovery failure."),
        };
        MainViewModel failing = new(
            failingDiscovery,
            new StubIconProvider());

        await failing.RefreshProcessesAsync();

        ContextIssueViewModel issue = Assert.Single(failing.ProcessNotices);
        Assert.Equal("gui", issue.Source);
        Assert.Contains("Synthetic discovery failure", issue.Message);
    }

    private static ChromiumDiscoveryResult CreateDiscoveryResult(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        ProcessGraph graph,
        IReadOnlyList<DiscoveryIssue>? issues = null)
    {
        return new ChromiumDiscoveryResult(
            SnapshotTime,
            processes,
            graph,
            graph.CreateProcessTree(),
            new MojoPipeInspectionResult(
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
                []),
            issues ?? []);
    }

    private static ProcessDetailsResult CreateDetails(
        ProcessSnapshotEntry process)
    {
        return new ProcessDetailsResult(
            "1.0",
            SnapshotTime,
            true,
            [
                new ProcessDetailEntry(
                    process.Identity(),
                    process.ParentProcessId,
                    process.ImageName,
                    Sensitive(@"C:\Apps\sample.exe"),
                    Sensitive(
                        @"""C:\Apps\sample.exe"" --user-data-dir=C:\Profile"),
                    [
                        new ProcessSwitchDetail(
                            "user-data-dir",
                            true,
                            Sensitive(@"C:\Profile")),
                    ],
                    "browser",
                    "command-line",
                    Sensitive(@"C:\Profile"),
                    new ProcessExecutableVersion(
                        "1.2.3.4",
                        "1.2.3.4",
                        "Sample",
                        "Example",
                        "sample.exe"),
                    "x64",
                    "x64",
                    "Medium",
                    false,
                    null,
                    ["test evidence"],
                    [],
                    []),
            ],
            []);
    }

    private static SensitiveStringValue Sensitive(string value)
    {
        return new SensitiveStringValue(value, false, "test");
    }

    private static ChromiumInstallation CreateInstallation(
        string name,
        string platform,
        string path,
        string kind = "Application",
        string version = "1.0",
        bool? isSharedRuntime = false)
    {
        return new ChromiumInstallation(
            name,
            kind,
            platform,
            path,
            Path.Combine(path, $"{name}.exe"),
            version,
            null,
            new InstallationMetadata(
                "x64",
                null,
                "Portable",
                "test",
                "test",
                null,
                null,
                null,
                isSharedRuntime,
                "High"),
            []);
    }

    private static ProcessGraphEdge CreateEdge(
        ProcessSnapshotEntry source,
        ProcessSnapshotEntry target,
        ProcessRelationshipType relationship)
    {
        return new ProcessGraphEdge(
            source.Identity(),
            target.Identity(),
            relationship,
            new ProcessRelationshipEvidence(
                "test",
                ProcessRelationshipConfidence.High,
                SnapshotTime,
                new Dictionary<string, string?>
                {
                    ["reason"] = "test association",
                }));
    }

    private static ProcessSnapshotEntry CreateProcess(
        int processId,
        string imageName,
        bool isLikelyChromium)
    {
        return new ProcessSnapshotEntry(
            processId,
            0,
            SnapshotTime.AddSeconds(processId),
            imageName,
            null,
            null,
            isLikelyChromium ? "browser" : null,
            null,
            isLikelyChromium,
            [],
            null);
    }

    private static IEnumerable<ProcessPresentationBranch> Flatten(
        IEnumerable<ProcessPresentationBranch> roots)
    {
        foreach (ProcessPresentationBranch root in roots)
        {
            yield return root;
            foreach (ProcessPresentationBranch child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<ProcessTreeItemViewModel> FlattenTree(
        IEnumerable<ProcessTreeItemViewModel> roots)
    {
        foreach (ProcessTreeItemViewModel root in roots)
        {
            yield return root;
            foreach (ProcessTreeItemViewModel child in FlattenTree(root.Children))
            {
                yield return child;
            }
        }
    }

    private sealed class StubIconProvider : IProcessIconProvider
    {
        public ValueTask<ImageSource?> GetIconAsync(
            string? executablePath,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ImageSource?>(null);
        }
    }

    private sealed class StubGuiDiscoveryService : IGuiDiscoveryService
    {
        public Func<CancellationToken, ValueTask<ChromiumDiscoveryResult>>
            DiscoverProcesses
        { get; init; } =
            _ => ValueTask.FromResult(
                CreateDiscoveryResult([], new ProcessGraph([], [])));

        public Func<int, CancellationToken, ValueTask<ProcessDetailsResult>>
            DiscoverDetails
        { get; init; } =
            (_, _) => ValueTask.FromResult(
                new ProcessDetailsResult(
                    "1.0",
                    SnapshotTime,
                    true,
                    [],
                    []));

        public Func<CancellationToken, ValueTask<MojoPipeEnumerationResult>>
            EnumerateMojoPipes
        { get; init; } =
            _ => ValueTask.FromResult(
                new MojoPipeEnumerationResult([], []));

        public Func<CancellationToken, ValueTask<InstallationDiscoveryResult>>
            DiscoverInstallations
        { get; init; } =
            _ => ValueTask.FromResult(new InstallationDiscoveryResult(
                SnapshotTime,
                [],
                new InstallationDiscoveryStatistics(
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                []));

        public ValueTask<ChromiumDiscoveryResult> DiscoverProcessesAsync(
            CancellationToken cancellationToken)
        {
            return DiscoverProcesses(cancellationToken);
        }

        public ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            return DiscoverDetails(processId, cancellationToken);
        }

        public ValueTask<MojoPipeEnumerationResult> EnumerateMojoPipesAsync(
            CancellationToken cancellationToken)
        {
            return EnumerateMojoPipes(cancellationToken);
        }

        public ValueTask<DiagnosticArtifactDiscoveryResult>
            DiscoverDiagnosticsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(
                new DiagnosticArtifactDiscoveryResult(
                    "1.0",
                    SnapshotTime,
                    true,
                    true,
                    [],
                    [],
                    []));
        }

        public ValueTask<InstallationDiscoveryResult> DiscoverInstallationsAsync(
            CancellationToken cancellationToken)
        {
            return DiscoverInstallations(cancellationToken);
        }
    }
}

file static class ProcessSnapshotEntryExtensions
{
    public static ProcessIdentity Identity(this ProcessSnapshotEntry process)
    {
        return new ProcessIdentity(
            process.ProcessId,
            process.CreationTime);
    }
}
