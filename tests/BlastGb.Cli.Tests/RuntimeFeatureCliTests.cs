using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Cli;
using BlastGb.Core;
using static BlastGb.Cli.Tests.CliTests;

namespace BlastGb.Cli.Tests;

public sealed class RuntimeFeatureCliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast runtime features " + Guid.NewGuid().ToString("N"));
    public RuntimeFeatureCliTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);
    private static string Error((int Status, JsonNode Json) result) => result.Json["error"]!["code"]!.GetValue<string>();

    private (string Path, byte[] Rom) Cartridge(bool battery = false)
    {
        byte[] rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        if (battery)
        {
            rom[0x147] = 3;
            rom[0x149] = 2;
            rom = RomTools.RepairChecksums(rom);
        }
        string path = PathFor(battery ? "battery game.gb" : "game.gb");
        File.WriteAllBytes(path, rom);
        return (path, rom);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenshotIsAnIndependentPngWithReportedPixelHash(bool debug)
    {
        var (path, rom) = Cartridge();
        string screenshot = PathFor("capture.png");
        (int Status, JsonNode Json) result;
        if (debug)
        {
            string script = PathFor("debug.json");
            File.WriteAllText(script, """{"version":1,"commands":[{"op":"frame","count":2}]}""");
            result = await Invoke("debug", path, "--script", script, "--screenshot", screenshot, "--provenance");
        }
        else result = await Invoke("run", path, "--frames", "2", "--screenshot", screenshot, "--provenance");
        Assert.Equal(0, result.Status);
        byte[] png = File.ReadAllBytes(screenshot);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(160, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(144, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        var decoded = PngImage.Decode(png);
        Assert.Equal(160, decoded.Width);
        Assert.Equal(144, decoded.Height);
        // Independently inflate the encoder's unfiltered RGBA scanlines before checking the runtime hash.
        using var compressed = new MemoryStream();
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) compressed.Write(png.AsSpan(offset + 8, length));
            offset += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var pixels = new byte[160 * 144 * 4];
        for (int row = 0; row < 144; row++)
        {
            Assert.Equal(0, zlib.ReadByte());
            zlib.ReadExactly(pixels.AsSpan(row * 640, 640));
        }
        Assert.Equal(-1, zlib.ReadByte());
        Assert.Equal(pixels, decoded.Rgba);
        Assert.Equal(result.Json["data"]!["framebufferSha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(pixels)));
        Assert.NotNull(result.Json["data"]!["provenance"]!["engineSha256"]);
        Assert.Equal(rom, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task InvalidPngAndChecksumFailuresPreserveExistingTileOutput()
    {
        string input = PathFor("input.png"), output = PathFor("tiles.2bpp");
        byte[] retained = [1, 2, 3];
        File.WriteAllBytes(output, retained);
        byte[] valid = new PngImage(8, 8, Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray()).Encode();
        byte[] corrupted = valid.ToArray();
        corrupted[29] ^= 1; // IHDR CRC; retain an otherwise valid complete image.
        foreach (byte[] malformed in new[] { new byte[] { 1, 2, 3 }, valid[..^4], corrupted })
        {
            File.WriteAllBytes(input, malformed);
            Assert.Equal(1, (await Invoke("tiles", "import", input, "--output", output, "--overwrite")).Status);
            Assert.Equal(retained, File.ReadAllBytes(output));
            Assert.Equal(malformed, File.ReadAllBytes(input));
        }
    }

    [Fact]
    public async Task RuntimeArtifactsCannotReplaceAnyConsumedInput()
    {
        var (romPath, rom) = Cartridge();
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("run", romPath, "--screenshot", romPath, "--overwrite")));
        foreach (string option in new[] { "assert", "until", "symbols", "save-input" })
        {
            string source = PathFor(option + ".input");
            byte[] retained = System.Text.Encoding.UTF8.GetBytes("source " + option);
            File.WriteAllBytes(source, retained);
            Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("run", romPath, "--" + option, source, "--screenshot", source, "--overwrite")));
            Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("run", romPath, "--" + option, source, "--save-output", source, "--overwrite")));
            Assert.Equal(retained, File.ReadAllBytes(source));
        }
        string script = PathFor("script.json");
        const string commands = """{"version":1,"commands":[{"op":"step"}]}""";
        File.WriteAllText(script, commands);
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("debug", romPath, "--script", script, "--screenshot", script, "--overwrite")));
        Assert.Equal(commands, File.ReadAllText(script));
        Assert.Equal(rom, File.ReadAllBytes(romPath));
    }

    [Fact]
    public async Task RuntimeOutputsRejectSymbolicLinksAndAliasedInputParents()
    {
        if (OperatingSystem.IsWindows()) return;
        var (path, rom) = Cartridge();
        string target = PathFor("retained.png"), link = PathFor("link.png");
        File.WriteAllBytes(target, [7, 8]);
        File.CreateSymbolicLink(link, target);
        Assert.Equal("UNSAFE_PATH", Error(await Invoke("run", path, "--screenshot", link, "--overwrite")));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(target));
        Directory.CreateSymbolicLink(PathFor("alias"), directory);
        string alias = PathFor("alias/game.gb");
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("run", path, "--screenshot", alias, "--overwrite")));
        Assert.Equal(rom, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task LaterArtifactStagingFailurePreservesEarlierScreenshotAndAllInputs()
    {
        var (path, rom) = Cartridge(true);
        string screenshot = PathFor("old.png"), blockedParent = PathFor("ordinary file");
        File.WriteAllBytes(screenshot, [9, 8, 7]);
        File.WriteAllText(blockedParent, "retained parent");
        string save = Path.Combine(blockedParent, "save.sav");
        var result = await Invoke("run", path, "--frames", "1", "--screenshot", screenshot, "--save-output", save, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(screenshot));
        Assert.Equal("retained parent", File.ReadAllText(blockedParent));
        Assert.Equal(rom, File.ReadAllBytes(path));
        Assert.False(File.Exists(save));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.bak"));
    }

    [Fact]
    public async Task UntilReturnsExitThreeWithBoundedTraceWhenConditionIsUnreached()
    {
        var (path, rom) = Cartridge();
        string condition = PathFor("until.json");
        File.WriteAllText(condition, """{"registers":{"pc":65535}}""");
        var result = await Invoke("run", path, "--until", condition, "--max-instructions", "3", "--trace-capacity", "2");
        Assert.Equal(3, result.Status);
        Assert.Equal("VALIDATION_FAILED", Error(result));
        Assert.False(result.Json["data"]!["untilReached"]!.GetValue<bool>());
        Assert.Equal("instruction-budget", result.Json["data"]!["stopReason"]!.GetValue<string>());
        Assert.Equal(2, result.Json["data"]!["trace"]!.AsArray().Count);
        Assert.NotNull(result.Json["data"]!["trace"]![0]!["registers"]!["pc"]);
        File.WriteAllText(condition, """{"registers":{"pc":256}}""");
        var reached = await Invoke("run", path, "--until", condition);
        Assert.Equal(0, reached.Status);
        Assert.True(reached.Json["data"]!["untilReached"]!.GetValue<bool>());
        Assert.Equal(0, reached.Json["data"]!["ticks"]!.GetValue<double>());
        Assert.Equal(rom, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task BatterySaveRoundTripsThroughCliAndRejectsOverwriteOfInputs()
    {
        var (path, rom) = Cartridge(true);
        string script = PathFor("populate.json"), firstSave = PathFor("first.sav"), secondSave = PathFor("second.sav");
        File.WriteAllText(script, """{"version":1,"commands":[{"op":"write","address":0,"bytes":[10]},{"op":"write","address":40960,"bytes":[42]}]}""");
        Assert.Equal(0, (await Invoke("debug", path, "--script", script, "--save-output", firstSave)).Status);
        byte[] save = File.ReadAllBytes(firstSave);
        Assert.Equal(8192, save.Length);
        Assert.Equal(42, save[0]);
        Assert.Equal(0, (await Invoke("run", path, "--frames", "1", "--save-input", firstSave, "--save-output", secondSave)).Status);
        Assert.Equal(save, File.ReadAllBytes(secondSave));
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("run", path, "--save-output", path, "--overwrite")));
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("run", path, "--save-input", firstSave, "--save-output", firstSave, "--overwrite")));
        Assert.Equal(save, File.ReadAllBytes(firstSave));
        Assert.Equal(rom, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task NamedCaptureCannotCollideWithBatterySaveOutput()
    {
        var (path, rom) = Cartridge(true);
        string script = PathFor("capture.json"), save = PathFor("ready.png");
        const string commands = """{"version":1,"commands":[{"op":"capture","name":"ready"}]}""";
        File.WriteAllText(script, commands);
        File.WriteAllBytes(save, [9, 8, 7]);
        var result = await Invoke("debug", path, "--script", script, "--capture-dir", directory, "--save-output", save, "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(save));
        Assert.Equal(commands, File.ReadAllText(script));
        Assert.Equal(rom, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task UnsupportedCartridgeDoesNotWriteEitherRuntimeArtifact()
    {
        var (path, rom) = Cartridge();
        string save = PathFor("save.sav"), screenshot = PathFor("capture.png");
        File.WriteAllBytes(screenshot, [7, 9]);
        var result = await Invoke("run", path, "--frames", "1", "--save-output", save, "--screenshot", screenshot, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.False(File.Exists(save));
        Assert.Equal(new byte[] { 7, 9 }, File.ReadAllBytes(screenshot));
        Assert.Equal(rom, File.ReadAllBytes(path));
    }
}
