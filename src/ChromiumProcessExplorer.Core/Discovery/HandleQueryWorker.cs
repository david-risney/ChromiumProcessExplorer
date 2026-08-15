using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>
/// Runs potentially blocking foreign-handle queries in a process that callers
/// can terminate safely on timeout.
/// </summary>
public static partial class HandleQueryWorker
{
    /// <summary>The hidden command argument used to enter worker mode.</summary>
    public const string WorkerArgument = "--handle-query-worker";
    internal const string ReadyMessage = "READY";

    /// <summary>Runs the line-oriented worker protocol until input closes.</summary>
    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        using StreamReader reader = new(input, leaveOpen: true);
        await using StreamWriter writer = new(output, leaveOpen: true)
        {
            AutoFlush = true,
        };

        await writer.WriteLineAsync(ReadyMessage.AsMemory(), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            HandleQueryRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<HandleQueryRequest>(line);
            }
            catch (JsonException exception)
            {
                await WriteResponseAsync(
                    writer,
                    new HandleQueryResponse(0, false, null, null, null, null, null, exception.Message),
                    cancellationToken);
                continue;
            }

            if (request is null)
            {
                continue;
            }

            HandleQueryResponse response = QueryHandle(
                request,
                stage =>
                {
                    string progress = JsonSerializer.Serialize(
                        new HandleQueryProgress("progress", request.RequestId, stage));
                    writer.WriteLine(progress);
                });
            await WriteResponseAsync(writer, response, cancellationToken);
        }

        return 0;
    }

    private static async ValueTask WriteResponseAsync(
        StreamWriter writer,
        HandleQueryResponse response,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(response);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    private static HandleQueryResponse QueryHandle(
        HandleQueryRequest request,
        Action<string> reportStage)
    {
        using SafeFileHandle handle =
            new(new nint(request.HandleValue), ownsHandle: true);

        try
        {
            reportStage("file-type");
            if (NativeMethods.GetFileType(handle) != FileTypePipe)
            {
                return new HandleQueryResponse(
                    request.RequestId,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);
            }

            reportStage("object-name");
            string? objectName = QueryObjectName(handle);
            reportStage("server-process-id");
            int? serverProcessId = NativeMethods.GetNamedPipeServerProcessId(
                handle,
                out uint serverPid)
                ? checked((int)serverPid)
                : null;
            reportStage("client-process-id");
            int? clientProcessId = NativeMethods.GetNamedPipeClientProcessId(
                handle,
                out uint clientPid)
                ? checked((int)clientPid)
                : null;
            reportStage("pipe-local-information");
            PipeLocalInformation? localInformation = QueryPipeLocalInformation(handle);

            return new HandleQueryResponse(
                request.RequestId,
                true,
                objectName,
                serverProcessId,
                clientProcessId,
                GetPipeEnd(localInformation?.NamedPipeEnd),
                GetPipeState(localInformation?.NamedPipeState),
                null);
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or OverflowException)
        {
            return new HandleQueryResponse(
                request.RequestId,
                true,
                null,
                null,
                null,
                null,
                null,
                exception.Message);
        }
    }

    private static string? QueryObjectName(SafeFileHandle handle)
    {
        int bufferLength = 1024;

        while (bufferLength <= 1024 * 1024)
        {
            nint buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                int status = NativeMethods.NtQueryObject(
                    handle,
                    ObjectNameInformation,
                    buffer,
                    bufferLength,
                    out int requiredLength);

                if (status >= 0)
                {
                    UnicodeString name = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return name.Length == 0 || name.Buffer == 0
                        ? null
                        : Marshal.PtrToStringUni(name.Buffer, name.Length / sizeof(char));
                }

                if (status is not StatusInfoLengthMismatch
                    and not StatusBufferOverflow
                    and not StatusBufferTooSmall)
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

    private static PipeLocalInformation? QueryPipeLocalInformation(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<PipeLocalInformation>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status = NativeMethods.NtQueryInformationFile(
                handle,
                out _,
                buffer,
                (uint)size,
                FilePipeLocalInformation);

            return status >= 0
                ? Marshal.PtrToStructure<PipeLocalInformation>(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetPipeEnd(uint? pipeEnd)
    {
        return pipeEnd switch
        {
            0 => "client",
            1 => "server",
            _ => null,
        };
    }

    private static string? GetPipeState(uint? state)
    {
        return state switch
        {
            1 => "disconnected",
            2 => "listening",
            3 => "connected",
            4 => "closing",
            _ => null,
        };
    }

    private const uint FileTypePipe = 0x0003;
    private const int ObjectNameInformation = 1;
    private const int FilePipeLocalInformation = 24;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    internal sealed record HandleQueryRequest(long RequestId, long HandleValue);

    internal sealed record HandleQueryProgress(
        string MessageType,
        long RequestId,
        string Stage);

    internal sealed record HandleQueryResponse(
        long RequestId,
        bool IsPipe,
        string? ObjectName,
        int? ServerProcessId,
        int? ClientProcessId,
        string? LocalEnd,
        string? State,
        string? Error);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoStatusBlock
    {
        public readonly nint Status;
        public readonly nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PipeLocalInformation
    {
        public readonly uint NamedPipeType;
        public readonly uint NamedPipeConfiguration;
        public readonly uint MaximumInstances;
        public readonly uint CurrentInstances;
        public readonly uint InboundQuota;
        public readonly uint ReadDataAvailable;
        public readonly uint OutboundQuota;
        public readonly uint WriteQuotaAvailable;
        public readonly uint NamedPipeState;
        public readonly uint NamedPipeEnd;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint GetFileType(SafeFileHandle file);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeServerProcessId(
            SafeFileHandle pipe,
            out uint serverProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeClientProcessId(
            SafeFileHandle pipe,
            out uint clientProcessId);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryObject(
            SafeFileHandle handle,
            int objectInformationClass,
            nint objectInformation,
            int objectInformationLength,
            out int returnLength);

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationFile(
            SafeFileHandle handle,
            out IoStatusBlock ioStatusBlock,
            nint fileInformation,
            uint length,
            int fileInformationClass);
    }
}
