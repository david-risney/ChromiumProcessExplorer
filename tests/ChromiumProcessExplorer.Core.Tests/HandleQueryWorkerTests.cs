using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ChromiumProcessExplorer.Core.Discovery;

namespace ChromiumProcessExplorer.Core.Tests;

public sealed class HandleQueryWorkerTests
{
    [Fact]
    public async Task WorkerReturnsConnectedPipeEndpoints()
    {
        string pipeName = $"cpe-test-{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        Task serverConnection = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await serverConnection;

        using Process currentProcess = Process.GetCurrentProcess();
        Assert.True(NativeMethods.DuplicateHandle(
            currentProcess.Handle,
            server.SafePipeHandle.DangerousGetHandle(),
            currentProcess.Handle,
            out nint duplicateHandle,
            0,
            false,
            DuplicateSameAccess));

        string request = JsonSerializer.Serialize(
            new
            {
                RequestId = 1,
                HandleValue = duplicateHandle.ToInt64(),
            });
        await using MemoryStream input = new(
            Encoding.UTF8.GetBytes(request + Environment.NewLine));
        await using MemoryStream output = new();

        int exitCode = await HandleQueryWorker.RunAsync(input, output);

        Assert.Equal(0, exitCode);
        output.Position = 0;
        using StreamReader reader = new(output, leaveOpen: true);
        Assert.Equal("READY", await reader.ReadLineAsync());
        List<string> messages = [];
        while (await reader.ReadLineAsync() is { } message)
        {
            messages.Add(message);
        }

        Assert.Contains(
            messages,
            message => message.Contains(
                "\"Stage\":\"object-name\"",
                StringComparison.Ordinal));
        string responseLine = messages[^1];
        using JsonDocument response = JsonDocument.Parse(responseLine);
        JsonElement root = response.RootElement;
        Assert.True(root.GetProperty("IsPipe").GetBoolean());
        Assert.Contains(
            pipeName,
            root.GetProperty("ObjectName").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Environment.ProcessId, root.GetProperty("ServerProcessId").GetInt32());
        Assert.Equal(Environment.ProcessId, root.GetProperty("ClientProcessId").GetInt32());
        Assert.Equal("connected", root.GetProperty("State").GetString());
    }

    private const uint DuplicateSameAccess = 0x00000002;

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            nint sourceProcess,
            nint sourceHandle,
            nint targetProcess,
            out nint targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);
    }
}
