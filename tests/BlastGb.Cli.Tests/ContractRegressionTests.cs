using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class ContractRegressionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast-contracts-" + Guid.NewGuid().ToString("N"));

    public ContractRegressionTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task EveryHelpCommandUsesTheCurrentVersion()
    {
        var capabilities = (JsonObject)AutomationContracts.GetCapabilities();
        var aliases = capabilities["commandDefinitions"]!.AsArray()
            .SelectMany(command => command!["aliases"]?.AsArray() ?? [])
            .Select(alias => alias!.GetValue<string>());
        foreach (var command in AutomationContracts.CommandNames.Concat(aliases).Concat(["", "rom", "asm", "project", "mcp"]).Distinct())
        {
            var result = await CliTests.Invoke([.. command.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--help"]);
            Assert.Equal(0, result.Status);
            Assert.StartsWith($"Blast GB Tools {AutomationContracts.Version}\n\n", result.Json["data"]!["text"]!.GetValue<string>());
        }
        using var output = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["--help"], output, TextWriter.Null));
        Assert.StartsWith($"Blast GB Tools {AutomationContracts.Version}\n\n", output.ToString());
        Assert.Equal(AutomationContracts.Version, capabilities["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task IntegralJsonNumbersWorkAcrossDebugAssertionsAndManifests()
    {
        var project = Path.Combine(directory, "game");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        var manifest = Path.Combine(project, "gb-project.json");
        File.WriteAllText(manifest, """
            {"format":"blast-gb-workspace","version":1e0,"name":"integral values","kind":"game",
             "game":"game.json","output":"build/game.gb","romSize":32768.0}
            """);
        Assert.Equal(0, (await CliTests.Invoke("project", "resolve", project)).Status);
        var build = await CliTests.Invoke("build", project);
        Assert.Equal(0, build.Status);
        var rom = build.Json["data"]!["output"]!.GetValue<string>();
        var original = File.ReadAllBytes(rom);
        var script = Path.Combine(directory, "script.json");
        File.WriteAllText(script, """
            {"version":1.0,"commands":[{"op":"step","count":1e0},
             {"op":"write","address":4.9152e4,"bytes":[4.2e1,0.0]},
             {"op":"assert","memory":[{"address":49152.0,"bytes":[42.0,0e99]}]}]}
            """);
        var debug = await CliTests.Invoke("debug", rom, "--script", script);
        Assert.Equal(0, debug.Status);
        Assert.True(debug.Json["data"]!["assertionsPassed"]!.GetValue<bool>());
        var assertions = Path.Combine(directory, "assertions.json");
        File.WriteAllText(assertions, """{"registers":{"pc":3.36e2}}""");
        Assert.Equal(0, (await CliTests.Invoke("test", rom, "--breakpoint", "0x150", "--assert", assertions)).Status);
        Assert.Equal(original, File.ReadAllBytes(rom));
    }

    [Fact]
    public async Task IntegralPatchValuesPreserveGuardsAndRejectFractionalWrites()
    {
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        var input = Path.Combine(directory, "original.gb");
        var output = Path.Combine(directory, "patched.gb");
        var patches = Path.Combine(directory, "patches.json");
        File.WriteAllBytes(input, rom);
        File.WriteAllText(patches, $"[{{\"offset\":3.36e2,\"bytes\":[0.0],\"expected\":[{rom[336]}e0]}}]");
        Assert.Equal(0, (await CliTests.Invoke("rom", "patch", input, patches, "--output", output)).Status);
        var patched = File.ReadAllBytes(output);
        Assert.Equal(0, patched[336]);
        Assert.Equal(rom, File.ReadAllBytes(input));
        foreach (var invalid in new[] { "1.5", "1.000000000000000000000000000001", "1e-9999", "256.0", "1e9999" })
        {
            File.WriteAllText(patches, $"[{{\"offset\":336,\"bytes\":[{invalid}],\"expected\":[{rom[336]}]}}]");
            var failure = await CliTests.Invoke("rom", "patch", input, patches, "--output", output, "--overwrite");
            Assert.Equal(1, failure.Status);
            Assert.Equal("INVALID_PATCH", failure.Json["error"]!["code"]!.GetValue<string>());
            Assert.Equal(patched, File.ReadAllBytes(output));
            Assert.Equal(rom, File.ReadAllBytes(input));
        }
    }

    [Fact]
    public void ByteArraySerializationAcceptsOnlyExactIntegralNumbers()
    {
        Assert.Equal(new byte[] { 0, 42, 255 }, JsonSerializer.Deserialize<byte[]>("[0.0,4.2e1,255.000]", JsonDefaults.Options));
        foreach (var number in new[] { "-0.1", "0.000000000000000000000000000001", "255.00000000000000000000000001", "256", "1e-9999" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<byte[]>($"[{number}]", JsonDefaults.Options));
    }

    [Fact]
    public async Task CancelledRunReturnsFailureAndPreservesCartridge()
    {
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        var path = Path.Combine(directory, "cancel.gb");
        File.WriteAllBytes(path, rom);
        using var cancellation = new CancellationTokenSource();
        // RuntimeRegressionTests cancel from frame/audio callbacks during execution.
        // Test the CLI error contract here without relying on thread-pool timer scheduling.
        cancellation.Cancel();
        using var output = new StringWriter();
        var status = await CliApplication.RunAsync(["run", path, "--frames", "3600", "--json"], output, TextWriter.Null, cancellation.Token);
        Assert.Equal(1, status);
        var result = JsonNode.Parse(output.ToString())!;
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("CANCELLED", result["error"]!["code"]!.GetValue<string>());
        Assert.Equal(rom, File.ReadAllBytes(path));
    }
}
