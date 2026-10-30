using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BlastGb.Cli;

namespace BlastGb.Cli.Tests;

public sealed class McpTests
{
    private static readonly string Initialize = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        """ + "\n";

    private static async Task<JsonObject[]> ExchangeAsync(string input,
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>>? runner = null) =>
        await ExchangeAsync(Encoding.UTF8.GetBytes(input), runner);

    private static async Task<JsonObject[]> ExchangeAsync(byte[] input,
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>>? runner = null)
    {
        using var source = new MemoryStream(input);
        using var destination = new MemoryStream();
        Assert.Equal(0, await McpServer.ServeAsync(runner ?? CliApplication.RunAsync, source, destination));
        return Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }

    private static string Call(int id, string name, object? arguments = null) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } }) + "\n";

    private sealed class MessageStream : Stream
    {
        private readonly Channel<byte[]> messages = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private byte[] current = [];
        private int offset;
        public void Send(string message) => Assert.True(messages.Writer.TryWrite(Encoding.UTF8.GetBytes(message)));
        public void Complete(Exception? exception = null) => messages.Writer.Complete(exception);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset == current.Length)
            {
                if (!await messages.Reader.WaitToReadAsync(cancellationToken)) return 0;
                current = await messages.Reader.ReadAsync(cancellationToken);
                offset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTransportCancelsActiveWorkAndWaitsForCleanup(bool cleanupFails)
    {
        using var source = new MessageStream();
        using var destination = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportFailure = new IOException("transport read failed");
        int calls = 0;
        CancellationToken activeToken = default;
        string directory = Directory.CreateTempSubdirectory("blast-mcp-failure-").FullName;
        string outputPath = Path.Combine(directory, "unexpected.txt");
        async Task<int> Runner(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                File.WriteAllText(outputPath, "queued mutation");
                return 0;
            }
            activeToken = cancellationToken;
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                File.WriteAllText(outputPath, "active mutation");
                return 0;
            }
            finally
            {
                cleaningUp.SetResult();
                await releaseCleanup.Task;
                cleanupFinished.SetResult();
                if (cleanupFails) throw new IOException("secondary cleanup failure");
            }
        }
        source.Send(Initialize + Call(2, "gb_cli", new { arguments = new[] { "version" } }) +
            Call(3, "gb_cli", new { arguments = new[] { "version" } }));
        var server = McpServer.ServeAsync(Runner, source, destination, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            source.Complete(transportFailure);
            await cleaningUp.Task.WaitAsync(timeout.Token);
            Assert.True(activeToken.IsCancellationRequested);
            Assert.False(server.IsCompleted);
            releaseCleanup.SetResult();
            var failure = await Assert.ThrowsAsync<IOException>(() => server.WaitAsync(timeout.Token));
            Assert.Same(transportFailure, failure);
            Assert.True(cleanupFinished.Task.IsCompletedSuccessfully);
            Assert.Equal(1, calls);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await timeout.CancelAsync();
            try { await server; }
            catch (Exception) { }
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task EndOfInputDrainsWorkWithoutCancellingItsToken()
    {
        using var source = new MessageStream();
        using var destination = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Runner(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            Assert.False(cancellationToken.IsCancellationRequested);
            return await CliApplication.RunAsync(args, output, error, cancellationToken);
        }
        source.Send(Initialize + Call(2, "gb_cli", new { arguments = new[] { "version" } }));
        var server = McpServer.ServeAsync(Runner, source, destination, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        source.Complete();
        release.SetResult();
        Assert.Equal(0, await server.WaitAsync(timeout.Token));
        var responses = Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.False(JsonNode.Parse(responses[1])!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CancelsActiveAndQueuedCallsThenContinuesSerialExecution()
    {
        using var source = new MessageStream();
        using var destination = new MemoryStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, active = 0, maximumActive = 0;
        async Task<int> Runner(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref calls);
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            try
            {
                if (count == 1)
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return await CliApplication.RunAsync(args, output, error, cancellationToken);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        source.Send(Initialize + Call(2, "gb_cli", new { arguments = new[] { "version" } }));
        var server = McpServer.ServeAsync(Runner, source, destination);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Send(Call(3, "gb_cli", new { arguments = new[] { "version" } }) +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":3e0}}\n" +
            Call(4, "gb_cli", new { arguments = new[] { "version" } }) +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}\n");
        source.Complete();
        Assert.Equal(0, await server.WaitAsync(TimeSpan.FromSeconds(10)));
        var results = Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(4, results.Length);
        Assert.Equal("CANCELLED", results[1]["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal("CANCELLED", results[2]["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
        Assert.False(results[3]["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(2, calls);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task SaturatedQueueStillReadsCancellationNotifications()
    {
        using var source = new MessageStream();
        using var destination = new MemoryStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<int> Runner(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await CliApplication.RunAsync(args, output, error, cancellationToken);
        }
        source.Send(Initialize + Call(2, "gb_cli", new { arguments = new[] { "version" } }));
        var server = McpServer.ServeAsync(Runner, source, destination);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Send(string.Concat(Enumerable.Range(3, 140).Select(id => Call(id, "gb_cli", new { arguments = new[] { "version" } }))) +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}\n");
        source.Complete();
        Assert.Equal(0, await server.WaitAsync(TimeSpan.FromSeconds(10)));
        var results = Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(142, results.Length);
        Assert.Contains(results, result => result["result"]?["structuredContent"]?["error"]?["code"]?.GetValue<string>() == "MCP_BUSY");
        var cancelled = Assert.Single(results, result => result["id"]?.GetValue<int>() == 2);
        Assert.Equal("CANCELLED", cancelled["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("unknown-version", "2025-11-25")]
    public async Task NegotiatesProtocolAndDiscoversTools(string requested, string expected)
    {
        var results = await ExchangeAsync(Initialize.Replace("2025-11-25", requested, StringComparison.Ordinal) +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n");
        Assert.Equal(2, results.Length);
        Assert.Equal(expected, results[0]["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(AutomationContracts.Version, results[0]["result"]!["serverInfo"]!["version"]!.GetValue<string>());
        Assert.Equal(["gb_cli", "gb_capabilities", "gb_schema"], results[1]["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task EnforcesHandshakeAndNeverRepliesToNotifications()
    {
        var results = await ExchangeAsync("""
            {"jsonrpc":"2.0","id":0,"method":"tools/list"}
            {"jsonrpc":"2.0","method":"tools/list"}
            {"jsonrpc":"2.0","id":1,"method":"ping"}
            """ + "\n");
        Assert.Equal(2, results.Length);
        Assert.Equal(-32002, results[0]["error"]!["code"]!.GetValue<int>());
        Assert.Empty(results[1]["result"]!.AsObject());
    }

    [Fact]
    public async Task DelegatesToCliAndReturnsBothStructuredAndTextEvidence()
    {
        var results = await ExchangeAsync(Initialize + Call(2, "gb_cli", new { arguments = new[] { "version" } }) +
            Call(3, "gb_capabilities") + Call(4, "gb_schema", new { name = "project" }));
        Assert.Equal(4, results.Length);
        foreach (var response in results.Skip(1))
        {
            var result = response["result"]!;
            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.Equal(0, result["structuredContent"]!["exitCode"]!.GetValue<int>());
            Assert.True(JsonNode.DeepEquals(result["structuredContent"],
                JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())));
        }
        Assert.Equal(AutomationContracts.Version, results[1]["result"]!["structuredContent"]!["envelope"]!["data"]!["version"]!.GetValue<string>());
        Assert.Contains("debug", results[2]["result"]!["structuredContent"]!["envelope"]!["data"]!["commands"]!.AsArray()
            .Select(value => value!.GetValue<string>()));
        Assert.Equal("urn:blast-gb:schema:project:1", results[3]["result"]!["structuredContent"]!["envelope"]!["data"]!["schema"]!["$id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReturnsCommandFailuresAndRejectsRecursion()
    {
        var results = await ExchangeAsync(Initialize + Call(2, "gb_cli", new { arguments = new[] { "unknown" } }) +
            Call(3, "gb_cli", new { arguments = new[] { "mcp", "serve" } }) +
            Call(4, "gb_cli", new { arguments = new[] { "--", "mcp", "serve" } }));
        Assert.Equal(2, results[1]["result"]!["structuredContent"]!["exitCode"]!.GetValue<int>());
        Assert.True(results[1]["result"]!["isError"]!.GetValue<bool>());
        foreach (var result in results.Skip(2))
            Assert.Equal("MCP_RECURSION", result["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ValidatesToolArgumentsAndRecoversFromBadRequests()
    {
        var results = await ExchangeAsync(Initialize + Call(2, "gb_cli", new { arguments = Array.Empty<string>() }) +
            Call(3, "gb_schema", new { name = "__proto__" }) + Call(4, "gb_capabilities", new { unexpected = true }) +
            Call(5, "gb_cli", new { arguments = new[] { new string('x', 32769) } }) +
            "not json\n" + Call(6, "gb_cli", new { arguments = new[] { "version", "--json" } }));
        Assert.Equal(7, results.Length);
        foreach (var result in results.Skip(1).Take(4))
            Assert.Equal("MCP_INVALID_ARGUMENTS", result["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(-32700, results[5]["error"]!["code"]!.GetValue<int>());
        Assert.False(results[6]["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RejectsOversizeAndMalformedUtf8LinesWithoutLosingFraming()
    {
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.UTF8.GetBytes(Initialize));
        bytes.Write(Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024 + 1) + "\n"));
        bytes.Write([0xff, (byte)'\n']);
        bytes.Write(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}\n"));
        var results = await ExchangeAsync(bytes.ToArray());
        Assert.Equal(4, results.Length);
        Assert.Equal(-32700, results[1]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, results[2]["error"]!["code"]!.GetValue<int>());
        Assert.Empty(results[3]["result"]!.AsObject());
    }

    [Fact]
    public async Task RejectsPartialFinalLine()
    {
        var results = await ExchangeAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
        Assert.Single(results);
        Assert.Equal(-32700, results[0]["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task RejectsDuplicateJsonPropertiesWithoutTerminatingTheConnection()
    {
        var results = await ExchangeAsync("""
            {"jsonrpc":"2.0","id":1,"method":"ping","method":"initialize"}
            {"jsonrpc":"2.0","id":2,"method":"ping"}
            """ + "\n");
        Assert.Equal(-32700, results[0]["error"]!["code"]!.GetValue<int>());
        Assert.Empty(results[1]["result"]!.AsObject());
    }

    [Fact]
    public async Task BoundsCompleteResponseIncludingDuplicatedTextEvidence()
    {
        var results = await ExchangeAsync(Initialize + Call(2, "gb_cli", new { arguments = new[] { "version" } }),
            async (_, output, _, _) =>
            {
                await output.WriteAsync(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    command = "test",
                    ok = true,
                    data = new { text = new string('x', 8 * 1024 * 1024) }
                }));
                return 0;
            });
        Assert.Equal("MCP_OUTPUT_LIMIT", results[1]["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"\\ud800\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"\\udc00\",\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"\\ud800\":0}")]
    public async Task RejectsMalformedUnicodeWithoutTerminatingTheConnection(string request)
    {
        var results = await ExchangeAsync(request + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}\n");
        Assert.Equal(2, results.Length);
        Assert.Equal(-32700, results[0]["error"]!["code"]!.GetValue<int>());
        Assert.Empty(results[1]["result"]!.AsObject());
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("9007199254740991")]
    [InlineData("\"\\ud83d\\ude00\"")]
    public async Task AcceptsEquivalentSafeIntegerIdsAndValidUnicode(string id)
    {
        var results = await ExchangeAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"ping\"}}\n");
        Assert.Single(results);
        Assert.Empty(results[0]["result"]!.AsObject());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(id), results[0]["id"]));
    }

    [Theory]
    [InlineData("9007199254740991.1")]
    [InlineData("1.000000000000000000000000000001")]
    [InlineData("1e-9999")]
    public async Task RejectsFractionalRequestIdsWithoutRounding(string id)
    {
        var results = await ExchangeAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"ping\"}}\n");
        Assert.Single(results);
        Assert.Equal(-32600, results[0]["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void NumericByteArraysRetainWireCompatibility()
    {
        Assert.Equal("[0,42,255]", JsonSerializer.Serialize(new byte[] { 0, 42, 255 }, JsonDefaults.Options));
        Assert.Equal(new byte[] { 0, 42, 255 }, JsonSerializer.Deserialize<byte[]>("[0,42,255]", JsonDefaults.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<byte[]>("[256]", JsonDefaults.Options));
    }
}
