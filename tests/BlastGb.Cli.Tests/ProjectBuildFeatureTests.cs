using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class ProjectBuildFeatureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast build receipts " + Guid.NewGuid().ToString("N"));
    public ProjectBuildFeatureTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string name) => Path.GetFullPath(Path.Combine(directory, name));
    private static string Error((int Status, JsonNode Json) result) => result.Json["error"]!["code"]!.GetValue<string>();
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private void WriteManifest(JsonObject manifest) => File.WriteAllText(At("gb-project.json"), manifest.ToJsonString());
    private static JsonObject SourceManifest() => new()
    {
        ["format"] = "blast-gb-workspace",
        ["version"] = 1,
        ["name"] = "Feature tests",
        ["kind"] = "source",
        ["source"] = "main.asm",
        ["output"] = "build/game.gb",
        ["receipt"] = "build/receipt.json"
    };

    [Fact]
    public async Task BuiltInReceiptIsDeterministicAndStatusChecksInputsAndOutputsWithoutBuilding()
    {
        var project = At("game");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        var manifestPath = Path.Combine(project, "gb-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["receipt"] = "build/receipt.json";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var built = await CliTests.Invoke("build", project);
        Assert.True(built.Status == 0, built.Json.ToJsonString());
        var receiptPath = built.Json["data"]!["receipt"]!.GetValue<string>();
        var receiptBytes = File.ReadAllBytes(receiptPath);
        var receipt = JsonNode.Parse(receiptBytes)!;
        Assert.Equal(1, receipt["schemaVersion"]!.GetValue<int>());
        Assert.Equal("builtin-gamespec", receipt["engine"]!.GetValue<string>());
        Assert.Empty(receipt["tools"]!.AsArray());
        Assert.Equal(new[] { manifestPath, Path.Combine(project, "game.json") }.Order(), receipt["inputs"]!.AsArray().Select(file => file!["path"]!.GetValue<string>()).Order());
        Assert.Equal(0, (await CliTests.Invoke("project", "status", project)).Status);
        Assert.Equal(0, (await CliTests.Invoke("build", project, "--overwrite")).Status);
        Assert.Equal(receiptBytes, File.ReadAllBytes(receiptPath));

        var gamePath = Path.Combine(project, "game.json");
        var originalGame = File.ReadAllBytes(gamePath);
        File.AppendAllText(gamePath, "\n ");
        var stale = await CliTests.Invoke("project", "status", project);
        Assert.Equal(3, stale.Status);
        Assert.True(stale.Json["data"]!["stale"]!.GetValue<bool>());
        Assert.Contains(stale.Json["data"]!["changes"]!.AsArray(), change => change!["kind"]!.GetValue<string>() == "input" && change["path"]!.GetValue<string>() == gamePath);
        Assert.Equal(receiptBytes, File.ReadAllBytes(receiptPath));
        File.WriteAllBytes(gamePath, originalGame);
        Assert.Equal(0, (await CliTests.Invoke("project", "status", project)).Status);

        var romPath = built.Json["data"]!["output"]!.GetValue<string>();
        var rom = File.ReadAllBytes(romPath);
        rom[0x150] ^= 1;
        File.WriteAllBytes(romPath, rom);
        stale = await CliTests.Invoke("project", "status", project);
        Assert.Equal(3, stale.Status);
        Assert.Contains(stale.Json["data"]!["changes"]!.AsArray(), change => change!["kind"]!.GetValue<string>() == "output");
        File.Delete(romPath);
        stale = await CliTests.Invoke("project", "status", project);
        Assert.Equal(3, stale.Status);
        Assert.Contains(stale.Json["data"]!["changes"]!.AsArray(), change => change!["reason"]!.GetValue<string>() == "missing");
    }

    [Fact]
    public async Task ExplicitReceiptOverridesManifestAndMissingOrMalformedReceiptsAreReported()
    {
        var project = At("game");
        await CliTests.Invoke("project", "init", project, "--kind", "game");
        var receipt = At("selected-receipt.json");
        var missing = await CliTests.Invoke("project", "status", project, "--receipt", receipt);
        Assert.Equal(3, missing.Status);
        Assert.False(File.Exists(receipt));
        var built = await CliTests.Invoke("build", project, "--receipt", receipt);
        Assert.True(built.Status == 0, built.Json.ToJsonString());
        Assert.Equal(0, (await CliTests.Invoke("project", "status", project, "--receipt", receipt)).Status);
        File.WriteAllText(receipt, "{\"schemaVersion\":1}");
        var invalid = await CliTests.Invoke("project", "status", project, "--receipt", receipt);
        Assert.Equal(1, invalid.Status);
        Assert.Equal("INVALID_RECEIPT", Error(invalid));
    }

    [Theory]
    [InlineData("sources", "[]")]
    [InlineData("sources", "null")]
    [InlineData("sources", "[null]")]
    [InlineData("sources", "[\"../outside.asm\"]")]
    [InlineData("sources", "[\"main.asm\",\"main.asm\"]")]
    [InlineData("includeDirectories", "[\"../outside\"]")]
    [InlineData("includeDirectories", "[\"missing\"]")]
    [InlineData("includeDirectories", "null")]
    [InlineData("defines", "{\"--output\":\"wrong.gb\"}")]
    [InlineData("defines", "{\"VALUE\":5}")]
    [InlineData("defines", "{\"VALUE\":null}")]
    [InlineData("defines", "{\"VALUE\":\"1\\n2\"}")]
    [InlineData("defines", "{\"VALUE\\n\":\"1\"}")]
    [InlineData("receipt", "\"../receipt.json\"")]
    [InlineData("tests", "\"../tests.json\"")]
    [InlineData("assets", "[null]")]
    [InlineData("assets", "[{\"input\":\"image.png\"}]")]
    [InlineData("assets", "[{\"input\":\"image.png\",\"output\":\"../image.2bpp\"}]")]
    [InlineData("assets", "[{\"input\":\"image.png\",\"output\":\"image.2bpp\",\"tilemap\":null}]")]
    public async Task InvalidBuildConfigurationIsRejectedBeforeRunningTools(string property, string json)
    {
        File.WriteAllText(At("main.asm"), "; retained\n");
        var manifest = SourceManifest();
        if (property == "sources") manifest.Remove("source");
        manifest[property] = JsonNode.Parse(json);
        WriteManifest(manifest);
        var result = await CliTests.Invoke("build", directory);
        Assert.Equal(1, result.Status);
        Assert.Equal("INVALID_PROJECT", Error(result));
        Assert.Equal("; retained\n", File.ReadAllText(At("main.asm")));
        Assert.False(Directory.Exists(At("build")));
    }

    [Fact]
    public async Task SourceAndSourcesAreMutuallyExclusive()
    {
        var manifest = SourceManifest();
        manifest["sources"] = new JsonArray("other.asm");
        WriteManifest(manifest);
        var result = await CliTests.Invoke("project", "resolve", directory);
        Assert.Equal("INVALID_PROJECT", Error(result));
    }

    [NativeRgbdsFact]
    public async Task MultipleObjectsUseDefinesAndIncludeDirectoriesAndProduceReproducibleReceipts()
    {
        CreateMultiSourceProject();
        var built = await CliTests.Invoke("build", directory);
        Assert.True(built.Status == 0, built.Json.ToJsonString());
        var rom = File.ReadAllBytes(At("build/game.gb"));
        Assert.Equal(new byte[] { 0xc3, 0x50, 0x01, 42, 11, 22, 33 }, rom[..7]);
        Assert.Equal(new byte[] { 0x3e, 7, 0xc9 }, rom[0x150..0x153]);
        var receiptBytes = File.ReadAllBytes(At("build/receipt.json"));
        var receipt = JsonNode.Parse(receiptBytes)!;
        Assert.Equal(new[] { At("main.asm"), At("other.asm") }, receipt["options"]!["sources"]!.AsArray().Select(source => source!.GetValue<string>()));
        Assert.Equal("42", receipt["options"]!["defines"]!["BUILD_VALUE"]!.GetValue<string>());
        Assert.Equal(new[] { "rgbasm", "rgblink" }, receipt["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));
        foreach (var tool in receipt["tools"]!.AsArray())
        {
            Assert.True(Path.IsPathFullyQualified(tool!["path"]!.GetValue<string>()));
            Assert.NotEmpty(tool["version"]!.GetValue<string>());
            Assert.Equal(Hash(File.ReadAllBytes(tool["path"]!.GetValue<string>())), tool["sha256"]!.GetValue<string>());
        }
        var expectedInputs = new[] { "gb-project.json", "main.asm", "other.asm", "includes/config.inc", "includes/payload.bin" }.Select(At).Order();
        Assert.Equal(expectedInputs, receipt["inputs"]!.AsArray().Select(file => file!["path"]!.GetValue<string>()).Order());
        Assert.Equal(3, receipt["outputs"]!.AsArray().Count);
        Assert.Equal(0, (await CliTests.Invoke("project", "status", directory)).Status);
        var rebuilt = await CliTests.Invoke("build", directory, "--overwrite");
        Assert.True(rebuilt.Status == 0, rebuilt.Json.ToJsonString());
        Assert.Equal(receiptBytes, File.ReadAllBytes(At("build/receipt.json")));
        File.WriteAllBytes(At("includes/payload.bin"), [9, 9, 9]);
        var stale = await CliTests.Invoke("project", "status", directory);
        Assert.Equal(3, stale.Status);
        Assert.Contains(stale.Json["data"]!["changes"]!.AsArray(), item => item!["path"]!.GetValue<string>() == At("includes/payload.bin"));
    }

    [NativeRgbdsFact]
    public async Task LaterAssemblyFailureAndReceiptDependencyCollisionPreserveAllPreviousFiles()
    {
        CreateMultiSourceProject();
        var built = await CliTests.Invoke("build", directory);
        Assert.True(built.Status == 0, built.Json.ToJsonString());
        var artifacts = new[] { "game.gb", "game.sym", "game.map", "receipt.json" }.Select(name => At("build/" + name)).ToDictionary(path => path, File.ReadAllBytes);
        var second = File.ReadAllText(At("other.asm"));
        File.WriteAllText(At("other.asm"), "not valid assembly\n");
        var failed = await CliTests.Invoke("build", directory, "--overwrite");
        Assert.Equal("BUILD_FAILED", Error(failed));
        foreach (var (path, bytes) in artifacts) Assert.Equal(bytes, File.ReadAllBytes(path));
        File.WriteAllText(At("other.asm"), second);
        var manifest = JsonNode.Parse(File.ReadAllText(At("gb-project.json")))!.AsObject();
        manifest["receipt"] = "includes/config.inc";
        WriteManifest(manifest);
        var include = File.ReadAllBytes(At("includes/config.inc"));
        failed = await CliTests.Invoke("build", directory, "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", Error(failed));
        Assert.Equal(include, File.ReadAllBytes(At("includes/config.inc")));
        foreach (var (path, bytes) in artifacts) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [NativeRgbdsFact]
    public async Task AssetOverlayBuildConsumesNewTilesAndCommitsAssetsAndReceiptTogether()
    {
        CreateAssetProject();
        Directory.CreateDirectory(At("generated"));
        File.WriteAllBytes(At("generated/tiles.2bpp"), Enumerable.Repeat((byte)255, 16).ToArray());
        var built = await CliTests.Invoke("build", directory, "--overwrite");
        Assert.True(built.Status == 0, built.Json.ToJsonString());
        var tiles = File.ReadAllBytes(At("generated/tiles.2bpp"));
        Assert.Equal(tiles, File.ReadAllBytes(At("build/game.gb"))[..16]);
        Assert.NotEqual(Enumerable.Repeat((byte)255, 16).ToArray(), tiles);
        var receipt = JsonNode.Parse(File.ReadAllText(At("build/receipt.json")))!;
        Assert.Contains(receipt["tools"]!.AsArray(), tool => tool!["name"]!.GetValue<string>() == "rgbgfx");
        Assert.Contains(receipt["inputs"]!.AsArray(), file => file!["path"]!.GetValue<string>() == At("image.png"));
        Assert.Contains(receipt["inputs"]!.AsArray(), file => file!["path"]!.GetValue<string>() == At("generated/tiles.2bpp"));
        Assert.Contains(receipt["outputs"]!.AsArray(), file => file!["path"]!.GetValue<string>() == At("generated/tiles.2bpp"));
        Assert.DoesNotContain("blast-gb-build-", receipt.ToJsonString());
        Assert.Equal(0, (await CliTests.Invoke("project", "status", directory)).Status);
        var before = new[] { "generated/tiles.2bpp", "build/game.gb", "build/game.sym", "build/game.map", "build/receipt.json" }.Select(At).ToDictionary(path => path, File.ReadAllBytes);
        File.AppendAllText(At("main.asm"), "invalid opcode\n");
        var failed = await CliTests.Invoke("build", directory, "--overwrite");
        Assert.Equal("BUILD_FAILED", Error(failed));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [NativeRgbdsFact]
    public async Task ReadingAnOldAssetByAbsolutePathCannotOverwriteThatConsumedInput()
    {
        CreateAssetProject();
        Directory.CreateDirectory(At("generated"));
        var tiles = Enumerable.Repeat((byte)99, 16).ToArray();
        File.WriteAllBytes(At("generated/tiles.2bpp"), tiles);
        File.WriteAllText(At("main.asm"), "SECTION \"ROM\", ROM0[0]\nINCBIN \"" + At("generated/tiles.2bpp").Replace('\\', '/') + "\"\nds $4000 - @, 0\n");
        var failed = await CliTests.Invoke("build", directory, "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", Error(failed));
        Assert.Equal(tiles, File.ReadAllBytes(At("generated/tiles.2bpp")));
        Assert.False(File.Exists(At("build/game.gb")));
        Assert.False(File.Exists(At("build/receipt.json")));
    }

    private void CreateMultiSourceProject()
    {
        Directory.CreateDirectory(At("includes"));
        File.WriteAllText(At("includes/config.inc"), "DEF INCLUDED_VALUE EQU 7\n");
        File.WriteAllBytes(At("includes/payload.bin"), [11, 22, 33]);
        File.WriteAllText(At("main.asm"), "INCLUDE \"config.inc\"\nSECTION \"Entry\", ROM0[0]\nEntry:: jp Other\ndb BUILD_VALUE\nINCBIN \"payload.bin\"\nSECTION \"Header\", ROM0[$0100]\nds $50, 0\n");
        File.WriteAllText(At("other.asm"), "INCLUDE \"config.inc\"\nSECTION \"Other\", ROM0[$0150]\nOther:: ld a, INCLUDED_VALUE\nret\n");
        var manifest = SourceManifest();
        manifest.Remove("source");
        manifest["sources"] = new JsonArray("main.asm", "other.asm");
        manifest["includeDirectories"] = new JsonArray("includes");
        manifest["defines"] = new JsonObject { ["BUILD_VALUE"] = "42" };
        WriteManifest(manifest);
    }

    private void CreateAssetProject()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var index = 0; index < 64; index++)
        {
            var shade = (byte)((index % 4) * 85);
            pixels[index * 4] = shade;
            pixels[index * 4 + 1] = shade;
            pixels[index * 4 + 2] = shade;
            pixels[index * 4 + 3] = 255;
        }
        File.WriteAllBytes(At("image.png"), PngEncoder.EncodeRgba(pixels, 8, 8));
        File.WriteAllText(At("main.asm"), "SECTION \"ROM\", ROM0[0]\nINCBIN \"generated/tiles.2bpp\"\nds $4000 - @, 0\n");
        var manifest = SourceManifest();
        manifest["assets"] = new JsonArray(new JsonObject { ["input"] = "image.png", ["output"] = "generated/tiles.2bpp" });
        WriteManifest(manifest);
    }
}
