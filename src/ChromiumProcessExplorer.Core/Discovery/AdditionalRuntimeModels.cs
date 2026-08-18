using System.Text.Json.Serialization;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Stable runtime-family identifiers shared by Core consumers.</summary>
public static class RuntimePlatformIds
{
    /// <summary>Qt WebEngine runtime family.</summary>
    public const string QtWebEngine = "qt-webengine";

    /// <summary>NW.js runtime family.</summary>
    public const string Nwjs = "nwjs";

    /// <summary>Browser-managed installed app or PWA.</summary>
    public const string BrowserPwa = "browser-pwa";

    /// <summary>Corroborated Chromium embedder with no stronger family match.</summary>
    public const string ChromiumGeneric = "chromium-generic";

    /// <summary>Chromium Embedded Framework runtime family.</summary>
    public const string Cef = "cef";

    /// <summary>Microsoft WebView2 runtime family.</summary>
    public const string WebView2 = "webview2";

    /// <summary>Electron runtime family.</summary>
    public const string Electron = "electron";
}

/// <summary>Normalized roles used by additional Chromium runtime adapters.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AdditionalRuntimeProcessRole>))]
public enum AdditionalRuntimeProcessRole
{
    /// <summary>Native application process hosting an embedded runtime.</summary>
    Host,

    /// <summary>Browser or main process.</summary>
    Browser,

    /// <summary>Renderer process.</summary>
    Renderer,

    /// <summary>GPU process.</summary>
    Gpu,

    /// <summary>Utility process.</summary>
    Utility,

    /// <summary>Crash handler process.</summary>
    CrashHandler,

    /// <summary>Another runtime-associated process.</summary>
    Other,
}

/// <summary>One evidence item supporting a runtime-family classification.</summary>
public sealed record AdditionalRuntimeEvidence(
    string Source,
    string Detail,
    string? RawValue = null);

/// <summary>A process classified by the cross-platform runtime adapter.</summary>
public sealed record AdditionalRuntimeProcessInfo(
    int ProcessId,
    string PlatformId,
    AdditionalRuntimeProcessRole Role,
    ProcessRelationshipConfidence Confidence,
    bool IsBrowserManaged,
    IReadOnlyList<string> Annotations,
    IReadOnlyList<AdditionalRuntimeEvidence> Evidence);

/// <summary>A confidence-scored host/main to Chromium subprocess relationship.</summary>
public sealed record AdditionalRuntimeAssociation(
    int SourceProcessId,
    int TargetProcessId,
    string PlatformId,
    int Score,
    ProcessRelationshipConfidence Confidence,
    IReadOnlyList<AdditionalRuntimeEvidence> Evidence);

/// <summary>A process explicitly excluded from Chromium runtime fallback.</summary>
public sealed record RuntimeExclusion(
    int ProcessId,
    string PlatformId,
    IReadOnlyList<AdditionalRuntimeEvidence> Evidence);

/// <summary>First-class Qt WebEngine, NW.js, PWA, and generic analysis.</summary>
public sealed record AdditionalRuntimeAnalysis(
    IReadOnlyList<AdditionalRuntimeProcessInfo> Processes,
    IReadOnlyList<AdditionalRuntimeAssociation> Associations,
    IReadOnlyList<RuntimeExclusion> Exclusions,
    IReadOnlyList<DiscoveryIssue> Issues)
{
    /// <summary>Gets an empty analysis.</summary>
    public static AdditionalRuntimeAnalysis Empty { get; } =
        new([], [], [], []);
}
