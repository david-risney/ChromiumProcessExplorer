namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Classifies WebView2 processes and correlates native hosts with browser
/// processes using generation-safe, explainable evidence.
/// </summary>
public static class WebView2RuntimeAdapter
{
    private const int MinimumReportedAssociationScore = 65;

    private static readonly HashSet<string> HostModuleMarkers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "EmbeddedBrowserWebView.dll",
            "Microsoft.Web.WebView2.Core.dll",
            "Microsoft.Web.WebView2.Core.WinMD",
            "Microsoft.Web.WebView2.WinForms.dll",
            "Microsoft.Web.WebView2.Wpf.dll",
            "WebView2Loader.dll",
        };

    private static readonly HashSet<string> HostLeafWindowClasses =
        new(StringComparer.Ordinal)
        {
            "Chrome_WidgetWin_0",
            "Windows.UI.Core.CoreComponentInputSource",
        };

    /// <summary>Analyzes one process, Mojo, and optional window snapshot.</summary>
    public static WebView2RuntimeAnalysis Analyze(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        MojoPipeInspectionResult mojoInspection,
        WindowSnapshotResult? windowSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(mojoInspection);

        WindowSnapshotResult windows = windowSnapshot ?? WindowSnapshotResult.Empty;
        Dictionary<int, ProcessSnapshotEntry> processesById = processes
            .ToDictionary(process => process.ProcessId);
        Candidate[] candidates = processes.Select(CreateCandidate).ToArray();
        Candidate[] hosts = candidates.Where(candidate => candidate.IsHost).ToArray();
        Candidate[] browsers = candidates.Where(candidate => candidate.IsBrowser).ToArray();
        List<DiscoveryIssue> issues = [.. windows.Issues];
        Dictionary<(int Host, int Browser), List<WebView2Evidence>> windowEvidence =
            FindWindowEvidence(windows, processesById, issues);
        HashSet<(int First, int Second)> mojoPairs = GetMojoPairs(mojoInspection);
        List<WebView2HostAssociation> associations = [];

        foreach (Candidate host in hosts)
        {
            foreach (Candidate browser in browsers)
            {
                List<WebView2Evidence> evidence = [];
                int score = 0;

                evidence.AddRange(host.Evidence);
                score += 30;
                evidence.Add(new WebView2Evidence(
                    "runtime-executable",
                    "The target is a WebView2 browser process.",
                    browser.Process.ExecutablePath ?? browser.Process.ImageName));
                score += 15;

                bool validatedParent = browser.Process.ParentProcessId
                        == host.Process.ProcessId
                    && HasValidatedParentGeneration(host.Process, browser.Process);
                if (validatedParent)
                {
                    score += 30;
                    evidence.Add(new WebView2Evidence(
                        "process-snapshot",
                        "The browser has a generation-safe OS parent relationship to the host."));
                }

                if (windowEvidence.TryGetValue(
                    (host.Process.ProcessId, browser.Process.ProcessId),
                    out List<WebView2Evidence>? matchingWindowEvidence))
                {
                    score += 45;
                    evidence.AddRange(matchingWindowEvidence);
                }

                if (mojoPairs.Contains(
                    (host.Process.ProcessId, browser.Process.ProcessId)))
                {
                    score += 15;
                    evidence.Add(new WebView2Evidence(
                        "mojo-endpoint-inspection",
                        "A live Mojo endpoint connects the host and browser processes."));
                }

                score = Math.Min(score, 100);
                if (score < MinimumReportedAssociationScore)
                {
                    continue;
                }

                ProcessRelationshipConfidence confidence = score >= 85
                    ? ProcessRelationshipConfidence.High
                    : ProcessRelationshipConfidence.Medium;
                bool hasRelationshipEvidence = validatedParent
                    || matchingWindowEvidence is not null
                    || mojoPairs.Contains(
                        (host.Process.ProcessId, browser.Process.ProcessId));
                associations.Add(new WebView2HostAssociation(
                    host.Process.ProcessId,
                    browser.Process.ProcessId,
                    score,
                    confidence,
                    hasRelationshipEvidence && confidence
                        == ProcessRelationshipConfidence.High,
                    evidence));
            }
        }

        AddDisagreementIssues(associations, issues);
        WebView2ProcessInfo[] processInfos = candidates
            .Where(candidate => candidate.IsHost || candidate.IsRuntime)
            .Select(candidate => new WebView2ProcessInfo(
                candidate.Process.ProcessId,
                candidate.IsHost
                    ? WebView2ProcessRole.Host
                    : candidate.IsBrowser
                        ? WebView2ProcessRole.Browser
                        : WebView2ProcessRole.Subprocess,
                candidate.Evidence,
                candidate.Process.ModuleInspectionError))
            .OrderBy(info => info.ProcessId)
            .ToArray();

        return new WebView2RuntimeAnalysis(
            processInfos,
            associations
                .OrderBy(association => association.HostProcessId)
                .ThenBy(association => association.BrowserProcessId)
                .ToArray(),
            windows,
            issues);
    }

    private static Candidate CreateCandidate(ProcessSnapshotEntry process)
    {
        List<WebView2Evidence> evidence = [];
        foreach (string module in process.LoadedModules)
        {
            string moduleName = Path.GetFileName(module);
            if (HostModuleMarkers.Contains(moduleName)
                || moduleName.StartsWith(
                    "Microsoft.Web.WebView2.",
                    StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(new WebView2Evidence(
                    "loaded-module",
                    $"Loaded WebView2 module {moduleName}.",
                    module));
            }
        }

        string executableName = Path.GetFileName(
            process.ExecutablePath ?? process.ImageName);
        bool isRuntime = executableName.Equals(
            "msedgewebview2.exe",
            StringComparison.OrdinalIgnoreCase);
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
            process.CommandLine);
        bool isBrowser = isRuntime && !commandLine.HasSwitch("type");
        return new Candidate(
            process,
            evidence.Count > 0 && !isRuntime,
            isRuntime,
            isBrowser,
            evidence);
    }

    private static Dictionary<(int Host, int Browser), List<WebView2Evidence>>
        FindWindowEvidence(
            WindowSnapshotResult windowSnapshot,
            IReadOnlyDictionary<int, ProcessSnapshotEntry> processesById,
            List<DiscoveryIssue> issues)
    {
        Dictionary<long, WindowSnapshotEntry> byHandle = windowSnapshot.Windows
            .ToDictionary(window => window.WindowHandle);
        Dictionary<(int Host, int Browser), List<WebView2Evidence>> results = [];

        foreach (WindowSnapshotEntry leaf in windowSnapshot.Windows.Where(
            window => HostLeafWindowClasses.Contains(window.ClassName)))
        {
            if (!TryGetCurrentProcess(leaf, processesById, out _))
            {
                continue;
            }

            (long? Handle, string Source)[] references =
            [
                (leaf.FirstChildWindowHandle, "child-window"),
                (leaf.CrossProcessChildWindowHandle, "window-property"),
            ];
            HashSet<int> referencedProcessIds = [];
            foreach ((long? targetHandle, string source) in references)
            {
                if (targetHandle is not long handle
                    || !byHandle.TryGetValue(handle, out WindowSnapshotEntry? target)
                    || target.OwnerProcessId == leaf.OwnerProcessId
                    || !TryGetCurrentProcess(target, processesById, out ProcessSnapshotEntry? process)
                    || !Path.GetFileName(process.ExecutablePath ?? process.ImageName).Equals(
                        "msedgewebview2.exe",
                        StringComparison.OrdinalIgnoreCase)
                    || ChromiumCommandLine.Parse(process.CommandLine).HasSwitch("type"))
                {
                    continue;
                }

                referencedProcessIds.Add(target.OwnerProcessId);
                (int Host, int Browser) key = (
                    leaf.OwnerProcessId,
                    target.OwnerProcessId);
                if (!results.TryGetValue(key, out List<WebView2Evidence>? evidence))
                {
                    results.Add(key, evidence = []);
                }

                evidence.Add(new WebView2Evidence(
                    source,
                    source == "window-property"
                        ? "CrossProcessChildHWND links a WebView2 host leaf to the browser process."
                        : "The first child HWND of a WebView2 host leaf belongs to the browser process.",
                    $"0x{handle:X}",
                    leaf.WindowHandle));
                evidence.Add(new WebView2Evidence(
                    "window-class",
                    $"Observed WebView2 host leaf class {leaf.ClassName}.",
                    leaf.ClassName,
                    leaf.WindowHandle));
            }

            if (referencedProcessIds.Count > 1)
            {
                issues.Add(new DiscoveryIssue(
                    "webview2-window-disagreement",
                    $"HWND 0x{leaf.WindowHandle:X} references multiple WebView2 "
                        + $"browser processes: {string.Join(", ", referencedProcessIds.Order())}.",
                    leaf.OwnerProcessId));
            }
        }

        return results;
    }

    private static bool TryGetCurrentProcess(
        WindowSnapshotEntry window,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> processesById,
        out ProcessSnapshotEntry process)
    {
        if (window.InspectionError is null
            && processesById.TryGetValue(window.OwnerProcessId, out process!)
            && !process.IsProcessIdReused
            && (process.CreationTime is null
                || window.OwnerProcessCreationTime is null
                || process.CreationTime == window.OwnerProcessCreationTime))
        {
            return true;
        }

        process = null!;
        return false;
    }

    private static HashSet<(int First, int Second)> GetMojoPairs(
        MojoPipeInspectionResult inspection)
    {
        HashSet<(int First, int Second)> pairs = [];
        foreach (NamedPipeConnection connection in inspection.Pipes.SelectMany(
            pipe => pipe.Connections))
        {
            if (connection.ServerProcessId is int server
                && connection.ClientProcessId is int client)
            {
                pairs.Add((server, client));
                pairs.Add((client, server));
            }
        }

        return pairs;
    }

    private static bool HasValidatedParentGeneration(
        ProcessSnapshotEntry parent,
        ProcessSnapshotEntry child)
    {
        return !parent.IsProcessIdReused
            && !child.IsProcessIdReused
            && (parent.CreationTime is null
                || child.CreationTime is null
                || parent.CreationTime <= child.CreationTime);
    }

    private static void AddDisagreementIssues(
        IReadOnlyList<WebView2HostAssociation> associations,
        List<DiscoveryIssue> issues)
    {
        foreach (IGrouping<int, WebView2HostAssociation> group in associations
            .Where(association => association.Evidence.Any(
                evidence => evidence.Source is "child-window" or "window-property"))
            .GroupBy(association => association.HostProcessId))
        {
            int[] windowBrowsers = group
                .Select(association => association.BrowserProcessId)
                .Distinct()
                .ToArray();
            int[] parentBrowsers = associations
                .Where(association => association.HostProcessId == group.Key
                    && association.Evidence.Any(evidence =>
                        evidence.Source == "process-snapshot"))
                .Select(association => association.BrowserProcessId)
                .Distinct()
                .ToArray();
            if (windowBrowsers.Length > 0
                && parentBrowsers.Length > 0
                && !windowBrowsers.Intersect(parentBrowsers).Any())
            {
                issues.Add(new DiscoveryIssue(
                    "webview2-evidence-disagreement",
                    $"Window evidence for host {group.Key} identifies browser "
                        + $"{string.Join(", ", windowBrowsers)}, while parent evidence "
                        + $"identifies {string.Join(", ", parentBrowsers)}.",
                    group.Key));
            }
        }
    }

    private sealed record Candidate(
        ProcessSnapshotEntry Process,
        bool IsHost,
        bool IsRuntime,
        bool IsBrowser,
        IReadOnlyList<WebView2Evidence> Evidence);
}
