using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class AuthoringCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast-authoring-" + Guid.NewGuid().ToString("N"));
    public AuthoringCommandTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string name) => Path.Combine(directory, name);

    private (string Path, GameSpec Game, byte[] Original) WriteGame(string mode = "gbc")
    {
        var map = Enumerable.Repeat(new string('#', 20), 18).ToArray();
        map[1] = "#PC#################";
        var starter = GameCompiler.CreateStarterSpec();
        var game = starter with
        {
            Title = "  CUSTOM FOREST  ",
            Mode = mode,
            Map = map,
            Palette = starter.Palette with { Player = "#123ABC" },
            Objective = "  Collect the single stone.  "
        };
        var json = JsonSerializer.Serialize(game, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true });
        var original = Encoding.UTF8.GetBytes(json.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n");
        var path = At(mode + "-game.json");
        File.WriteAllBytes(path, original);
        return (path, game, original);
    }

    [Fact]
    public async Task ValidationReportsNormalizedGameWithoutWritingArtifacts()
    {
        var (path, _, original) = WriteGame();
        var result = await CliTests.Invoke("game", "validate", path);
        Assert.True(result.Status == 0, result.Json.ToJsonString());
        Assert.True(result.Json["data"]!["valid"]!.GetValue<bool>());
        Assert.Equal("CUSTOM FOREST", result.Json["data"]!["game"]!["title"]!.GetValue<string>());
        Assert.Equal("#123abc", result.Json["data"]!["game"]!["palette"]!["player"]!.GetValue<string>());
        Assert.Equal(1, result.Json["data"]!["collectibles"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(directory));
        Assert.Empty(Directory.GetDirectories(directory));
        var schema = await CliTests.Invoke("schema", "game");
        Assert.Equal(0, schema.Status);
        Assert.Equal("urn:blast-gb:schema:game:1", schema.Json["data"]!["schema"]!["$id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExportPreservesOriginalJsonAndCustomizedAssembly()
    {
        var (path, game, original) = WriteGame();
        var target = At("source-project") + Path.DirectorySeparatorChar;
        var exported = await CliTests.Invoke("game", "export", path, target, "--name", "Editable game");
        Assert.True(exported.Status == 0, exported.Json.ToJsonString());
        var root = Path.TrimEndingDirectorySeparator(target);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "game.json")));
        Assert.Equal(GameCompiler.Build(game).Source, File.ReadAllText(Path.Combine(root, "source", "main.asm")));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "gb-project.json")))!;
        Assert.Equal("source", manifest["kind"]!.GetValue<string>());
        Assert.Equal("Editable game", manifest["name"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(root, "build")));
        var snapshot = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
        var repeated = await CliTests.Invoke("game", "export", path, root);
        Assert.Equal(1, repeated.Status);
        Assert.Equal("OUTPUT_EXISTS", repeated.Json["error"]!["code"]!.GetValue<string>());
        foreach (var (file, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task InvalidGameLeavesInputAndExportDestinationUntouched()
    {
        var (path, game, _) = WriteGame();
        File.WriteAllText(path, JsonSerializer.Serialize(game with { Map = ["P"] }, JsonDefaults.Options));
        var original = File.ReadAllBytes(path);
        var target = At("invalid-project");
        foreach (var arguments in new[] { new[] { "game", "validate", path }, new[] { "game", "export", path, target } })
        {
            var failure = await CliTests.Invoke(arguments);
            Assert.Equal(1, failure.Status);
            Assert.Equal("INVALID_GAME", failure.Json["error"]!["code"]!.GetValue<string>());
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(target));
        }
    }

    [NativeRgbdsFact]
    public async Task ExportedCustomizedGamesRebuildExactlyAndExposeRealRgbdsSymbols()
    {
        foreach (var mode in new[] { "gb", "gbc" })
        {
            var (path, game, original) = WriteGame(mode);
            var project = At(mode + "-source");
            var exported = await CliTests.Invoke("game", "export", path, project);
            Assert.True(exported.Status == 0, exported.Json.ToJsonString());
            var built = await CliTests.Invoke("build", project);
            Assert.True(built.Status == 0, built.Json.ToJsonString());
            Assert.Equal(GameCompiler.Build(game).Rom, File.ReadAllBytes(built.Json["data"]!["output"]!.GetValue<string>()));
            var symbols = await CliTests.Invoke("asm", "symbols", built.Json["data"]!["symbols"]!.GetValue<string>(), "--symbol", "Entry");
            Assert.True(symbols.Status == 0, symbols.Json.ToJsonString());
            Assert.Equal(0x150, symbols.Json["data"]!["symbols"]![0]!["romOffset"]!.GetValue<int>());
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task PatchCreationProducesCompleteGuardsAndReproducesAfterRom()
    {
        var before = new byte[131072];
        GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom.CopyTo(before, 0);
        var after = (byte[])before.Clone();
        for (var index = 0x150; index < after.Length; index += 53) after[index] ^= 0x42;
        var input = At("before.gb");
        var changed = At("after.gb");
        var patches = At("patches.json");
        File.WriteAllBytes(input, before);
        File.WriteAllBytes(changed, after);
        var created = await CliTests.Invoke("rom", "patch-create", input, changed, "--output", patches);
        Assert.True(created.Status == 0, created.Json.ToJsonString());
        var records = JsonSerializer.Deserialize<RomPatch[]>(File.ReadAllText(patches), JsonDefaults.Options)!;
        Assert.All(records, patch => Assert.NotNull(patch.Expected));
        Assert.Equal(after, RomTools.Patch(before, records));
        var applied = At("applied.gb");
        Assert.Equal(0, (await CliTests.Invoke("rom", "patch", input, patches, "--output", applied)).Status);
        Assert.Equal(after, File.ReadAllBytes(applied));
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(after, File.ReadAllBytes(changed));
        var identical = await CliTests.Invoke("rom", "patch-create", input, input, "--output", patches, "--overwrite");
        Assert.Equal(0, identical.Status);
        Assert.Equal(0, identical.Json["data"]!["patches"]!.GetValue<int>());
        Assert.Empty(JsonNode.Parse(File.ReadAllText(patches))!.AsArray());
    }

    [Fact]
    public async Task PatchCreationFailuresPreserveBothRomsAndExistingOutput()
    {
        var before = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        var after = before[..^1];
        var input = At("before.gb");
        var changed = At("after.gb");
        var patches = At("patches.json");
        File.WriteAllBytes(input, before);
        File.WriteAllBytes(changed, after);
        File.WriteAllText(patches, "retained");
        var resized = await CliTests.Invoke("rom", "patch-create", input, changed, "--output", patches, "--overwrite");
        Assert.Equal(1, resized.Status);
        Assert.Equal("INVALID_PATCH", resized.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal("retained", File.ReadAllText(patches));
        foreach (var output in new[] { input, changed })
        {
            var collision = await CliTests.Invoke("rom", "patch-create", input, changed, "--output", output, "--overwrite");
            Assert.Equal(1, collision.Status);
            Assert.Equal("UNSAFE_OUTPUT", collision.Json["error"]!["code"]!.GetValue<string>());
        }
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(after, File.ReadAllBytes(changed));
    }

    [Fact]
    public async Task SymbolsDistinguishLabelsConstantsAndUnmappedAddresses()
    {
        var path = At("game.sym");
        const string text = "00:0150 Entry\n02:4567 OtherBank\n00:c000 WorkRam\n2a Answer\n";
        File.WriteAllText(path, text);
        var result = await CliTests.Invoke("asm", "symbols", path);
        Assert.True(result.Status == 0, result.Json.ToJsonString());
        var symbols = result.Json["data"]!["symbols"]!.AsArray();
        Assert.Equal(4, symbols.Count);
        Assert.Equal(0x8567, symbols[1]!["romOffset"]!.GetValue<int>());
        Assert.True(symbols[2]!.AsObject().ContainsKey("romOffset"));
        Assert.Null(symbols[2]!["romOffset"]);
        Assert.Equal("constant", symbols[3]!["kind"]!.GetValue<string>());
        Assert.Equal(42, symbols[3]!["constantValue"]!.GetValue<int>());
        Assert.Null(symbols[3]!["address"]);
        var filtered = await CliTests.Invoke("asm", "symbols", path, "--symbol", "Entry");
        Assert.Equal(0, filtered.Status);
        Assert.Single(filtered.Json["data"]!["symbols"]!.AsArray());
        var missing = await CliTests.Invoke("asm", "symbols", path, "--symbol", "entry");
        Assert.Equal("SYMBOL_NOT_FOUND", missing.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task InvalidAndOversizedSymbolFilesFailWithoutPartialResults()
    {
        var path = At("invalid.sym");
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("00:0150 Entry\n01:4000 Entry\n"), new byte[] { 0xff, 0xfe, 0xfd } })
        {
            File.WriteAllBytes(path, bytes);
            var failure = await CliTests.Invoke("asm", "symbols", path);
            Assert.Equal(1, failure.Status);
            Assert.Equal("INVALID_SYMBOLS", failure.Json["error"]!["code"]!.GetValue<string>());
            Assert.Null(failure.Json["data"]);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        using (var stream = File.Create(path)) stream.SetLength(RgbdsSymbols.MaxFileBytes + 1L);
        var oversized = await CliTests.Invoke("asm", "symbols", path);
        Assert.Equal("INVALID_SYMBOLS", oversized.Json["error"]!["code"]!.GetValue<string>());
    }
}
