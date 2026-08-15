using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

internal static partial class WindowsSystemHandleSnapshotter
{
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    public static SystemFileHandleSnapshot CaptureFileHandles(
        IReadOnlySet<int> processIds)
    {
        string probePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");

        using SafeFileHandle probeHandle = File.OpenHandle(
            probePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        List<SystemHandleEntry> handles = Capture();
        int currentProcessId = Environment.ProcessId;
        nuint probeValue = unchecked((nuint)probeHandle.DangerousGetHandle());
        SystemHandleEntry? probeEntry = handles.FirstOrDefault(
            entry => entry.OwnerProcessId == currentProcessId
                && entry.HandleValue == probeValue);

        if (probeEntry is null)
        {
            throw new InvalidOperationException(
                "Unable to identify the system object type index for file handles.");
        }

        SystemHandleEntry[] filteredHandles = handles
            .Where(entry => entry.ObjectTypeIndex == probeEntry.ObjectTypeIndex
                && processIds.Contains(entry.OwnerProcessId))
            .ToArray();
        SystemHandleEntry[] uniqueHandles = filteredHandles
            .GroupBy(entry => entry.ObjectAddress != 0
                ? $"object:{entry.ObjectAddress:X}"
                : $"handle:{entry.OwnerProcessId}:{entry.HandleValue:X}")
            .Select(group => group.First())
            .ToArray();

        return new SystemFileHandleSnapshot(filteredHandles.Length, uniqueHandles);
    }

    private static List<SystemHandleEntry> Capture()
    {
        int bufferLength = 4 * 1024 * 1024;

        while (bufferLength <= 256 * 1024 * 1024)
        {
            nint buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(
                    SystemExtendedHandleInformation,
                    buffer,
                    bufferLength,
                    out int requiredLength);

                if (status == StatusInfoLengthMismatch)
                {
                    bufferLength = Math.Max(bufferLength * 2, requiredLength);
                    continue;
                }

                if (status < 0)
                {
                    throw new InvalidOperationException(
                        $"NtQuerySystemInformation(SystemExtendedHandleInformation) "
                        + $"failed with NTSTATUS 0x{status:X8}.");
                }

                return Parse(buffer, bufferLength);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            "The Windows handle snapshot exceeded the maximum supported size.");
    }

    private static List<SystemHandleEntry> Parse(nint buffer, int bufferLength)
    {
        nuint handleCount = unchecked((nuint)Marshal.ReadIntPtr(buffer));
        int headerSize = nint.Size * 2;
        int entrySize = Marshal.SizeOf<SystemHandleTableEntry>();
        nuint maximumEntries = (nuint)Math.Max(0, (bufferLength - headerSize) / entrySize);
        handleCount = Math.Min(handleCount, maximumEntries);

        List<SystemHandleEntry> handles =
            new(handleCount > int.MaxValue ? int.MaxValue : (int)handleCount);

        for (nuint index = 0; index < handleCount; index++)
        {
            nint entryPointer = buffer + headerSize + checked((nint)(index * (nuint)entrySize));
            SystemHandleTableEntry entry =
                Marshal.PtrToStructure<SystemHandleTableEntry>(entryPointer);

            if (entry.UniqueProcessId > int.MaxValue)
            {
                continue;
            }

            handles.Add(new SystemHandleEntry(
                checked((int)entry.UniqueProcessId),
                entry.HandleValue,
                unchecked((nuint)entry.Object),
                entry.ObjectTypeIndex,
                entry.GrantedAccess));
        }

        return handles;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SystemHandleTableEntry
    {
        public readonly nint Object;
        public readonly nuint UniqueProcessId;
        public readonly nuint HandleValue;
        public readonly uint GrantedAccess;
        public readonly ushort CreatorBackTraceIndex;
        public readonly ushort ObjectTypeIndex;
        public readonly uint HandleAttributes;
        public readonly uint Reserved;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ntdll.dll")]
        internal static partial int NtQuerySystemInformation(
            int systemInformationClass,
            nint systemInformation,
            int systemInformationLength,
            out int returnLength);
    }
}

internal sealed record SystemHandleEntry(
    int OwnerProcessId,
    nuint HandleValue,
    nuint ObjectAddress,
    ushort ObjectTypeIndex,
    uint GrantedAccess);

internal sealed record SystemFileHandleSnapshot(
    int FileHandleCount,
    IReadOnlyList<SystemHandleEntry> UniqueHandles);
