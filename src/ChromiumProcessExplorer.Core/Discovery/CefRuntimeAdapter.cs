namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Classifies CEF processes and derives evidence-backed runtime associations.
/// </summary>
public static class CefRuntimeAdapter
{
    private static readonly string[] CefRuntimeMarkers =
    [
        "libcef.dll",
        "chrome_elf.dll",
        "icudtl.dat",
        "v8_context_snapshot.bin",
        "resources.pak",
    ];

    private static readonly Dictionary<string, string> WrapperMarkers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CefSharp.dll"] = "CefSharp",
            ["CefSharp.Core.dll"] = "CefSharp",
            ["CefSharp.Core.Runtime.dll"] = "CefSharp",
            ["CefSharp.BrowserSubprocess.exe"] = "CefSharp",
            ["jcef.dll"] = "JCEF",
        };

    private static readonly Dictionary<string, (string Category, string Detail)>
        WarningSwitches =
            new Dictionary<string, (string Category, string Detail)>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["no-sandbox"] = (
                    "security",
                    "Disables the Chromium sandbox and is intended only for testing."),
                ["disable-web-security"] = (
                    "security",
                    "Disables same-origin web security when used with a user data directory."),
                ["single-process"] = (
                    "unsupported",
                    "Runs Chromium in unsupported single-process mode."),
                ["disable-kill-after-bad-ipc"] = (
                    "security",
                    "Prevents termination after bad IPC and weakens a security boundary."),
                ["remote-debugging-port"] = (
                    "debugging",
                    "Exposes a DevTools remote-debugging endpoint."),
                ["remote-debugging-pipe"] = (
                    "debugging",
                    "Exposes DevTools remote debugging over pipes."),
            };

    /// <summary>Analyzes one generation-safe process snapshot for CEF evidence.</summary>
    public static CefRuntimeAnalysis Analyze(
        IReadOnlyList<ProcessSnapshotEntry> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        Candidate[] candidates = processes
            .Select(CreateCandidate)
            .ToArray();
        Candidate[] browsers = candidates
            .Where(candidate => candidate.HasDirectCefEvidence
                && candidate.Role == CefProcessRole.Browser)
            .ToArray();

        List<CefProcessAssociation> associations = [];
        HashSet<int> includedProcessIds = browsers
            .Select(candidate => candidate.Process.ProcessId)
            .ToHashSet();

        foreach (Candidate candidate in candidates.Where(
            candidate => candidate.Role != CefProcessRole.Browser))
        {
            (Candidate Browser, AssociationScore Score)? best =
                FindBestBrowser(candidate, browsers);
            bool matchedConfiguredSubprocess = best is not null
                && best.Value.Score.MatchedConfiguredSubprocess;
            if (!candidate.HasDirectCefEvidence
                && !matchedConfiguredSubprocess
                && best?.Score.Score < 50)
            {
                continue;
            }

            includedProcessIds.Add(candidate.Process.ProcessId);
            if (best is null || best.Value.Score.Score < 35)
            {
                continue;
            }

            Candidate browser = best.Value.Browser;
            AssociationScore score = best.Value.Score;
            includedProcessIds.Add(browser.Process.ProcessId);
            CefAssociationConfidence confidence = score.Score switch
            {
                >= 75 => CefAssociationConfidence.High,
                >= 50 => CefAssociationConfidence.Medium,
                _ => CefAssociationConfidence.Low,
            };
            bool authoritative = confidence == CefAssociationConfidence.High
                && score.ValidatedParent
                && candidate.HasExplicitProcessType;
            associations.Add(new CefProcessAssociation(
                browser.Process.ProcessId,
                candidate.Process.ProcessId,
                score.Score,
                confidence,
                authoritative,
                score.Evidence));
        }

        Dictionary<int, CefDeploymentLayout> layouts =
            DetermineLayouts(candidates, associations);
        CefProcessInfo[] results = candidates
            .Where(candidate => includedProcessIds.Contains(candidate.Process.ProcessId))
            .Select(candidate => candidate.ToProcessInfo(
                layouts.GetValueOrDefault(
                    candidate.Process.ProcessId,
                    CefDeploymentLayout.Unknown)))
            .OrderBy(info => info.ProcessId)
            .ToArray();

        return new CefRuntimeAnalysis(
            results,
            associations
                .OrderBy(association => association.BrowserProcessId)
                .ThenBy(association => association.SubprocessProcessId)
                .ToArray());
    }

    private static Candidate CreateCandidate(ProcessSnapshotEntry process)
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(process.CommandLine);
        string? rawType = commandLine.GetSwitchValue("type");
        bool hasExplicitType = commandLine.HasSwitch("type");
        CefProcessRole role = ClassifyRole(rawType, hasExplicitType);
        List<CefEvidence> evidence = [];
        HashSet<string> wrappers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string item in process.Evidence)
        {
            evidence.Add(new CefEvidence("process-snapshot", item));
            AddWrapperFromText(item, wrappers);
        }

        bool hasRuntimeModule = false;
        foreach (string module in process.LoadedModules)
        {
            string moduleName = Path.GetFileName(module);
            if (CefRuntimeMarkers.Contains(moduleName, StringComparer.OrdinalIgnoreCase))
            {
                hasRuntimeModule = true;
                evidence.Add(new CefEvidence(
                    "loaded-module",
                    $"Loaded CEF runtime module {moduleName}.",
                    module));
            }

            AddWrapperFromMarker(moduleName, wrappers);
            AddWrapperFromText(module, wrappers);
        }

        string? executableDirectory = GetExecutableDirectory(process.ExecutablePath);
        bool hasRuntimeFile = false;
        if (executableDirectory is not null)
        {
            foreach (string marker in CefRuntimeMarkers)
            {
                string markerPath = Path.Combine(executableDirectory, marker);
                if (File.Exists(markerPath))
                {
                    hasRuntimeFile = true;
                    evidence.Add(new CefEvidence(
                        "filesystem-marker",
                        $"Found CEF runtime marker {marker}.",
                        markerPath));
                }
            }

            foreach ((string marker, string wrapper) in WrapperMarkers)
            {
                string markerPath = Path.Combine(executableDirectory, marker);
                if (File.Exists(markerPath))
                {
                    wrappers.Add(wrapper);
                    evidence.Add(new CefEvidence(
                        "filesystem-marker",
                        $"Found {wrapper} marker {marker}.",
                        markerPath));
                }
            }
        }

        foreach (string argument in commandLine.Arguments.Skip(1))
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                evidence.Add(new CefEvidence("command-line-switch", argument));
            }
        }

        string executableName = Path.GetFileName(
            process.ExecutablePath ?? process.ImageName);
        bool isKnownSample = executableName.Equals(
                "cefclient.exe",
                StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("cefsimple.exe", StringComparison.OrdinalIgnoreCase);
        bool hasCefTextEvidence = process.Evidence.Any(IsCefTextEvidence);
        bool hasDirectCefEvidence = hasRuntimeModule
            || hasRuntimeFile
            || wrappers.Count > 0
            || isKnownSample
            || hasCefTextEvidence
            || commandLine.HasSwitch("browser-subprocess-path");

        string? userDataDirectory = commandLine.GetSwitchValue("user-data-dir")
            ?? process.UserDataDirectory;
        string? crashReportDirectory =
            commandLine.GetSwitchValue("crash-dumps-dir")
            ?? commandLine.GetSwitchValue("database");
        string? crashReporterConfiguration = GetExistingSiblingFile(
            executableDirectory,
            "crash_reporter.cfg");
        string? devToolsActivePort = GetExistingChildFile(
            userDataDirectory,
            "DevToolsActivePort");
        CefRuntimePaths runtimePaths = new(
            userDataDirectory,
            commandLine.GetSwitchValue("log-file"),
            commandLine.GetSwitchValue("resources-dir-path"),
            commandLine.GetSwitchValue("locales-dir-path"),
            commandLine.GetSwitchValue("browser-subprocess-path"),
            crashReportDirectory,
            crashReporterConfiguration,
            devToolsActivePort);

        List<CefSwitchWarning> warnings = [];
        foreach ((string name, (string category, string detail)) in WarningSwitches)
        {
            if (commandLine.HasSwitch(name))
            {
                warnings.Add(new CefSwitchWarning($"--{name}", category, detail));
            }
        }

        return new Candidate(
            process,
            role,
            rawType,
            hasExplicitType,
            commandLine.GetSwitchValue("service-sandbox-type"),
            commandLine.GetSwitchValue("utility-sub-type"),
            runtimePaths,
            commandLine.GetSwitchValue("remote-debugging-port"),
            commandLine.HasSwitch("remote-debugging-pipe"),
            wrappers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings,
            evidence,
            hasDirectCefEvidence,
            hasRuntimeModule || hasRuntimeFile);
    }

    private static CefProcessRole ClassifyRole(
        string? rawType,
        bool hasExplicitType)
    {
        if (!hasExplicitType)
        {
            return CefProcessRole.Browser;
        }

        return rawType?.ToLowerInvariant() switch
        {
            "renderer" => CefProcessRole.Renderer,
            "gpu-process" => CefProcessRole.Gpu,
            "utility" => CefProcessRole.Utility,
            "crashpad-handler" => CefProcessRole.Crashpad,
            _ => CefProcessRole.Other,
        };
    }

    private static (Candidate Browser, AssociationScore Score)? FindBestBrowser(
        Candidate subprocess,
        IReadOnlyList<Candidate> browsers)
    {
        (Candidate Browser, AssociationScore Score)? best = null;
        foreach (Candidate browser in browsers)
        {
            AssociationScore score = ScoreAssociation(browser, subprocess);
            if (best is null || score.Score > best.Value.Score.Score)
            {
                best = (browser, score);
            }
        }

        return best;
    }

    private static AssociationScore ScoreAssociation(
        Candidate browser,
        Candidate subprocess)
    {
        int score = 0;
        List<string> evidence = [];
        bool validatedParent = browser.Process.ProcessId
            == subprocess.Process.ParentProcessId
            && IsCreatedBefore(browser.Process, subprocess.Process);
        if (validatedParent)
        {
            score += 40;
            evidence.Add("Generation-safe parent process relationship.");
        }

        bool matchedConfiguredSubprocess = PathsEqual(
            browser.RuntimePaths.BrowserSubprocessPath,
            subprocess.Process.ExecutablePath);
        if (matchedConfiguredSubprocess)
        {
            score += 35;
            evidence.Add("Matches the browser's explicit --browser-subprocess-path.");
        }

        if (PathsEqual(
            browser.Process.ExecutablePath,
            subprocess.Process.ExecutablePath))
        {
            score += 25;
            evidence.Add("Uses the same executable as the browser process.");
        }
        else if (DirectoriesEqual(
            browser.Process.ExecutablePath,
            subprocess.Process.ExecutablePath))
        {
            score += 10;
            evidence.Add("Executable is in the browser's runtime directory.");
        }

        if (HasStartupProximity(browser.Process, subprocess.Process))
        {
            score += 10;
            evidence.Add("Started within 30 seconds of the browser process.");
        }

        if (PathsEqual(
            browser.RuntimePaths.UserDataDirectory,
            subprocess.RuntimePaths.UserDataDirectory))
        {
            score += 15;
            evidence.Add("Uses the same explicit user data directory.");
        }

        if (browser.HasRuntimeEvidence && subprocess.HasRuntimeEvidence)
        {
            score += 10;
            evidence.Add("Both processes have CEF runtime file or module evidence.");
        }

        return new AssociationScore(
            Math.Min(score, 100),
            validatedParent,
            matchedConfiguredSubprocess,
            evidence);
    }

    private static Dictionary<int, CefDeploymentLayout> DetermineLayouts(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<CefProcessAssociation> associations)
    {
        Dictionary<int, CefDeploymentLayout> layouts = [];
        Dictionary<int, Candidate> byProcessId = candidates.ToDictionary(
            candidate => candidate.Process.ProcessId);

        foreach (Candidate candidate in candidates)
        {
            string executableName = Path.GetFileName(
                candidate.Process.ExecutablePath ?? candidate.Process.ImageName);
            if (executableName.Equals("bootstrap.exe", StringComparison.OrdinalIgnoreCase)
                || executableName.Equals(
                    "bootstrapc.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                layouts[candidate.Process.ProcessId] =
                    CefDeploymentLayout.BootstrapOrDllHosted;
            }
        }

        foreach (CefProcessAssociation association in associations)
        {
            Candidate browser = byProcessId[association.BrowserProcessId];
            Candidate subprocess = byProcessId[association.SubprocessProcessId];
            CefDeploymentLayout layout;
            if (layouts.GetValueOrDefault(browser.Process.ProcessId)
                == CefDeploymentLayout.BootstrapOrDllHosted)
            {
                layout = CefDeploymentLayout.BootstrapOrDllHosted;
            }
            else if (PathsEqual(
                browser.Process.ExecutablePath,
                subprocess.Process.ExecutablePath))
            {
                layout = CefDeploymentLayout.SameExecutable;
            }
            else
            {
                layout = CefDeploymentLayout.SeparateSubprocess;
            }

            layouts[browser.Process.ProcessId] = layout;
            layouts[subprocess.Process.ProcessId] = layout;
        }

        return layouts;
    }

    private static void AddWrapperFromMarker(
        string marker,
        HashSet<string> wrappers)
    {
        if (WrapperMarkers.TryGetValue(marker, out string? wrapper))
        {
            wrappers.Add(wrapper);
        }
    }

    private static void AddWrapperFromText(
        string text,
        HashSet<string> wrappers)
    {
        if (text.Contains("CefSharp", StringComparison.OrdinalIgnoreCase))
        {
            wrappers.Add("CefSharp");
        }

        if (text.Contains("JCEF", StringComparison.OrdinalIgnoreCase)
            || text.Contains("jcef.dll", StringComparison.OrdinalIgnoreCase))
        {
            wrappers.Add("JCEF");
        }

        if (text.Contains("CEF4Delphi", StringComparison.OrdinalIgnoreCase))
        {
            wrappers.Add("CEF4Delphi");
        }
    }

    private static bool IsCefTextEvidence(string evidence)
    {
        return evidence.Contains("libcef.dll", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("CEF runtime", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("CefSharp", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("JCEF", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("CEF4Delphi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCreatedBefore(
        ProcessSnapshotEntry possibleParent,
        ProcessSnapshotEntry child)
    {
        return possibleParent.CreationTime is null
            || child.CreationTime is null
            || possibleParent.CreationTime <= child.CreationTime;
    }

    private static bool HasStartupProximity(
        ProcessSnapshotEntry browser,
        ProcessSnapshotEntry subprocess)
    {
        return browser.CreationTime is not null
            && subprocess.CreationTime is not null
            && subprocess.CreationTime >= browser.CreationTime
            && subprocess.CreationTime - browser.CreationTime <= TimeSpan.FromSeconds(30);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(
                NormalizePath(left),
                NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool DirectoriesEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && PathsEqual(Path.GetDirectoryName(left), Path.GetDirectoryName(right));
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(path.Trim('"'));
    }

    private static string? GetExecutableDirectory(string? executablePath)
    {
        return string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetDirectoryName(executablePath);
    }

    private static string? GetExistingSiblingFile(
        string? directory,
        string fileName)
    {
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string? GetExistingChildFile(
        string? directory,
        string fileName)
    {
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : GetExistingSiblingFile(directory, fileName);
    }

    private sealed record Candidate(
        ProcessSnapshotEntry Process,
        CefProcessRole Role,
        string? RawProcessType,
        bool HasExplicitProcessType,
        string? UtilityRole,
        string? UtilitySubType,
        CefRuntimePaths RuntimePaths,
        string? RemoteDebuggingPort,
        bool RemoteDebuggingPipe,
        IReadOnlyList<string> Wrappers,
        IReadOnlyList<CefSwitchWarning> SwitchWarnings,
        IReadOnlyList<CefEvidence> Evidence,
        bool HasDirectCefEvidence,
        bool HasRuntimeEvidence)
    {
        public CefProcessInfo ToProcessInfo(CefDeploymentLayout layout)
        {
            return new CefProcessInfo(
                Process.ProcessId,
                Role,
                RawProcessType,
                UtilityRole,
                UtilitySubType,
                layout,
                RuntimePaths,
                RemoteDebuggingPort,
                RemoteDebuggingPipe,
                Wrappers,
                SwitchWarnings,
                Evidence);
        }
    }

    private sealed record AssociationScore(
        int Score,
        bool ValidatedParent,
        bool MatchedConfiguredSubprocess,
        IReadOnlyList<string> Evidence);
}
