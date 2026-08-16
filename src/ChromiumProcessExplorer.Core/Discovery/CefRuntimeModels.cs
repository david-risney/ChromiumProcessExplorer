namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>CEF process roles derived from documented Chromium switches.</summary>
public enum CefProcessRole
{
    /// <summary>The CEF browser and application host process.</summary>
    Browser,

    /// <summary>A renderer process.</summary>
    Renderer,

    /// <summary>A GPU process.</summary>
    Gpu,

    /// <summary>A utility process.</summary>
    Utility,

    /// <summary>A Crashpad handler process.</summary>
    Crashpad,

    /// <summary>A recognized CEF process with another Chromium type.</summary>
    Other,
}

/// <summary>Known CEF deployment layouts.</summary>
public enum CefDeploymentLayout
{
    /// <summary>The browser executable is also used for subprocesses.</summary>
    SameExecutable,

    /// <summary>A distinct browser subprocess executable is configured.</summary>
    SeparateSubprocess,

    /// <summary>A CEF bootstrap executable or DLL-hosted layout is present.</summary>
    BootstrapOrDllHosted,

    /// <summary>The available evidence does not identify the layout.</summary>
    Unknown,
}

/// <summary>Confidence assigned to an inferred CEF process association.</summary>
public enum CefAssociationConfidence
{
    /// <summary>A weak association supported by limited evidence.</summary>
    Low,

    /// <summary>An association supported by multiple corroborating signals.</summary>
    Medium,

    /// <summary>A strong association supported by direct and corroborating signals.</summary>
    High,
}

/// <summary>Observed evidence supporting CEF classification.</summary>
public sealed record CefEvidence(
    string Source,
    string Detail,
    string? Path = null);

/// <summary>Explicit CEF and Chromium runtime paths observed for a process.</summary>
public sealed record CefRuntimePaths(
    string? UserDataDirectory,
    string? LogFile,
    string? ResourcesDirectory,
    string? LocalesDirectory,
    string? BrowserSubprocessPath,
    string? CrashReportDirectory,
    string? CrashReportConfigurationFile,
    string? DevToolsActivePortFile);

/// <summary>A risky or diagnostic Chromium switch observed on a CEF process.</summary>
public sealed record CefSwitchWarning(
    string Switch,
    string Category,
    string Detail);

/// <summary>CEF-specific information derived for a captured process.</summary>
public sealed record CefProcessInfo(
    int ProcessId,
    CefProcessRole Role,
    string? RawProcessType,
    string? UtilityRole,
    string? UtilitySubType,
    CefDeploymentLayout Layout,
    CefRuntimePaths RuntimePaths,
    string? RemoteDebuggingPort,
    bool RemoteDebuggingPipe,
    IReadOnlyList<string> Wrappers,
    IReadOnlyList<CefSwitchWarning> SwitchWarnings,
    IReadOnlyList<CefEvidence> Evidence,
    string? ModuleInspectionError);

/// <summary>
/// A scored association between a CEF browser process and one subprocess.
/// </summary>
public sealed record CefProcessAssociation(
    int BrowserProcessId,
    int SubprocessProcessId,
    int Score,
    CefAssociationConfidence Confidence,
    bool IsAuthoritative,
    IReadOnlyList<string> Evidence);

/// <summary>A scored association between an application host and a CEF browser.</summary>
public sealed record CefHostAssociation(
    int HostProcessId,
    int BrowserProcessId,
    int Score,
    CefAssociationConfidence Confidence,
    bool IsAuthoritative,
    IReadOnlyList<string> Evidence);

/// <summary>CEF-specific analysis for a process snapshot.</summary>
public sealed record CefRuntimeAnalysis(
    IReadOnlyList<CefProcessInfo> Processes,
    IReadOnlyList<CefProcessAssociation> Associations,
    IReadOnlyList<CefHostAssociation> HostAssociations)
{
    /// <summary>Gets an empty CEF analysis.</summary>
    public static CefRuntimeAnalysis Empty { get; } = new([], [], []);
}
