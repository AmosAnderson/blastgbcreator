using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
