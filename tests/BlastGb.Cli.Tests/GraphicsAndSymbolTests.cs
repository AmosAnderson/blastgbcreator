using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class GraphicsAndSymbolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "blast graphics " + Guid.NewGuid().ToString("N"));
    public GraphicsAndSymbolTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string FilePath(string name) => Path.Combine(root, name);

    private string Rom()
    {
        var path = FilePath("game.gb");
        File.WriteAllBytes(path, GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom);
        return path;
    }

    [Fact]
    public async Task ScreenshotsAndNamedCapturesPreservePixelsAndSources()
    {
        var rom = Rom();
        var original = File.ReadAllBytes(rom);
        var script = FilePath("play.json");
        File.WriteAllText(script, """{"version":1,"commands":[{"op":"frame","count":3},{"op":"capture","name":"ready"}]}""");
        var screenshot = FilePath("final.png");
        var captures = FilePath("captures");
        var result = await CliTests.Invoke("debug", rom, "--script", script, "--screenshot", screenshot, "--capture-dir", captures);
        Assert.Equal(0, result.Status);
        Assert.Equal(File.ReadAllBytes(screenshot), File.ReadAllBytes(Path.Combine(captures, "ready.png")));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(screenshot)[..8]);
        Assert.Equal(result.Json["data"]!["framebufferSha256"]!.GetValue<string>(), result.Json["data"]!["captures"]![0]!["framebufferSha256"]!.GetValue<string>());
        Assert.Null(result.Json["data"]!["framebufferRgba"]);
        Assert.Equal(original, File.ReadAllBytes(rom));
    }

    [Fact]
    public async Task FailedVisualAssertionWritesEvidenceAndStopsLaterOperations()
    {
        var rom = Rom();
        var script = FilePath("play.json");
        File.WriteAllText(script, """{"version":1,"commands":[{"op":"frame"},{"op":"assert","framebufferSha256":"0000000000000000000000000000000000000000000000000000000000000000"},{"op":"capture","name":"unreached"}]}""");
        var result = await CliTests.Invoke("debug", rom, "--script", script, "--capture-dir", FilePath("captures"));
        Assert.Equal(3, result.Status);
        Assert.True(File.Exists(FilePath("captures/failure-1.png")));
        Assert.False(File.Exists(FilePath("captures/unreached.png")));
    }

    [Fact]
    public async Task ScreenshotAndCaptureCollisionsPreserveEveryInput()
    {
        var rom = Rom();
        var original = File.ReadAllBytes(rom);
        var bad = await CliTests.Invoke("run", rom, "--frames", "1", "--screenshot", rom, "--overwrite");
        Assert.Equal(1, bad.Status);
        Assert.Equal("UNSAFE_OUTPUT", bad.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllBytes(rom));
        var script = FilePath("ready.png");
        var text = """{"version":1,"commands":[{"op":"capture","name":"ready"}]}""";
        File.WriteAllText(script, text);
        bad = await CliTests.Invoke("debug", rom, "--script", script, "--capture-dir", root, "--overwrite");
        Assert.Equal(1, bad.Status);
        Assert.Equal(text, File.ReadAllText(script));
        bad = await CliTests.Invoke("debug", rom, "--script", script, "--capture-dir", FilePath("images"), "--screenshot", FilePath("images/ready.png"));
        Assert.Equal(1, bad.Status);
        Assert.False(Directory.Exists(FilePath("images")));
    }

    [Fact]
    public async Task SymbolsResolveRomOffsetsAndMappedRamWithoutDiscardingDuplicates()
    {
        var rom = Rom();
        var symbols = FilePath("game.sym");
        File.WriteAllText(symbols, "00:0150 Entry\n01:4000 Banked\n00:c000 Player\n00:ff80 Scratch\n002a Answer\n");
        var disassembly = await CliTests.Invoke("asm", "decompile", rom, "--symbols", symbols, "--symbol", "Banked", "--count", "1");
        Assert.Equal(0, disassembly.Status);
        Assert.Equal(16384, disassembly.Json["data"]!["offset"]!.GetValue<int>());
        var script = FilePath("test.json");
        File.WriteAllText(script, """{"version":1,"commands":[{"op":"write","address":"Player","bytes":[42]},{"op":"assert","memory":[{"address":"Player","bytes":[42]}]},{"op":"breakpoints","addresses":["Entry"]},{"op":"run","frames":1,"maxInstructions":100},{"op":"assert","registers":{"pc":"Entry"}}]}""");
        Assert.Equal(0, (await CliTests.Invoke("debug", rom, "--symbols", symbols, "--script", script)).Status);
        var run = await CliTests.Invoke("run", rom, "--symbols", symbols, "--breakpoint", "Entry", "--memory", "Player:2");
        Assert.Equal(0, run.Status);
        Assert.Equal(336, run.Json["data"]!["registers"]!["pc"]!.GetValue<int>());
        foreach (var label in new[] { "Banked", "Answer", "Missing" })
            Assert.Equal(1, (await CliTests.Invoke("run", rom, "--symbols", symbols, "--breakpoint", label)).Status);
        File.WriteAllText(script, """{"version":1,"commands":[{"op":"read","address":"Player","address":49152}]}""");
        Assert.Equal(1, (await CliTests.Invoke("debug", rom, "--symbols", symbols, "--script", script)).Status);
    }

    [Fact]
    public async Task TileCommandsRoundTripNumericPixelsAndRejectUnsafeWrites()
    {
        var pixels = Enumerable.Range(0, 128).Select(i => i % 4).ToArray();
        var input = FilePath("pixels.json");
        File.WriteAllText(input, JsonSerializer.Serialize(pixels));
        var tiles = FilePath("tiles.2bpp");
        Assert.Equal(0, (await CliTests.Invoke("asset", "encode", input, "--output", tiles)).Status);
        var decoded = FilePath("decoded.json");
        Assert.Equal(0, (await CliTests.Invoke("asset", "decode", tiles, "--output", decoded)).Status);
        Assert.Equal(pixels, JsonSerializer.Deserialize<int[]>(File.ReadAllText(decoded)));
        Assert.Equal(1, (await CliTests.Invoke("asset", "encode", input, "--output", input, "--overwrite")).Status);
        File.WriteAllText(input, "[0.000000000000000000000001]");
        var before = File.ReadAllBytes(tiles);
        Assert.Equal(1, (await CliTests.Invoke("asset", "encode", input, "--output", tiles, "--overwrite")).Status);
        Assert.Equal(before, File.ReadAllBytes(tiles));
    }

    [Fact]
    public async Task RgbgfxProducesAllAssetsAndPreservesExistingBatchOnCollision()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RGBDS_BIN"))) return;
        var input = FilePath("tiles.png");
        File.WriteAllBytes(input, PngEncoder.EncodeRgba(Enumerable.Repeat(new byte[] { 255, 255, 255, 255 }, 64).SelectMany(pixel => pixel).ToArray(), 8, 8));
        var tiles = FilePath("tiles.2bpp");
        var map = FilePath("map.bin");
        var palette = FilePath("pal.bin");
        var attrs = FilePath("attr.bin");
        var result = await CliTests.Invoke("asset", "convert", input, "--output", tiles, "--tilemap", map, "--palette", palette, "--attrmap", attrs);
        Assert.True(result.Status == 0, result.Json.ToJsonString());
        Assert.Equal(16, File.ReadAllBytes(tiles).Length);
        Assert.Single(File.ReadAllBytes(map));
        Assert.Single(File.ReadAllBytes(attrs));
        Assert.Equal(8, File.ReadAllBytes(palette).Length);
        var original = File.ReadAllBytes(input);
        result = await CliTests.Invoke("asset", "convert", input, "--output", tiles, "--tilemap", input, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal(original, File.ReadAllBytes(input));
    }
}
