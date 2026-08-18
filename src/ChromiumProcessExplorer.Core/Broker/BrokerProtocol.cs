using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromiumProcessExplorer.Core.Broker;

using Discovery;

/// <summary>Versioned operations exposed by the privileged broker.</summary>
public static class BrokerOperations
{
    /// <summary>Returns broker status and capabilities.</summary>
    public const string Probe = "probe";

    /// <summary>Returns redacted or explicitly sensitive process details.</summary>
    public const string ProcessDetails = "process-details";

    /// <summary>Returns installation discovery.</summary>
    public const string Installations = "installations";

    /// <summary>Returns passive diagnostic artifact discovery.</summary>
    public const string Diagnostics = "diagnostics";

    /// <summary>Returns CDP transport discovery.</summary>
    public const string Cdp = "cdp";

    /// <summary>Gets the complete approved operation set.</summary>
    public static IReadOnlySet<string> Approved { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Probe,
            ProcessDetails,
            Installations,
            Diagnostics,
            Cdp,
        };
}

/// <summary>One strict broker request.</summary>
public sealed record BrokerRequest(
    string Version,
    Guid RequestId,
    string Operation,
    JsonElement Arguments);

/// <summary>A stable structured broker error.</summary>
public sealed record BrokerError(
    string Code,
    string Message,
    IReadOnlyList<string>? MissingCapabilities = null,
    string? RecommendedAction = null);

/// <summary>One broker response.</summary>
public sealed record BrokerResponse(
    string Version,
    Guid RequestId,
    bool Ok,
    bool Partial,
    JsonElement? Result,
    BrokerError? Error);

/// <summary>Status returned by the broker probe.</summary>
public sealed record BrokerProbeResult(
    bool Installed,
    bool IsElevated,
    bool BrokerRunning,
    IReadOnlyList<string> AvailableCapabilities,
    IReadOnlyList<string> RequiresElevationFor,
    string RecommendedAction);

/// <summary>Arguments for process-details requests.</summary>
public sealed record BrokerProcessDetailsArguments(
    int? ProcessId = null);

/// <summary>Caller identity used by the broker authorization boundary.</summary>
public sealed record BrokerCallerIdentity(
    string UserSid,
    string LogonSessionId,
    int? ProcessId = null);

/// <summary>Compares named-pipe callers to the broker user and logon session.</summary>
public static class BrokerCallerAuthorizer
{
    /// <summary>Returns whether the caller belongs to the exact broker logon.</summary>
    public static bool IsAuthorized(
        BrokerCallerIdentity broker,
        BrokerCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(caller);
        return broker.UserSid.Equals(
                caller.UserSid,
                StringComparison.OrdinalIgnoreCase)
            && broker.LogonSessionId.Equals(
                caller.LogonSessionId,
                StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Length-prefixed JSON framing shared by broker clients and servers.</summary>
public static class BrokerMessageCodec
{
    /// <summary>Protocol version implemented by this build.</summary>
    public const string Version = "1.0";

    /// <summary>Maximum request or response payload.</summary>
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Writes one bounded JSON frame.</summary>
    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                $"Broker message exceeds {MaximumMessageBytes} bytes.");
        }

        byte[] length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads one bounded JSON frame.</summary>
    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken);
        int length = BitConverter.ToInt32(lengthBytes);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                $"Invalid broker message length {length}.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("Broker message contained null JSON.");
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The broker transport closed before a complete frame arrived.");
            }

            offset += read;
        }
    }
}

/// <summary>Executes the fixed read-only broker operation set.</summary>
public interface IBrokerOperationExecutor
{
    /// <summary>Executes one validated operation.</summary>
    ValueTask<BrokerResponse> ExecuteAsync(
        BrokerRequest request,
        bool isElevated,
        CancellationToken cancellationToken = default);
}

/// <summary>Maps broker operations directly to reusable Core APIs.</summary>
public sealed class ChromiumBrokerOperationExecutor : IBrokerOperationExecutor
{
    private static readonly JsonSerializerOptions ArgumentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ChromiumProcessDiscovery _discovery;
    private readonly string _workerPath;

    /// <summary>Creates an executor using built-in Windows discovery providers.</summary>
    public ChromiumBrokerOperationExecutor()
        : this(
            new ChromiumProcessDiscovery(),
            Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The current executable path is unavailable."))
    {
    }

    /// <summary>Creates an executor using a custom discovery coordinator.</summary>
    public ChromiumBrokerOperationExecutor(
        ChromiumProcessDiscovery discovery,
        string workerPath)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        _discovery = discovery;
        _workerPath = workerPath;
    }

    /// <inheritdoc />
    public async ValueTask<BrokerResponse> ExecuteAsync(
        BrokerRequest request,
        bool isElevated,
        CancellationToken cancellationToken = default)
    {
        if (!BrokerOperations.Approved.Contains(request.Operation))
        {
            return Error(
                request,
                "invalid_operation",
                "The requested broker operation is not approved.");
        }

        try
        {
            if (request.Operation is BrokerOperations.Probe
                or BrokerOperations.Installations
            or BrokerOperations.Diagnostics
            or BrokerOperations.Cdp)
            {
                EnsureEmptyArguments(request);
            }

            object result = request.Operation switch
            {
                BrokerOperations.Probe => CreateProbe(isElevated),
                BrokerOperations.ProcessDetails =>
                    await DiscoverProcessDetailsAsync(request, cancellationToken),
                BrokerOperations.Installations =>
                    await _discovery.DiscoverInstallationsAsync(
                        cancellationToken: cancellationToken),
                BrokerOperations.Diagnostics =>
                    await _discovery.DiscoverDiagnosticArtifactsAsync(
                        includeSensitiveValues: false,
                        cancellationToken: cancellationToken),
                BrokerOperations.Cdp =>
                    await _discovery.DiscoverCdpAsync(
                        new HandleQueryWorkerOptions(_workerPath, 0),
                        cancellationToken: cancellationToken),
                _ => throw new InvalidOperationException(
                    "The operation allowlist and dispatcher are inconsistent."),
            };
            return Success(request, result, partial: !isElevated);
        }
        catch (JsonException exception)
        {
            return Error(
                request,
                "malformed_request",
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Error(
                request,
                "invalid_arguments",
                exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Error(
                request,
                "access_denied",
                exception.Message,
                partial: true);
        }
    }

    private async ValueTask<ProcessDetailsResult> DiscoverProcessDetailsAsync(
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        BrokerProcessDetailsArguments arguments = DeserializeArguments(
            request,
            new BrokerProcessDetailsArguments());
        if (arguments.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "PID must be positive when supplied.");
        }

        return await _discovery.DiscoverProcessDetailsAsync(
            arguments.ProcessId,
            includeSensitiveValues: false,
            cancellationToken: cancellationToken);
    }

    private static T DeserializeArguments<T>(
        BrokerRequest request,
        T defaultValue)
    {
        return request.Arguments.ValueKind is JsonValueKind.Undefined
            or JsonValueKind.Null
            ? defaultValue
            : request.Arguments.Deserialize<T>(ArgumentJsonOptions)
                ?? throw new JsonException("Arguments must be a JSON object.");
    }

    private static void EnsureEmptyArguments(BrokerRequest request)
    {
        if (request.Arguments.EnumerateObject().Any())
        {
            throw new JsonException(
                $"{request.Operation} does not accept arguments.");
        }
    }

    private static BrokerProbeResult CreateProbe(bool isElevated)
    {
        string[] capabilities =
        [
            BrokerOperations.Probe,
            BrokerOperations.ProcessDetails,
            BrokerOperations.Installations,
            BrokerOperations.Diagnostics,
            BrokerOperations.Cdp,
        ];
        return new BrokerProbeResult(
            true,
            isElevated,
            true,
            capabilities,
            isElevated
                ? []
                : [BrokerOperations.ProcessDetails, BrokerOperations.Diagnostics],
            isElevated ? "none" : "restart_broker_elevated");
    }

    private static BrokerResponse Success(
        BrokerRequest request,
        object result,
        bool partial)
    {
        return new BrokerResponse(
            BrokerMessageCodec.Version,
            request.RequestId,
            true,
            partial,
            JsonSerializer.SerializeToElement(result),
            null);
    }

    private static BrokerResponse Error(
        BrokerRequest request,
        string code,
        string message,
        bool partial = false)
    {
        return new BrokerResponse(
            BrokerMessageCodec.Version,
            request.RequestId,
            false,
            partial,
            null,
            new BrokerError(code, message));
    }
}

/// <summary>Configuration for the named-pipe broker.</summary>
public sealed record BrokerServerOptions(
    string PipeName,
    TimeSpan RequestTimeout,
    string AuditLogPath)
{
    /// <summary>Creates secure per-user defaults.</summary>
    public static BrokerServerOptions CreateDefault()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new BrokerServerOptions(
            "ChromiumProcessExplorer.Broker.v1",
            TimeSpan.FromSeconds(90),
            Path.Combine(
                localAppData,
                "ChromiumProcessExplorer",
                "broker-audit.jsonl"));
    }
}

/// <summary>Same-user, same-logon named-pipe broker server.</summary>
public sealed class ChromiumBrokerServer
{
    private readonly BrokerServerOptions _options;
    private readonly IBrokerOperationExecutor _executor;
    private readonly BrokerCallerIdentity _brokerIdentity;
    private readonly bool _isElevated;

    /// <summary>Creates a broker server.</summary>
    public ChromiumBrokerServer(
        BrokerServerOptions options,
        IBrokerOperationExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executor);
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Request timeout must be positive.");
        }

        _options = options;
        _executor = executor;
        _brokerIdentity = GetCurrentIdentity();
        _isElevated = IsCurrentProcessElevated();
    }

    /// <summary>Runs until cancellation, accepting one request per connection.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = new(
                _options.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            await HandleConnectionAsync(pipe, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Guid requestId = Guid.Empty;
        string operation = "unknown";
        string status = "transport_error";
        BrokerCallerIdentity? caller = null;
        try
        {
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    serverCancellationToken);
            deadline.CancelAfter(_options.RequestTimeout);
            BrokerRequest request =
                await BrokerMessageCodec.ReadAsync<BrokerRequest>(
                    pipe,
                    deadline.Token);
            requestId = request.RequestId;
            operation = request.Operation;
            caller = GetClientIdentity(pipe);
            if (!BrokerCallerAuthorizer.IsAuthorized(_brokerIdentity, caller))
            {
                status = "unauthorized";
                return;
            }

            BrokerResponse response = ValidateRequest(request)
                ?? await _executor.ExecuteAsync(
                    request,
                    _isElevated,
                    deadline.Token);
            status = response.Ok ? "ok" : response.Error?.Code ?? "error";
            await BrokerMessageCodec.WriteAsync(
                pipe,
                response,
                deadline.Token);
        }
        catch (OperationCanceledException)
        {
            status = "timeout";
        }
        catch (UnauthorizedAccessException exception)
        {
            status = $"unauthorized:{exception.GetType().Name}";
            Console.Error.WriteLine(
                $"warning: broker rejected caller: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is InvalidDataException
            or EndOfStreamException
            or IOException
            or JsonException)
        {
            status = $"malformed_request:{exception.GetType().Name}";
            Console.Error.WriteLine(
                $"warning: broker request failed: {exception.Message}");
        }
        finally
        {
            stopwatch.Stop();
            await WriteAuditAsync(
                requestId,
                operation,
                caller,
                status,
                stopwatch.Elapsed);
        }
    }

    private static BrokerResponse? ValidateRequest(BrokerRequest request)
    {
        if (request.Version != BrokerMessageCodec.Version)
        {
            return CreateValidationError(
                request,
                "unsupported_version",
                $"Expected broker protocol {BrokerMessageCodec.Version}.");
        }

        if (request.RequestId == Guid.Empty)
        {
            return CreateValidationError(
                request,
                "malformed_request",
                "RequestId must be a non-empty GUID.");
        }

        if (!BrokerOperations.Approved.Contains(request.Operation))
        {
            return CreateValidationError(
                request,
                "invalid_operation",
                "The requested operation is not approved.");
        }

        if (request.Arguments.ValueKind is not (
            JsonValueKind.Object
            or JsonValueKind.Null
            or JsonValueKind.Undefined))
        {
            return CreateValidationError(
                request,
                "malformed_request",
                "Arguments must be a JSON object.");
        }

        return null;
    }

    private static BrokerResponse CreateValidationError(
        BrokerRequest request,
        string code,
        string message)
    {
        return new BrokerResponse(
            BrokerMessageCodec.Version,
            request.RequestId,
            false,
            false,
            null,
            new BrokerError(code, message));
    }

    private async Task WriteAuditAsync(
        Guid requestId,
        string operation,
        BrokerCallerIdentity? caller,
        string status,
        TimeSpan elapsed)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_options.AuditLogPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            object audit = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                RequestId = requestId,
                Operation = operation,
                CallerSid = caller?.UserSid,
                CallerLogonSessionId = caller?.LogonSessionId,
                CallerProcessId = caller?.ProcessId,
                Status = status,
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
            };
            await File.AppendAllTextAsync(
                _options.AuditLogPath,
                JsonSerializer.Serialize(audit) + Environment.NewLine);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: broker audit write failed: {exception.Message}");
        }
    }

    private static BrokerCallerIdentity GetClientIdentity(
        NamedPipeServerStream pipe)
    {
        int? processId = GetClientProcessId(pipe);
        BrokerCallerIdentity? identity = null;
        pipe.RunAsClient(() =>
        {
            using WindowsIdentity client = WindowsIdentity.GetCurrent(true)
                ?? throw new UnauthorizedAccessException(
                    "The impersonated client identity was unavailable.");
            identity = CreateIdentity(client, processId);
        });
        return identity
            ?? throw new UnauthorizedAccessException(
                "The named-pipe client identity was unavailable.");
    }

    private static BrokerCallerIdentity GetCurrentIdentity()
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        return CreateIdentity(current, Environment.ProcessId);
    }

    private static BrokerCallerIdentity CreateIdentity(
        WindowsIdentity identity,
        int? processId)
    {
        string userSid = identity.User?.Value
            ?? throw new UnauthorizedAccessException(
                "The Windows user SID was unavailable.");
        string logonSessionId = GetAuthenticationId(identity);
        return new BrokerCallerIdentity(
            userSid,
            logonSessionId,
            processId);
    }

    private static string GetAuthenticationId(WindowsIdentity identity)
    {
        if (!GetTokenInformation(
            identity.AccessToken.DangerousGetHandle(),
            TokenInformationClass.TokenStatistics,
            out TokenStatistics statistics,
            Marshal.SizeOf<TokenStatistics>(),
            out _))
        {
            throw new UnauthorizedAccessException(
                $"Token statistics query failed with {Marshal.GetLastWin32Error()}.");
        }

        return $"{statistics.AuthenticationId.HighPart:X8}:"
            + $"{statistics.AuthenticationId.LowPart:X8}";
    }

    private static int? GetClientProcessId(NamedPipeServerStream pipe)
    {
        return GetNamedPipeClientProcessId(
                pipe.SafePipeHandle.DangerousGetHandle(),
                out uint processId)
            ? checked((int)processId)
            : null;
    }

    /// <summary>Returns whether the current process token is elevated.</summary>
    public static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        TokenInformationClass tokenInformationClass,
        out TokenStatistics tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        nint pipe,
        out uint clientProcessId);

    private enum TokenInformationClass
    {
        TokenStatistics = 10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;

        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;

        public Luid AuthenticationId;

        public long ExpirationTime;

        public int TokenType;

        public int ImpersonationLevel;

        public uint DynamicCharged;

        public uint DynamicAvailable;

        public uint GroupCount;

        public uint PrivilegeCount;

        public Luid ModifiedId;
    }
}

/// <summary>Client abstraction used by CLI and MCP bridges.</summary>
public interface IChromiumBrokerClient
{
    /// <summary>Sends one approved operation.</summary>
    ValueTask<BrokerResponse> SendAsync(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Client for one-request-per-connection broker calls.</summary>
public sealed class ChromiumBrokerClient : IChromiumBrokerClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a broker client.</summary>
    public ChromiumBrokerClient(string pipeName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Timeout must be positive.");
        }

        _pipeName = pipeName;
        _timeout = timeout;
    }

    /// <summary>Sends one request to the local broker.</summary>
    public async ValueTask<BrokerResponse> SendAsync(
        string operation,
        object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!BrokerOperations.Approved.Contains(operation))
        {
            return ClientError(
                Guid.NewGuid(),
                "invalid_operation",
                "The requested operation is not approved.");
        }

        Guid requestId = Guid.NewGuid();
        await using NamedPipeClientStream pipe = new(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            int connectTimeout = checked((int)Math.Min(
                _timeout.TotalMilliseconds,
                int.MaxValue));
            await pipe.ConnectAsync(connectTimeout, cancellationToken);
            using CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            deadline.CancelAfter(_timeout);
            BrokerRequest request = new(
                BrokerMessageCodec.Version,
                requestId,
                operation,
                JsonSerializer.SerializeToElement(arguments ?? new { }));
            await BrokerMessageCodec.WriteAsync(pipe, request, deadline.Token);
            BrokerResponse response =
                await BrokerMessageCodec.ReadAsync<BrokerResponse>(
                    pipe,
                    deadline.Token);
            if (response.RequestId != requestId)
            {
                return ClientError(
                    requestId,
                    "stale_response",
                    "The broker response request ID did not match.");
            }

            return response;
        }
        catch (TimeoutException)
        {
            return ClientError(
                requestId,
                "broker_not_running",
                "The privileged broker is not running.",
                "start_admin_broker");
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return ClientError(
                requestId,
                "broker_timeout",
                "The broker request exceeded its deadline.",
                "retry_or_restart_broker");
        }
        catch (IOException exception)
        {
            return ClientError(
                requestId,
                "broker_not_running",
                exception.Message,
                "start_admin_broker");
        }
    }

    private static BrokerResponse ClientError(
        Guid requestId,
        string code,
        string message,
        string? recommendedAction = null)
    {
        return new BrokerResponse(
            BrokerMessageCodec.Version,
            requestId,
            false,
            true,
            null,
            new BrokerError(
                code,
                message,
                RecommendedAction: recommendedAction));
    }
}
