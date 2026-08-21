using System.Text.Json;
using ChromiumProcessExplorer.Core;
using ChromiumProcessExplorer.Core.Broker;

namespace ChromiumProcessExplorer.Mcp;

/// <summary>Minimal stdio MCP server backed by the typed local broker.</summary>
public sealed class McpServer(
    IChromiumBrokerClient client,
    TextReader input,
    TextWriter output,
    TextWriter error)
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Runs until standard input closes.</summary>
    public async Task RunAsync()
    {
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement? id = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await WriteResponseAsync(
                        Error(null, -32600, "Request must be a JSON object."));
                    continue;
                }

                string? method = root.TryGetProperty(
                    "method",
                    out JsonElement methodElement)
                    ? methodElement.GetString()
                    : null;
                id = root.TryGetProperty(
                    "id",
                    out JsonElement idElement)
                    ? idElement.Clone()
                    : null;
                if (method is null)
                {
                    await WriteResponseAsync(
                        Error(id, -32600, "Method is required."));
                    continue;
                }

                if (id is null)
                {
                    continue;
                }

                object response = method switch
                {
                    "initialize" => Result(
                        id,
                        new
                        {
                            protocolVersion = ProtocolVersion,
                            capabilities = new { tools = new { } },
                            serverInfo = new
                            {
                                name = "chromium-process-explorer",
                                version = ProductVersion.Version,
                            },
                        }),
                    "ping" => Result(id, new { }),
                    "tools/list" => Result(id, new { tools = CreateTools() }),
                    "tools/call" => await CallToolAsync(root, id),
                    _ => Error(id, -32601, "Method not found."),
                };
                await WriteResponseAsync(response);
            }
            catch (JsonException exception)
            {
                await WriteResponseAsync(
                    Error(id, -32700, exception.Message));
            }
            catch (Exception exception)
            {
                await error.WriteLineAsync(
                    $"cpe-mcp error: {exception.Message}");
                await WriteResponseAsync(
                    Error(id, -32603, "Internal error."));
            }
        }
    }

    private async Task<object> CallToolAsync(
        JsonElement root,
        JsonElement? id)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out JsonElement nameElement))
        {
            return Error(id, -32602, "Tool name is required.");
        }

        string? name = nameElement.GetString();
        JsonElement arguments = parameters.TryGetProperty(
            "arguments",
            out JsonElement argumentsElement)
            ? argumentsElement
            : JsonSerializer.SerializeToElement(new { });
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Error(id, -32602, "Tool arguments must be an object.");
        }

        (string Operation, object Arguments)? mapping;
        try
        {
            mapping = MapTool(name, arguments);
        }
        catch (JsonException exception)
        {
            return Error(id, -32602, exception.Message);
        }

        if (mapping is null)
        {
            return Error(id, -32602, "Unknown or invalid tool arguments.");
        }

        BrokerResponse response = await client.SendAsync(
            mapping.Value.Operation,
            mapping.Value.Arguments);
        string text = JsonSerializer.Serialize(response, JsonOptions);
        return Result(
            id,
            new
            {
                content = new[] { new { type = "text", text } },
                isError = !response.Ok,
            });
    }

    private static (string Operation, object Arguments)? MapTool(
        string? name,
        JsonElement arguments)
    {
        return name switch
        {
            "cpe_probe" => EmptyArguments(
                arguments,
                BrokerOperations.Probe),
            "cpe_process_details" => (
                BrokerOperations.ProcessDetails,
                new BrokerProcessDetailsArguments(
                    GetOptionalPositiveInt(
                        RequireProperties(arguments, "pid"),
                        "pid"))),
            "cpe_installations" => EmptyArguments(
                arguments,
                BrokerOperations.Installations),
            "cpe_diagnostics" => EmptyArguments(
                arguments,
                BrokerOperations.Diagnostics),
            "cpe_cdp" => EmptyArguments(
                arguments,
                BrokerOperations.Cdp),
            _ => null,
        };
    }

    private static (string Operation, object Arguments) EmptyArguments(
        JsonElement arguments,
        string operation)
    {
        RequireProperties(arguments);
        return (operation, new { });
    }

    private static JsonElement RequireProperties(
        JsonElement arguments,
        params string[] allowedNames)
    {
        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (!allowedNames.Contains(
                property.Name,
                StringComparer.Ordinal))
            {
                throw new JsonException(
                    $"Unknown tool argument '{property.Name}'.");
            }
        }

        return arguments;
    }

    private static int? GetOptionalPositiveInt(
        JsonElement arguments,
        string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (!value.TryGetInt32(out int result) || result <= 0)
        {
            throw new JsonException($"{name} must be a positive integer.");
        }

        return result;
    }

    private static object[] CreateTools()
    {
        return
        [
            Tool(
                "cpe_probe",
                "Check whether the elevated Chromium Process Explorer broker is available.",
                EmptySchema()),
            Tool(
                "cpe_process_details",
                "Get redacted process details for Chromium processes or one PID.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pid = new
                        {
                            type = "integer",
                            minimum = 1,
                            description = "Optional process ID.",
                        },
                    },
                    additionalProperties = false,
                }),
            Tool(
                "cpe_installations",
                "Discover Chromium browsers, runtimes, and applications.",
                EmptySchema()),
            Tool(
                "cpe_diagnostics",
                "Passively discover redacted logs, dumps, traces, and risky settings.",
                EmptySchema()),
            Tool(
                "cpe_cdp",
                "Discover configured and validated Chrome DevTools Protocol transports.",
                EmptySchema()),
        ];
    }

    private static object EmptySchema()
    {
        return new
        {
            type = "object",
            properties = new { },
            additionalProperties = false,
        };
    }

    private static object Tool(
        string name,
        string description,
        object inputSchema)
    {
        return new { name, description, inputSchema };
    }

    private static object Result(JsonElement? id, object result)
    {
        return new { jsonrpc = "2.0", id, result };
    }

    private static object Error(JsonElement? id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message },
        };
    }

    private async Task WriteResponseAsync(object response)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(
            response,
            JsonOptions));
        await output.FlushAsync();
    }
}
