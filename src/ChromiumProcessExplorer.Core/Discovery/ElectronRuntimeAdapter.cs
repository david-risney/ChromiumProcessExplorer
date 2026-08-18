using System.Text.Json;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Detects packaged and development Electron applications and derives
/// confidence-scored process relationships.
/// </summary>
public static class ElectronRuntimeAdapter
{
    private const int MaximumPackageJsonLength = 1024 * 1024;
    private const int MinimumAssociationScore = 50;

    private static readonly HashSet<string> NodeScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".cjs",
            ".js",
            ".mjs",
        };

    /// <summary>Analyzes passive process evidence and optional app-side data.</summary>
    public static ElectronRuntimeAnalysis Analyze(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        IReadOnlyList<ElectronCooperativeProcessInfo>? cooperativeProcesses = null)
    {
        ArgumentNullException.ThrowIfNull(processes);

        Dictionary<ProcessIdentity, ElectronCooperativeProcessInfo> cooperativeByIdentity =
            (cooperativeProcesses ?? []).ToDictionary(item => item.Identity);
        Candidate[] candidates = processes
            .Select(process => CreateCandidate(
                process,
                cooperativeByIdentity.GetValueOrDefault(
                    new ProcessIdentity(process.ProcessId, process.CreationTime))))
            .ToArray();
        Candidate[] mains = candidates.Where(candidate => candidate.IsMain).ToArray();
        List<ElectronProcessAssociation> associations = [];

        foreach (Candidate child in candidates.Where(candidate => !candidate.IsMain))
        {
            (Candidate Main, AssociationScore Score)? best = null;
            foreach (Candidate main in mains)
            {
                AssociationScore score = ScoreAssociation(main, child);
                if (best is null || score.Score > best.Value.Score.Score)
                {
                    best = (main, score);
                }
            }

            if (best is null || best.Value.Score.Score < MinimumAssociationScore)
            {
                continue;
            }

            ProcessRelationshipConfidence confidence = best.Value.Score.Score >= 75
                ? ProcessRelationshipConfidence.High
                : ProcessRelationshipConfidence.Medium;
            associations.Add(new ElectronProcessAssociation(
                best.Value.Main.Process.ProcessId,
                child.Process.ProcessId,
                best.Value.Score.Score,
                confidence,
                confidence == ProcessRelationshipConfidence.High
                    && best.Value.Score.ValidatedParent,
                best.Value.Score.Evidence));
        }

        Dictionary<int, ElectronRuntimePaths> enrichedMainPaths = mains.ToDictionary(
            main => main.Process.ProcessId,
            main => EnrichMainPaths(main, candidates, associations));
        HashSet<int> included = mains
            .Select(main => main.Process.ProcessId)
            .Concat(associations.Select(association => association.ChildProcessId))
            .ToHashSet();
        ElectronProcessInfo[] processInfos = candidates
            .Where(candidate => included.Contains(candidate.Process.ProcessId))
            .Select(candidate => candidate.ToProcessInfo(
                enrichedMainPaths.GetValueOrDefault(
                    candidate.Process.ProcessId,
                    candidate.Paths)))
            .OrderBy(info => info.ProcessId)
            .ToArray();

        return new ElectronRuntimeAnalysis(
            processInfos,
            associations
                .OrderBy(association => association.MainProcessId)
                .ThenBy(association => association.ChildProcessId)
                .ToArray(),
            []);
    }

    private static Candidate CreateCandidate(
        ProcessSnapshotEntry process,
        ElectronCooperativeProcessInfo? cooperative)
    {
        ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(process.CommandLine);
        string? rawType = commandLine.GetSwitchValue("type");
        string? utilitySubType = commandLine.GetSwitchValue("utility-sub-type");
        string? windowType = commandLine.GetSwitchValue("window-type");
        PackagingInfo packaging = InspectPackaging(process, commandLine);
        bool isNodeHelper = IsNodeHelper(process, commandLine);
        bool hasExplicitType = commandLine.HasSwitch("type");
        bool isMain = !hasExplicitType
            && !isNodeHelper
            && process.CommandLine is not null
            && packaging.HasElectronEvidence;
        ElectronProcessRole role = cooperative?.Role
            ?? ClassifyRole(rawType, windowType, isNodeHelper, isMain);
        List<ElectronEvidence> evidence = [.. packaging.Evidence];

        if (hasExplicitType)
        {
            evidence.Add(new ElectronEvidence(
                "command-line-switch",
                "Observed Chromium process type.",
                rawType));
        }

        if (utilitySubType is not null)
        {
            evidence.Add(new ElectronEvidence(
                "command-line-switch",
                "Observed utility subtype.",
                utilitySubType));
        }

        if (windowType is not null)
        {
            evidence.Add(new ElectronEvidence(
                "command-line-switch",
                "Observed Electron window type.",
                windowType));
        }

        if (isNodeHelper)
        {
            evidence.Add(new ElectronEvidence(
                "process-environment-evidence",
                "The process is marked as an ELECTRON_RUN_AS_NODE helper."));
        }

        if (cooperative is not null)
        {
            evidence.Add(new ElectronEvidence(
                "cooperative-electron-api",
                $"Electron app-side process data classified this process as {cooperative.Role}.",
                cooperative.Source));
            if (cooperative.ServiceName is not null)
            {
                evidence.Add(new ElectronEvidence(
                    "cooperative-electron-api",
                    "Electron reported a process service name.",
                    cooperative.ServiceName));
            }

            if (cooperative.WebContentsType is not null)
            {
                evidence.Add(new ElectronEvidence(
                    "cooperative-electron-api",
                    "Electron reported a webContents type.",
                    cooperative.WebContentsType));
            }
        }

        ElectronRuntimePaths paths = CreatePaths(process, commandLine, packaging);
        return new Candidate(
            process,
            isMain,
            role,
            rawType,
            utilitySubType,
            windowType,
            paths,
            packaging.PackageIdentity,
            packaging.PackageName,
            packaging.PackageVersion,
            cooperative is not null,
            evidence);
    }

    private static PackagingInfo InspectPackaging(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine)
    {
        string? executablePath = process.ExecutablePath;
        string? installDirectory = executablePath is null
            ? null
            : Path.GetDirectoryName(executablePath);
        string? resourcesDirectory = installDirectory is null
            ? null
            : Path.Combine(installDirectory, "resources");
        string? appPath = commandLine.GetSwitchValue("app-path");
        if (appPath is null && resourcesDirectory is not null)
        {
            string archive = Path.Combine(resourcesDirectory, "app.asar");
            string looseApplication = Path.Combine(resourcesDirectory, "app");
            if (File.Exists(archive))
            {
                appPath = archive;
            }
            else if (Directory.Exists(looseApplication))
            {
                appPath = looseApplication;
            }
        }

        string executableName = Path.GetFileName(
            executablePath ?? process.ImageName);
        if (appPath is null
            && executableName.Equals("electron.exe", StringComparison.OrdinalIgnoreCase))
        {
            appPath = commandLine.Arguments
                .Skip(1)
                .FirstOrDefault(argument =>
                    !argument.StartsWith('-'));
        }

        List<ElectronEvidence> evidence = [];
        if (executableName.Equals("electron.exe", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new ElectronEvidence(
                "runtime-executable",
                "The executable uses Electron's development runtime name.",
                executablePath ?? process.ImageName));
        }

        if (appPath is not null)
        {
            string source = commandLine.HasSwitch("app-path")
                ? "command-line-switch"
                : "filesystem-marker";
            evidence.Add(new ElectronEvidence(
                source,
                Path.GetExtension(appPath).Equals(
                    ".asar",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Found an Electron ASAR application."
                    : "Found an Electron application directory.",
                appPath));
        }

        bool hasElectronEvidence = evidence.Count > 0;
        string? packageJson = GetPackageJsonPath(appPath);
        (string? packageName, string? packageVersion) = ReadPackageMetadata(
            packageJson);
        ElectronPackageIdentity? packageIdentity = ParsePackageIdentity(
            executablePath);
        if (packageIdentity is not null)
        {
            evidence.Add(new ElectronEvidence(
                "windows-package-path",
                "The executable is inside a WindowsApps package.",
                packageIdentity.PackageFullName));
        }

        return new PackagingInfo(
            hasElectronEvidence,
            installDirectory,
            resourcesDirectory,
            appPath,
            packageJson,
            packageIdentity,
            packageName,
            packageVersion,
            evidence);
    }

    private static ElectronRuntimePaths CreatePaths(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine,
        PackagingInfo packaging)
    {
        ElectronPathObservation? install = ObserveExisting(
            packaging.InstallDirectory,
            "executable-path",
            ProcessRelationshipConfidence.High);
        ElectronPathObservation? packageRoot = ObserveExisting(
            GetPackageRoot(process.ExecutablePath, packaging.PackageIdentity),
            "windows-package-path",
            ProcessRelationshipConfidence.High);
        ElectronPathObservation? resources = ObserveExisting(
            packaging.ResourcesDirectory,
            "packaged-layout",
            ProcessRelationshipConfidence.High);
        ElectronPathObservation? application = ObserveExisting(
            packaging.ApplicationPath,
            commandLine.HasSwitch("app-path")
                ? "command-line-switch"
                : "packaged-layout",
            ProcessRelationshipConfidence.High);
        string? unpackedPath = packaging.ResourcesDirectory is null
            ? null
            : packaging.ApplicationPath?.EndsWith(
                ".asar",
                StringComparison.OrdinalIgnoreCase) == true
                ? Path.Combine(packaging.ResourcesDirectory, "app.asar.unpacked")
                : Path.Combine(packaging.ResourcesDirectory, "app");
        ElectronPathObservation? unpacked = ObserveExisting(
            unpackedPath,
            "packaged-layout",
            ProcessRelationshipConfidence.Medium);
        ElectronPathObservation? packageJson = ObserveExisting(
            packaging.PackageJson,
            "package-metadata",
            ProcessRelationshipConfidence.High);
        string? userDataValue = commandLine.GetSwitchValue("user-data-dir")
            ?? process.UserDataDirectory;
        ElectronPathObservation? userData = Observe(
            userDataValue,
            "command-line-switch",
            ProcessRelationshipConfidence.High);
        ElectronPathObservation? sessionData = userData is null
            ? null
            : Observe(
                userData.Value,
                "electron-default",
                ProcessRelationshipConfidence.Low);
        ElectronPathObservation? logs = userData is null
            ? null
            : Observe(
                Path.Combine(userData.Value, "logs"),
                "electron-default",
                ProcessRelationshipConfidence.Low);
        string? crashPath = commandLine.GetSwitchValue("database")
            ?? (userData is null ? null : Path.Combine(userData.Value, "Crashpad"));
        ElectronPathObservation? crash = Observe(
            crashPath,
            commandLine.HasSwitch("database")
                ? "command-line-switch"
                : "electron-default",
            commandLine.HasSwitch("database")
                ? ProcessRelationshipConfidence.High
                : ProcessRelationshipConfidence.Low);

        return new ElectronRuntimePaths(
            install,
            packageRoot,
            resources,
            application,
            unpacked,
            packageJson,
            userData,
            sessionData,
            logs,
            crash,
            null);
    }

    private static ElectronRuntimePaths EnrichMainPaths(
        Candidate main,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<ElectronProcessAssociation> associations)
    {
        if (main.Paths.UserDataDirectory is not null)
        {
            return main.Paths;
        }

        int[] children = associations
            .Where(association => association.MainProcessId
                == main.Process.ProcessId)
            .Select(association => association.ChildProcessId)
            .ToArray();
        ElectronPathObservation? userData = candidates
            .Where(candidate => children.Contains(candidate.Process.ProcessId))
            .Select(candidate => candidate.Paths.UserDataDirectory)
            .Where(path => path is not null)
            .GroupBy(path => path!.Value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .FirstOrDefault();
        if (userData is null)
        {
            return main.Paths;
        }

        return main.Paths with
        {
            UserDataDirectory = userData with
            {
                Source = "associated-electron-children",
                Confidence = ProcessRelationshipConfidence.Medium,
            },
            SessionDataDirectory = Observe(
                userData.Value,
                "electron-default",
                ProcessRelationshipConfidence.Low),
            LogsDirectory = Observe(
                Path.Combine(userData.Value, "logs"),
                "electron-default",
                ProcessRelationshipConfidence.Low),
            CrashDumpsDirectory = Observe(
                Path.Combine(userData.Value, "Crashpad"),
                "electron-default",
                ProcessRelationshipConfidence.Low),
        };
    }

    private static AssociationScore ScoreAssociation(
        Candidate main,
        Candidate child)
    {
        int score = 0;
        bool parentMatches = main.Process.ProcessId
            == child.Process.ParentProcessId;
        bool validatedParent = parentMatches
            && HasValidatedParentGeneration(main.Process, child.Process);
        List<ElectronEvidence> evidence = [];
        if (validatedParent)
        {
            score += 50;
            evidence.Add(new ElectronEvidence(
                "process-snapshot",
                "Generation-safe parent process relationship."));
        }
        else if (parentMatches)
        {
            score += 20;
            evidence.Add(new ElectronEvidence(
                "process-snapshot",
                "Parent PID matches, but process generations were not validated."));
        }

        if (PathsEqual(main.Process.ExecutablePath, child.Process.ExecutablePath))
        {
            score += 25;
            evidence.Add(new ElectronEvidence(
                "executable-path",
                "Uses the same packaged Electron executable.",
                child.Process.ExecutablePath));
        }

        if (PathsEqual(
            main.Paths.ApplicationPath?.Value,
            child.Paths.ApplicationPath?.Value))
        {
            score += 15;
            evidence.Add(new ElectronEvidence(
                "application-path",
                "Uses the same Electron application path.",
                child.Paths.ApplicationPath?.Value));
        }

        if (PathsEqual(
            main.Paths.UserDataDirectory?.Value,
            child.Paths.UserDataDirectory?.Value))
        {
            score += 10;
            evidence.Add(new ElectronEvidence(
                "user-data-directory",
                "Uses the same explicit user data directory.",
                child.Paths.UserDataDirectory?.Value));
        }

        if (HasStartupProximity(main.Process, child.Process))
        {
            score += 5;
            evidence.Add(new ElectronEvidence(
                "process-snapshot",
                "Started within 30 seconds of the Electron main process."));
        }

        if (child.HasCooperativeEvidence)
        {
            score += 10;
            evidence.Add(new ElectronEvidence(
                "cooperative-electron-api",
                "App-side Electron process data confirms membership."));
        }

        return new AssociationScore(
            Math.Min(score, 100),
            validatedParent,
            evidence);
    }

    private static ElectronProcessRole ClassifyRole(
        string? rawType,
        string? windowType,
        bool isNodeHelper,
        bool isMain)
    {
        if (isNodeHelper)
        {
            return ElectronProcessRole.NodeHelper;
        }

        if (isMain)
        {
            return ElectronProcessRole.Main;
        }

        if (windowType?.Contains(
            "devtools",
            StringComparison.OrdinalIgnoreCase) == true)
        {
            return ElectronProcessRole.DevTools;
        }

        return rawType?.ToLowerInvariant() switch
        {
            "renderer" => ElectronProcessRole.Renderer,
            "gpu-process" => ElectronProcessRole.Gpu,
            "utility" => ElectronProcessRole.Utility,
            "worker" => ElectronProcessRole.Worker,
            "service-worker" => ElectronProcessRole.ServiceWorker,
            "crashpad-handler" => ElectronProcessRole.Crashpad,
            _ => ElectronProcessRole.Other,
        };
    }

    private static bool IsNodeHelper(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine)
    {
        return process.Evidence.Any(item => item.Contains(
                "ELECTRON_RUN_AS_NODE",
                StringComparison.OrdinalIgnoreCase))
            || commandLine.Arguments.Any(argument => argument.Equals(
                "ELECTRON_RUN_AS_NODE",
                StringComparison.OrdinalIgnoreCase))
            || commandLine.Arguments
                .Skip(1)
                .Where(argument => !argument.StartsWith('-'))
                .Any(argument => NodeScriptExtensions.Contains(
                    Path.GetExtension(argument)));
    }

    private static ElectronPathObservation? ObserveExisting(
        string? value,
        string source,
        ProcessRelationshipConfidence confidence)
    {
        return value is null
            ? null
            : new ElectronPathObservation(
                value,
                source,
                confidence,
                File.Exists(value) || Directory.Exists(value));
    }

    private static ElectronPathObservation? Observe(
        string? value,
        string source,
        ProcessRelationshipConfidence confidence)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : new ElectronPathObservation(
                value,
                source,
                confidence,
                File.Exists(value) || Directory.Exists(value));
    }

    private static string? GetPackageJsonPath(string? applicationPath)
    {
        if (applicationPath is null
            || applicationPath.EndsWith(".asar", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string candidate = Path.Combine(applicationPath, "package.json");
        return File.Exists(candidate) ? candidate : null;
    }

    private static (string? Name, string? Version) ReadPackageMetadata(
        string? packageJsonPath)
    {
        if (packageJsonPath is null)
        {
            return (null, null);
        }

        try
        {
            FileInfo info = new(packageJsonPath);
            if (info.Length > MaximumPackageJsonLength)
            {
                return (null, null);
            }

            using FileStream stream = File.OpenRead(packageJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            string? name = root.TryGetProperty("productName", out JsonElement productName)
                ? productName.GetString()
                : root.TryGetProperty("name", out JsonElement packageName)
                    ? packageName.GetString()
                    : null;
            string? version = root.TryGetProperty("version", out JsonElement packageVersion)
                ? packageVersion.GetString()
                : null;
            return (name, version);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return (null, null);
        }
    }

    private static ElectronPackageIdentity? ParsePackageIdentity(
        string? executablePath)
    {
        if (executablePath is null)
        {
            return null;
        }

        string[] segments = executablePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        int windowsAppsIndex = Array.FindIndex(
            segments,
            segment => segment.Equals(
                "WindowsApps",
                StringComparison.OrdinalIgnoreCase));
        if (windowsAppsIndex < 0 || windowsAppsIndex + 1 >= segments.Length)
        {
            return null;
        }

        string packageFullName = segments[windowsAppsIndex + 1];
        string[] parts = packageFullName.Split('_');
        return new ElectronPackageIdentity(
            packageFullName,
            parts.ElementAtOrDefault(0) ?? packageFullName,
            parts.ElementAtOrDefault(1),
            parts.ElementAtOrDefault(2),
            parts.ElementAtOrDefault(4));
    }

    private static string? GetPackageRoot(
        string? executablePath,
        ElectronPackageIdentity? identity)
    {
        if (executablePath is null || identity is null)
        {
            return null;
        }

        int index = executablePath.IndexOf(
            identity.PackageFullName,
            StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? null
            : executablePath[..(index + identity.PackageFullName.Length)];
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

    private static bool HasStartupProximity(
        ProcessSnapshotEntry first,
        ProcessSnapshotEntry second)
    {
        return first.CreationTime is not null
            && second.CreationTime is not null
            && (first.CreationTime.Value - second.CreationTime.Value).Duration()
                <= TimeSpan.FromSeconds(30);
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)
            || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record PackagingInfo(
        bool HasElectronEvidence,
        string? InstallDirectory,
        string? ResourcesDirectory,
        string? ApplicationPath,
        string? PackageJson,
        ElectronPackageIdentity? PackageIdentity,
        string? PackageName,
        string? PackageVersion,
        IReadOnlyList<ElectronEvidence> Evidence);

    private sealed record Candidate(
        ProcessSnapshotEntry Process,
        bool IsMain,
        ElectronProcessRole Role,
        string? RawType,
        string? UtilitySubType,
        string? WindowType,
        ElectronRuntimePaths Paths,
        ElectronPackageIdentity? PackageIdentity,
        string? PackageName,
        string? PackageVersion,
        bool HasCooperativeEvidence,
        IReadOnlyList<ElectronEvidence> Evidence)
    {
        public ElectronProcessInfo ToProcessInfo(ElectronRuntimePaths paths)
        {
            return new ElectronProcessInfo(
                Process.ProcessId,
                Role,
                RawType,
                UtilitySubType,
                WindowType,
                paths,
                PackageIdentity,
                PackageName,
                PackageVersion,
                HasCooperativeEvidence,
                Evidence);
        }
    }

    private sealed record AssociationScore(
        int Score,
        bool ValidatedParent,
        IReadOnlyList<ElectronEvidence> Evidence);
}
