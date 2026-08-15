using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Captures and enriches a Windows process snapshot.</summary>
public sealed partial class WindowsProcessSnapshotter : IProcessSnapshotProvider
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SystemProcessInformation = 5;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    private static readonly HashSet<string> KnownChromiumExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "brave.exe",
            "cefclient.exe",
            "cefsimple.exe",
            "chrome.exe",
            "chromium.exe",
            "electron.exe",
            "msedge.exe",
            "msedgewebview2.exe",
            "opera.exe",
            "vivaldi.exe",
        };

    /// <summary>Captures process identity first, then enriches it in parallel.</summary>
    public async ValueTask<IReadOnlyList<ProcessSnapshotEntry>> CaptureAsync(
        int? maximumConcurrency = null,
        CancellationToken cancellationToken = default)
    {
        List<BasicProcessEntry> basicProcesses = CaptureBasicSnapshot();
        ProcessSnapshotEntry[] results = new ProcessSnapshotEntry[basicProcesses.Count];

        ParallelOptions options = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(
                1,
                maximumConcurrency ?? Environment.ProcessorCount),
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, basicProcesses.Count),
            options,
            (index, _) =>
            {
                results[index] = Enrich(basicProcesses[index]);
                return ValueTask.CompletedTask;
            });

        return results;
    }

    private static List<BasicProcessEntry> CaptureBasicSnapshot()
    {
        int bufferLength = 1024 * 1024;

        while (bufferLength <= 64 * 1024 * 1024)
        {
            nint buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(
                    SystemProcessInformation,
                    buffer,
                    bufferLength,
                    out int requiredLength);

                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall)
                {
                    bufferLength = Math.Max(bufferLength * 2, requiredLength);
                    continue;
                }

                if (status < 0)
                {
                    throw new InvalidOperationException(
                        $"NtQuerySystemInformation(SystemProcessInformation) failed "
                        + $"with NTSTATUS 0x{status:X8}.");
                }

                return ParseBasicSnapshot(buffer, bufferLength);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            "The Windows process snapshot exceeded the maximum supported size.");
    }

    private static List<BasicProcessEntry> ParseBasicSnapshot(
        nint buffer,
        int bufferLength)
    {
        List<BasicProcessEntry> processes = [];
        int offset = 0;

        while (offset >= 0 && offset < bufferLength)
        {
            nint entryPointer = buffer + offset;
            SystemProcessEntry entry =
                Marshal.PtrToStructure<SystemProcessEntry>(entryPointer);
            long processIdValue = entry.UniqueProcessId.ToInt64();
            long parentProcessIdValue = entry.InheritedFromUniqueProcessId.ToInt64();

            if (processIdValue is >= 0 and <= int.MaxValue
                && parentProcessIdValue is >= 0 and <= int.MaxValue)
            {
                string imageName = entry.ImageName.Length == 0 || entry.ImageName.Buffer == 0
                    ? GetSpecialProcessName(checked((int)processIdValue))
                    : Marshal.PtrToStringUni(
                        entry.ImageName.Buffer,
                        entry.ImageName.Length / sizeof(char)) ?? string.Empty;
                DateTimeOffset? creationTime = entry.CreateTime > 0
                    ? DateTimeOffset.FromFileTime(entry.CreateTime)
                    : null;

                processes.Add(new BasicProcessEntry(
                    checked((int)processIdValue),
                    checked((int)parentProcessIdValue),
                    imageName,
                    creationTime));
            }

            if (entry.NextEntryOffset == 0)
            {
                break;
            }

            offset = checked(offset + (int)entry.NextEntryOffset);
        }

        return processes;
    }

    private static string GetSpecialProcessName(int processId)
    {
        return processId switch
        {
            0 => "System Idle Process",
            4 => "System",
            _ => string.Empty,
        };
    }

    private static ProcessSnapshotEntry Enrich(BasicProcessEntry basic)
    {
        DateTimeOffset? creationTime = basic.CreationTime;
        string? executablePath = null;
        string? commandLine = null;
        string? metadataError = null;

        using SafeFileHandle process = NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            basic.ProcessId);

        if (process.IsInvalid)
        {
            metadataError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        else
        {
            try
            {
                DateTimeOffset? reopenedCreationTime = QueryCreationTime(process);
                if (basic.CreationTime is not null
                    && reopenedCreationTime is not null
                    && basic.CreationTime != reopenedCreationTime)
                {
                    metadataError =
                        "The process ID was reused after the system snapshot was captured.";
                }
                else
                {
                    creationTime ??= reopenedCreationTime;
                    executablePath = QueryExecutablePath(process);
                    commandLine = QueryCommandLine(process);
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or InvalidOperationException)
            {
                metadataError = exception.Message;
            }
        }

        ChromiumCommandLine parsed = ChromiumCommandLine.Parse(commandLine);
        string? processType = parsed.GetSwitchValue("type");
        string? userDataDirectory = parsed.GetSwitchValue("user-data-dir");
        List<string> evidence = [];

        string executableName = Path.GetFileName(executablePath ?? basic.ImageName);
        if (KnownChromiumExecutables.Contains(executableName))
        {
            evidence.Add($"known executable: {executableName}");
        }

        if (parsed.HasSwitch("type"))
        {
            evidence.Add("--type command-line switch");
        }

        if (parsed.HasSwitch("user-data-dir"))
        {
            evidence.Add("--user-data-dir command-line switch");
        }

        bool isLikelyChromium = evidence.Count > 0;
        if (isLikelyChromium && string.IsNullOrEmpty(processType))
        {
            processType = "browser";
        }

        return new ProcessSnapshotEntry(
            basic.ProcessId,
            basic.ParentProcessId,
            creationTime,
            basic.ImageName,
            executablePath,
            commandLine,
            processType,
            userDataDirectory,
            isLikelyChromium,
            evidence,
            metadataError);
    }

    private static DateTimeOffset? QueryCreationTime(SafeFileHandle process)
    {
        return NativeMethods.GetProcessTimes(
            process,
            out FileTime creation,
            out _,
            out _,
            out _)
            ? DateTimeOffset.FromFileTime(creation.ToLong())
            : null;
    }

    private static string? QueryExecutablePath(SafeFileHandle process)
    {
        char[] buffer = new char[32768];
        uint length = (uint)buffer.Length;
        return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref length)
            ? new string(buffer, 0, checked((int)length))
            : null;
    }

    private static string? QueryCommandLine(SafeFileHandle process)
    {
        int bufferLength = 4096;

        while (bufferLength <= 1024 * 1024)
        {
            nint buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int status = NativeMethods.NtQueryInformationProcess(
                    process,
                    ProcessCommandLineInformation,
                    buffer,
                    bufferLength,
                    out int requiredLength);

                if (status >= 0)
                {
                    UnicodeString value = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return value.Length == 0 || value.Buffer == 0
                        ? string.Empty
                        : Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char));
                }

                if (status is not StatusInfoLengthMismatch and not StatusBufferTooSmall)
                {
                    return null;
                }

                bufferLength = Math.Max(bufferLength * 2, requiredLength);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private sealed record BasicProcessEntry(
        int ProcessId,
        int ParentProcessId,
        string ImageName,
        DateTimeOffset? CreationTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SystemProcessEntry
    {
        public readonly uint NextEntryOffset;
        public readonly uint NumberOfThreads;
        public readonly long WorkingSetPrivateSize;
        public readonly uint HardFaultCount;
        public readonly uint NumberOfThreadsHighWatermark;
        public readonly ulong CycleTime;
        public readonly long CreateTime;
        public readonly long UserTime;
        public readonly long KernelTime;
        public readonly UnicodeString ImageName;
        public readonly int BasePriority;
        public readonly nint UniqueProcessId;
        public readonly nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint Low;
        private readonly uint High;

        public long ToLong() => ((long)High << 32) | Low;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial SafeFileHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "QueryFullProcessImageNameW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryFullProcessImageName(
            SafeFileHandle process,
            uint flags,
            [Out] char[] executableName,
            ref uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(
            SafeFileHandle process,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQuerySystemInformation(
            int systemInformationClass,
            nint systemInformation,
            int systemInformationLength,
            out int returnLength);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationProcess(
            SafeFileHandle process,
            int processInformationClass,
            nint processInformation,
            int processInformationLength,
            out int returnLength);
    }
}
