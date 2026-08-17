using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Captures Windows HWND ownership and child topology.</summary>
public sealed class WindowsWindowSnapshotProvider : IWindowSnapshotProvider
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GetWindowChild = 5;
    private const int MaximumClassNameLength = 256;

    /// <inheritdoc />
    public ValueTask<WindowSnapshotResult> CaptureAsync(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        Dictionary<int, ProcessSnapshotEntry> processesById = processes
            .ToDictionary(process => process.ProcessId);
        Dictionary<long, RawWindow> windows = [];
        List<DiscoveryIssue> issues = [];
        bool cancellationRequested = false;
        EnumWindowsCallback callback = (window, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationRequested = true;
                return false;
            }

            AddWindow(window, null);
            return true;
        };

        if (!NativeMethods.EnumWindows(callback, 0) && !cancellationRequested)
        {
            int error = Marshal.GetLastWin32Error();
            issues.Add(new DiscoveryIssue(
                "window-enumeration",
                error == 0
                    ? "EnumWindows stopped before completing the snapshot."
                    : new Win32Exception(error).Message,
                NativeErrorCode: error == 0 ? null : error));
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (RawWindow topLevelWindow in windows.Values
            .Where(window => window.ParentWindowHandle is null)
            .ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumWindowsCallback childCallback = (window, _) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationRequested = true;
                    return false;
                }

                AddWindow(window, null);
                return true;
            };
            if (!NativeMethods.EnumChildWindows(
                ToNativeHandle(topLevelWindow.WindowHandle),
                childCallback,
                0)
                && !cancellationRequested)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    issues.Add(new DiscoveryIssue(
                        "child-window-enumeration",
                        new Win32Exception(error).Message,
                        topLevelWindow.OwnerProcessId,
                        error));
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        foreach (RawWindow window in windows.Values.ToArray())
        {
            if (window.FirstChildWindowHandle is long firstChild)
            {
                AddWindow(ToNativeHandle(firstChild), window.WindowHandle);
            }

            if (window.CrossProcessChildWindowHandle is long crossProcessChild)
            {
                AddWindow(ToNativeHandle(crossProcessChild), window.WindowHandle);
            }
        }

        Dictionary<int, (DateTimeOffset? CreationTime, string? Error)> identities =
            windows.Values
                .Select(window => window.OwnerProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToDictionary(processId => processId, QueryCreationTime);

        WindowSnapshotEntry[] results = windows.Values
            .OrderBy(window => window.WindowHandle)
            .Select(window =>
            {
                identities.TryGetValue(
                    window.OwnerProcessId,
                    out (DateTimeOffset? CreationTime, string? Error) identity);
                string? error = window.InspectionError ?? identity.Error;
                if (processesById.TryGetValue(
                    window.OwnerProcessId,
                    out ProcessSnapshotEntry? process)
                    && process.CreationTime is not null
                    && identity.CreationTime is not null
                    && process.CreationTime != identity.CreationTime)
                {
                    error = ProcessSnapshotEntry.ProcessIdReuseError;
                }

                if (error is not null)
                {
                    issues.Add(new DiscoveryIssue(
                        "window-owner-identity",
                        $"HWND 0x{window.WindowHandle:X}: {error}",
                        window.OwnerProcessId));
                }

                return new WindowSnapshotEntry(
                    window.WindowHandle,
                    window.ParentWindowHandle,
                    window.FirstChildWindowHandle,
                    window.CrossProcessChildWindowHandle,
                    window.OwnerProcessId,
                    identity.CreationTime,
                    window.OwnerThreadId,
                    window.ClassName,
                    window.IsVisible,
                    error);
            })
            .ToArray();

        return ValueTask.FromResult(new WindowSnapshotResult(
            capturedAt,
            results,
            issues));

        void AddWindow(nint window, long? fallbackParent)
        {
            if (window == 0)
            {
                return;
            }

            long handle = window.ToInt64();
            if (windows.ContainsKey(handle))
            {
                return;
            }

            uint threadId = NativeMethods.GetWindowThreadProcessId(
                window,
                out uint ownerProcessIdValue);
            int ownerProcessId = ownerProcessIdValue <= int.MaxValue
                ? checked((int)ownerProcessIdValue)
                : 0;
            char[] classNameBuffer = new char[MaximumClassNameLength];
            int classNameLength = NativeMethods.GetClassName(
                window,
                classNameBuffer,
                classNameBuffer.Length);
            string? inspectionError = null;
            if (classNameLength == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    inspectionError = new Win32Exception(error).Message;
                }
            }

            nint parent = NativeMethods.GetParent(window);
            nint firstChild = NativeMethods.GetWindow(window, GetWindowChild);
            nint crossProcessChild = NativeMethods.GetProp(
                window,
                "CrossProcessChildHWND");
            windows.Add(handle, new RawWindow(
                handle,
                parent == 0 ? fallbackParent : parent.ToInt64(),
                firstChild == 0 ? null : firstChild.ToInt64(),
                crossProcessChild == 0 ? null : crossProcessChild.ToInt64(),
                ownerProcessId,
                threadId,
                classNameLength == 0
                    ? string.Empty
                    : new string(classNameBuffer, 0, classNameLength),
                NativeMethods.IsWindowVisible(window),
                inspectionError));
        }
    }

    private static (DateTimeOffset? CreationTime, string? Error) QueryCreationTime(
        int processId)
    {
        using SafeFileHandle process = NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (process.IsInvalid)
        {
            return (
                null,
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        return NativeMethods.GetProcessTimes(
            process,
            out FileTime creation,
            out _,
            out _,
            out _)
                ? (DateTimeOffset.FromFileTime(creation.ToLong()), null)
                : (
                    null,
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private static nint ToNativeHandle(long handle)
    {
        return checked((nint)handle);
    }

    private sealed record RawWindow(
        long WindowHandle,
        long? ParentWindowHandle,
        long? FirstChildWindowHandle,
        long? CrossProcessChildWindowHandle,
        int OwnerProcessId,
        uint OwnerThreadId,
        string ClassName,
        bool IsVisible,
        string? InspectionError);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint Low;
        private readonly uint High;

        public long ToLong() => ((long)High << 32) | Low;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            nint parentWindow,
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern int GetClassName(
            nint window,
            [Out] char[] className,
            int maximumCount);

        [DllImport("user32.dll")]
        internal static extern nint GetParent(nint window);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetPropW",
            CharSet = CharSet.Unicode)]
        internal static extern nint GetProp(nint window, string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(
            SafeFileHandle process,
            out FileTime creationTime,
            out FileTime exitTime,
            out FileTime kernelTime,
            out FileTime userTime);
    }
}
