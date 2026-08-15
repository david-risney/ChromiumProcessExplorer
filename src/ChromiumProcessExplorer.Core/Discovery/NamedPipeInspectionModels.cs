namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>A discovered connection for one named-pipe instance.</summary>
public sealed record NamedPipeConnection(
    int HandleOwnerProcessId,
    string? HandleOwnerImageName,
    int? ServerProcessId,
    string? ServerImageName,
    int? ClientProcessId,
    string? ClientImageName,
    string? LocalEnd,
    string? State);

/// <summary>A Mojo pipe name and any endpoint connections discovered for it.</summary>
public sealed record MojoPipeInfo(
    string Name,
    int? ProcessIdHint,
    IReadOnlyList<NamedPipeConnection> Connections);

/// <summary>Statistics for one bounded handle-inspection scan.</summary>
public sealed record NamedPipeInspectionStatistics(
    int CandidatePipeCount,
    int RelevantProcessCount,
    int FileHandleCount,
    int UniqueFileObjectCount,
    int QueriedHandleCount,
    int PipeHandleCount,
    int MatchedMojoHandleCount,
    int TimedOutQueryCount,
    int WorkerRestartCount,
    TimeSpan Elapsed);

/// <summary>Describes a foreign handle query that exceeded its deadline.</summary>
public sealed record TimedOutHandleQuery(
    int OwnerProcessId,
    string? OwnerImageName,
    ulong HandleValue,
    uint GrantedAccess,
    string QueryStage,
    TimeSpan Elapsed);

/// <summary>The endpoint-enriched result of Mojo pipe discovery.</summary>
public sealed record MojoPipeInspectionResult(
    DateTimeOffset CapturedAt,
    IReadOnlyList<MojoPipeInfo> Pipes,
    NamedPipeInspectionStatistics Statistics,
    IReadOnlyList<TimedOutHandleQuery> TimedOutQueries,
    IReadOnlyList<DiscoveryIssue> Issues);

/// <summary>Configures the isolated process used for foreign-handle queries.</summary>
public sealed record HandleQueryWorkerOptions(
    string ExecutablePath,
    int MaximumWorkers = 0,
    TimeSpan? QueryTimeout = null)
{
    /// <summary>Gets the effective bounded worker count.</summary>
    public int EffectiveMaximumWorkers => MaximumWorkers > 0
        ? MaximumWorkers
        : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Gets the effective per-handle deadline.</summary>
    public TimeSpan EffectiveQueryTimeout
    {
        get
        {
            TimeSpan timeout = QueryTimeout ?? TimeSpan.FromMilliseconds(500);
            if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(QueryTimeout),
                    "The handle query timeout must be a finite positive duration.");
            }

            return timeout;
        }
    }
}
