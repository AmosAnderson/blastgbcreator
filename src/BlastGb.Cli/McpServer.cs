using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Serialization;

namespace BlastGb.Cli;

/// <summary>A bounded, tools-only MCP stdio adapter over the ordinary CLI.</summary>
public static class McpServer
{
    private const int MaxRequestBytes = 2 * 1024 * 1024;
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int MaxArgumentBytes = 32 * 1024;
    private const int MaxArgumentsBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18"];

    public static async Task<int> ServeAsync(
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> runCli,
        Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var connection = new Connection(runCli, output, connectionCancellation.Token);
        using var pending = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var oversized = false;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                var offset = 0;
                while (offset < count)
                {
                    var newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                    var end = newline < 0 ? count : newline;
                    if (!oversized)
                    {
                        if (pending.Length + end - offset > MaxRequestBytes)
                        {
                            pending.SetLength(0);
                            oversized = true;
                            await connection.RpcErrorAsync(null, -32700, "Message exceeded the 2 MiB request limit.");
                        }
                        else
                        {
                            pending.Write(buffer, offset, end - offset);
                        }
                    }
                    if (newline >= 0)
                    {
                        if (!oversized)
                            await connection.ReceiveAsync(pending.GetBuffer().AsMemory(0, (int)pending.Length));
                        pending.SetLength(0);
                        oversized = false;
                    }
                    offset = end + 1;
                }
            }
            if (pending.Length != 0 && !oversized)
                await connection.RpcErrorAsync(null, -32700, "A stdio message must end with a newline.");
            await connection.DrainAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            // Every exit owns the lifetime of its workers. Preserve the original transport
            // error while cancelling active calls and awaiting their cleanup before Dispose.
            try { await connectionCancellation.CancelAsync(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
            try { await connection.DrainAsync(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { }
        }
        return 0;
    }

    private sealed class Connection(
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> runCli,
        Stream output, CancellationToken cancellationToken) : IDisposable
    {
        private bool initialized;
        private bool ready;
        private Task queued = Task.CompletedTask;
        private readonly SemaphoreSlim queueSlots = new(128);
        private readonly SemaphoreSlim outputLock = new(1);
        private readonly Dictionary<string, CancellationTokenSource> requests = new(StringComparer.Ordinal);
        private readonly object requestLock = new();

        public Task DrainAsync() => queued;

        private async Task QueueAsync(Func<Task> action, Func<Task>? overloaded = null)
        {
            if (!queueSlots.Wait(0))
            {
                await (overloaded ?? action)();
                return;
            }
            var previous = queued;
            queued = Task.Run(async () =>
            {
                try
                {
                    await previous;
                    cancellationToken.ThrowIfCancellationRequested();
                    await action();
                }
                finally { queueSlots.Release(); }
            }, CancellationToken.None);
        }

        public void Dispose()
        {
            lock (requestLock)
            {
                foreach (var source in requests.Values) source.Dispose();
                requests.Clear();
            }
            queueSlots.Dispose();
            outputLock.Dispose();
        }

        public Task RpcErrorAsync(JsonNode? id, int code, string message) => QueueAsync(() => SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }));

        private Task ResultAsync(JsonNode id, JsonNode result) => QueueAsync(() => SendResultAsync(id, result));

        private Task SendResultAsync(JsonNode id, JsonNode result) => SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result
        });

        private async Task SendAsync(JsonObject message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);
            if (bytes.Length + 1 > MaxResponseBytes)
            {
                message = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = message["id"]?.DeepClone(),
                    ["result"] = ToolError("MCP_OUTPUT_LIMIT", "Command response exceeded the 16 MiB response limit.")
                };
                bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);
            }
            await outputLock.WaitAsync(cancellationToken);
            try
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            finally { outputLock.Release(); }
        }

        public async Task ReceiveAsync(ReadOnlyMemory<byte> line)
        {
            JsonNode? parsed;
            try
            {
                using var document = JsonDocument.Parse(StrictUtf8.GetString(line.Span));
                ValidateJson(document.RootElement);
                parsed = JsonNode.Parse(document.RootElement.GetRawText());
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
            {
                await RpcErrorAsync(null, -32700, "Invalid UTF-8 JSON message.");
                return;
            }
            if (parsed is not JsonObject message || Text(message["jsonrpc"]) != "2.0" || Text(message["method"]) is not { } method)
            {
                await RpcErrorAsync(null, -32600, "Expected one JSON-RPC 2.0 request or notification.");
                return;
            }
            if (!message.ContainsKey("id"))
            {
                if (method == "notifications/initialized" && initialized)
                    ready = true;
                if (method == "notifications/cancelled" && message["params"] is JsonObject cancelled && ValidId(cancelled["requestId"]))
                {
                    lock (requestLock)
                        if (requests.TryGetValue(RequestKey(cancelled["requestId"]!), out var source)) source.Cancel();
                }
                return;
            }
            var id = message["id"];
            if (!ValidId(id))
            {
                await RpcErrorAsync(null, -32600, "Request id must be a string (at most 4 KiB) or a safe integer.");
                return;
            }
            var parameters = message["params"] as JsonObject;
            if (message.ContainsKey("params") && parameters is null)
            {
                await RpcErrorAsync(id, -32602, "params must be an object.");
                return;
            }
            if (method == "ping")
            {
                await ResultAsync(id!, new JsonObject());
                return;
            }
            if (method == "initialize")
            {
                if (initialized)
                {
                    await RpcErrorAsync(id, -32600, "The connection has already initialized.");
                    return;
                }
                if (parameters is null || Text(parameters["protocolVersion"]) is not { } version ||
                    parameters["capabilities"] is not JsonObject || parameters["clientInfo"] is not JsonObject client ||
                    Text(client["name"]) is null || Text(client["version"]) is null)
                {
                    await RpcErrorAsync(id, -32602, "initialize requires protocolVersion, capabilities, and clientInfo.");
                    return;
                }
                initialized = true;
                await ResultAsync(id!, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersions.Contains(version, StringComparer.Ordinal) ? version : ProtocolVersions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "blast-gb-tools", ["version"] = AutomationContracts.Version },
                    ["instructions"] = "Use gb_capabilities and gb_schema before gb_cli. Commands use the server working directory and ordinary CLI file permissions. Tool results contain structuredContent.exitCode and structuredContent.envelope."
                });
                return;
            }
            if (method is not ("tools/list" or "tools/call"))
            {
                await RpcErrorAsync(id, -32601, $"Unknown method: {method}");
                return;
            }
            if (!ready)
            {
                await RpcErrorAsync(id, -32002, "Complete initialize and notifications/initialized before using tools.");
                return;
            }
            if (method == "tools/list")
            {
                if (parameters?.ContainsKey("cursor") == true)
                {
                    await RpcErrorAsync(id, -32602, "This fixed tool list does not accept a pagination cursor.");
                    return;
                }
                await ResultAsync(id!, new JsonObject { ["tools"] = Tools() });
                return;
            }
            if (parameters is null || Text(parameters["name"]) is not { } name)
            {
                await RpcErrorAsync(id, -32602, "tools/call requires a tool name.");
                return;
            }
            if (name is not ("gb_cli" or "gb_capabilities" or "gb_schema"))
            {
                await RpcErrorAsync(id, -32602, $"Unknown tool: {name}");
                return;
            }
            var arguments = parameters["arguments"] ?? new JsonObject();
            if (arguments is not JsonObject args)
            {
                await RpcErrorAsync(id, -32602, "Tool arguments must be an object.");
                return;
            }
            var requestKey = RequestKey(id!);
            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool duplicate;
            lock (requestLock) duplicate = !requests.TryAdd(requestKey, requestCancellation);
            if (duplicate)
            {
                requestCancellation.Dispose();
                await RpcErrorAsync(id, -32600, "A tool call with this request id is already pending.");
                return;
            }
            void CompleteRequest()
            {
                lock (requestLock) requests.Remove(requestKey);
                requestCancellation.Dispose();
            }
            await QueueAsync(async () =>
            {
                try
                {
                    JsonObject result;
                    try
                    {
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        result = name switch
                        {
                            "gb_cli" when OnlyKeys(args, "arguments") => await InvokeAsync(args["arguments"], requestCancellation.Token),
                            "gb_cli" => ToolError("MCP_INVALID_ARGUMENTS", "gb_cli accepts only arguments."),
                            "gb_capabilities" when OnlyKeys(args) => await InvokeAsync(new JsonArray("capabilities"), requestCancellation.Token),
                            "gb_capabilities" => ToolError("MCP_INVALID_ARGUMENTS", "gb_capabilities accepts no arguments."),
                            "gb_schema" when OnlyKeys(args, "name") && Text(args["name"]) is { } schema &&
                                AutomationContracts.SchemaNames.Contains(schema, StringComparer.Ordinal) =>
                                await InvokeAsync(new JsonArray("schema", schema), requestCancellation.Token),
                            _ => ToolError("MCP_INVALID_ARGUMENTS", $"name must be one of: {string.Join(", ", AutomationContracts.SchemaNames)}.")
                        };
                    }
                    catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        result = ToolError("CANCELLED", "The MCP request was cancelled.");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        result = ToolError("MCP_COMMAND_FAILED", exception.Message);
                    }
                    await SendResultAsync(id!, result);
                }
                finally
                {
                    CompleteRequest();
                }
            }, async () =>
            {
                try { await SendResultAsync(id!, ToolError("MCP_BUSY", "The server already has 128 queued messages; retry after a pending call finishes.")); }
                finally { CompleteRequest(); }
            });
        }

        private async Task<JsonObject> InvokeAsync(JsonNode? values, CancellationToken requestCancellation)
        {
            if (values is not JsonArray { Count: >= 1 and <= 4096 } arguments)
                return ToolError("MCP_INVALID_ARGUMENTS", "arguments must contain 1..4096 strings.");
            var args = new List<string>();
            var total = 0;
            foreach (var argument in arguments)
            {
                if (Text(argument) is not { } value || value.Contains('\0'))
                    return ToolError("MCP_INVALID_ARGUMENTS", "Every argument must be a string without NUL characters.");
                var bytes = Encoding.UTF8.GetByteCount(value);
                total += bytes;
                if (bytes > MaxArgumentBytes || total > MaxArgumentsBytes)
                    return ToolError("MCP_INVALID_ARGUMENTS", "Arguments are limited to 32 KiB each and 1 MiB combined.");
                args.Add(value);
            }
            if (CliApplication.SelectsMcpCommand([.. args]))
                return ToolError("MCP_RECURSION", "The MCP server cannot invoke the mcp command recursively.");
            var separator = args.IndexOf("--");
            if (!args.Take(separator < 0 ? args.Count : separator).Contains("--json", StringComparer.Ordinal))
                args.Insert(0, "--json");
            var budget = new CaptureBudget();
            using var stdout = new CaptureWriter(budget);
            using var stderr = new CaptureWriter(budget);
            var exitCode = await runCli([.. args], stdout, stderr, requestCancellation);
            if (budget.Exceeded)
                return ToolError("MCP_OUTPUT_LIMIT", "CLI output exceeded the 16 MiB response limit.");
            JsonObject? envelope;
            try { envelope = JsonNode.Parse(stdout.ToString()) as JsonObject; }
            catch (JsonException) { envelope = null; }
            if (envelope?["schemaVersion"] is not JsonValue schemaVersion || !schemaVersion.TryGetValue<int>(out var schema) || schema != 1 ||
                envelope["ok"] is not JsonValue success || !success.TryGetValue<bool>(out var ok))
                return ToolError("MCP_INVALID_OUTPUT", "The CLI did not return one JSON result envelope.");
            var structured = new JsonObject { ["exitCode"] = exitCode, ["envelope"] = envelope };
            if (stderr.ToString() is { Length: > 0 } error)
                structured["stderr"] = error;
            return ToolResult(structured, exitCode != 0 || !ok);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string RequestKey(JsonNode id)
    {
        if (Text(id) is { } text) return "s:" + text;
        using var parsed = JsonDocument.Parse(id.ToJsonString());
        _ = JsonInteger.TryGetInt64(parsed.RootElement, out long number);
        return "n:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate object property.");
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateJson(item);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            // JsonDocument defers decoding escaped strings. Validate them before
            // request dispatch so malformed UTF-16 cannot terminate the session.
            _ = element.GetString();
        }
    }
    private static bool OnlyKeys(JsonObject value, params string[] keys) => value.All(pair => keys.Contains(pair.Key, StringComparer.Ordinal));
    private static bool ValidId(JsonNode? node) => node is JsonValue value &&
        (value.TryGetValue<string>(out var text) && Encoding.UTF8.GetByteCount(text) <= 4096 ||
         value.TryGetValue<JsonElement>(out var element) && JsonInteger.TryGetInt64(element, out var number) &&
         number is >= -9007199254740991 and <= 9007199254740991);

    private static JsonObject ToolError(string code, string message) => ToolResult(new JsonObject
    {
        ["exitCode"] = null,
        ["envelope"] = null,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    }, true);

    private static JsonObject ToolResult(JsonObject structured, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString(JsonDefaults.Options) }),
        ["structuredContent"] = structured,
        ["isError"] = isError
    };

    private static JsonArray Tools() => new(
        Tool("gb_cli", "Run one Blast GB command using the same CLI and JSON contract. Pass arguments without the executable or --json. Paths use the server working directory. Commands can create files; ordinary destination and overwrite rules apply.",
            new JsonObject
            {
                ["arguments"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxArgumentBytes },
                    ["minItems"] = 1,
                    ["maxItems"] = 4096
                }
            }, ["arguments"], false),
        Tool("gb_capabilities", "Discover the CLI commands, options, schemas, toolchains, and file side effects.", new JsonObject(), [], true),
        Tool("gb_schema", "Read a bundled JSON Schema by name.", new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(AutomationContracts.SchemaNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) }
        }, ["name"], true));

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required, bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = !readOnly, ["idempotentHint"] = readOnly, ["openWorldHint"] = false }
    };

    private sealed class CaptureBudget
    {
        public long Bytes { get; set; }
        public bool Exceeded => Bytes > MaxResponseBytes;
    }

    private sealed class CaptureWriter(CaptureBudget budget) : TextWriter
    {
        private readonly StringBuilder text = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value)
        {
            if (value is null) return;
            budget.Bytes += Encoding.UTF8.GetByteCount(value);
            if (!budget.Exceeded) text.Append(value);
        }
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
        public override string ToString() => text.ToString();
    }
}
