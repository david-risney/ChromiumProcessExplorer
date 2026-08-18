using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

internal static partial class WindowsProcessPipeInspector
{
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x00000002;

    public static async ValueTask<ProcessPipeInspectionResult> InspectAsync(
        IReadOnlySet<int> processIds,
        IReadOnlyList<ProcessSnapshotEntry> processes,
        HandleQueryWorkerOptions workerOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerOptions.ExecutablePath);
        _ = workerOptions.EffectiveQueryTimeout;

        Dictionary<int, ProcessSnapshotEntry> processById =
            processes.ToDictionary(process => process.ProcessId);
        ConcurrentBag<ProcessPipeHandleInfo> pipes = [];
        ConcurrentBag<TimedOutHandleQuery> timedOutQueries = [];
        ConcurrentBag<DiscoveryIssue> issues = [];
        SystemFileHandleSnapshot snapshot;

        try
        {
            snapshot = WindowsSystemHandleSnapshotter.CaptureFileHandles(processIds);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            return new ProcessPipeInspectionResult(
                [],
                [],
                [new DiscoveryIssue("cdp-pipe-handle-snapshot", exception.Message)]);
        }

        IGrouping<int, SystemHandleEntry>[] ownerGroups = snapshot.UniqueHandles
            .GroupBy(handle => handle.OwnerProcessId)
            .OrderBy(group => group.Key)
            .ToArray();
        int nextGroup = -1;
        int workerCount = Math.Min(
            workerOptions.EffectiveMaximumWorkers,
            Math.Max(1, ownerGroups.Length));

        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(
            _ => RunWorkerAsync()));

        return new ProcessPipeInspectionResult(
            pipes
                .OrderBy(pipe => pipe.OwnerProcessId)
                .ThenBy(pipe => pipe.HandleValue)
                .ToArray(),
            timedOutQueries
                .OrderBy(query => query.OwnerProcessId)
                .ThenBy(query => query.HandleValue)
                .ToArray(),
            issues.ToArray());

        async Task RunWorkerAsync()
        {
            await using WindowsNamedPipeEndpointInspector.HandleQueryProcess worker =
                new(
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
                issues.Add(new DiscoveryIssue(
                    "cdp-pipe-handle-worker",
                    exception.Message));
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
                        "cdp-pipe-duplicate-handle",
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

                    HandleQueryWorker.HandleQueryResponse? response;
                    try
                    {
                        response = await worker.QueryAsync(
                            workerHandle,
                            cancellationToken);
                    }
                    catch (WindowsNamedPipeEndpointInspector.HandleQueryTimeoutException
                        exception)
                    {
                        timedOutQueries.Add(new TimedOutHandleQuery(
                            group.Key,
                            GetImageName(group.Key),
                            handle.HandleValue,
                            handle.GrantedAccess,
                            exception.QueryStage,
                            exception.Elapsed));
                        if (!await RestartAsync(worker, group.Key))
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
                            "cdp-pipe-handle-worker",
                            exception.Message,
                            group.Key));
                        if (!await RestartAsync(worker, group.Key))
                        {
                            return;
                        }

                        continue;
                    }

                    if (response is not { IsPipe: true })
                    {
                        continue;
                    }

                    pipes.Add(new ProcessPipeHandleInfo(
                        group.Key,
                        handle.HandleValue,
                        response.ObjectName,
                        response.ServerProcessId,
                        response.ClientProcessId,
                        response.LocalEnd,
                        response.State,
                        response.Error));
                }
            }
        }

        async ValueTask<bool> RestartAsync(
            WindowsNamedPipeEndpointInspector.HandleQueryProcess worker,
            int processId)
        {
            try
            {
                await worker.RestartAsync(cancellationToken);
                return true;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or IOException
                    or TimeoutException
                    or Win32Exception)
            {
                issues.Add(new DiscoveryIssue(
                    "cdp-pipe-handle-worker",
                    $"Unable to restart the worker: {exception.Message}",
                    processId));
                return false;
            }
        }

        string? GetImageName(int? processId)
        {
            return processId is int value
                && processById.TryGetValue(value, out ProcessSnapshotEntry? process)
                    ? process.ImageName
                    : null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        SafeFileHandle sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    private static class NativeMethods
    {
        internal static SafeFileHandle OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId)
        {
            return WindowsProcessPipeInspector.OpenProcess(
                desiredAccess,
                inheritHandle,
                processId);
        }

        internal static bool DuplicateHandle(
            SafeFileHandle sourceProcess,
            nint sourceHandle,
            nint targetProcess,
            out nint targetHandle,
            uint desiredAccess,
            bool inheritHandle,
            uint options)
        {
            return WindowsProcessPipeInspector.DuplicateHandle(
                sourceProcess,
                sourceHandle,
                targetProcess,
                out targetHandle,
                desiredAccess,
                inheritHandle,
                options);
        }
    }
}

internal sealed record ProcessPipeHandleInfo(
    int OwnerProcessId,
    ulong HandleValue,
    string? ObjectName,
    int? ServerProcessId,
    int? ClientProcessId,
    string? LocalEnd,
    string? State,
    string? Error);

internal sealed record ProcessPipeInspectionResult(
    IReadOnlyList<ProcessPipeHandleInfo> Pipes,
    IReadOnlyList<TimedOutHandleQuery> TimedOutQueries,
    IReadOnlyList<DiscoveryIssue> Issues);
