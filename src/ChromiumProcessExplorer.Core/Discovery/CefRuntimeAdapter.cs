namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Classifies CEF processes and derives evidence-backed runtime associations.
/// </summary>
public static class CefRuntimeAdapter
{
    private const string CefRuntimeAnchor = "libcef.dll";
    private const int MinimumCandidateAssociationScore = 50;
    private const int MinimumReportedAssociationScore = 35;
    private const int MinimumReportedHostAssociationScore = 60;
    private const int HighConfidenceScore = 75;
    private const int MediumConfidenceScore = 50;

    private static readonly string[] CefCorroboratingMarkers =
    [
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
        List<CefHostAssociation> hostAssociations = FindHostAssociations(
            candidates,
            browsers);
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
                && (best?.Score.Score ?? 0) < MinimumCandidateAssociationScore)
            {
                continue;
            }

            includedProcessIds.Add(candidate.Process.ProcessId);
            if (best is null
                || best.Value.Score.Score < MinimumReportedAssociationScore)
            {
                continue;
            }

            Candidate browser = best.Value.Browser;
            AssociationScore score = best.Value.Score;
            includedProcessIds.Add(browser.Process.ProcessId);
            CefAssociationConfidence confidence = GetConfidence(score.Score);
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
                .ToArray(),
            hostAssociations);
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

        bool hasCefAnchor = false;
        foreach (string module in process.LoadedModules)
        {
            string moduleName = Path.GetFileName(module);
            if (moduleName.Equals(CefRuntimeAnchor, StringComparison.OrdinalIgnoreCase))
            {
                hasCefAnchor = true;
                evidence.Add(new CefEvidence(
                    "loaded-module",
                    $"Loaded CEF runtime module {moduleName}.",
                    module));
            }
            else if (moduleName.Equals(
                "chrome_elf.dll",
                StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(new CefEvidence(
                    "loaded-module",
                    $"Loaded Chromium module {moduleName}, corroborating CEF evidence.",
                    module));
            }

            AddWrapperFromMarker(moduleName, wrappers);
            AddWrapperFromText(module, wrappers);
        }

        string? executableDirectory = GetExecutableDirectory(process.ExecutablePath);
        if (executableDirectory is not null)
        {
            string anchorPath = Path.Combine(executableDirectory, CefRuntimeAnchor);
            if (File.Exists(anchorPath))
            {
                hasCefAnchor = true;
                evidence.Add(new CefEvidence(
                    "filesystem-marker",
                    $"Found CEF runtime marker {CefRuntimeAnchor}.",
                    anchorPath));
            }

            foreach (string marker in CefCorroboratingMarkers)
            {
                string markerPath = Path.Combine(executableDirectory, marker);
                if (File.Exists(markerPath))
                {
                    evidence.Add(new CefEvidence(
                        "filesystem-marker",
                        $"Found Chromium runtime marker {marker}, corroborating CEF evidence.",
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
        bool hasDirectCefEvidence = hasCefAnchor
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
            GetFilePathSwitchValue(commandLine, "log-file"),
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
            hasCefAnchor);
    }

    private static List<CefHostAssociation> FindHostAssociations(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<Candidate> browsers)
    {
        Dictionary<int, Candidate> byProcessId = candidates.ToDictionary(
            candidate => candidate.Process.ProcessId);
        List<CefHostAssociation> results = [];

        foreach (Candidate browser in browsers)
        {
            if (!byProcessId.TryGetValue(
                browser.Process.ParentProcessId,
                out Candidate? host)
                || host.Process.IsProcessIdReused
                || !HasValidatedParentGeneration(host.Process, browser.Process))
            {
                continue;
            }

            int score = 40;
            List<string> evidence =
            [
                "Generation-safe parent process relationship.",
            ];
            bool referencesHostProcessId = CommandLineReferencesValue(
                browser.Process.CommandLine,
                host.Process.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            if (referencesHostProcessId)
            {
                score += 30;
                evidence.Add(
                    "The browser command line explicitly references the host process ID.");
            }

            bool referencesHostExecutable = CommandLineReferencesPath(
                browser.Process.CommandLine,
                host.Process.ExecutablePath);
            if (referencesHostExecutable)
            {
                score += 30;
                evidence.Add(
                    "The browser command line explicitly references the host executable.");
            }

            if (browser.HasRuntimeEvidence)
            {
                score += 10;
                evidence.Add("The browser has direct CEF runtime evidence.");
            }

            score = Math.Min(score, 100);
            if (score < MinimumReportedHostAssociationScore)
            {
                continue;
            }

            CefAssociationConfidence confidence = GetConfidence(score);
            results.Add(new CefHostAssociation(
                host.Process.ProcessId,
                browser.Process.ProcessId,
                score,
                confidence,
                confidence == CefAssociationConfidence.High
                    && (referencesHostProcessId || referencesHostExecutable),
                evidence));
        }

        return results
            .OrderBy(association => association.HostProcessId)
            .ThenBy(association => association.BrowserProcessId)
            .ToList();
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
        bool parentProcessIdMatches = browser.Process.ProcessId
            == subprocess.Process.ParentProcessId;
        bool validatedParent = parentProcessIdMatches
            && HasValidatedParentGeneration(browser.Process, subprocess.Process);
        if (validatedParent)
        {
            score += 40;
            evidence.Add("Generation-safe parent process relationship.");
        }
        else if (parentProcessIdMatches)
        {
            score += 20;
            evidence.Add(
                "Parent process ID matches, but creation times do not validate the generation.");
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

        foreach (IGrouping<int, CefProcessAssociation> group in associations.GroupBy(
            association => association.BrowserProcessId))
        {
            Candidate browser = byProcessId[group.Key];
            Candidate[] chromiumSubprocesses = group
                .Select(association => byProcessId[association.SubprocessProcessId])
                .Where(candidate => candidate.Role != CefProcessRole.Crashpad)
                .ToArray();
            CefDeploymentLayout layout = ClassifyConfiguredBrowserLayout(browser)
                ?? (chromiumSubprocesses.Length == 0
                    ? CefDeploymentLayout.Unknown
                    : chromiumSubprocesses.All(subprocess => PathsEqual(
                        browser.Process.ExecutablePath,
                        subprocess.Process.ExecutablePath))
                            ? CefDeploymentLayout.SameExecutable
                            : CefDeploymentLayout.SeparateSubprocess);

            layouts[browser.Process.ProcessId] = layout;
            foreach (CefProcessAssociation association in group)
            {
                layouts[association.SubprocessProcessId] = layout;
            }
        }

        foreach (Candidate browser in candidates.Where(
            candidate => candidate.Role == CefProcessRole.Browser
                && !layouts.ContainsKey(candidate.Process.ProcessId)))
        {
            if (ClassifyConfiguredBrowserLayout(browser) is
                CefDeploymentLayout layout)
            {
                layouts[browser.Process.ProcessId] = layout;
            }
        }

        return layouts;
    }

    private static CefDeploymentLayout? ClassifyConfiguredBrowserLayout(
        Candidate browser)
    {
        string executableName = Path.GetFileName(
            browser.Process.ExecutablePath ?? browser.Process.ImageName);
        if (executableName.Equals("bootstrap.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals(
                "bootstrapc.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return CefDeploymentLayout.BootstrapOrDllHosted;
        }

        return string.IsNullOrWhiteSpace(browser.RuntimePaths.BrowserSubprocessPath)
            ? null
            : CefDeploymentLayout.SeparateSubprocess;
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

        if (text.Contains("JCEF", StringComparison.OrdinalIgnoreCase))
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

    private static bool HasValidatedParentGeneration(
        ProcessSnapshotEntry possibleParent,
        ProcessSnapshotEntry child)
    {
        return possibleParent.CreationTime is not null
            && child.CreationTime is not null
            && possibleParent.CreationTime <= child.CreationTime;
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

    private static string? GetFilePathSwitchValue(
        ChromiumCommandLine commandLine,
        string switchName)
    {
        string? value = commandLine.GetSwitchValue(switchName);
        return !string.IsNullOrWhiteSpace(value)
            && !value.All(char.IsAsciiDigit)
                ? value
                : null;
    }

    private static bool CommandLineReferencesValue(
        string? commandLine,
        string expectedValue)
    {
        ChromiumCommandLine parsed = ChromiumCommandLine.Parse(commandLine);
        return parsed.Arguments
            .Skip(1)
            .Select(GetArgumentValue)
            .Any(value => string.Equals(
                value,
                expectedValue,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool CommandLineReferencesPath(
        string? commandLine,
        string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        ChromiumCommandLine parsed = ChromiumCommandLine.Parse(commandLine);
        return parsed.Arguments
            .Skip(1)
            .Select(GetArgumentValue)
            .Any(value => PathsEqual(value, expectedPath));
    }

    private static string GetArgumentValue(string argument)
    {
        int separator = argument.IndexOf('=');
        return separator < 0 ? argument : argument[(separator + 1)..];
    }

    private static CefAssociationConfidence GetConfidence(int score)
    {
        return score switch
        {
            >= HighConfidenceScore => CefAssociationConfidence.High,
            >= MediumConfidenceScore => CefAssociationConfidence.Medium,
            _ => CefAssociationConfidence.Low,
        };
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
                Evidence,
                Process.ModuleInspectionError);
        }
    }

    private sealed record AssociationScore(
        int Score,
        bool ValidatedParent,
        bool MatchedConfiguredSubprocess,
        IReadOnlyList<string> Evidence);
}
