using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ChromiumProcessExplorer.Core.Discovery;

internal interface ICdpListenerOwnerResolver
{
    CdpListenerOwnerResult Resolve(int port);
}

internal sealed record CdpListenerOwnerResult(
    IReadOnlyList<int> ProcessIds,
    string? Error);

internal sealed partial class WindowsCdpListenerOwnerResolver
    : ICdpListenerOwnerResolver
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;
    private const int TcpTableOwnerPidListener = 3;
    private const uint MibTcpStateListen = 2;

    public CdpListenerOwnerResult Resolve(int port)
    {
        uint size = 0;
        uint status = NativeMethods.GetExtendedTcpTable(
            0,
            ref size,
            true,
            (int)AddressFamily.InterNetwork,
            TcpTableOwnerPidListener,
            0);
        if (status != ErrorInsufficientBuffer)
        {
            return new CdpListenerOwnerResult(
                [],
                new Win32Exception(checked((int)status)).Message);
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            status = NativeMethods.GetExtendedTcpTable(
                buffer,
                ref size,
                true,
                (int)AddressFamily.InterNetwork,
                TcpTableOwnerPidListener,
                0);
            if (status != NoError)
            {
                return new CdpListenerOwnerResult(
                    [],
                    new Win32Exception(checked((int)status)).Message);
            }

            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            int offset = sizeof(uint);
            HashSet<int> processIds = [];
            for (int index = 0; index < count; index++)
            {
                MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                    buffer + offset + (index * rowSize));
                int localPort = unchecked(
                    (ushort)System.Net.IPAddress.NetworkToHostOrder(
                        unchecked((short)row.LocalPort)));
                if (row.State == MibTcpStateListen
                    && localPort == port
                    && row.OwningProcessId <= int.MaxValue)
                {
                    processIds.Add(checked((int)row.OwningProcessId));
                }
            }

            return new CdpListenerOwnerResult(
                processIds.Order().ToArray(),
                null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibTcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint RemoteAddress;
        public readonly uint RemotePort;
        public readonly uint OwningProcessId;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("iphlpapi.dll")]
        internal static partial uint GetExtendedTcpTable(
            nint tcpTable,
            ref uint size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily,
            int tableClass,
            uint reserved);
    }
}

internal interface IChromeRemoteDebuggingRestrictionDetector
{
    string? GetRestriction(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine);
}

internal sealed class ChromeRemoteDebuggingRestrictionDetector
    : IChromeRemoteDebuggingRestrictionDetector
{
    public string? GetRestriction(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine)
    {
        if (!string.Equals(
            Path.GetFileName(process.ExecutablePath ?? process.ImageName),
            "chrome.exe",
            StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return null;
        }

        FileVersionInfo version;
        try
        {
            version = FileVersionInfo.GetVersionInfo(process.ExecutablePath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or ArgumentException
                or Win32Exception)
        {
            return null;
        }

        string defaultDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Google",
            "Chrome",
            "User Data");
        return GetRestrictionForVersion(
            process,
            commandLine,
            version.ProductName,
            version.ProductMajorPart,
            defaultDirectory);
    }

    internal static string? GetRestrictionForVersion(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine,
        string? productName,
        int productMajorVersion,
        string defaultDirectory)
    {
        if (productMajorVersion < 136
            || productName is null
            || !productName.Contains(
                "Google Chrome",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? configuredDirectory =
            commandLine.GetSwitchValue("user-data-dir");
        if (!string.IsNullOrWhiteSpace(configuredDirectory)
            && !PathsEqual(configuredDirectory, defaultDirectory))
        {
            return null;
        }

        return "Google Chrome 136 and later ignore remote-debugging switches "
            + "for the default user data directory. Use a non-default "
            + "--user-data-dir or Chrome for Testing.";
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right)
                    .TrimEnd(Path.DirectorySeparatorChar),
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
}
