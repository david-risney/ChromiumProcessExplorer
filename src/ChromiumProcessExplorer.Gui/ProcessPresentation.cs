using System.ComponentModel;
using System.Text.RegularExpressions;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Gui;

public sealed record ProcessPresentationDescriptor(
    ProcessIdentity Identity,
    ProcessSnapshotEntry Process,
    string Platform,
    string Role,
    bool IsHost,
    bool HasWarning);

public sealed record ProcessPresentationBranch(
    string BranchKey,
    ProcessPresentationDescriptor Process,
    bool IsReference,
    IReadOnlyList<ProcessPresentationBranch> Children);

public sealed record ProcessPresentationTree(
    IReadOnlyList<ProcessPresentationBranch> Roots,
    IReadOnlyDictionary<ProcessIdentity, ProcessPresentationDescriptor> Processes);

public static class ProcessPresentationTreeBuilder
{
    public static ProcessPresentationTree Build(ChromiumDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Dictionary<int, Classification> classifications =
            BuildClassifications(result);
        HashSet<int> warningProcessIds = CollectIssues(result)
            .Where(issue => issue.ProcessId is not null)
            .Select(issue => issue.ProcessId!.Value)
            .ToHashSet();
        Dictionary<int, ProcessPresentationDescriptor> descriptors = result.Processes
            .Where(process => classifications.ContainsKey(process.ProcessId))
            .ToDictionary(
                process => process.ProcessId,
                process =>
                {
                    Classification classification =
                        classifications[process.ProcessId];
                    return new ProcessPresentationDescriptor(
                        new ProcessIdentity(
                            process.ProcessId,
                            process.CreationTime),
                        process,
                        classification.Platform,
                        classification.Role,
                        classification.IsHost,
                        warningProcessIds.Contains(process.ProcessId));
                });

        Dictionary<int, int[]> parents = descriptors.Keys
            .ToDictionary(
                processId => processId,
                processId => GetVisualParents(
                    processId,
                    descriptors,
                    result.ProcessGraph));
        int[] rootProcessIds = OrderByRoleThenProcessId(
            descriptors.Keys.Where(
                processId => parents[processId].Length == 0),
            descriptors);
        if (rootProcessIds.Length == 0)
        {
            rootProcessIds = OrderByRoleThenProcessId(
                descriptors.Keys,
                descriptors);
        }

        Dictionary<int, int> occurrenceCounts = [];
        List<ProcessPresentationBranch> roots = [];
        foreach (int processId in rootProcessIds)
        {
            roots.Add(BuildBranch(
                processId,
                "root",
                descriptors,
                parents,
                occurrenceCounts,
                []));
        }

        HashSet<int> represented = roots
            .SelectMany(Flatten)
            .Select(branch => branch.Process.Identity.ProcessId)
            .ToHashSet();
        foreach (int processId in OrderByRoleThenProcessId(
            descriptors.Keys.Where(
                processId => !represented.Contains(processId)),
            descriptors))
        {
            roots.Add(BuildBranch(
                processId,
                "root",
                descriptors,
                parents,
                occurrenceCounts,
                []));
        }

        return new ProcessPresentationTree(
            roots,
            descriptors.Values.ToDictionary(process => process.Identity));
    }

    public static IReadOnlyList<DiscoveryIssue> CollectIssues(
        ChromiumDiscoveryResult result)
    {
        return result.Issues
            .Concat(result.Cdp.Issues)
            .Concat(result.MojoPipeInspection.Issues)
            .Concat(result.CefRuntime.Processes
                .Where(process => process.ModuleInspectionError is not null)
                .Select(process => new DiscoveryIssue(
                    "cef-modules",
                    process.ModuleInspectionError!,
                    process.ProcessId)))
            .Concat(result.WebView2Runtime.Issues)
            .Concat(result.ElectronRuntime.Issues)
            .Concat(result.AdditionalRuntime.Issues)
            .ToArray();
    }

    private static ProcessPresentationBranch BuildBranch(
        int processId,
        string parentKey,
        IReadOnlyDictionary<int, ProcessPresentationDescriptor> descriptors,
        IReadOnlyDictionary<int, int[]> parents,
        IDictionary<int, int> occurrenceCounts,
        HashSet<int> path)
    {
        ProcessPresentationDescriptor descriptor = descriptors[processId];
        occurrenceCounts.TryGetValue(processId, out int occurrence);
        occurrenceCounts[processId] = occurrence + 1;
        string branchKey = $"{parentKey}/{descriptor.Identity}";
        if (!path.Add(processId))
        {
            return new ProcessPresentationBranch(
                branchKey,
                descriptor,
                occurrence > 0,
                []);
        }

        int[] childIds = OrderByRoleThenProcessId(
            parents
                .Where(pair => pair.Value.Contains(processId))
                .Select(pair => pair.Key),
            descriptors);
        ProcessPresentationBranch[] children = childIds
            .Where(childId => !path.Contains(childId))
            .Select(childId => BuildBranch(
                childId,
                branchKey,
                descriptors,
                parents,
                occurrenceCounts,
                [.. path]))
            .ToArray();
        return new ProcessPresentationBranch(
            branchKey,
            descriptor,
            occurrence > 0,
            children);
    }

    private static int[] OrderByRoleThenProcessId(
        IEnumerable<int> processIds,
        IReadOnlyDictionary<int, ProcessPresentationDescriptor> descriptors)
    {
        return processIds
            .OrderBy(processId => GetRoleOrder(descriptors[processId].Role))
            .ThenBy(
                processId => descriptors[processId].Role,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(processId => processId)
            .ToArray();
    }

    private static int GetRoleOrder(string role)
    {
        return string.Equals(
            role,
            "Renderer",
            StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static IEnumerable<ProcessPresentationBranch> Flatten(
        ProcessPresentationBranch root)
    {
        yield return root;
        foreach (ProcessPresentationBranch child in root.Children)
        {
            foreach (ProcessPresentationBranch descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static int[] GetVisualParents(
        int targetProcessId,
        Dictionary<int, ProcessPresentationDescriptor> descriptors,
        ProcessGraph graph)
    {
        ProcessPresentationDescriptor target = descriptors[targetProcessId];
        ProcessGraphEdge[] incoming = graph
            .GetIncomingEdges(target.Identity)
            .Where(edge => descriptors.ContainsKey(edge.Source.ProcessId))
            .ToArray();
        foreach (ProcessRelationshipType type in new[]
        {
            ProcessRelationshipType.EmbeddedBy,
            ProcessRelationshipType.ChromiumSubprocess,
            ProcessRelationshipType.OsParent,
        })
        {
            int[] candidates = incoming
                .Where(edge => edge.Type == type)
                .Select(edge => edge.Source.ProcessId)
                .Distinct()
                .Order()
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates;
            }
        }

        return [];
    }

    private static Dictionary<int, Classification> BuildClassifications(
        ChromiumDiscoveryResult result)
    {
        Dictionary<int, Classification> classifications = [];
        foreach (ProcessSnapshotEntry process in result.Processes.Where(
            process => process.IsLikelyChromium))
        {
            classifications[process.ProcessId] = new(
                FormatChromiumPlatform(process),
                NormalizeRole(
                    process.ChromiumProcessType ?? "Browser",
                    GetUtilitySubType(process)),
                false);
        }

        Dictionary<int, ProcessSnapshotEntry> snapshots = result.Processes
            .ToDictionary(process => process.ProcessId);
        foreach (AdditionalRuntimeProcessInfo process in
            result.AdditionalRuntime.Processes)
        {
            snapshots.TryGetValue(
                process.ProcessId,
                out ProcessSnapshotEntry? snapshot);
            string platform = process.PlatformId == RuntimePlatformIds.ChromiumGeneric
                && classifications.TryGetValue(
                    process.ProcessId,
                    out Classification? existing)
                    ? existing.Platform
                    : FormatPlatform(process.PlatformId);
            classifications[process.ProcessId] = new(
                platform,
                NormalizeRole(
                    process.Role.ToString(),
                    snapshot is null ? null : GetUtilitySubType(snapshot)),
                process.Role == AdditionalRuntimeProcessRole.Host);
        }

        foreach (CefProcessInfo process in result.CefRuntime.Processes)
        {
            classifications[process.ProcessId] = new(
                "CEF",
                NormalizeRole(
                    process.Role.ToString(),
                    process.UtilitySubType ?? process.UtilityRole),
                false);
        }

        foreach (CefHostAssociation association in
            result.CefRuntime.HostAssociations)
        {
            classifications.TryAdd(
                association.HostProcessId,
                new Classification("CEF", "Host", true));
        }

        foreach (ElectronProcessInfo process in result.ElectronRuntime.Processes)
        {
            classifications[process.ProcessId] = new(
                "Electron",
                NormalizeRole(
                    process.Role.ToString(),
                    process.UtilitySubType),
                process.Role == ElectronProcessRole.Main);
        }

        foreach (WebView2ProcessInfo process in result.WebView2Runtime.Processes)
        {
            snapshots.TryGetValue(
                process.ProcessId,
                out ProcessSnapshotEntry? snapshot);
            string role = process.Role == WebView2ProcessRole.Subprocess
                ? NormalizeRole(
                    snapshot?.ChromiumProcessType ?? "Subprocess",
                    snapshot is null ? null : GetUtilitySubType(snapshot))
                : NormalizeRole(process.Role.ToString(), null);
            classifications[process.ProcessId] = new(
                "WebView2",
                role,
                process.Role == WebView2ProcessRole.Host);
        }

        foreach (WebView2HostAssociation association in
            result.WebView2Runtime.HostAssociations)
        {
            classifications.TryAdd(
                association.HostProcessId,
                new Classification("WebView2", "Host", true));
        }

        foreach (ProcessGraphEdge edge in result.ProcessGraph.Edges.Where(
            edge => edge.Type == ProcessRelationshipType.EmbeddedBy))
        {
            if (classifications.TryGetValue(
                edge.Target.ProcessId,
                out Classification? targetClassification))
            {
                classifications.TryAdd(
                    edge.Source.ProcessId,
                    new Classification(
                        targetClassification.Platform,
                        "Host",
                        true));
            }
        }

        AddInferredBrowserParents(
            classifications,
            snapshots,
            result.ProcessGraph);

        return classifications;
    }

    private static void AddInferredBrowserParents(
        Dictionary<int, Classification> classifications,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> snapshots,
        ProcessGraph graph)
    {
        foreach (ProcessSnapshotEntry parent in snapshots.Values.Where(
            process => !classifications.ContainsKey(process.ProcessId)))
        {
            Classification[] childClassifications = graph
                .GetOutgoingEdges(new ProcessIdentity(
                    parent.ProcessId,
                    parent.CreationTime))
                .Where(edge => edge.Type == ProcessRelationshipType.OsParent)
                .Select(edge => snapshots.GetValueOrDefault(
                    edge.Target.ProcessId))
                .Where(child => child is not null
                    && child.ChromiumProcessType is not null
                    && IsSameExecutable(parent, child))
                .Select(child => classifications.GetValueOrDefault(
                    child!.ProcessId))
                .Where(classification => classification is not null)
                .Cast<Classification>()
                .ToArray();
            if (childClassifications.Length == 0)
            {
                continue;
            }

            string[] platforms = childClassifications
                .Select(classification => classification.Platform)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string platform = platforms.Length == 1
                ? platforms[0]
                : "Chromium";
            bool isElectron = platform.Equals(
                "Electron",
                StringComparison.OrdinalIgnoreCase);
            classifications[parent.ProcessId] = new(
                platform,
                isElectron ? "Main" : "Browser",
                isElectron);
        }
    }

    private static bool IsSameExecutable(
        ProcessSnapshotEntry first,
        ProcessSnapshotEntry second)
    {
        if (!string.IsNullOrWhiteSpace(first.ExecutablePath)
            && !string.IsNullOrWhiteSpace(second.ExecutablePath))
        {
            return first.ExecutablePath.Equals(
            second.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
        }

        return first.ImageName.Equals(
            second.ImageName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPlatform(string platformId)
    {
        return platformId switch
        {
            RuntimePlatformIds.QtWebEngine => "Qt WebEngine",
            RuntimePlatformIds.Nwjs => "NW.js",
            RuntimePlatformIds.BrowserPwa => "Browser app",
            RuntimePlatformIds.ChromiumGeneric => "Chromium",
            RuntimePlatformIds.Cef => "CEF",
            RuntimePlatformIds.WebView2 => "WebView2",
            RuntimePlatformIds.Electron => "Electron",
            _ => platformId,
        };
    }

    private static string FormatChromiumPlatform(ProcessSnapshotEntry process)
    {
        string imageName = process.ImageName;
        if (imageName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
            || imageName.Equals(
                "msedgewebview2.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return imageName.StartsWith(
                "msedgewebview2",
                StringComparison.OrdinalIgnoreCase)
                ? "WebView2"
                : "Edge";
        }

        if (imageName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        if (imageName.Contains("brave", StringComparison.OrdinalIgnoreCase))
        {
            return "Brave";
        }

        if (imageName.Contains("vivaldi", StringComparison.OrdinalIgnoreCase))
        {
            return "Vivaldi";
        }

        if (imageName.Contains("opera", StringComparison.OrdinalIgnoreCase))
        {
            return "Opera";
        }

        return "Chromium";
    }

    private static string NormalizeRole(
        string role,
        string? utilitySubType)
    {
        if (role.Equals("utility", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(utilitySubType))
        {
            return FormatUtilityRole(utilitySubType);
        }

        return role.ToLowerInvariant() switch
        {
            "browser" => "Browser",
            "main" => "Main",
            "host" => "Host",
            "renderer" => "Renderer",
            "gpu" or "gpu-process" => "GPU",
            "utility" => "Utility",
            "crashpad" or "crashhandler" or "crash-handler"
                or "crashpad-handler" => "Crash handler",
            "devtools" => "DevTools",
            "worker" => "Worker",
            "serviceworker" or "service-worker" => "Service worker",
            "nodehelper" or "node-helper" => "Node helper",
            "subprocess" => "Subprocess",
            "other" => "Other",
            _ => ToDisplayWords(role),
        };
    }

    private static string? GetUtilitySubType(ProcessSnapshotEntry process)
    {
        try
        {
            ChromiumCommandLine commandLine =
                ChromiumCommandLine.Parse(process.CommandLine);
            return commandLine.GetSwitchValue("utility-sub-type")
                ?? commandLine.GetSwitchValue("service-sandbox-type");
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static string FormatUtilityRole(string rawValue)
    {
        if (rawValue.Contains("NetworkService", StringComparison.OrdinalIgnoreCase))
        {
            return "Network service";
        }

        if (rawValue.Contains("AudioService", StringComparison.OrdinalIgnoreCase))
        {
            return "Audio service";
        }

        if (rawValue.Contains("StorageService", StringComparison.OrdinalIgnoreCase))
        {
            return "Storage service";
        }

        if (rawValue.Contains("DataDecoder", StringComparison.OrdinalIgnoreCase))
        {
            return "Data decoder";
        }

        string value = rawValue.Split('.').Last();
        return ToDisplayWords(value);
    }

    private static string ToDisplayWords(string value)
    {
        string words = Regex.Replace(
            value.Replace('-', ' ').Replace('_', ' '),
            "([a-z0-9])([A-Z])",
            "$1 $2");
        if (words.Length == 0)
        {
            return "Other";
        }

        return (char.ToUpperInvariant(words[0]) + words[1..]).Trim();
    }

    private sealed record Classification(
        string Platform,
        string Role,
        bool IsHost);
}
