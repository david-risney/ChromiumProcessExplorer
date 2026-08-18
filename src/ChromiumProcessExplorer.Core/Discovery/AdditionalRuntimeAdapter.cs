namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Classifies Qt WebEngine, NW.js, browser PWAs, and generic embedders.</summary>
public static class AdditionalRuntimeAdapter
{
    private const int MinimumAssociationScore = 50;

    private static readonly HashSet<string> QtModuleNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Qt5WebEngineCore.dll",
            "Qt6WebEngineCore.dll",
        };

    private static readonly HashSet<string> NwModuleNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "nw.dll",
            "node.dll",
        };

    private static readonly Dictionary<string, string> ExclusionModules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sciter.dll"] = "nonchromium.sciter",
            ["sciter-x.dll"] = "nonchromium.sciter",
            ["Ultralight.dll"] = "nonchromium.ultralight",
            ["WebCore.dll"] = "nonchromium.ultralight",
        };

    /// <summary>Analyzes passive process, module, command-line, and layout evidence.</summary>
    public static AdditionalRuntimeAnalysis Analyze(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CefRuntimeAnalysis? cef = null,
        ElectronRuntimeAnalysis? electron = null,
        WebView2RuntimeAnalysis? webView2 = null)
    {
        ArgumentNullException.ThrowIfNull(processes);

        HashSet<int> claimedProcessIds = (cef?.Processes ?? [])
            .Select(process => process.ProcessId)
            .Concat((electron?.Processes ?? []).Select(process => process.ProcessId))
            .Concat((webView2?.Processes ?? []).Select(process => process.ProcessId))
            .ToHashSet();
        List<AdditionalRuntimeProcessInfo> infos = [];
        List<RuntimeExclusion> exclusions = [];

        foreach (ProcessSnapshotEntry process in processes)
        {
            RuntimeExclusion? exclusion = CreateExclusion(process);
            if (exclusion is not null)
            {
                exclusions.Add(exclusion);
                continue;
            }

            if (claimedProcessIds.Contains(process.ProcessId))
            {
                continue;
            }

            AdditionalRuntimeProcessInfo? info =
                CreatePwaInfo(process)
                ?? CreateQtInfo(process)
                ?? CreateNwInfo(process);
            if (info is not null)
            {
                infos.Add(info);
                continue;
            }

            if (CreateGenericInfo(process) is { } generic)
            {
                infos.Add(generic);
            }
        }

        Dictionary<int, ProcessSnapshotEntry> processesById =
            processes.ToDictionary(process => process.ProcessId);
        AddBrowserManagedChildren(
            infos,
            exclusions,
            claimedProcessIds,
            processes,
            processesById);
        List<AdditionalRuntimeAssociation> associations = [];
        foreach (AdditionalRuntimeProcessInfo child in infos.Where(
            info => info.Role is not (
                AdditionalRuntimeProcessRole.Host
                or AdditionalRuntimeProcessRole.Browser)))
        {
            AdditionalRuntimeProcessInfo? source = FindBestSource(
                child,
                infos,
                processesById);
            if (source is null)
            {
                continue;
            }

            int score = ScoreAssociation(
                processesById[source.ProcessId],
                processesById[child.ProcessId]);
            if (score < MinimumAssociationScore)
            {
                continue;
            }

            associations.Add(new AdditionalRuntimeAssociation(
                source.ProcessId,
                child.ProcessId,
                child.PlatformId,
                score,
                score >= 80
                    ? ProcessRelationshipConfidence.High
                    : ProcessRelationshipConfidence.Medium,
                [
                    new AdditionalRuntimeEvidence(
                        "process-association",
                        "Matched runtime family and process ancestry.",
                        $"score={score}"),
                ]));
        }

        AddCefAnnotations(infos, cef);
        return new AdditionalRuntimeAnalysis(
            infos.OrderBy(info => info.ProcessId).ToArray(),
            associations
                .OrderBy(association => association.SourceProcessId)
                .ThenBy(association => association.TargetProcessId)
                .ToArray(),
            exclusions.OrderBy(exclusion => exclusion.ProcessId).ToArray(),
            []);
    }

    private static AdditionalRuntimeProcessInfo? CreatePwaInfo(
        ProcessSnapshotEntry process)
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
            process.CommandLine);
        string? appId = commandLine.GetSwitchValue("app-id");
        string? appUrl = commandLine.GetSwitchValue("app");
        if (string.IsNullOrWhiteSpace(appId)
            && string.IsNullOrWhiteSpace(appUrl))
        {
            return null;
        }

        string? browser = ClassifyKnownBrowser(process);
        if (browser is null)
        {
            return null;
        }

        List<AdditionalRuntimeEvidence> evidence =
        [
            new(
                "command-line-switch",
                appId is null
                    ? "Browser app mode was configured with an application URL."
                    : "Browser app mode was configured with an installed app ID.",
                appId ?? appUrl),
        ];
        return new AdditionalRuntimeProcessInfo(
            process.ProcessId,
            RuntimePlatformIds.BrowserPwa,
            ClassifyRole(commandLine, defaultBrowserRole: true),
            ProcessRelationshipConfidence.High,
            true,
            [
                $"browser={browser}",
                appId is null ? "app-mode" : $"app-id={appId}",
            ],
            evidence);
    }

    private static void AddBrowserManagedChildren(
        List<AdditionalRuntimeProcessInfo> infos,
        IReadOnlyList<RuntimeExclusion> exclusions,
        IReadOnlySet<int> claimedProcessIds,
        IReadOnlyList<ProcessSnapshotEntry> processes,
        Dictionary<int, ProcessSnapshotEntry> processesById)
    {
        HashSet<int> classifiedIds = infos
            .Where(info => info.PlatformId != RuntimePlatformIds.ChromiumGeneric)
            .Select(info => info.ProcessId)
            .Concat(exclusions.Select(exclusion => exclusion.ProcessId))
            .Concat(claimedProcessIds)
            .ToHashSet();
        bool added;
        do
        {
            added = false;
            foreach (ProcessSnapshotEntry process in processes)
            {
                if (classifiedIds.Contains(process.ProcessId)
                    || !processesById.TryGetValue(
                        process.ParentProcessId,
                        out ProcessSnapshotEntry? parent))
                {
                    continue;
                }

                AdditionalRuntimeProcessInfo? parentInfo = infos.FirstOrDefault(
                    info => info.ProcessId == parent.ProcessId
                        && info.PlatformId == RuntimePlatformIds.BrowserPwa);
                ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
                    process.CommandLine);
                if (parentInfo is null
                    || !commandLine.HasSwitch("type")
                    || !IsGenerationPlausible(parent, process))
                {
                    continue;
                }

                infos.RemoveAll(info => info.ProcessId == process.ProcessId);
                infos.Add(new AdditionalRuntimeProcessInfo(
                    process.ProcessId,
                    RuntimePlatformIds.BrowserPwa,
                    ClassifyRole(commandLine, defaultBrowserRole: false),
                    ProcessRelationshipConfidence.High,
                    true,
                    parentInfo.Annotations,
                    [
                        new AdditionalRuntimeEvidence(
                            "process-ancestry",
                            "Inherited browser-managed app identity from the parent process.",
                            parent.ProcessId.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)),
                    ]));
                classifiedIds.Add(process.ProcessId);
                added = true;
            }
        }
        while (added);
    }

    private static AdditionalRuntimeProcessInfo? CreateQtInfo(
        ProcessSnapshotEntry process)
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
            process.CommandLine);
        string executableName = Path.GetFileName(
            process.ExecutablePath ?? process.ImageName);
        string[] modules = process.LoadedModules
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        List<AdditionalRuntimeEvidence> evidence = [];
        if (executableName.Equals(
            "QtWebEngineProcess.exe",
            StringComparison.OrdinalIgnoreCase)
            || executableName.Equals(
                "QtWebEngineProcessd.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new(
                "executable-name",
                "Observed the Qt WebEngine helper executable.",
                executableName));
        }

        foreach (string module in modules.Where(QtModuleNames.Contains))
        {
            evidence.Add(new(
                "loaded-module",
                "Loaded a Qt WebEngine Core module.",
                module));
        }

        AddLayoutEvidence(
            process,
            evidence,
            "qtwebengine_resources.pak",
            "Qt WebEngine resource pack");
        if (evidence.Count == 0)
        {
            return null;
        }

        AdditionalRuntimeProcessRole role = commandLine.HasSwitch("type")
            ? ClassifyRole(commandLine, defaultBrowserRole: false)
            : executableName.StartsWith(
                "QtWebEngineProcess",
                StringComparison.OrdinalIgnoreCase)
                ? AdditionalRuntimeProcessRole.Other
                : AdditionalRuntimeProcessRole.Host;
        return new AdditionalRuntimeProcessInfo(
            process.ProcessId,
            RuntimePlatformIds.QtWebEngine,
            role,
            evidence.Count >= 2
                ? ProcessRelationshipConfidence.High
                : ProcessRelationshipConfidence.Medium,
            false,
            [],
            evidence);
    }

    private static AdditionalRuntimeProcessInfo? CreateNwInfo(
        ProcessSnapshotEntry process)
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
            process.CommandLine);
        string executableName = Path.GetFileName(
            process.ExecutablePath ?? process.ImageName);
        string[] modules = process.LoadedModules
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();
        List<AdditionalRuntimeEvidence> evidence = [];
        if (executableName.Equals("nw.exe", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new(
                "executable-name",
                "Observed the standard NW.js executable.",
                executableName));
        }

        foreach (string module in modules.Where(NwModuleNames.Contains))
        {
            evidence.Add(new(
                "loaded-module",
                "Loaded an NW.js runtime module.",
                module));
        }

        AddLayoutEvidence(process, evidence, "package.nw", "NW.js package");
        AddLayoutEvidence(process, evidence, "nw.dll", "NW.js runtime library");
        if (evidence.Count == 0)
        {
            return null;
        }

        return new AdditionalRuntimeProcessInfo(
            process.ProcessId,
            RuntimePlatformIds.Nwjs,
            commandLine.HasSwitch("type")
                ? ClassifyRole(commandLine, defaultBrowserRole: false)
                : AdditionalRuntimeProcessRole.Browser,
            evidence.Count >= 2
                ? ProcessRelationshipConfidence.High
                : ProcessRelationshipConfidence.Medium,
            false,
            [],
            evidence);
    }

    private static AdditionalRuntimeProcessInfo? CreateGenericInfo(
        ProcessSnapshotEntry process)
    {
        List<AdditionalRuntimeEvidence> evidence = [];
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
            process.CommandLine);
        if (commandLine.HasSwitch("type"))
        {
            evidence.Add(new(
                "command-line-switch",
                "Observed a Chromium subprocess type.",
                commandLine.GetSwitchValue("type")));
        }

        if (commandLine.HasSwitch("user-data-dir"))
        {
            evidence.Add(new(
                "command-line-switch",
                "Observed a Chromium user-data directory."));
        }

        if (process.LoadedModules.Any(module =>
            Path.GetFileName(module).Equals(
                "chrome_elf.dll",
                StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new(
                "loaded-module",
                "Loaded Chromium's chrome_elf module.",
                "chrome_elf.dll"));
        }

        if (evidence.Count < 2)
        {
            return null;
        }

        return new AdditionalRuntimeProcessInfo(
            process.ProcessId,
            RuntimePlatformIds.ChromiumGeneric,
            ClassifyRole(commandLine, defaultBrowserRole: true),
            ProcessRelationshipConfidence.Medium,
            false,
            [],
            evidence);
    }

    private static RuntimeExclusion? CreateExclusion(
        ProcessSnapshotEntry process)
    {
        AdditionalRuntimeEvidence[] evidence = process.LoadedModules
            .Select(Path.GetFileName)
            .Where(name => name is not null
                && ExclusionModules.ContainsKey(name))
            .Select(name => new AdditionalRuntimeEvidence(
                "loaded-module",
                "Loaded a known non-Chromium web engine module.",
                name))
            .ToArray();
        if (evidence.Length == 0)
        {
            return null;
        }

        string platformId = ExclusionModules[evidence[0].RawValue!];
        return new RuntimeExclusion(
            process.ProcessId,
            platformId,
            evidence);
    }

    private static void AddLayoutEvidence(
        ProcessSnapshotEntry process,
        List<AdditionalRuntimeEvidence> evidence,
        string fileName,
        string description)
    {
        string? directory = Path.GetDirectoryName(process.ExecutablePath);
        if (directory is null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                evidence.Add(new(
                    "filesystem-marker",
                    $"Found {description} next to the executable.",
                    path));
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
        }
    }

    private static AdditionalRuntimeProcessInfo? FindBestSource(
        AdditionalRuntimeProcessInfo child,
        IReadOnlyList<AdditionalRuntimeProcessInfo> infos,
        Dictionary<int, ProcessSnapshotEntry> processes)
    {
        ProcessSnapshotEntry childProcess = processes[child.ProcessId];
        return infos
            .Where(info => info.PlatformId == child.PlatformId
                && info.ProcessId != child.ProcessId
                && (childProcess.ParentProcessId != info.ProcessId
                    || IsGenerationPlausible(
                        processes[info.ProcessId],
                        childProcess))
                && (info.Role is AdditionalRuntimeProcessRole.Host
                        or AdditionalRuntimeProcessRole.Browser
                    || childProcess.ParentProcessId == info.ProcessId))
            .OrderByDescending(info => ScoreAssociation(
                processes[info.ProcessId],
                childProcess))
            .FirstOrDefault();
    }

    private static int ScoreAssociation(
        ProcessSnapshotEntry source,
        ProcessSnapshotEntry target)
    {
        int score = 25;
        if (target.ParentProcessId == source.ProcessId
            && IsGenerationPlausible(source, target))
        {
            score += 55;
        }

        if (PathsEqual(
            Path.GetDirectoryName(source.ExecutablePath),
            Path.GetDirectoryName(target.ExecutablePath)))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(source.UserDataDirectory)
            && PathsEqual(
                source.UserDataDirectory,
                target.UserDataDirectory))
        {
            score += 20;
        }

        return score;
    }

    private static bool IsGenerationPlausible(
        ProcessSnapshotEntry parent,
        ProcessSnapshotEntry child)
    {
        return parent.CreationTime is null
            || child.CreationTime is null
            || parent.CreationTime <= child.CreationTime;
    }

    private static bool PathsEqual(string? first, string? second)
    {
        return first is not null
            && second is not null
            && string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static AdditionalRuntimeProcessRole ClassifyRole(
        ChromiumCommandLine commandLine,
        bool defaultBrowserRole)
    {
        string? type = commandLine.GetSwitchValue("type");
        if (type is null)
        {
            return defaultBrowserRole
                ? AdditionalRuntimeProcessRole.Browser
                : AdditionalRuntimeProcessRole.Other;
        }

        return type.ToLowerInvariant() switch
        {
            "renderer" => AdditionalRuntimeProcessRole.Renderer,
            "gpu-process" => AdditionalRuntimeProcessRole.Gpu,
            "utility" => AdditionalRuntimeProcessRole.Utility,
            "crashpad-handler" => AdditionalRuntimeProcessRole.CrashHandler,
            _ => AdditionalRuntimeProcessRole.Other,
        };
    }

    private static string? ClassifyKnownBrowser(ProcessSnapshotEntry process)
    {
        string fileName = Path.GetFileName(
            process.ExecutablePath ?? process.ImageName);
        return fileName.ToLowerInvariant() switch
        {
            "chrome.exe" => "chrome",
            "msedge.exe" => "edge",
            "brave.exe" => "brave",
            "vivaldi.exe" => "vivaldi",
            "opera.exe" => "opera",
            "chromium.exe" => "chromium",
            _ => null,
        };
    }

    private static void AddCefAnnotations(
        List<AdditionalRuntimeProcessInfo> infos,
        CefRuntimeAnalysis? cef)
    {
        if (cef is null)
        {
            return;
        }

        foreach (CefProcessInfo process in cef.Processes.Where(
            process => process.Wrappers.Count > 0))
        {
            infos.Add(new AdditionalRuntimeProcessInfo(
                process.ProcessId,
                RuntimePlatformIds.Cef,
                process.Role == CefProcessRole.Browser
                    ? AdditionalRuntimeProcessRole.Browser
                    : AdditionalRuntimeProcessRole.Other,
                ProcessRelationshipConfidence.High,
                false,
                process.Wrappers
                    .Select(wrapper => $"cef.{wrapper.ToLowerInvariant()}")
                    .ToArray(),
                process.Evidence
                    .Select(item => new AdditionalRuntimeEvidence(
                        item.Source,
                        item.Detail,
                        item.Path))
                    .ToArray()));
        }
    }
}
