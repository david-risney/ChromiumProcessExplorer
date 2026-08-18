using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Builds stable detailed diagnostics from one process snapshot.</summary>
public sealed class ProcessDetailsProvider
{
    private const string SchemaVersion = "1.0";
    private readonly IProcessDetailsPlatformInspector _platformInspector;

    /// <summary>Creates a provider using the Windows process inspector.</summary>
    public ProcessDetailsProvider()
        : this(new WindowsProcessDetailsPlatformInspector())
    {
    }

    internal ProcessDetailsProvider(
        IProcessDetailsPlatformInspector platformInspector)
    {
        ArgumentNullException.ThrowIfNull(platformInspector);
        _platformInspector = platformInspector;
    }

    /// <summary>Creates details for selected process snapshot entries.</summary>
    public ProcessDetailsResult Create(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        bool includeSensitiveValues)
    {
        ArgumentNullException.ThrowIfNull(processes);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        List<DiscoveryIssue> resultIssues = [];
        ProcessDetailEntry[] entries = processes
            .OrderBy(process => process.ProcessId)
            .Select(CreateEntry)
            .ToArray();
        return new ProcessDetailsResult(
            SchemaVersion,
            capturedAt,
            includeSensitiveValues,
            entries,
            resultIssues);

        ProcessDetailEntry CreateEntry(ProcessSnapshotEntry process)
        {
            ProcessPlatformDetails platform = _platformInspector.Inspect(process);
            List<DiscoveryIssue> issues = [.. platform.Issues];
            if (process.MetadataError is not null)
            {
                issues.Add(new DiscoveryIssue(
                    "process-metadata",
                    process.MetadataError,
                    process.ProcessId));
            }

            if (process.ModuleInspectionError is not null)
            {
                issues.Add(new DiscoveryIssue(
                    "loaded-modules",
                    process.ModuleInspectionError,
                    process.ProcessId));
            }

            if (process.CreationTime is not null
                && platform.ReopenedCreationTime is not null
                && process.CreationTime != platform.ReopenedCreationTime)
            {
                issues.Add(new DiscoveryIssue(
                    "process-identity",
                    ProcessSnapshotEntry.ProcessIdReuseError,
                    process.ProcessId));
                platform = platform with
                {
                    Architecture = null,
                    NativeArchitecture = null,
                    IntegrityLevel = null,
                    IsElevated = null,
                    PackageFullName = null,
                    ExecutableVersion = null,
                };
            }

            ChromiumCommandLine commandLine = ChromiumCommandLine.Parse(
                process.CommandLine);
            ProcessSwitchDetail[] switches = commandLine.Switches
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ProcessSwitchDetail(
                    item.Key,
                    item.Value is not null,
                    CreateSensitive(item.Value, "command-line-switch-value")))
                .ToArray();
            bool observedRole = commandLine.HasSwitch("type");
            return new ProcessDetailEntry(
                new ProcessIdentity(process.ProcessId, process.CreationTime),
                process.ParentProcessId,
                process.ImageName,
                CreateSensitive(process.ExecutablePath, "filesystem-path"),
                CreateSensitive(process.CommandLine, "command-line"),
                switches,
                process.ChromiumProcessType,
                observedRole
                    ? "observed-command-line"
                    : process.ChromiumProcessType is null
                        ? "unclassified"
                        : "inferred-browser",
                CreateSensitive(
                    process.UserDataDirectory,
                    "user-data-directory"),
                platform.ExecutableVersion,
                platform.Architecture,
                platform.NativeArchitecture,
                platform.IntegrityLevel,
                platform.IsElevated,
                platform.PackageFullName,
                process.Evidence,
                process.LoadedModules.Select(module =>
                    CreateSensitive(module, "loaded-module-path")).ToArray(),
                issues);
        }

        SensitiveStringValue CreateSensitive(
            string? value,
            string classification)
        {
            return new SensitiveStringValue(
                includeSensitiveValues ? value : null,
                !includeSensitiveValues && value is not null,
                classification);
        }
    }
}

internal interface IProcessDetailsPlatformInspector
{
    ProcessPlatformDetails Inspect(ProcessSnapshotEntry process);
}

internal sealed class WindowsProcessDetailsPlatformInspector
    : IProcessDetailsPlatformInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const int TokenIntegrityLevel = 25;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public ProcessPlatformDetails Inspect(ProcessSnapshotEntry process)
    {
        List<DiscoveryIssue> issues = [];
        ProcessExecutableVersion? version = GetVersion(
            process.ExecutablePath,
            process.ProcessId,
            issues);
        using SafeFileHandle handle = NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            process.ProcessId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            issues.Add(new DiscoveryIssue(
                "process-details-open",
                new Win32Exception(error).Message,
                process.ProcessId,
                error));
            return new ProcessPlatformDetails(
                null,
                null,
                null,
                null,
                null,
                null,
                version,
                issues);
        }

        DateTimeOffset? creationTime = QueryCreationTime(handle);
        (string? architecture, string? nativeArchitecture) =
            QueryArchitecture(handle, process.ProcessId, issues);
        (string? integrity, bool? elevated) =
            QueryToken(handle, process.ProcessId, issues);
        string? package = QueryPackage(handle, process.ProcessId, issues);
        return new ProcessPlatformDetails(
            creationTime,
            architecture,
            nativeArchitecture,
            integrity,
            elevated,
            package,
            version,
            issues);
    }

    private static ProcessExecutableVersion? GetVersion(
        string? path,
        int processId,
        List<DiscoveryIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return new ProcessExecutableVersion(
                info.FileVersion,
                info.ProductVersion,
                info.ProductName,
                info.CompanyName,
                info.OriginalFilename);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
            issues.Add(new DiscoveryIssue(
                "executable-version",
                exception.Message,
                processId));
            return null;
        }
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

    private static (string? Process, string? Native) QueryArchitecture(
        SafeFileHandle process,
        int processId,
        List<DiscoveryIssue> issues)
    {
        if (!NativeMethods.IsWow64Process2(
            process,
            out ushort processMachine,
            out ushort nativeMachine))
        {
            int error = Marshal.GetLastWin32Error();
            issues.Add(new DiscoveryIssue(
                "process-architecture",
                new Win32Exception(error).Message,
                processId,
                error));
            return (null, null);
        }

        string native = FormatMachine(nativeMachine);
        return (
            processMachine == 0 ? native : FormatMachine(processMachine),
            native);
    }

    private static (string? Integrity, bool? Elevated) QueryToken(
        SafeFileHandle process,
        int processId,
        List<DiscoveryIssue> issues)
    {
        if (!NativeMethods.OpenProcessToken(process, TokenQuery, out SafeFileHandle token))
        {
            int error = Marshal.GetLastWin32Error();
            issues.Add(new DiscoveryIssue(
                "process-token",
                new Win32Exception(error).Message,
                processId,
                error));
            return (null, null);
        }

        using (token)
        {
            string? integrity = QueryIntegrity(token);
            bool? elevated = QueryElevation(token);
            return (integrity, elevated);
        }
    }

    private static string? QueryIntegrity(SafeFileHandle token)
    {
        _ = NativeMethods.GetTokenInformation(
            token,
            TokenIntegrityLevel,
            0,
            0,
            out int required);
        if (required <= 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!NativeMethods.GetTokenInformation(
                token,
                TokenIntegrityLevel,
                buffer,
                required,
                out _))
            {
                return null;
            }

            TokenMandatoryLabel label =
                Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
            SecurityIdentifier sid = new(label.Label.Sid);
            int rid = int.Parse(
                sid.Value.Split('-')[^1],
                System.Globalization.CultureInfo.InvariantCulture);
            return rid switch
            {
                < 0x1000 => "Untrusted",
                < 0x2000 => "Low",
                < 0x3000 => "Medium",
                < 0x4000 => "High",
                < 0x5000 => "System",
                _ => "Protected",
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool? QueryElevation(SafeFileHandle token)
    {
        int size = sizeof(int);
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            return NativeMethods.GetTokenInformation(
                token,
                TokenElevation,
                buffer,
                size,
                out _)
                    ? Marshal.ReadInt32(buffer) != 0
                    : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? QueryPackage(
        SafeFileHandle process,
        int processId,
        List<DiscoveryIssue> issues)
    {
        int length = 0;
        int result = NativeMethods.GetPackageFullName(process, ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer || length <= 0)
        {
            if (result != 0)
            {
                issues.Add(new DiscoveryIssue(
                    "process-package",
                    new Win32Exception(result).Message,
                    processId,
                    result));
            }

            return null;
        }

        char[] buffer = new char[length];
        result = NativeMethods.GetPackageFullName(process, ref length, buffer);
        if (result != 0)
        {
            issues.Add(new DiscoveryIssue(
                "process-package",
                new Win32Exception(result).Message,
                processId,
                result));
            return null;
        }

        int stringLength = Array.IndexOf(buffer, '\0');
        return new string(
            buffer,
            0,
            stringLength < 0 ? buffer.Length : stringLength);
    }

    private static string FormatMachine(ushort machine)
    {
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0xAA64 => "arm64",
            0x01c4 => "arm",
            _ => $"0x{machine:X4}",
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint Low;
        private readonly uint High;

        public long ToLong() => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes
    {
        public readonly nint Sid;
        public readonly uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenMandatoryLabel
    {
        public readonly SidAndAttributes Label;
    }

    private static class NativeMethods
    {
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process2(
            SafeFileHandle process,
            out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(
            SafeFileHandle process,
            uint desiredAccess,
            out SafeFileHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            SafeFileHandle token,
            int informationClass,
            nint information,
            int informationLength,
            out int returnLength);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode)]
        internal static extern int GetPackageFullName(
            SafeFileHandle process,
            ref int packageFullNameLength,
            [Out] char[]? packageFullName);
    }
}
