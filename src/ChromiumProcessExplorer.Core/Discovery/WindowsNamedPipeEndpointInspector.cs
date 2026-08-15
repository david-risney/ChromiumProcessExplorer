using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

internal static partial class WindowsNamedPipeEndpointInspector
{
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x00000002;

    public static async ValueTask<MojoPipeInspectionResult> InspectAsync(
        IReadOnlyList<MojoPipeCandidate> candidates,
        IReadOnlyList<ProcessSnapshotEntry> processes,
        HandleQueryWorkerOptions workerOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerOptions.ExecutablePath);
        _ = workerOptions.EffectiveQueryTimeout;

        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        ConcurrentBag<DiscoveryIssue> issues = [];
        ConcurrentBag<TimedOutHandleQuery> timedOutQueries = [];
        Dictionary<int, ProcessSnapshotEntry> processById =
            processes.ToDictionary(process => process.ProcessId);
        HashSet<int> seedProcessIds = processes
            .Where(process => process.IsLikelyChromium)
            .Select(process => process.ProcessId)
            .Concat(candidates
                .Where(candidate => candidate.ProcessIdHint is not null)
                .Select(candidate => candidate.ProcessIdHint!.Value))
            .ToHashSet();
        HashSet<int> relevantProcessIds = seedProcessIds;

        SystemFileHandleSnapshot handleSnapshot;
        try
        {
            handleSnapshot =
                WindowsSystemHandleSnapshotter.CaptureFileHandles(relevantProcessIds);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            issues.Add(new DiscoveryIssue("handle-snapshot", exception.Message));
            return CreateResult(
                capturedAt,
                candidates,
                new ConcurrentDictionary<
                    string,
                    ConcurrentDictionary<string, NamedPipeConnection>>(
                        StringComparer.OrdinalIgnoreCase),
                relevantProcessIds.Count,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                stopwatch.Elapsed,
                timedOutQueries,
                issues);
        }

        Dictionary<string, MojoPipeCandidate> candidateByName = candidates
            .DistinctBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                candidate => candidate.Name,
                StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, ConcurrentDictionary<string, NamedPipeConnection>>
            connectionsByPipe = new(StringComparer.OrdinalIgnoreCase);
        IGrouping<int, SystemHandleEntry>[] ownerGroups = handleSnapshot.UniqueHandles
            .GroupBy(handle => handle.OwnerProcessId)
            .OrderBy(group => group.Key)
            .ToArray();

        int nextGroup = -1;
        int queriedHandleCount = 0;
        int pipeHandleCount = 0;
        int matchedMojoHandleCount = 0;
        int timedOutQueryCount = 0;
        int workerRestartCount = 0;
        int workerCount = Math.Min(
            workerOptions.EffectiveMaximumWorkers,
            Math.Max(1, ownerGroups.Length));

        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => RunWorkerLoopAsync())
            .ToArray();
        await Task.WhenAll(workers);

        if (timedOutQueryCount > 0)
        {
            issues.Add(new DiscoveryIssue(
                "handle-query",
                $"{timedOutQueryCount} handle queries timed out; their helper processes "
                + "were terminated and replaced."));
        }

        return CreateResult(
            capturedAt,
            candidates,
            connectionsByPipe,
            relevantProcessIds.Count,
            handleSnapshot.FileHandleCount,
            handleSnapshot.UniqueHandles.Count,
            queriedHandleCount,
            pipeHandleCount,
            matchedMojoHandleCount,
            timedOutQueryCount,
            workerRestartCount,
            stopwatch.Elapsed,
            timedOutQueries,
            issues);

        async Task RunWorkerLoopAsync()
        {
            await using HandleQueryProcess worker = new(
                workerOptions.ExecutablePath,
                workerOptions.EffectiveQueryTimeout);

            try
            {
                await worker.StartAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or TimeoutException
                    or Win32Exception)
            {
                issues.Add(new DiscoveryIssue("handle-worker", exception.Message));
                return;
            }

            while (true)
            {
                int groupIndex = Interlocked.Increment(ref nextGroup);
                if (groupIndex >= ownerGroups.Length)
                {
                    return;
                }

                IGrouping<int, SystemHandleEntry> group = ownerGroups[groupIndex];
                using SafeFileHandle sourceProcess = NativeMethods.OpenProcess(
                    ProcessDuplicateHandle,
                    false,
                    group.Key);

                if (sourceProcess.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    issues.Add(new DiscoveryIssue(
                        "duplicate-handle",
                        new Win32Exception(error).Message,
                        group.Key,
                        error));
                    continue;
                }

                foreach (SystemHandleEntry handle in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!NativeMethods.DuplicateHandle(
                        sourceProcess,
                        unchecked((nint)handle.HandleValue),
                        worker.ProcessHandle,
                        out nint workerHandle,
                        0,
                        false,
                        DuplicateSameAccess))
                    {
                        continue;
                    }

                    Interlocked.Increment(ref queriedHandleCount);
                    HandleQueryWorker.HandleQueryResponse? response;
                    try
                    {
                        response = await worker.QueryAsync(workerHandle, cancellationToken);
                    }
                    catch (HandleQueryTimeoutException exception)
                    {
                        Interlocked.Increment(ref timedOutQueryCount);
                        timedOutQueries.Add(new TimedOutHandleQuery(
                            group.Key,
                            GetImageName(group.Key),
                            handle.HandleValue,
                            handle.GrantedAccess,
                            exception.QueryStage,
                            exception.Elapsed));
                        if (!await TryRestartWorkerAsync(worker, group.Key))
                        {
                            return;
                        }

                        continue;
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException
                            or IOException
                            or JsonException)
                    {
                        issues.Add(new DiscoveryIssue(
                            "handle-worker",
                            exception.Message,
                            group.Key));
                        if (!await TryRestartWorkerAsync(worker, group.Key))
                        {
                            return;
                        }

                        continue;
                    }

                    if (response is null || !response.IsPipe)
                    {
                        continue;
                    }

                    Interlocked.Increment(ref pipeHandleCount);
                    string? pipeName = GetPipeName(response.ObjectName);
                    if (pipeName is null || !candidateByName.ContainsKey(pipeName))
                    {
                        continue;
                    }

                    Interlocked.Increment(ref matchedMojoHandleCount);
                    NamedPipeConnection connection = new(
                        group.Key,
                        GetImageName(group.Key),
                        response.ServerProcessId,
                        GetImageName(response.ServerProcessId),
                        response.ClientProcessId,
                        GetImageName(response.ClientProcessId),
                        response.LocalEnd,
                        response.State);
                    string connectionKey =
                        $"{connection.ServerProcessId}:{connection.ClientProcessId}:"
                        + $"{connection.HandleOwnerProcessId}:{connection.LocalEnd}";
                    connectionsByPipe
                        .GetOrAdd(
                            pipeName,
                            _ => new ConcurrentDictionary<string, NamedPipeConnection>())
                        .TryAdd(connectionKey, connection);
                }
            }

            async ValueTask<bool> TryRestartWorkerAsync(
                HandleQueryProcess handleQueryWorker,
                int processId)
            {
                try
                {
                    await handleQueryWorker.RestartAsync(cancellationToken);
                    Interlocked.Increment(ref workerRestartCount);
                    return true;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or IOException
                        or TimeoutException
                        or Win32Exception)
                {
                    issues.Add(new DiscoveryIssue(
                        "handle-worker",
                        $"Unable to restart the worker: {exception.Message}",
                        processId));
                    return false;
                }
            }
        }

        string? GetImageName(int? processId)
        {
            return processId is int value
                && processById.TryGetValue(value, out ProcessSnapshotEntry? process)
                && !process.IsProcessIdReused
                    ? process.ImageName
                    : null;
        }
    }

    private static MojoPipeInspectionResult CreateResult(
        DateTimeOffset capturedAt,
        IReadOnlyList<MojoPipeCandidate> candidates,
        ConcurrentDictionary<string, ConcurrentDictionary<string, NamedPipeConnection>>
            connectionsByPipe,
        int relevantProcessCount,
        int fileHandleCount,
        int uniqueFileObjectCount,
        int queriedHandleCount,
        int pipeHandleCount,
        int matchedMojoHandleCount,
        int timedOutQueryCount,
        int workerRestartCount,
        TimeSpan elapsed,
        IEnumerable<TimedOutHandleQuery> timedOutQueries,
        IEnumerable<DiscoveryIssue> issues)
    {
        MojoPipeInfo[] pipes = candidates
            .DistinctBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new MojoPipeInfo(
                candidate.Name,
                candidate.ProcessIdHint,
                connectionsByPipe.TryGetValue(candidate.Name, out var connections)
                    ? connections.Values
                        .OrderBy(connection => connection.ServerProcessId)
                        .ThenBy(connection => connection.ClientProcessId)
                        .ToArray()
                    : []))
            .ToArray();

        return new MojoPipeInspectionResult(
            capturedAt,
            pipes,
            new NamedPipeInspectionStatistics(
                pipes.Length,
                relevantProcessCount,
                fileHandleCount,
                uniqueFileObjectCount,
                queriedHandleCount,
                pipeHandleCount,
                matchedMojoHandleCount,
                timedOutQueryCount,
                workerRestartCount,
                elapsed),
            timedOutQueries
                .OrderBy(query => query.OwnerProcessId)
                .ThenBy(query => query.HandleValue)
                .ToArray(),
            issues.ToArray());
    }

    private static string? GetPipeName(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        int separator = objectName.LastIndexOf('\\');
        return separator >= 0 ? objectName[(separator + 1)..] : objectName;
    }

    private sealed class HandleQueryProcess : IAsyncDisposable
    {
        private readonly string _executablePath;
        private readonly TimeSpan _queryTimeout;
        private Process? _process;
        private StreamWriter? _writer;
        private StreamReader? _reader;
        private long _requestId;

        public HandleQueryProcess(string executablePath, TimeSpan queryTimeout)
        {
            _executablePath = executablePath;
            _queryTimeout = queryTimeout;
        }

        public nint ProcessHandle => _process?.Handle
            ?? throw new InvalidOperationException("The handle-query worker is not running.");

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = _executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add(HandleQueryWorker.WorkerArgument);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start the handle-query worker.");
            _writer = _process.StandardInput;
            _writer.AutoFlush = true;
            _reader = _process.StandardOutput;

            string? ready = await _reader
                .ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (!string.Equals(ready, HandleQueryWorker.ReadyMessage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The handle-query worker did not complete its startup handshake.");
            }
        }

        public async ValueTask<HandleQueryWorker.HandleQueryResponse?> QueryAsync(
            nint workerHandle,
            CancellationToken cancellationToken)
        {
            if (_writer is null || _reader is null)
            {
                throw new InvalidOperationException("The handle-query worker is not running.");
            }

            long requestId = Interlocked.Increment(ref _requestId);
            string request = JsonSerializer.Serialize(
                new HandleQueryWorker.HandleQueryRequest(
                    requestId,
                    workerHandle.ToInt64()));
            await _writer.WriteLineAsync(request.AsMemory(), cancellationToken);

            long startedAt = Stopwatch.GetTimestamp();
            string queryStage = "dispatch";

            while (true)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
                TimeSpan remaining = _queryTimeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new HandleQueryTimeoutException(queryStage, elapsed);
                }

                string? responseLine;
                try
                {
                    responseLine = await _reader
                        .ReadLineAsync(cancellationToken)
                        .AsTask()
                        .WaitAsync(remaining, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new HandleQueryTimeoutException(
                        queryStage,
                        Stopwatch.GetElapsedTime(startedAt));
                }

                if (responseLine is null)
                {
                    throw new IOException("The handle-query worker exited without a response.");
                }

                using JsonDocument message = JsonDocument.Parse(responseLine);
                JsonElement root = message.RootElement;
                if (root.TryGetProperty("MessageType", out JsonElement messageType)
                    && string.Equals(
                        messageType.GetString(),
                        "progress",
                        StringComparison.Ordinal))
                {
                    long progressRequestId = root.GetProperty("RequestId").GetInt64();
                    if (progressRequestId != requestId)
                    {
                        throw new InvalidOperationException(
                            "The handle-query worker returned progress for an unexpected request.");
                    }

                    queryStage = root.GetProperty("Stage").GetString() ?? queryStage;
                    continue;
                }

                HandleQueryWorker.HandleQueryResponse? response =
                    JsonSerializer.Deserialize<HandleQueryWorker.HandleQueryResponse>(responseLine);
                if (response?.RequestId != requestId)
                {
                    throw new InvalidOperationException(
                        "The handle-query worker returned an unexpected response.");
                }

                return response;
            }
        }

        public async ValueTask RestartAsync(CancellationToken cancellationToken)
        {
            await StopAsync();
            await StartAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        private async ValueTask StopAsync()
        {
            _writer?.Dispose();
            _reader?.Dispose();

            if (_process is not null)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }

                _process.Dispose();
            }

            _process = null;
            _writer = null;
            _reader = null;
        }
    }

    private sealed class HandleQueryTimeoutException : TimeoutException
    {
        public HandleQueryTimeoutException(string queryStage, TimeSpan elapsed)
            : base($"The handle query timed out during {queryStage}.")
        {
            QueryStage = queryStage;
            Elapsed = elapsed;
        }

        public string QueryStage { get; }

        public TimeSpan Elapsed { get; }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeFileHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DuplicateHandle(
            SafeFileHandle sourceProcess,
            nint sourceHandle,
            nint targetProcess,
            out nint targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);
    }
}
