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
    public void PresentationTreeIncludesSameExecutableChromiumParent()
    {
        ProcessSnapshotEntry parent = CreateProcess(
            17636,
            "code.exe",
            false) with
        {
            ExecutablePath = @"C:\Code\code.exe",
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            17637,
            "code.exe",
            true) with
        {
            ParentProcessId = parent.ProcessId,
            ExecutablePath = parent.ExecutablePath,
            ChromiumProcessType = "renderer",
        };
        ProcessSnapshotEntry unrelatedParent = CreateProcess(
            20000,
            "explorer.exe",
            false) with
        {
            ExecutablePath = @"C:\Windows\explorer.exe",
        };
        ProcessSnapshotEntry otherRenderer = CreateProcess(
            20001,
            "code.exe",
            true) with
        {
            ParentProcessId = unrelatedParent.ProcessId,
            ExecutablePath = parent.ExecutablePath,
            ChromiumProcessType = "renderer",
        };
        ProcessGraph graph = new(
            [parent, renderer, unrelatedParent, otherRenderer],
            [
                CreateEdge(
                    parent,
                    renderer,
                    ProcessRelationshipType.OsParent),
                CreateEdge(
                    unrelatedParent,
                    otherRenderer,
                    ProcessRelationshipType.OsParent),
            ]);

        ProcessPresentationTree tree = ProcessPresentationTreeBuilder.Build(
            CreateDiscoveryResult(
                [parent, renderer, unrelatedParent, otherRenderer],
                graph));
        ProcessPresentationBranch parentBranch = Assert.Single(
            tree.Roots,
            root => root.Process.Identity.ProcessId == parent.ProcessId);

        Assert.Equal("Browser", parentBranch.Process.Role);
        Assert.Contains(
            parentBranch.Children,
            child => child.Process.Identity.ProcessId == renderer.ProcessId);
        Assert.DoesNotContain(
            Flatten(tree.Roots),
            branch => branch.Process.Identity.ProcessId
                == unrelatedParent.ProcessId);
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
    public void PresentationTreeSortsByRoleThenPidWithRenderersLast()
    {
        ProcessSnapshotEntry browser = CreateProcess(100, "chrome.exe", true);
        ProcessSnapshotEntry laterGpu = CreateProcess(
            500,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "gpu-process",
        };
        ProcessSnapshotEntry earlierGpu = CreateProcess(
            300,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "gpu-process",
        };
        ProcessSnapshotEntry utility = CreateProcess(
            200,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "utility",
        };
        ProcessSnapshotEntry renderer = CreateProcess(
            150,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        ProcessSnapshotEntry rootRenderer = CreateProcess(
            50,
            "other.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        ProcessGraph graph = new(
            [browser, laterGpu, earlierGpu, utility, renderer, rootRenderer],
            [
                CreateEdge(
                    browser,
                    laterGpu,
                    ProcessRelationshipType.ChromiumSubprocess),
                CreateEdge(
                    browser,
                    earlierGpu,
                    ProcessRelationshipType.ChromiumSubprocess),
                CreateEdge(
                    browser,
                    utility,
                    ProcessRelationshipType.ChromiumSubprocess),
                CreateEdge(
                    browser,
                    renderer,
                    ProcessRelationshipType.ChromiumSubprocess),
            ]);

        ProcessPresentationTree tree = ProcessPresentationTreeBuilder.Build(
            CreateDiscoveryResult(
                [browser, laterGpu, earlierGpu, utility, renderer, rootRenderer],
                graph));
        ProcessPresentationBranch browserBranch = Assert.Single(
            tree.Roots,
            root => root.Process.Identity.ProcessId == browser.ProcessId);

        Assert.Equal(
            [earlierGpu.ProcessId, laterGpu.ProcessId, utility.ProcessId, renderer.ProcessId],
            browserBranch.Children.Select(child =>
                child.Process.Identity.ProcessId));
        Assert.Equal(
            rootRenderer.ProcessId,
            tree.Roots[^1].Process.Identity.ProcessId);
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
        viewModel.ProcessFilter = "role:renderer";
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
    public async Task RefreshAppliesExistingIconBeforeIconReloadCompletes()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        ChromiumDiscoveryResult result = CreateDiscoveryResult(
            [process],
            new ProcessGraph([process], []));
        ProcessSnapshotEntry added = CreateProcess(
            456,
            "other.exe",
            true);
        ChromiumDiscoveryResult changed = CreateDiscoveryResult(
            [process, added],
            new ProcessGraph([process, added], []));
        DrawingImage icon = new();
        icon.Freeze();
        TaskCompletionSource reloadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseReload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        DelegatingIconProvider icons = new(async (_, cancellationToken) =>
        {
            requests++;
            if (requests > 1)
            {
                reloadStarted.TrySetResult();
                await releaseReload.Task.WaitAsync(cancellationToken);
            }

            return icon;
        });
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(result),
            DiscoverLightProcesses = (_, _) => ValueTask.FromResult(changed),
        };
        using MainViewModel viewModel = new(
            discovery,
            icons,
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();
        Assert.Same(icon, Assert.Single(viewModel.ProcessRoots).Icon);

        viewModel.StartAutoRefresh();
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.StopAutoRefresh();

        ProcessTreeItemViewModel surviving = Assert.Single(
            viewModel.ProcessRoots,
            item => item.Identity == process.Identity());
        Assert.Same(icon, surviving.Icon);

        releaseReload.SetResult();
        await WaitForAsync(() => !viewModel.IsProcessActivityBusy);
    }

    [Fact]
    public async Task SelectingCachedProcessGenerationDoesNotReloadDetails()
    {
        ProcessSnapshotEntry first = CreateProcess(123, "first.exe", true);
        ProcessSnapshotEntry second = CreateProcess(456, "second.exe", true);
        Dictionary<int, int> detailQueries = [];
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [first, second],
                    new ProcessGraph([first, second], []))),
            DiscoverDetails = (processId, _) =>
            {
                detailQueries[processId] =
                    detailQueries.GetValueOrDefault(processId) + 1;
                return ValueTask.FromResult(CreateDetails(
                    processId == first.ProcessId ? first : second));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel firstItem = Assert.Single(
            viewModel.ProcessRoots,
            item => item.Identity == first.Identity());
        ProcessTreeItemViewModel secondItem = Assert.Single(
            viewModel.ProcessRoots,
            item => item.Identity == second.Identity());

        await viewModel.SelectProcessAsync(firstItem);
        await viewModel.SelectProcessAsync(secondItem);
        await viewModel.SelectProcessAsync(firstItem);

        Assert.Equal(1, detailQueries[first.ProcessId]);
        Assert.Equal(1, detailQueries[second.ProcessId]);
        Assert.Equal(first.Identity(), viewModel.ProcessInspector?.Identity);
        Assert.Contains(
            "cached details",
            viewModel.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LightRefreshRetainsCachedInspectorWithoutReloadingDetails()
    {
        ProcessSnapshotEntry process = CreateProcess(123, "sample.exe", true);
        ProcessSnapshotEntry added = CreateProcess(456, "other.exe", true);
        ChromiumDiscoveryResult initial = CreateDiscoveryResult(
            [process],
            new ProcessGraph([process], []));
        ChromiumDiscoveryResult changed = CreateDiscoveryResult(
            [process, added],
            new ProcessGraph([process, added], []));
        int detailQueries = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(initial),
            DiscoverLightProcesses = (_, _) => ValueTask.FromResult(changed),
            DiscoverDetails = (_, _) =>
            {
                detailQueries++;
                return ValueTask.FromResult(CreateDetails(process));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectProcessAsync(
            Assert.Single(viewModel.ProcessRoots));

        viewModel.StartAutoRefresh();
        await WaitForAsync(() => viewModel.ProcessRoots.Count == 2);
        viewModel.StopAutoRefresh();

        Assert.Equal(1, detailQueries);
        Assert.Equal(process.Identity(), viewModel.ProcessInspector?.Identity);
        Assert.NotNull(viewModel.ProcessInspector);
    }

    [Fact]
    public async Task SelectionChangedWhileRefreshFinishesIsNotOverwritten()
    {
        ProcessSnapshotEntry browser = CreateProcess(123, "chrome.exe", true);
        ProcessSnapshotEntry renderer = CreateProcess(
            124,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        ChromiumDiscoveryResult initial = CreateDiscoveryResult(
            [browser],
            new ProcessGraph([browser], []),
            cdp: new CdpDiscoveryResult(
                SnapshotTime,
                [CreateValidatedTransport(browser.ProcessId)]));
        ChromiumDiscoveryResult changed = CreateDiscoveryResult(
            [browser, renderer],
            new ProcessGraph(
                [browser, renderer],
                [CreateEdge(
                    browser,
                    renderer,
                    ProcessRelationshipType.ChromiumSubprocess)]),
            cdp: new CdpDiscoveryResult(
                SnapshotTime,
                [CreateValidatedTransport(browser.ProcessId)]));
        int extractionCount = 0;
        TaskCompletionSource extractionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseExtraction =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(initial),
            DiscoverLightProcesses = (_, _) => ValueTask.FromResult(changed),
            DiscoverDetails = (processId, _) => ValueTask.FromResult(
                CreateDetails(
                    processId == browser.ProcessId ? browser : renderer)),
            DiscoverProcessInternals = async (_, _, _, cancellationToken) =>
            {
                extractionCount++;
                if (extractionCount > 1)
                {
                    extractionStarted.TrySetResult();
                    await releaseExtraction.Task.WaitAsync(cancellationToken);
                }

                return new CdpProcessInternalsResult(
                    SnapshotTime,
                    browser.ProcessId,
                    "chrome://process-internals/",
                    [],
                    []);
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectProcessAsync(
            Assert.Single(viewModel.ProcessRoots));

        viewModel.StartAutoRefresh();
        await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        ProcessTreeItemViewModel browserItem = Assert.Single(
            viewModel.FilteredProcessRoots);
        browserItem.IsExpanded = true;
        ProcessTreeItemViewModel rendererItem = Assert.Single(
            FlattenTree(viewModel.ProcessRoots),
            item => item.Identity == renderer.Identity());
        await viewModel.SelectProcessAsync(rendererItem);
        releaseExtraction.SetResult();
        await WaitForAsync(() => !viewModel.IsExtractingProcessInternals);
        viewModel.StopAutoRefresh();

        Assert.Equal(renderer.Identity(), viewModel.SelectedProcess?.Identity);
        Assert.Equal(renderer.Identity(), viewModel.ProcessInspector?.Identity);
        Assert.True(Assert.Single(viewModel.FilteredProcessRoots).IsExpanded);
    }

    [Fact]
    public async Task RefreshSelectedProcessDetailsReloadsCachedInspector()
    {
        ProcessSnapshotEntry process = CreateProcess(123, "sample.exe", true);
        int detailQueries = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
            DiscoverDetails = (_, _) =>
            {
                detailQueries++;
                return ValueTask.FromResult(CreateDetails(process));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectProcessAsync(
            Assert.Single(viewModel.ProcessRoots));

        Assert.True(viewModel.CanRefreshProcessDetails);
        await viewModel.RefreshSelectedProcessDetailsAsync();

        Assert.Equal(2, detailQueries);
        Assert.Equal(process.Identity(), viewModel.ProcessInspector?.Identity);
        Assert.Equal("Loaded details for PID 123.", viewModel.Status);
    }

    [Fact]
    public async Task AutoRefreshUsesLightProcessDiscovery()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        ChromiumDiscoveryResult result = CreateDiscoveryResult(
            [process],
            new ProcessGraph([process], []));
        int fullRefreshes = 0;
        int lightRefreshes = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ =>
            {
                fullRefreshes++;
                return ValueTask.FromResult(result);
            },
            DiscoverLightProcesses = (previous, _) =>
            {
                lightRefreshes++;
                return ValueTask.FromResult(previous);
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();

        viewModel.StartAutoRefresh();
        await Task.Delay(75);
        viewModel.StopAutoRefresh();
        await viewModel.RefreshProcessesAsync();

        Assert.Equal(2, fullRefreshes);
        Assert.True(lightRefreshes > 0);
    }

    [Fact]
    public async Task FullRefreshQueuesBehindActiveLightRefresh()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        ChromiumDiscoveryResult result = CreateDiscoveryResult(
            [process],
            new ProcessGraph([process], []));
        int fullRefreshes = 0;
        TaskCompletionSource lightStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseLight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ =>
            {
                fullRefreshes++;
                return ValueTask.FromResult(result);
            },
            DiscoverLightProcesses = async (previous, cancellationToken) =>
            {
                lightStarted.TrySetResult();
                await releaseLight.Task.WaitAsync(cancellationToken);
                return previous;
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();

        viewModel.StartAutoRefresh();
        await lightStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task fullRefresh = viewModel.RefreshProcessesAsync();
        Task duplicateRefresh = viewModel.RefreshProcessesAsync();
        await Task.Delay(50);

        Assert.False(fullRefresh.IsCompleted);
        Assert.True(duplicateRefresh.IsCompleted);
        Assert.Contains(
            "queued",
            viewModel.Status,
            StringComparison.OrdinalIgnoreCase);

        releaseLight.SetResult();
        await fullRefresh;
        viewModel.StopAutoRefresh();

        Assert.Equal(2, fullRefreshes);
    }

    [Fact]
    public async Task ExitedProcessesRetainFormerSiblingOrder()
    {
        ProcessSnapshotEntry newFirst = CreateProcess(50, "new.exe", true);
        ProcessSnapshotEntry first = CreateProcess(100, "first.exe", true);
        ProcessSnapshotEntry exited = CreateProcess(200, "exited.exe", true);
        ProcessSnapshotEntry last = CreateProcess(300, "last.exe", true);
        Queue<ChromiumDiscoveryResult> results = new(
        [
            CreateDiscoveryResult(
                [first, exited, last],
                new ProcessGraph([first, exited, last], [])),
            CreateDiscoveryResult(
                [newFirst, first, last],
                new ProcessGraph([newFirst, first, last], [])),
        ]);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(results.Dequeue()),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        await viewModel.RefreshProcessesAsync();

        Assert.Equal(
            ["new.exe", "first.exe", "exited.exe", "last.exe"],
            viewModel.ProcessRoots.Select(item => item.ImageName));
        Assert.True(viewModel.ProcessRoots[2].IsStale);
    }

    [Fact]
    public async Task ExitedChildProcessesRetainFormerSiblingOrder()
    {
        ProcessSnapshotEntry browser = CreateProcess(1000, "browser.exe", true);
        ProcessSnapshotEntry newFirst = CreateProcess(1050, "new.exe", true);
        ProcessSnapshotEntry first = CreateProcess(1100, "first.exe", true);
        ProcessSnapshotEntry exited = CreateProcess(1200, "exited.exe", true);
        ProcessSnapshotEntry last = CreateProcess(1300, "last.exe", true);
        Queue<ChromiumDiscoveryResult> results = new(
        [
            CreateDiscoveryResult(
                [browser, first, exited, last],
                new ProcessGraph(
                    [browser, first, exited, last],
                    [
                        CreateEdge(
                            browser,
                            first,
                            ProcessRelationshipType.ChromiumSubprocess),
                        CreateEdge(
                            browser,
                            exited,
                            ProcessRelationshipType.ChromiumSubprocess),
                        CreateEdge(
                            browser,
                            last,
                            ProcessRelationshipType.ChromiumSubprocess),
                    ])),
            CreateDiscoveryResult(
                [browser, newFirst, first, last],
                new ProcessGraph(
                    [browser, newFirst, first, last],
                    [
                        CreateEdge(
                            browser,
                            newFirst,
                            ProcessRelationshipType.ChromiumSubprocess),
                        CreateEdge(
                            browser,
                            first,
                            ProcessRelationshipType.ChromiumSubprocess),
                        CreateEdge(
                            browser,
                            last,
                            ProcessRelationshipType.ChromiumSubprocess),
                    ])),
        ]);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(results.Dequeue()),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        await viewModel.RefreshProcessesAsync();

        ProcessTreeItemViewModel browserItem =
            Assert.Single(viewModel.ProcessRoots);
        Assert.Equal(
            ["new.exe", "first.exe", "exited.exe", "last.exe"],
            browserItem.Children.Select(item => item.ImageName));
        Assert.True(browserItem.Children[2].IsStale);
    }

    [Fact]
    public async Task RefreshPreservesExpandedParentAndSelectedStaleChild()
    {
        ProcessSnapshotEntry parent = CreateProcess(
            123,
            "sample.exe",
            true);
        ProcessSnapshotEntry child = CreateProcess(
            456,
            "sample.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        ChromiumDiscoveryResult initial = CreateDiscoveryResult(
            [parent, child],
            new ProcessGraph(
                [parent, child],
                [CreateEdge(
                    parent,
                    child,
                    ProcessRelationshipType.ChromiumSubprocess)]));
        int refreshCount = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                refreshCount++ == 0
                    ? initial
                    : CreateDiscoveryResult([], new ProcessGraph([], []))),
            DiscoverDetails = (_, _) =>
                ValueTask.FromResult(CreateDetails(child)),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel visibleParent =
            Assert.Single(viewModel.FilteredProcessRoots);
        visibleParent.IsExpanded = true;
        ProcessTreeItemViewModel visibleChild =
            Assert.Single(visibleParent.Children);
        await viewModel.SelectProcessAsync(visibleChild);

        await viewModel.RefreshProcessesAsync();

        ProcessTreeItemViewModel retainedParent =
            Assert.Single(viewModel.FilteredProcessRoots);
        ProcessTreeItemViewModel retainedChild =
            Assert.Single(retainedParent.Children);
        Assert.True(retainedParent.IsExpanded);
        Assert.True(retainedParent.IsStale);
        Assert.True(retainedChild.IsStale);
        Assert.Equal(child.Identity(), viewModel.SelectedProcess?.Identity);
        Assert.True(retainedChild.IsSelected);
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
    public async Task AutoRefreshAppliesChangedLightProcessResult()
    {
        ChromiumDiscoveryResult initial = CreateDiscoveryResult(
            [],
            new ProcessGraph([], []));
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        ChromiumDiscoveryResult changed = CreateDiscoveryResult(
            [process],
            new ProcessGraph([process], []));
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(initial),
            DiscoverLightProcesses = (_, _) => ValueTask.FromResult(changed),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();

        viewModel.StartAutoRefresh();
        await WaitForAsync(
            () => FlattenTree(viewModel.ProcessRoots)
                .Any(item => item.Identity == process.Identity()));
        viewModel.StopAutoRefresh();

        Assert.Contains(
            FlattenTree(viewModel.ProcessRoots),
            item => item.Identity == process.Identity());
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

        viewModel.ProcessFilter = "role:renderer";

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
        await WaitForAsync(() =>
            viewModel.FilteredInstallations.Count == 1);

        InstallationItemViewModel installation =
            Assert.Single(viewModel.FilteredInstallations);
        Assert.Equal("Razer Central", installation.Name);
    }

    [Fact]
    public async Task CommandLineSuggestionsIncludeMatchingPsReadLineHistory()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
            DiscoverInstallations = _ => ValueTask.FromResult(
                new InstallationDiscoveryResult(
                    SnapshotTime,
                    [
                        CreateInstallation(
                            "Google Chrome",
                            "Chrome",
                            @"C:\Program Files\Google\Chrome\Application",
                            executableName: "chrome.exe"),
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
        StubCommandLineHistoryProvider history = new(
            [
                "chrome --history-only=one",
                "chrome.exe --history-only=two",
                "& 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' --history-only=three",
                "notepad.exe --history-only=ignored",
            ]);
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            commandLineHistoryProvider: history);

        await viewModel.RefreshProcessesAsync();
        await viewModel.RefreshInstallationsAsync();
        viewModel.CommandLineSuggestionFilter = "--history-only";
        await Task.Delay(250);

        Assert.Contains(
            viewModel.FilteredCommandLineSuggestions,
            suggestion => suggestion.Argument == "--history-only=one"
                && suggestion.Origin == "PSReadLine history");
        Assert.Contains(
            viewModel.FilteredCommandLineSuggestions,
            suggestion => suggestion.Argument == "--history-only=two");
        Assert.Contains(
            viewModel.FilteredCommandLineSuggestions,
            suggestion => suggestion.Argument == "--history-only=three");
        Assert.DoesNotContain(
            viewModel.FilteredCommandLineSuggestions,
            suggestion => suggestion.Argument == "--history-only=ignored");
    }

    [Fact]
    public async Task InstallationFilterSupportsPropertyPrefixes()
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
                            @"C:\Program Files\Edge",
                            version: "140.0.1",
                            channel: "Internal"),
                        CreateInstallation(
                            "Google Chrome",
                            "Chrome",
                            @"C:\Program Files\Chrome",
                            version: "139.0.1",
                            channel: "Stable"),
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

        viewModel.InstallationFilter = "version:140 channel:intern";
        await WaitForAsync(() =>
            viewModel.FilteredInstallations.Count == 1);

        Assert.Equal(
            "Microsoft Edge",
            Assert.Single(viewModel.FilteredInstallations).Name);
    }

    [Fact]
    public void PropertyFilterSupportsQuotedAndUnqualifiedTerms()
    {
        Assert.True(PropertyFilter.Matches(
            "name:\"Google Chrome\" version:140 stable",
            ("name", "Google Chrome"),
            ("version", "140.0.1"),
            ("channel", "Stable")));
        Assert.False(PropertyFilter.Matches(
            "channel:beta",
            ("name", "Google Chrome"),
            ("channel", "Stable")));
    }

    [Fact]
    public void DetailOpenTargetsDetectPathsAndRegistryKeys()
    {
        Assert.Equal(
            DetailOpenTargetKind.FileSystem,
            DetailOpenTarget.Detect(@"C:\Apps\Sample\app.exe")?.Kind);
        Assert.Equal(
            DetailOpenTargetKind.Registry,
            DetailOpenTarget.Detect(
                @"HKLM\Software\Microsoft\Edge")?.Kind);
        Assert.Null(DetailOpenTarget.Detect("Stable"));
    }

    [Fact]
    public void DetailOpenTargetUsesConfiguredExternalToolService()
    {
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            new StubGuiDiscoveryService(),
            new StubIconProvider(),
            externalTools: externalTools);

        viewModel.OpenDetailTarget(
            new DetailOpenTarget(
                DetailOpenTargetKind.FileSystem,
                @"C:\Apps\Sample\app.exe"),
            installationContext: true);
        viewModel.OpenDetailTarget(
            new DetailOpenTarget(
                DetailOpenTargetKind.Registry,
                @"HKLM\Software\Microsoft\Edge"),
            installationContext: true);

        Assert.Equal(
            @"C:\Apps\Sample\app.exe",
            externalTools.OpenedFileSystemPath);
        Assert.Equal(
            @"HKLM\Software\Microsoft\Edge",
            externalTools.OpenedRegistryPath);
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
        await WaitForAsync(() =>
            viewModel.FilteredInstallations.Count == 1);

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
    public async Task SettingsPersistAndExtendInstallationSearchRoots()
    {
        RecordingSettingsStore settingsStore = new();
        StubGuiDiscoveryService discovery = new();
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            settings: new GuiSettings
            {
                AutoRefreshProcesses = false,
                AutoExtractFrameInfo = false,
                DebugCommand = "debugger.exe {pid}",
            },
            settingsStore: settingsStore);

        Assert.False(viewModel.AutoRefreshProcesses);
        Assert.False(viewModel.AutoExtractFrameInfo);
        Assert.Equal("debugger.exe {pid}", viewModel.DebugCommand);

        viewModel.AutoRefreshProcesses = true;
        viewModel.AutoExtractFrameInfo = true;
        viewModel.AdditionalInstallationFoldersText =
            "C:\\Apps\r\nC:\\Tools\r\nC:\\Apps";
        await viewModel.RefreshInstallationsAsync();

        Assert.True(settingsStore.LastSaved?.AutoRefreshProcesses);
        Assert.True(settingsStore.LastSaved?.AutoExtractFrameInfo);
        Assert.Equal(
            [@"C:\Apps", @"C:\Tools"],
            discovery.LastAdditionalInstallationFolders);
    }

    [Fact]
    public void JsonSettingsStoreRoundTripsUserPreferences()
    {
        string settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"cpe-settings-{Guid.NewGuid():N}.json");
        try
        {
            JsonGuiSettingsStore store = new(settingsPath);
            GuiSettings expected = new()
            {
                AutoRefreshProcesses = false,
                AutoExtractFrameInfo = false,
                DebugCommand = @"C:\Debuggers\windbgx.exe -p {pid}",
                FutureDebuggerCommand = @"C:\Debuggers\windbgx.exe",
                ProcessExplorerCommand = @"C:\Tools\procexp.exe /s:{pid}",
                AdditionalInstallationFolders =
                    [@"C:\Apps", @"C:\Tools"],
                CommandLineTemplates =
                [
                    new CommandLineTemplateSettings
                    {
                        Name = "Not in menus",
                        IsFavorite = false,
                    },
                ],
            };

            store.Save(expected);
            GuiSettingsLoadResult loaded = store.Load();

            Assert.Null(loaded.Error);
            Assert.Equal(
                expected.AutoRefreshProcesses,
                loaded.Settings.AutoRefreshProcesses);
            Assert.Equal(
                expected.AutoExtractFrameInfo,
                loaded.Settings.AutoExtractFrameInfo);
            Assert.Equal(expected.DebugCommand, loaded.Settings.DebugCommand);
            Assert.Equal(
                expected.FutureDebuggerCommand,
                loaded.Settings.FutureDebuggerCommand);
            Assert.Equal(
                expected.ProcessExplorerCommand,
                loaded.Settings.ProcessExplorerCommand);
            Assert.Equal(
                expected.AdditionalInstallationFolders,
                loaded.Settings.AdditionalInstallationFolders);
            Assert.False(Assert.Single(
                loaded.Settings.CommandLineTemplates).IsFavorite);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task ContextActionsUseConfiguredCommandsAndPackageIdentity()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
            DiscoverDetails = (_, _) => ValueTask.FromResult(
                CreateDetails(
                    process,
                    "Sample.Package_1.0.0.0_x64__publisher")),
        };
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            settings: new GuiSettings
            {
                DebugCommand = "debugger.exe -p {pid}",
                FutureDebuggerCommand = "future-debugger.exe",
                ProcessExplorerCommand = "procexp.exe /s:{pid}",
            },
            externalTools: externalTools);
        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel item =
            Assert.Single(viewModel.FilteredProcessRoots);

        viewModel.DebugProcess(item);
        viewModel.OpenProcessExplorer(item);
        await viewModel.KillProcessTreeAsync(item);
        await viewModel.DebugFutureLaunchesAsync(item);

        Assert.Equal((123, "debugger.exe -p {pid}"), externalTools.Debug);
        Assert.Equal(
            (123, "procexp.exe /s:{pid}"),
            externalTools.ProcessExplorer);
        Assert.Equal(process.Identity(), externalTools.Terminated);
        Assert.Equal(
            (
                "sample.exe",
                "Sample.Package_1.0.0.0_x64__publisher",
                "future-debugger.exe"),
            externalTools.Future);
    }

    [Fact]
    public async Task FutureDebugDoesNotGuessWhenPackageIdentityIsUnknown()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "sample.exe",
            true);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
        };
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            externalTools: externalTools);
        await viewModel.RefreshProcessesAsync();

        await viewModel.DebugFutureLaunchesAsync(
            Assert.Single(viewModel.FilteredProcessRoots));

        Assert.Null(externalTools.Future);
        Assert.Contains(
            viewModel.ProcessNotices,
            notice => notice.Message.Contains(
                "Package identity could not be determined",
                StringComparison.Ordinal));
    }

    [Fact]
    public void InstallFutureDebugUsesExecutableAndPackageIdentity()
    {
        ChromiumInstallation installation = CreateInstallation(
            "Packaged App",
            "WebView2",
            @"C:\Apps\Packaged") with
        {
            Metadata = new InstallationMetadata(
                "x64",
                "Example",
                "Package",
                "test",
                "test",
                new InstallationPackageIdentity(
                    "Sample.Package_1.0.0.0_x64__publisher",
                    "Sample.Package_publisher",
                    "Sample.Package",
                    "1.0.0.0",
                    "x64",
                    "publisher"),
                null,
                null,
                false,
                "High"),
        };
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            new StubGuiDiscoveryService(),
            new StubIconProvider(),
            externalTools: externalTools);

        viewModel.DebugFutureLaunches(
            new InstallationItemViewModel(installation));

        Assert.Equal(
            (
                "Packaged App.exe",
                "Sample.Package_1.0.0.0_x64__publisher",
                "windbgx.exe"),
            externalTools.Future);
    }

    [Fact]
    public void CommandTemplateRemovesArgumentsAndMergesValuedSwitches()
    {
        CommandLineTemplateSettings template = new()
        {
            AddParts =
            [
                "--enable-features=B,C",
                "--new-switch",
            ],
            RemoveParts =
            [
                new CommandLineRemovalSettings("--obsolete", false),
                new CommandLineRemovalSettings("^--remove-me$", true),
            ],
        };

        IReadOnlyList<string> arguments =
            CommandLineTemplateTransformer.Apply(
                "\"C:\\Chrome\\chrome.exe\" --enable-features=A "
                    + "--obsolete=value --remove-me --enable-features=B",
                template);

        Assert.Equal(
            ["--enable-features=A,B,C", "--new-switch"],
            arguments);
    }

    [Fact]
    public void CommandTemplateReplacesDuplicateScalarSwitch()
    {
        CommandLineTemplateSettings template = new()
        {
            AddParts = ["--remote-debugging-port=9222"],
        };

        IReadOnlyList<string> arguments =
            CommandLineTemplateTransformer.Apply(
                "chrome.exe --remote-debugging-port=9333",
                template);

        Assert.Equal(["--remote-debugging-port=9222"], arguments);
    }

    [Fact]
    public void CommandTemplateAddsSwitchesBeforeArgumentTerminator()
    {
        CommandLineTemplateSettings template = new()
        {
            AddParts = ["--remote-debugging-port=9222"],
            RemoveParts =
            [
                new CommandLineRemovalSettings("^--after$", true),
            ],
        };

        IReadOnlyList<string> arguments =
            CommandLineTemplateTransformer.Apply(
                "chrome.exe --existing -- --after positional",
                template);

        Assert.Equal(
            [
                "--existing",
                "--remote-debugging-port=9222",
                "--",
                "--after",
                "positional",
            ],
            arguments);
    }

    [Fact]
    public void CommandTemplateExpandsLaunchVariables()
    {
        string variableName = $"CPE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, @"C:\Profiles\Test");
        try
        {
            CommandLineTemplateSettings template = new()
            {
                AddParts =
                [
                    $"--environment={{env:{variableName}}}",
                    "--random={random-file}",
                    "--random-again={random-file}",
                    "--target={target-specific-file}",
                    "--target-again={target-specific-file}",
                    "--name={executable}",
                    "--chromium-placeholder={pid}",
                ],
            };

            IReadOnlyList<string> arguments =
                CommandLineTemplateTransformer.Apply(
                    "msedge.exe",
                    template,
                    @"C:\Program Files\Edge\msedge.exe");

            Assert.Contains(
                @"--environment=C:\Profiles\Test",
                arguments);
            string random = Assert.Single(
                arguments,
                argument => argument.StartsWith(
                    "--random=",
                    StringComparison.Ordinal))[9..];
            Assert.Equal(
                $"--random-again={random}",
                Assert.Single(
                    arguments,
                    argument => argument.StartsWith(
                        "--random-again=",
                        StringComparison.Ordinal)));
            Assert.Equal(Path.GetFileName(random), random);
            string target = Assert.Single(
                arguments,
                argument => argument.StartsWith(
                    "--target=",
                    StringComparison.Ordinal))[9..];
            Assert.Matches("^msedge-[0-9a-f]{8}$", target);
            Assert.Contains($"--target-again={target}", arguments);
            Assert.Contains("--name=msedge", arguments);
            Assert.Contains("--chromium-placeholder={pid}", arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void CommandTemplateTargetSpecificFileIsStableForWindowsPathCasing()
    {
        CommandLineTemplateSettings template = new()
        {
            AddParts = ["--target={target-specific-file}"],
        };

        string first = Assert.Single(CommandLineTemplateTransformer.Apply(
            "msedge.exe",
            template,
            @"C:\Program Files\Edge\msedge.exe"));
        string second = Assert.Single(CommandLineTemplateTransformer.Apply(
            "msedge.exe",
            template,
            @"c:\program files\edge\MSedge.EXE"));
        string otherTarget = Assert.Single(
            CommandLineTemplateTransformer.Apply(
                "msedge.exe",
                template,
                @"C:\Other\Edge\msedge.exe"));

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherTarget);
    }

    [Fact]
    public void CommandTemplateRejectsUndefinedEnvironmentVariable()
    {
        string variableName = $"CPE_MISSING_{Guid.NewGuid():N}";
        CommandLineTemplateSettings template = new()
        {
            AddParts = [$"--path={{env:{variableName}}}"],
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(() =>
                CommandLineTemplateTransformer.Apply(
                    "chrome.exe",
                    template,
                    @"C:\Chrome\chrome.exe"));

        Assert.Contains(variableName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewSettingsIncludeRemoteDebuggingTemplate()
    {
        CommandLineTemplateSettings template =
            Assert.Single(new GuiSettings().CommandLineTemplates);

        Assert.Equal("Enable remote debugging", template.Name);
        Assert.True(template.IsFavorite);
        Assert.Equal(".*", template.ApplicableExecutableRegex);
        Assert.Contains(
            "--remote-debugging-port=0",
            template.AddParts);
        Assert.Contains(
            "--user-data-dir=%LOCALAPPDATA%\\ChromiumProcessExplorer\\RemoteDebugging\\{executable}",
            template.AddParts);
    }

    [Fact]
    public void SettingsUpgradeUnmodifiedRemoteDebuggingTemplate()
    {
        string settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"cpe-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                settingsPath,
                """
                {
                  "CommandLineTemplates": [
                    {
                      "Id": "remote-debugging",
                      "Name": "Legacy remote debugging",
                      "ApplicableExecutableRegex": ".*",
                      "IsFavorite": true,
                      "AddParts": [ "--remote-debugging-port=9222" ],
                      "RemoveParts": []
                    },
                    {
                      "Id": "remote-debugging",
                      "Name": "Current remote debugging",
                      "ApplicableExecutableRegex": ".*",
                      "IsFavorite": true,
                      "AddParts": [
                        "--remote-debugging-port=9222",
                        "--user-data-dir=%LOCALAPPDATA%\\ChromiumProcessExplorer\\RemoteDebugging\\{executable}"
                      ],
                      "RemoveParts": []
                    }
                  ]
                }
                """);

            GuiSettingsLoadResult loaded =
                new JsonGuiSettingsStore(settingsPath).Load();
            Assert.All(
                loaded.Settings.CommandLineTemplates,
                template =>
                {
                    Assert.Contains(
                        "--remote-debugging-port=0",
                        template.AddParts);
                    Assert.Contains(
                        "--user-data-dir=%LOCALAPPDATA%\\ChromiumProcessExplorer\\RemoteDebugging\\{executable}",
                        template.AddParts);
                });
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task DevToolsShowsNamedTcpEndpointsAndHidesPrivatePipes()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true);
        CdpTransportInfo tcp = new(
            123,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Validated,
            "9222",
            9222,
            "command-line",
            "http://127.0.0.1:9222/json/version",
            "ws://127.0.0.1:9222/devtools/browser/test",
            "Chrome/136",
            "1.3",
            null,
            []);
        CdpTransportInfo pipe = new(
            123,
            CdpTransportKind.Pipe,
            CdpTransportStatus.AlreadyOwned,
            null,
            null,
            "command-line",
            null,
            null,
            null,
            null,
            null,
            []);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []),
                    cdp: new CdpDiscoveryResult(
                        SnapshotTime,
                        [pipe, tcp]))),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();

        DevToolsItemViewModel item = Assert.Single(viewModel.DevTools);
        Assert.Equal("chrome.exe (123)", item.ProcessLabel);
        Assert.Equal(CdpTransportKind.Tcp, item.Transport.Kind);
    }

    [Fact]
    public async Task DevToolsSelectionLoadsTargetsAndOpensChosenFrontend()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true);
        CdpTransportInfo transport = new(
            123,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Validated,
            "9222",
            9222,
            "command-line",
            "http://127.0.0.1:9222/json/version",
            "ws://127.0.0.1:9222/devtools/browser/test",
            "Chrome/140.0",
            "1.3",
            null,
            []);
        string? openedTarget = null;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []),
                    cdp: new CdpDiscoveryResult(
                        SnapshotTime,
                        [transport]))),
            DiscoverCdpTargets = (_, _) => ValueTask.FromResult(
                new CdpTargetListResult(
                    SnapshotTime,
                    [
                        new CdpInspectableTarget(
                            "target-1",
                            "page",
                            "Example",
                            "https://example.test/",
                            "http://127.0.0.1:9222/devtools/inspector.html",
                            "ws://127.0.0.1:9222/devtools/page/target-1"),
                    ],
                    [])),
            OpenDevTools = (_, targetId, _) =>
            {
                openedTarget = targetId;
                return ValueTask.CompletedTask;
            },
        };
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            externalTools: externalTools);

        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectDevToolsAsync(Assert.Single(viewModel.DevTools));
        await viewModel.OpenSelectedDevToolsAsync();
        viewModel.OpenSelectedRemoteDevTools();

        Assert.Equal("target-1", openedTarget);
        Assert.Equal(
            "http://127.0.0.1:9222/devtools/inspector.html",
            externalTools.OpenedUri);
    }

    [Fact]
    public async Task FullRefreshAutomaticallyDisplaysMappedFrames()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "msedge.exe",
            true);
        ProcessSnapshotEntry renderer = CreateProcess(
            456,
            "msedge.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        CdpTransportInfo transport = new(
            123,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Validated,
            "9222",
            9222,
            "command-line",
            "http://127.0.0.1:9222/json/version",
            "ws://127.0.0.1:9222/devtools/browser/test",
            "Edg/140.0",
            "1.3",
            null,
            []);
        int extractionCount = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process, renderer],
                    new ProcessGraph(
                        [process, renderer],
                        [CreateEdge(
                            process,
                            renderer,
                            ProcessRelationshipType.ChromiumSubprocess)]),
                    cdp: new CdpDiscoveryResult(
                        SnapshotTime,
                        [transport]))),
            DiscoverProcessInternals = (_, imageName, _, _) =>
            {
                extractionCount++;
                Assert.Equal("msedge.exe", imageName);
                return ValueTask.FromResult(
                    new CdpProcessInternalsResult(
                        SnapshotTime,
                        123,
                        "edge://process-internals/",
                        [
                            new CdpProcessInternalsFrame(
                                "Example",
                                0,
                                7,
                                12,
                                3,
                                renderer.Identity(),
                                "Active",
                                "https://example.test/",
                                4,
                                5,
                                6,
                                "https://example.test/",
                                null),
                            new CdpProcessInternalsFrame(
                                "Example subframe",
                                1,
                                7,
                                13,
                                3,
                                renderer.Identity(),
                                "Active",
                                "https://example.test/path",
                                4,
                                5,
                                6,
                                "https://example.test/path",
                                null),
                            new CdpProcessInternalsFrame(
                                "Accounts",
                                1,
                                7,
                                14,
                                4,
                                renderer.Identity(),
                                "Active",
                                "https://accounts.example.test/sign-in",
                                4,
                                5,
                                6,
                                "https://accounts.example.test/",
                                null),
                            new CdpProcessInternalsFrame(
                                "Reused PID",
                                0,
                                8,
                                15,
                                5,
                                new ProcessIdentity(
                                    renderer.ProcessId,
                                    renderer.CreationTime?.AddSeconds(-1)),
                                "Active",
                                "https://wrong-generation.test/",
                                4,
                                5,
                                6,
                                "https://wrong-generation.test/",
                                null),
                        ],
                        []));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel rendererItem = Assert.Single(
            FlattenTree(viewModel.ProcessRoots),
            item => item.Identity == renderer.Identity());
        await viewModel.SelectDevToolsAsync(Assert.Single(viewModel.DevTools));
        await viewModel.SelectProcessAsync(rendererItem);

        Assert.Equal(
            ["https://accounts.example.test", "https://example.test"],
            rendererItem.Origins);
        Assert.DoesNotContain(
            "wrong-generation",
            rendererItem.OriginSummary,
            StringComparison.Ordinal);
        Assert.Equal(3, rendererItem.Frames.Count);
        Assert.Equal(3, rendererItem.TreeChildren.Count);
        Assert.Equal(
            "Tab",
            Assert.IsType<ProcessFrameTreeItemViewModel>(
                rendererItem.TreeChildren[0]).Kind);
        Assert.Equal(
            "Frame",
            Assert.IsType<ProcessFrameTreeItemViewModel>(
                rendererItem.TreeChildren[1]).Kind);
        Assert.Equal(2, viewModel.ProcessInspector?.Origins.Count);
        Assert.Contains(
            viewModel.ProcessInspector!.Origins,
            row => row.Label == "Main-frame origin"
                && row.Value == "https://example.test");
        Assert.Contains(
            viewModel.ProcessInspector.Origins,
            row => row.Label == "Subframe origin"
                && row.Value == "https://accounts.example.test");
        Assert.Contains(
            "Extracted 4 frames",
            viewModel.DevToolsActionStatus,
            StringComparison.Ordinal);
        Assert.Equal(1, extractionCount);

        viewModel.ProcessFilter = "origin:accounts.example.test";
        Assert.Single(
            FlattenTree(viewModel.FilteredProcessRoots),
            item => item.Identity == renderer.Identity());
        viewModel.ProcessFilter = string.Empty;

        await viewModel.RefreshProcessesAsync();
        await viewModel.SelectDevToolsAsync(Assert.Single(viewModel.DevTools));

        Assert.Equal(2, extractionCount);
        Assert.Equal(
            2,
            Assert.Single(
                FlattenTree(viewModel.ProcessRoots),
                item => item.Identity == renderer.Identity()).Origins.Count);
        Assert.Contains(
            "Extracted 4 frames",
            viewModel.DevToolsActionStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LightRefreshExtractsOnlyCreatedOrTerminatedProcessGroups()
    {
        ProcessSnapshotEntry firstBrowser = CreateProcess(
            123,
            "chrome.exe",
            true);
        ProcessSnapshotEntry firstRenderer = CreateProcess(
            124,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        ProcessSnapshotEntry secondBrowser = CreateProcess(
            200,
            "msedge.exe",
            true);
        CdpTransportInfo firstTransport = new(
            123,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Validated,
            "9222",
            9222,
            "command-line",
            "http://127.0.0.1:9222/json/version",
            "ws://127.0.0.1:9222/devtools/browser/test",
            "Chrome/140.0",
            "1.3",
            null,
            []);
        CdpTransportInfo secondTransport = firstTransport with
        {
            ProcessId = 200,
            ConfiguredValue = "9333",
            Port = 9333,
            VersionEndpoint = "http://127.0.0.1:9333/json/version",
            WebSocketDebuggerUrl =
                "ws://127.0.0.1:9333/devtools/browser/test",
            Browser = "Edg/140.0",
        };
        ChromiumDiscoveryResult initial = CreateDiscoveryResult(
            [firstBrowser, secondBrowser],
            new ProcessGraph([firstBrowser, secondBrowser], []),
            cdp: new CdpDiscoveryResult(
                SnapshotTime,
                [firstTransport, secondTransport]));
        ChromiumDiscoveryResult changed = CreateDiscoveryResult(
            [firstBrowser, firstRenderer, secondBrowser],
            new ProcessGraph(
                [firstBrowser, firstRenderer, secondBrowser],
                [CreateEdge(
                    firstBrowser,
                    firstRenderer,
                    ProcessRelationshipType.ChromiumSubprocess)]),
            cdp: new CdpDiscoveryResult(
                SnapshotTime,
                [firstTransport, secondTransport]));
        int lightRefreshCount = 0;
        List<int> extractedProcessIds = [];
        TaskCompletionSource lightExtraction =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(initial),
            DiscoverLightProcesses = (_, _) => ValueTask.FromResult(
                Interlocked.Increment(ref lightRefreshCount) == 1
                    ? changed
                    : initial),
            DiscoverProcessInternals = (transport, _, _, _) =>
            {
                extractedProcessIds.Add(transport.ProcessId);
                if (extractedProcessIds.Count == 4)
                {
                    lightExtraction.TrySetResult();
                }

                return ValueTask.FromResult(
                    new CdpProcessInternalsResult(
                        SnapshotTime,
                        transport.ProcessId,
                        "chrome://process-internals/",
                        [],
                        []));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            TimeSpan.FromMilliseconds(10));
        await viewModel.RefreshProcessesAsync();

        viewModel.StartAutoRefresh();
        await lightExtraction.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.StopAutoRefresh();

        Assert.Equal([123, 200, 123, 123], extractedProcessIds);
    }

    [Fact]
    public async Task DisablingAutomaticFrameExtractionClearsCachedData()
    {
        ProcessSnapshotEntry browser = CreateProcess(123, "chrome.exe", true);
        ProcessSnapshotEntry renderer = CreateProcess(
            124,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        CdpTransportInfo transport = CreateValidatedTransport(browser.ProcessId);
        RecordingSettingsStore settingsStore = new();
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [browser, renderer],
                    new ProcessGraph(
                        [browser, renderer],
                        [CreateEdge(
                            browser,
                            renderer,
                            ProcessRelationshipType.ChromiumSubprocess)]),
                    cdp: new CdpDiscoveryResult(
                        SnapshotTime,
                        [transport]))),
            DiscoverProcessInternals = (_, _, _, _) => ValueTask.FromResult(
                CreateProcessInternalsResult(
                    browser.ProcessId,
                    renderer,
                    "https://example.test/")),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            settingsStore: settingsStore);

        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel rendererItem = Assert.Single(
            FlattenTree(viewModel.ProcessRoots),
            item => item.Identity == renderer.Identity());
        Assert.Single(rendererItem.Frames);

        viewModel.AutoExtractFrameInfo = false;

        Assert.Empty(rendererItem.Frames);
        Assert.Empty(rendererItem.Origins);
        Assert.False(settingsStore.LastSaved?.AutoExtractFrameInfo);
    }

    [Fact]
    public async Task DisablingAutomaticFrameExtractionDiscardsInFlightResult()
    {
        ProcessSnapshotEntry browser = CreateProcess(123, "chrome.exe", true);
        ProcessSnapshotEntry renderer = CreateProcess(
            124,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        CdpTransportInfo transport = CreateValidatedTransport(browser.ProcessId);
        TaskCompletionSource extractionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CdpProcessInternalsResult> extraction =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [browser, renderer],
                    new ProcessGraph(
                        [browser, renderer],
                        [CreateEdge(
                            browser,
                            renderer,
                            ProcessRelationshipType.ChromiumSubprocess)]),
                    cdp: new CdpDiscoveryResult(
                        SnapshotTime,
                        [transport]))),
            DiscoverProcessInternals = (_, _, _, _) =>
            {
                extractionStarted.SetResult();
                return new ValueTask<CdpProcessInternalsResult>(extraction.Task);
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        Task refresh = viewModel.RefreshProcessesAsync();
        await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.AutoExtractFrameInfo = false;
        extraction.SetResult(CreateProcessInternalsResult(
            browser.ProcessId,
            renderer,
            "https://example.test/"));
        await refresh;

        ProcessTreeItemViewModel rendererItem = Assert.Single(
            FlattenTree(viewModel.ProcessRoots),
            item => item.Identity == renderer.Identity());
        Assert.Empty(rendererItem.Frames);
        Assert.Empty(rendererItem.Origins);
    }

    [Fact]
    public async Task AutomaticFrameExtractionFailureRetainsLastSuccessfulData()
    {
        ProcessSnapshotEntry browser = CreateProcess(123, "chrome.exe", true);
        ProcessSnapshotEntry renderer = CreateProcess(
            124,
            "chrome.exe",
            true) with
        {
            ChromiumProcessType = "renderer",
        };
        CdpTransportInfo transport = CreateValidatedTransport(browser.ProcessId);
        int extractionCount = 0;
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [browser, renderer],
                    new ProcessGraph(
                        [browser, renderer],
                        [CreateEdge(
                            browser,
                            renderer,
                            ProcessRelationshipType.ChromiumSubprocess)]),
                    cdp: new CdpDiscoveryResult(
                        SnapshotTime,
                        [transport]))),
            DiscoverProcessInternals = (_, _, _, _) =>
            {
                extractionCount++;
                if (extractionCount == 2)
                {
                    return ValueTask.FromResult(
                        new CdpProcessInternalsResult(
                            SnapshotTime,
                            browser.ProcessId,
                            "chrome://process-internals/",
                            [],
                            [
                                new DiscoveryIssue(
                                    "cdp-process-internals",
                                    "Synthetic extraction failure.",
                                    browser.ProcessId),
                            ]));
                }

                return ValueTask.FromResult(
                    CreateProcessInternalsResult(
                        browser.ProcessId,
                        renderer,
                        "https://example.test/"));
            },
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        await viewModel.RefreshProcessesAsync();

        ProcessTreeItemViewModel rendererItem = Assert.Single(
            FlattenTree(viewModel.ProcessRoots),
            item => item.Identity == renderer.Identity());
        Assert.Single(rendererItem.Frames);
        Assert.Contains(
            viewModel.DevToolsNotices,
            issue => issue.Message.Contains(
                "Synthetic extraction failure",
                StringComparison.Ordinal));
    }

    [Fact]
    public void UnfavoriteTemplateIsExcludedOnlyFromContextMenus()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true) with
        {
            ExecutablePath = @"C:\Chrome\chrome.exe",
            CommandLine = "chrome.exe",
        };
        using MainViewModel viewModel = new(
            new StubGuiDiscoveryService(),
            new StubIconProvider(),
            settings: new GuiSettings
            {
                CommandLineTemplates =
                [
                    new CommandLineTemplateSettings
                    {
                        Name = "Hidden from menu",
                        IsFavorite = false,
                    },
                ],
            });
        ProcessTreeItemViewModel item = CreateTreeItem(
            process,
            "Chrome",
            "Browser",
            isHost: false);

        Assert.Single(viewModel.GetApplicableTemplates(item));
        Assert.Empty(viewModel.GetFavoriteApplicableTemplates(item));
    }

    [Fact]
    public async Task CurrentLineSuggestionsPutCompleteArgumentBeforeSwitch()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true) with
        {
            CommandLine =
                "chrome.exe --remote-debugging-port=9222",
        };
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        viewModel.SetCommandLineSuggestionContext(
            "--remote-debugging-port=9222",
            isVisible: true);
        await WaitForAsync(() =>
            viewModel.FilteredCommandLineSuggestions.FirstOrDefault()?.Argument
                == "--remote-debugging-port=9222");

        Assert.Equal(
            "--remote-debugging-port=9222",
            viewModel.FilteredCommandLineSuggestions[0].Argument);
        int switchIndex = viewModel.FilteredCommandLineSuggestions
            .Select((item, index) => (item.Argument, index))
            .Single(item => item.Argument == "--remote-debugging-port")
            .index;
        Assert.True(switchIndex > 0);
        Assert.Equal(
            viewModel.FilteredCommandLineSuggestions.Count,
            viewModel.FilteredCommandLineSuggestions
                .Select(item => item.Argument)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public async Task RunSectionLaunchesExplicitUiSelection()
    {
        ChromiumInstallation chrome = CreateInstallation(
            "Google Chrome",
            "Chrome",
            @"C:\Chrome",
            kind: "Browser");
        ChromiumInstallation edge = CreateInstallation(
            "Microsoft Edge",
            "Edge",
            @"C:\Edge",
            kind: "Browser");
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverInstallations = _ => ValueTask.FromResult(
                new InstallationDiscoveryResult(
                    SnapshotTime,
                    [chrome, edge],
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
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            externalTools: externalTools);

        await viewModel.RefreshInstallationsAsync();
        CommandLineRunTargetViewModel chromeTarget =
            viewModel.FilteredCommandLineRunTargets.Single(target =>
                target.ExecutablePath == chrome.ExecutablePath);
        viewModel.SelectedCommandLineRunTarget =
            viewModel.FilteredCommandLineRunTargets.Single(target =>
                target.ExecutablePath == edge.ExecutablePath);
        viewModel.RunCommandLineTarget(chromeTarget);

        Assert.Equal(chrome.ExecutablePath, externalTools.Launch?.Executable);
        Assert.Same(chromeTarget, viewModel.SelectedCommandLineRunTarget);
        Assert.Contains(
            "--remote-debugging-port=0",
            externalTools.Launch?.Arguments ?? []);
    }

    [Fact]
    public async Task RunSectionPreservesProcessIdentityForSharedExecutable()
    {
        ProcessSnapshotEntry first = CreateProcess(
            101,
            "chrome.exe",
            true) with
        {
            ExecutablePath = @"C:\Chrome\chrome.exe",
            CommandLine = "chrome.exe --profile-directory=First",
        };
        ProcessSnapshotEntry second = CreateProcess(
            102,
            "chrome.exe",
            true) with
        {
            ExecutablePath = first.ExecutablePath,
            CommandLine = "chrome.exe --profile-directory=Second",
        };
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [first, second],
                    new ProcessGraph([first, second], []))),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());
        await viewModel.RefreshProcessesAsync();
        CommandLineRunTargetViewModel selected =
            viewModel.FilteredCommandLineRunTargets.Single(target =>
                target.Process?.ProcessId == second.ProcessId);
        viewModel.SelectedCommandLineRunTarget = selected;

        viewModel.CommandLineRunFilter = "chrome";

        Assert.Equal(
            second.Identity(),
            viewModel.SelectedCommandLineRunTarget?.Process?.Identity);
    }

    [Fact]
    public void ChromiumCommandLineCatalogContainsSwitchesAndFeatures()
    {
        Assert.True(ChromiumCommandLineCatalog.Entries.Count >= 4000);
        Assert.Contains(
            ChromiumCommandLineCatalog.Entries,
            entry => entry.Argument == "--remote-debugging-port"
                && entry.Kind == "Switch");
        Assert.Contains(
            ChromiumCommandLineCatalog.Entries,
            entry => entry.Argument == "--enable-features=Vulkan"
                && entry.Kind == "Feature");
    }

    [Fact]
    public async Task CommandLineSuggestionSearchAddsSelectedArgumentOnce()
    {
        using MainViewModel viewModel = new(
            new StubGuiDiscoveryService(),
            new StubIconProvider());

        viewModel.CommandLineSuggestionFilter = "VulkanFromANGLE";
        await WaitForAsync(() =>
            viewModel.FilteredCommandLineSuggestions.Count == 1
            && viewModel.FilteredCommandLineSuggestions[0].Argument
                == "--enable-features=VulkanFromANGLE");

        CommandLineSuggestionViewModel suggestion = Assert.Single(
            viewModel.FilteredCommandLineSuggestions);
        Assert.Equal(
            "--enable-features=VulkanFromANGLE",
            suggestion.Argument);
        viewModel.SelectedCommandLineSuggestion = suggestion;
        viewModel.AddSelectedCommandLineSuggestion();
        viewModel.AddSelectedCommandLineSuggestion();

        Assert.Equal(
            1,
            viewModel.SelectedCommandLineTemplate!.AddPartsText.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries).Count(
                argument => argument
                    == "--enable-features=VulkanFromANGLE"));
    }

    [Fact]
    public async Task CommandLineSuggestionsIncludeRunningProcessArguments()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true) with
        {
            CommandLine = "chrome.exe --custom-running-switch=value",
        };
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        await viewModel.RefreshProcessesAsync();
        viewModel.CommandLineSuggestionFilter = "custom-running-switch";
        await WaitForAsync(() =>
            viewModel.FilteredCommandLineSuggestions.Count == 1
            && viewModel.FilteredCommandLineSuggestions[0].Argument
                == "--custom-running-switch=value");

        CommandLineSuggestionViewModel suggestion = Assert.Single(
            viewModel.FilteredCommandLineSuggestions);
        Assert.Equal(
            "--custom-running-switch=value",
            suggestion.Argument);
        Assert.Equal("Running processes", suggestion.Origin);
        Assert.Contains("chrome.exe", suggestion.Description);
    }

    [Fact]
    public async Task ProcessRefreshCancelsPendingSuggestionSearch()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true) with
        {
            CommandLine = "chrome.exe --custom-running-switch=value",
        };
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
        };
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider());

        viewModel.CommandLineSuggestionFilter = "custom-running-switch";
        await viewModel.RefreshProcessesAsync();
        await Task.Delay(300);

        CommandLineSuggestionViewModel suggestion = Assert.Single(
            viewModel.FilteredCommandLineSuggestions);
        Assert.Equal(
            "--custom-running-switch=value",
            suggestion.Argument);
    }

    [Fact]
    public async Task BrowserTemplateLaunchUsesOriginalArguments()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "chrome.exe",
            true) with
        {
            ExecutablePath = @"C:\Chrome\chrome.exe",
            CommandLine = "\"C:\\Chrome\\chrome.exe\" --profile-directory=Test",
        };
        StubGuiDiscoveryService discovery = new()
        {
            DiscoverProcesses = _ => ValueTask.FromResult(
                CreateDiscoveryResult(
                    [process],
                    new ProcessGraph([process], []))),
        };
        RecordingExternalToolService externalTools = new();
        using MainViewModel viewModel = new(
            discovery,
            new StubIconProvider(),
            externalTools: externalTools);
        await viewModel.RefreshProcessesAsync();
        ProcessTreeItemViewModel item =
            Assert.Single(viewModel.FilteredProcessRoots);
        CommandLineTemplateViewModel template =
            Assert.Single(viewModel.GetApplicableTemplates(item));

        viewModel.LaunchWithTemplate(item, template);

        Assert.Equal(@"C:\Chrome\chrome.exe", externalTools.Launch?.Executable);
        Assert.Equal(
            [
                "--profile-directory=Test",
                "--remote-debugging-port=0",
                "--user-data-dir=%LOCALAPPDATA%\\ChromiumProcessExplorer\\RemoteDebugging\\chrome",
            ],
            externalTools.Launch?.Arguments);
    }

    [Fact]
    public void TemplatesExcludeHostsSubprocessesAndWebView2()
    {
        ProcessSnapshotEntry process = CreateProcess(
            123,
            "msedgewebview2.exe",
            true) with
        {
            ExecutablePath = @"C:\WebView2\msedgewebview2.exe",
        };
        using MainViewModel viewModel = new(
            new StubGuiDiscoveryService(),
            new StubIconProvider());

        Assert.Empty(viewModel.GetApplicableTemplates(
            CreateTreeItem(process, "WebView2", "Browser", isHost: false)));
        Assert.Empty(viewModel.GetApplicableTemplates(
            CreateTreeItem(process, "Chrome", "Renderer", isHost: false)));
        Assert.Empty(viewModel.GetApplicableTemplates(
            CreateTreeItem(process, "Electron", "Browser", isHost: true)));
        Assert.Empty(viewModel.GetApplicableTemplates(
            CreateTreeItem(
                process with { CommandLine = null },
                "Chrome",
                "Browser",
                isHost: false)));
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "The expected asynchronous update did not complete.");
    }

    private static ChromiumDiscoveryResult CreateDiscoveryResult(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        ProcessGraph graph,
        IReadOnlyList<DiscoveryIssue>? issues = null,
        CdpDiscoveryResult? cdp = null)
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
            issues ?? [])
        {
            Cdp = cdp ?? new CdpDiscoveryResult(SnapshotTime, []),
        };
    }

    private static ProcessDetailsResult CreateDetails(
        ProcessSnapshotEntry process,
        string? packageFullName = null)
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
                    packageFullName,
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
        bool? isSharedRuntime = false,
        string? channel = null,
        string? executableName = null)
    {
        return new ChromiumInstallation(
            name,
            kind,
            platform,
            path,
            Path.Combine(path, executableName ?? $"{name}.exe"),
            version,
            channel,
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

    private static CdpTransportInfo CreateValidatedTransport(int processId)
    {
        return new CdpTransportInfo(
            processId,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Validated,
            "9222",
            9222,
            "command-line",
            "http://127.0.0.1:9222/json/version",
            "ws://127.0.0.1:9222/devtools/browser/test",
            "Chrome/140.0",
            "1.3",
            null,
            []);
    }

    private static CdpProcessInternalsResult CreateProcessInternalsResult(
        int browserProcessId,
        ProcessSnapshotEntry renderer,
        string url)
    {
        return new CdpProcessInternalsResult(
            SnapshotTime,
            browserProcessId,
            "chrome://process-internals/",
            [
                new CdpProcessInternalsFrame(
                    "Example",
                    0,
                    7,
                    12,
                    3,
                    renderer.Identity(),
                    "Active",
                    url,
                    4,
                    5,
                    6,
                    url,
                    null),
            ],
            []);
    }

    private static ProcessTreeItemViewModel CreateTreeItem(
        ProcessSnapshotEntry process,
        string platform,
        string role,
        bool isHost)
    {
        return new ProcessTreeItemViewModel(
            process.Identity,
            new ProcessPresentationDescriptor(
                process.Identity(),
                process,
                platform,
                role,
                isHost,
                false),
            false,
            false,
            []);
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

    private sealed class DelegatingIconProvider(
        Func<string?, CancellationToken, ValueTask<ImageSource?>> getIcon)
        : IProcessIconProvider
    {
        public ValueTask<ImageSource?> GetIconAsync(
            string? executablePath,
            CancellationToken cancellationToken)
        {
            return getIcon(executablePath, cancellationToken);
        }
    }

    private sealed class RecordingSettingsStore : IGuiSettingsStore
    {
        public GuiSettings? LastSaved { get; private set; }

        public GuiSettingsLoadResult Load()
        {
            return new GuiSettingsLoadResult(new GuiSettings(), null);
        }

        public void Save(GuiSettings settings)
        {
            LastSaved = settings;
        }
    }

    private sealed class RecordingExternalToolService : IExternalToolService
    {
        public (int ProcessId, string Command)? Debug { get; private set; }

        public (int ProcessId, string Command)? ProcessExplorer
        { get; private set; }

        public ProcessIdentity? Terminated { get; private set; }

        public (string Image, string? Package, string Command)? Future
        { get; private set; }

        public (string Executable, IReadOnlyList<string> Arguments)? Launch
        { get; private set; }

        public string? OpenedFileSystemPath { get; private set; }

        public string? OpenedRegistryPath { get; private set; }

        public string? OpenedUri { get; private set; }

        public void DebugProcess(int processId, string commandTemplate)
        {
            Debug = (processId, commandTemplate);
        }

        public void OpenProcessExplorer(
            int processId,
            string commandTemplate)
        {
            ProcessExplorer = (processId, commandTemplate);
        }

        public Task TerminateProcessTreeAsync(ProcessIdentity identity)
        {
            Terminated = identity;
            return Task.CompletedTask;
        }

        public void DebugFutureLaunches(
            string imageName,
            string? packageFullName,
            string debuggerCommand)
        {
            Future = (imageName, packageFullName, debuggerCommand);
        }

        public void LaunchExecutable(
            string executablePath,
            IReadOnlyList<string> arguments)
        {
            Launch = (executablePath, arguments);
        }

        public void OpenFileSystemPath(string path)
        {
            OpenedFileSystemPath = path;
        }

        public void OpenRegistryPath(string path)
        {
            OpenedRegistryPath = path;
        }

        public void OpenUri(string uri)
        {
            OpenedUri = uri;
        }
    }

    private sealed class StubGuiDiscoveryService : IGuiDiscoveryService
    {
        public Func<CancellationToken, ValueTask<ChromiumDiscoveryResult>>
            DiscoverProcesses
        { get; init; } =
            _ => ValueTask.FromResult(
                CreateDiscoveryResult([], new ProcessGraph([], [])));

        public Func<
            ChromiumDiscoveryResult,
            CancellationToken,
            ValueTask<ChromiumDiscoveryResult>>
            DiscoverLightProcesses
        { get; init; } =
            (previous, _) => ValueTask.FromResult(previous);

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

        public Func<CdpTransportInfo, CancellationToken, ValueTask<CdpTargetListResult>>
            DiscoverCdpTargets
        { get; init; } =
            (_, _) => ValueTask.FromResult(
                new CdpTargetListResult(SnapshotTime, [], []));

        public Func<CdpTransportInfo, string, CancellationToken, ValueTask>
            OpenDevTools
        { get; init; } =
            (_, _, _) => ValueTask.CompletedTask;

        public Func<
            CdpTransportInfo,
            string?,
            IReadOnlyList<ProcessSnapshotEntry>,
            CancellationToken,
            ValueTask<CdpProcessInternalsResult>>
            DiscoverProcessInternals
        { get; init; } =
            (transport, _, _, _) => ValueTask.FromResult(
                new CdpProcessInternalsResult(
                    SnapshotTime,
                    transport.ProcessId,
                    "chrome://process-internals/",
                    [],
                    []));

        public IReadOnlyList<string> LastAdditionalInstallationFolders
        { get; private set; } = [];

        public ValueTask<ChromiumDiscoveryResult> DiscoverProcessesAsync(
            CancellationToken cancellationToken)
        {
            return DiscoverProcesses(cancellationToken);
        }

        public ValueTask<ChromiumDiscoveryResult> DiscoverProcessesLightAsync(
            ChromiumDiscoveryResult previous,
            CancellationToken cancellationToken)
        {
            return DiscoverLightProcesses(previous, cancellationToken);
        }

        public ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            return DiscoverDetails(processId, cancellationToken);
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
            IReadOnlyList<string> additionalSearchRoots,
            CancellationToken cancellationToken)
        {
            LastAdditionalInstallationFolders = additionalSearchRoots;
            return DiscoverInstallations(cancellationToken);
        }

        public ValueTask<CdpTargetListResult> DiscoverCdpTargetsAsync(
            CdpTransportInfo transport,
            CancellationToken cancellationToken)
        {
            return DiscoverCdpTargets(transport, cancellationToken);
        }

        public ValueTask OpenDevToolsAsync(
            CdpTransportInfo transport,
            string targetId,
            CancellationToken cancellationToken)
        {
            return OpenDevTools(transport, targetId, cancellationToken);
        }

        public ValueTask<CdpProcessInternalsResult>
            DiscoverProcessInternalsAsync(
                CdpTransportInfo transport,
                string? imageName,
                IReadOnlyList<ProcessSnapshotEntry> processes,
                CancellationToken cancellationToken)
        {
            return DiscoverProcessInternals(
                transport,
                imageName,
                processes,
                cancellationToken);
        }

    }

    private sealed class StubCommandLineHistoryProvider(
        IReadOnlyList<string> commandLines)
        : ICommandLineHistoryProvider
    {
        public CommandLineHistoryResult Read()
        {
            return new CommandLineHistoryResult(commandLines, []);
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
