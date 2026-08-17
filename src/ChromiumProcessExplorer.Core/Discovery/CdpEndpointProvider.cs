using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ChromiumProcessExplorer.Core.Discovery;

/// <summary>Discovers configured CDP transports and validates loopback endpoints.</summary>
public sealed class CdpEndpointProvider : ICdpEndpointProvider
{
    private const int MaximumVersionResponseBytes = 64 * 1024;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    /// <summary>Creates a provider with loopback-only, non-proxying HTTP behavior.</summary>
    public CdpEndpointProvider(TimeSpan? requestTimeout = null)
        : this(
            new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                },
                disposeHandler: true),
            requestTimeout)
    {
    }

    /// <summary>Creates a provider using a caller-supplied HTTP client.</summary>
    public CdpEndpointProvider(
        HttpClient httpClient,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(1);
        if (_requestTimeout <= TimeSpan.Zero
            || _requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The CDP request timeout must be finite and positive.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<CdpDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ProcessSnapshotEntry> processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);

        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        List<CdpTransportInfo> transports = [];

        foreach (ProcessSnapshotEntry process in processes
            .Where(process => !process.IsProcessIdReused)
            .OrderBy(process => process.ProcessId))
        {
            ChromiumCommandLine commandLine =
                ChromiumCommandLine.Parse(process.CommandLine);
            if (commandLine.HasSwitch("type"))
            {
                continue;
            }

            AddPipeTransport(process, commandLine, transports);

            if (!commandLine.HasSwitch("remote-debugging-port"))
            {
                continue;
            }

            transports.Add(await DiscoverTcpAsync(
                process,
                commandLine.GetSwitchValue("remote-debugging-port"),
                cancellationToken));
        }

        return new CdpDiscoveryResult(capturedAt, transports);
    }

    private static void AddPipeTransport(
        ProcessSnapshotEntry process,
        ChromiumCommandLine commandLine,
        List<CdpTransportInfo> transports)
    {
        bool hasPipeSwitch = commandLine.HasSwitch("remote-debugging-pipe");
        string? ioPipes =
            commandLine.GetSwitchValue("remote-debugging-io-pipes");
        if (!hasPipeSwitch && ioPipes is null)
        {
            return;
        }

        List<string> evidence = [];
        if (hasPipeSwitch)
        {
            evidence.Add("--remote-debugging-pipe command-line switch.");
        }

        if (ioPipes is not null)
        {
            evidence.Add(
                "--remote-debugging-io-pipes identifies inherited pipe handles.");
        }

        transports.Add(new CdpTransportInfo(
            process.ProcessId,
            CdpTransportKind.Pipe,
            CdpTransportStatus.AlreadyOwned,
            commandLine.GetSwitchValue("remote-debugging-pipe") ?? ioPipes,
            null,
            "command-line",
            null,
            null,
            null,
            null,
            "The point-to-point debugging pipe is private to its existing controller.",
            evidence));
    }

    private async ValueTask<CdpTransportInfo> DiscoverTcpAsync(
        ProcessSnapshotEntry process,
        string? configuredValue,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(
            configuredValue,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int configuredPort)
            || configuredPort is < 0 or > ushort.MaxValue)
        {
            return CreateUnavailable(
                process.ProcessId,
                configuredValue,
                null,
                "command-line",
                "The --remote-debugging-port value is not a valid TCP port.");
        }

        (int? activePort, string? activePortError) = configuredPort == 0
            ? await TryReadActivePortAsync(process, cancellationToken)
            : (configuredPort, null);
        int? port = activePort;
        string source = configuredPort == 0
            ? "DevToolsActivePort"
            : "command-line";
        if (activePortError is not null)
        {
            return CreateUnavailable(
                process.ProcessId,
                configuredValue,
                null,
                source,
                activePortError);
        }

        if (port is null)
        {
            return new CdpTransportInfo(
                process.ProcessId,
                CdpTransportKind.Tcp,
                CdpTransportStatus.Configured,
                configuredValue,
                null,
                source,
                null,
                null,
                null,
                null,
                "An ephemeral port is configured, but DevToolsActivePort is unavailable.",
                ["--remote-debugging-port command-line switch."]);
        }

        return await ProbeVersionEndpointAsync(
            process.ProcessId,
            configuredValue,
            port.Value,
            source,
            cancellationToken);
    }

    private static async ValueTask<(int? Port, string? Error)>
        TryReadActivePortAsync(
        ProcessSnapshotEntry process,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(process.UserDataDirectory))
        {
            return (null, null);
        }

        string path = Path.Combine(
            process.UserDataDirectory,
            "DevToolsActivePort");
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken);
            return lines.Length > 0
                && int.TryParse(
                    lines[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int port)
                && port is > 0 and <= ushort.MaxValue
                    ? (port, null)
                    : (null, "DevToolsActivePort does not contain a valid port.");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            return (
                null,
                $"DevToolsActivePort could not be read: {exception.Message}");
        }
    }

    private async ValueTask<CdpTransportInfo> ProbeVersionEndpointAsync(
        int processId,
        string? configuredValue,
        int port,
        string source,
        CancellationToken cancellationToken)
    {
        Uri endpoint = new($"http://127.0.0.1:{port}/json/version");
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CreateUnavailable(
                    processId,
                    configuredValue,
                    port,
                    source,
                    $"The version endpoint returned HTTP "
                        + $"{(int)response.StatusCode}.");
            }

            byte[] payload = await ReadBoundedAsync(
                response.Content,
                timeout.Token);
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string? webSocketUrl = GetString(root, "webSocketDebuggerUrl");
            if (!TryValidateWebSocketUrl(webSocketUrl, port))
            {
                return CreateUnavailable(
                    processId,
                    configuredValue,
                    port,
                    source,
                    "The version response did not contain a loopback CDP WebSocket URL.");
            }

            return new CdpTransportInfo(
                processId,
                CdpTransportKind.Tcp,
                CdpTransportStatus.Validated,
                configuredValue,
                port,
                source,
                endpoint.AbsoluteUri,
                webSocketUrl,
                GetString(root, "Browser"),
                GetString(root, "Protocol-Version"),
                null,
                [
                    "--remote-debugging-port command-line switch.",
                    "Validated /json/version and webSocketDebuggerUrl.",
                ]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateUnavailable(
                processId,
                configuredValue,
                port,
                source,
                $"The loopback CDP probe exceeded {_requestTimeout.TotalMilliseconds:F0} ms.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or JsonException)
        {
            return CreateUnavailable(
                processId,
                configuredValue,
                port,
                source,
                exception.Message);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(
            cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using MemoryStream result = new();
            while (true)
            {
                int count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    return result.ToArray();
                }

                if (result.Length + count > MaximumVersionResponseBytes)
                {
                    throw new IOException(
                        "The CDP version response exceeded the 64 KiB limit.");
                }

                result.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool TryValidateWebSocketUrl(string? value, int port)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "ws" or "wss"
            && uri.IsLoopback
            && uri.Port == port
            && uri.AbsolutePath.StartsWith(
                "/devtools/",
                StringComparison.Ordinal);
    }

    private static CdpTransportInfo CreateUnavailable(
        int processId,
        string? configuredValue,
        int? port,
        string source,
        string error)
    {
        return new CdpTransportInfo(
            processId,
            CdpTransportKind.Tcp,
            CdpTransportStatus.Unavailable,
            configuredValue,
            port,
            source,
            port is null
                ? null
                : $"http://127.0.0.1:{port}/json/version",
            null,
            null,
            null,
            error,
            ["--remote-debugging-port command-line switch."]);
    }
}
