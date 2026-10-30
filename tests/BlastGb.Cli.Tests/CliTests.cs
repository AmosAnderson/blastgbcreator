using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast CLI checks " + Guid.NewGuid().ToString("N"));
    public CliTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);
    private (string Path, byte[] Rom) Cartridge()
    {
        var path = PathFor("original game.gbc");
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        File.WriteAllBytes(path, rom);
        return (path, rom);
    }
    internal static async Task<(int Status, JsonNode Json)> Invoke(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var status = await CliApplication.RunAsync([.. args, "--json"], output, error);
        Assert.Equal("", error.ToString());
        return (status, JsonNode.Parse(output.ToString())!);
    }
    private static string Error((int Status, JsonNode Json) result) => result.Json["error"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task DiscoversGroupedCommandsAndSchemas()
    {
        var capabilities = await Invoke("capabilities");
        Assert.Equal(0, capabilities.Status);
        Assert.Equal(1, capabilities.Json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("gb-project.json", capabilities.Json["data"]!["manifest"]!["filename"]!.GetValue<string>());
        Assert.Contains(capabilities.Json["data"]!["commands"]!.AsArray(), command => command!.GetValue<string>() == "rom info");
        Assert.Contains(capabilities.Json["data"]!["commandDefinitions"]!.AsArray(), command => command!["name"]!.GetValue<string>() == "project resolve");
        var schemas = await Invoke("schema");
        foreach (var schema in schemas.Json["data"]!["schemas"]!.AsArray())
        {
            var name = schema!.GetValue<string>();
            var found = await Invoke("schema", name);
            Assert.Equal(0, found.Status);
            Assert.Equal($"urn:blast-gb:schema:{name}:1", found.Json["data"]!["schema"]!["$id"]!.GetValue<string>());
        }
        Assert.Equal(2, (await Invoke("schema", "unknown")).Status);
    }

    [Fact]
    public async Task SupportsLegacyAliasesHelpAndVersion()
    {
        var (path, _) = Cartridge();
        var grouped = await Invoke("rom", "info", path);
        var legacy = await Invoke("inspect", path);
        Assert.Equal(0, grouped.Status);
        Assert.Equal("rom info", grouped.Json["command"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(grouped.Json["data"], legacy.Json["data"]));
        Assert.Equal(0, (await Invoke("rom", "verify", path)).Status);
        Assert.Equal(2, (await Invoke("rom", "unknown", path)).Status);
        Assert.Equal(2, (await Invoke("asm", "compile", path)).Status);
        var help = await Invoke("rom", "--help");
        Assert.Contains("gb rom COMMAND", help.Json["data"]!["text"]!.GetValue<string>());
        using var output = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["--version"], output, TextWriter.Null));
        Assert.Equal(AutomationContracts.Version, output.ToString().Trim());
    }

    [Theory]
    [InlineData("rom", "info", "--frames", "1")]
    [InlineData("version", "--version=yes")]
    [InlineData("version", "--json", "--json")]
    [InlineData("version", "--output")]
    [InlineData("version", "--unrecognized")]
    public async Task RejectsInvalidOptions(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.RunAsync(args, output, error));
    }

    [Theory]
    [InlineData("version", "--", "--json")]
    [InlineData("version", "--unrecognized", "--", "--json")]
    public async Task JsonPositionalAfterTerminatorDoesNotChangeErrorOutput(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.RunAsync(args, output, error));
        Assert.Equal("", output.ToString());
        Assert.StartsWith("USAGE:", error.ToString());
    }

    [Fact]
    public async Task JsonOptionBeforeTerminatorStillControlsOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.RunAsync(["version", "--json", "--", "--json"], output, error));
        Assert.Equal("", error.ToString());
        Assert.Equal("USAGE", JsonNode.Parse(output.ToString())!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResolvesProjectWithoutCreatingOutputAndProtectsCollisions()
    {
        var project = PathFor("source game");
        Assert.Equal(0, (await Invoke("project", "init", project)).Status);
        var resolved = await Invoke("project", "resolve", project);
        Assert.Equal(0, resolved.Status);
        Assert.Equal(Path.Combine(project, "build", "game.gb"), resolved.Json["data"]!["outputs"]!["rom"]!.GetValue<string>());
        Assert.Equal("rgbds", resolved.Json["data"]!["engine"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", resolved.Json["data"]!["inputs"]![0]!["sha256"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
        Assert.Equal(0, (await Invoke("project", "info", project)).Status);
        var manifestPath = Path.Combine(project, "gb-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["output"] = "source/main.asm";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("project", "resolve", project)));
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
    }

    [Fact]
    public async Task InitializesAndBuildsGameWithoutExternalToolsAndPreservesExistingOutput()
    {
        var project = PathFor("game");
        Assert.Equal(0, (await Invoke("init", project, "--kind", "game")).Status);
        var first = await Invoke("build", project);
        Assert.Equal(0, first.Status);
        Assert.Equal("builtin-gamespec", first.Json["data"]!["engine"]!.GetValue<string>());
        var output = first.Json["data"]!["output"]!.GetValue<string>();
        var expected = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        Assert.Equal(expected, File.ReadAllBytes(output));
        Assert.Equal("OUTPUT_EXISTS", Error(await Invoke("build", project)));
        Assert.Equal(expected, File.ReadAllBytes(output));
        var repeated = await Invoke("build", project, "--overwrite");
        Assert.Equal(0, repeated.Status);
        Assert.Equal(first.Json["data"]!["sha256"]!.GetValue<string>(), repeated.Json["data"]!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task SourceInitializationRefusesExistingDirectory()
    {
        var project = PathFor("source game");
        Assert.Equal(0, (await Invoke("init", project)).Status);
        var source = File.ReadAllText(Path.Combine(project, "source", "main.asm"));
        Assert.Contains("SECTION", source);
        Assert.Equal("OUTPUT_EXISTS", Error(await Invoke("init", project)));
        Assert.Equal(source, File.ReadAllText(Path.Combine(project, "source", "main.asm")));
    }

    [Fact]
    public async Task ImportsOriginalAndExportsByteExactAssemblyWithoutMutation()
    {
        var (path, rom) = Cartridge();
        var project = PathFor("mod workspace");
        Assert.Equal(0, (await Invoke("import", path, project)).Status);
        Assert.Equal(rom, File.ReadAllBytes(path));
        Assert.Equal(rom, File.ReadAllBytes(Path.Combine(project, "original.gb")));
        Assert.Contains("db ", File.ReadAllText(Path.Combine(project, "source", "main.asm")));
        var source = PathFor("export.asm");
        var exported = await Invoke("asm", "decompile", path, "--output", source);
        Assert.Equal(0, exported.Status);
        Assert.Equal("rgbds-byte-exact", exported.Json["data"]!["format"]!.GetValue<string>());
        Assert.Equal(File.ReadAllText(Path.Combine(project, "source", "main.asm")), File.ReadAllText(source));
        var listing = await Invoke("asm", "decompile", path, "--offset", "0x100", "--count", "2");
        Assert.Equal(2, listing.Json["data"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task GuardedPatchFailureDoesNotWriteAndSuccessPreservesInput()
    {
        var (path, rom) = Cartridge();
        var patches = PathFor("changes.json");
        var output = PathFor("patched.gb");
        File.WriteAllText(patches, JsonSerializer.Serialize(new[] { new { offset = 0x150, bytes = new[] { 0 }, expected = new[] { (rom[0x150] + 1) & 255 } } }));
        Assert.Equal(1, (await Invoke("patch", path, patches, "--output", output)).Status);
        Assert.False(File.Exists(output));
        Assert.Equal(rom, File.ReadAllBytes(path));
        File.WriteAllText(patches, JsonSerializer.Serialize(new[] { new { offset = 0x150, bytes = new[] { 0 }, expected = new[] { (int)rom[0x150] } } }));
        Assert.Equal(0, (await Invoke("rom", "patch", path, patches, "--output", output, "--fix-checksums")).Status);
        Assert.Equal(0, File.ReadAllBytes(output)[0x150]);
        Assert.Equal(rom, File.ReadAllBytes(path));
        Assert.Equal(0, (await Invoke("verify", output)).Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("patch", path, patches, "--output", path, "--overwrite")));
        File.WriteAllText(patches, "[{\"offset\":336,\"bytes\":[0]}]");
        Assert.Equal("INVALID_PATCH", Error(await Invoke("patch", path, patches, "--output", PathFor("unguarded.gb"))));
    }

    [Fact]
    public async Task VerifiesExactSizeAndReportsValidationFailure()
    {
        var (path, rom) = Cartridge();
        rom[0x150] ^= 1;
        File.WriteAllBytes(path, rom);
        var checkedRom = await Invoke("verify", path);
        Assert.Equal(3, checkedRom.Status);
        Assert.Equal("VALIDATION_FAILED", Error(checkedRom));
        File.WriteAllBytes(path, RomTools.RepairChecksums(rom[..^1]));
        checkedRom = await Invoke("verify", path);
        Assert.Equal(3, checkedRom.Status);
        Assert.True(checkedRom.Json["data"]!["headerChecksumValid"]!.GetValue<bool>());
        Assert.True(checkedRom.Json["data"]!["globalChecksumValid"]!.GetValue<bool>());
        Assert.False(checkedRom.Json["data"]!["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DiffBoundsLargeAlternatingChangesAndReportsLengthChanges()
    {
        var (path, rom) = Cartridge();
        Assert.True((await Invoke("rom", "diff", path, path)).Json["data"]!["identical"]!.GetValue<bool>());
        var before = new byte[8 * 1024 * 1024];
        rom.CopyTo(before, 0);
        var after = (byte[])before.Clone();
        for (var index = 0x150; index < after.Length; index += 2) after[index] ^= 1;
        File.WriteAllBytes(PathFor("before.gb"), before);
        File.WriteAllBytes(PathFor("after.gb"), after);
        var compared = await Invoke("rom", "diff", PathFor("before.gb"), PathFor("after.gb"));
        var data = compared.Json["data"]!;
        Assert.Equal((after.Length - 0x150) / 2, data["changedBytes"]!.GetValue<int>());
        Assert.Equal(64, data["ranges"]!.AsArray().Count);
        Assert.True(data["rangesTruncated"]!.GetValue<bool>());
        Assert.True(compared.Json.ToJsonString().Length < 16000);
        File.WriteAllBytes(PathFor("longer.gb"), [.. rom, 42, 43]);
        compared = await Invoke("rom", "diff", path, PathFor("longer.gb"));
        Assert.Equal(2, compared.Json["data"]!["addedBytes"]!.GetValue<int>());
        Assert.Empty(compared.Json["data"]!["ranges"]![0]!["before"]!.AsArray());
        Assert.Equal(rom, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ManifestRejectsTraversalAndInputSidecarCollisionsBeforeBuilding()
    {
        var project = PathFor("game");
        await Invoke("init", project, "--kind", "game");
        var manifestPath = Path.Combine(project, "gb-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["output"] = "../outside.gb";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Equal("INVALID_PROJECT", Error(await Invoke("build", project)));
        Assert.False(File.Exists(PathFor("outside.gb")));
        File.WriteAllText(PathFor("game.sym"), "; retained\n");
        File.WriteAllText(PathFor("gb-project.json"), """{"format":"blast-gb-workspace","version":1,"name":"protected","kind":"source","source":"game.sym","output":"game.gb"}""");
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("build", directory, "--overwrite")));
        Assert.Equal("; retained\n", File.ReadAllText(PathFor("game.sym")));
        Assert.False(File.Exists(PathFor("game.gb")));
    }

    [Fact]
    public async Task SymbolicLinkOutputAliasesCannotReplaceInputsOrEscapeWorkspaces()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links requires privileges on some Windows hosts.
        var (path, rom) = Cartridge();
        var patches = PathFor("changes.json");
        File.WriteAllText(patches, JsonSerializer.Serialize(new[] { new { offset = 0x150, bytes = new[] { 0 }, expected = new[] { (int)rom[0x150] } } }));
        Directory.CreateSymbolicLink(PathFor("alias"), directory);
        Assert.Equal("UNSAFE_OUTPUT", Error(await Invoke("patch", path, patches, "--output", PathFor("alias/original game.gbc"), "--overwrite")));
        Assert.Equal(rom, File.ReadAllBytes(path));
        var project = PathFor("game");
        await Invoke("init", project, "--kind", "game");
        Directory.CreateDirectory(PathFor("outside"));
        File.WriteAllText(PathFor("outside/game.gb"), "retained");
        Directory.CreateSymbolicLink(Path.Combine(project, "build"), PathFor("outside"));
        Assert.Equal("UNSAFE_PATH", Error(await Invoke("build", project, "--overwrite")));
        Assert.Equal("retained", File.ReadAllText(PathFor("outside/game.gb")));
    }

    [Fact]
    public async Task CpuAssertionsAndDebuggerReportEvidenceWithoutChangingCartridge()
    {
        var (path, rom) = Cartridge();
        Assert.Equal(2, (await Invoke("test", path)).Status);
        Assert.Equal(2, (await Invoke("debug", path)).Status);
        var checks = PathFor("checks.json");
        File.WriteAllText(checks, """{"registers":{"pc":336}}""");
        var checkedRom = await Invoke("test", path, "--assert", checks, "--breakpoint", "0x150", "--memory", "0x150:1");
        Assert.Equal(0, checkedRom.Status);
        Assert.Equal("breakpoint", checkedRom.Json["data"]!["stopReason"]!.GetValue<string>());
        Assert.True(checkedRom.Json["data"]!["assertionsPassed"]!.GetValue<bool>());
        Assert.Matches("^[0-9a-f]{64}$", checkedRom.Json["data"]!["framebufferSha256"]!.GetValue<string>());
        File.WriteAllText(checks, """{"registers":{"pc":337}}""");
        Assert.Equal(3, (await Invoke("test", path, "--assert", checks, "--breakpoint", "0x150")).Status);
        var script = PathFor("debug.json");
        File.WriteAllText(script, """{"version":1,"commands":[{"op":"write","address":49152,"bytes":[42]},{"op":"read","address":49152},{"op":"assert","memory":[{"address":49152,"bytes":[42]}]}]}""");
        var debug = await Invoke("debug", path, "--script", script);
        Assert.Equal(0, debug.Status);
        Assert.Equal(42, debug.Json["data"]!["results"]![1]!["bytes"]![0]!.GetValue<int>());
        Assert.Equal(rom, File.ReadAllBytes(path));
        var missed = await Invoke("run", path, "--frames", "1", "--breakpoint", "0xdead", "--max-instructions", "2");
        Assert.Equal(3, missed.Status);
        Assert.Equal("instruction-budget", missed.Json["data"]!["stopReason"]!.GetValue<string>());
    }


    [Theory]
    [InlineData("romSize")]
    [InlineData("fixChecksums")]
    [InlineData("rom")]
    public async Task ManifestOptionalFieldsRejectExplicitNull(string field)
    {
        var project = PathFor("nullable");
        await Invoke("init", project, "--kind", "game");
        var path = Path.Combine(project, "gb-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest[field] = null;
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Equal("INVALID_PROJECT", Error(await Invoke("build", project)));
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
    }

    [Theory]
    [InlineData("[{\"offset\":null,\"bytes\":[0],\"expected\":[0]}]")]
    [InlineData("[{\"offset\":336,\"bytes\":[null],\"expected\":[0]}]")]
    public async Task MalformedPatchFailsWithoutCreatingOutput(string json)
    {
        var (path, rom) = Cartridge();
        var patches = PathFor("invalid.json");
        File.WriteAllText(patches, json);
        var output = PathFor("invalid-output.gb");
        Assert.Equal("INVALID_PATCH", Error(await Invoke("patch", path, patches, "--output", output)));
        Assert.False(File.Exists(output));
        Assert.Equal(rom, File.ReadAllBytes(path));
    }


    [Fact]
    public async Task UnknownHeaderSizeRetainsExplicitNullBankCountInJson()
    {
        var (path, rom) = Cartridge();
        rom[0x148] = 0xff;
        File.WriteAllBytes(path, rom);
        var inspected = await Invoke("rom", "info", path);
        Assert.Equal(0, inspected.Status);
        Assert.True(inspected.Json["data"]!.AsObject().ContainsKey("declaredRomBanks"));
        Assert.Null(inspected.Json["data"]!["declaredRomBanks"]);
    }

    [Fact]
    public async Task CancellationBeforeCommandHasStructuredFailure()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var output = new StringWriter();
        Assert.Equal(1, await CliApplication.RunAsync(["version", "--json"], output, TextWriter.Null, source.Token));
        Assert.Equal("CANCELLED", JsonNode.Parse(output.ToString())!["error"]!["code"]!.GetValue<string>());
    }
}
