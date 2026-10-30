using System.Diagnostics;
using System.Text.Json;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class UtilityFeatureTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blast-utilities-").FullName;
    private string At(string name) => Path.Combine(directory, name);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task ExportedPatchesRoundTripEveryChangeBeyondPreviewLimits()
    {
        var before = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        var after = (byte[])before.Clone();
        for (int i = 0; i < 80; i++)
            for (int j = 0; j < 40; j++) after[1024 + i * 48 + j] ^= 255;
        File.WriteAllBytes(At("before.gb"), before);
        File.WriteAllBytes(At("after.gb"), after);
        var diff = await CliTests.Invoke("rom", "diff", At("before.gb"), At("after.gb"), "--patch-output", At("changes.json"));
        Assert.Equal(0, diff.Status);
        Assert.True(diff.Json["data"]!["rangesTruncated"]!.GetValue<bool>());
        Assert.Equal(80, diff.Json["data"]!["patchCount"]!.GetValue<int>());
        using var patches = JsonDocument.Parse(File.ReadAllText(At("changes.json")));
        Assert.Equal(80, patches.RootElement.GetArrayLength());
        Assert.Equal(40, patches.RootElement[79].GetProperty("bytes").GetArrayLength());
        var applied = await CliTests.Invoke("rom", "patch", At("before.gb"), At("changes.json"), "--output", At("patched.gb"));
        Assert.Equal(0, applied.Status);
        Assert.Equal(after, File.ReadAllBytes(At("patched.gb")));
        before[1024] ^= 1;
        File.WriteAllBytes(At("wrong-revision.gb"), before);
        Assert.Equal(1, (await CliTests.Invoke("rom", "patch", At("wrong-revision.gb"), At("changes.json"), "--output", At("patched.gb"), "--overwrite")).Status);
        Assert.Equal(after, File.ReadAllBytes(At("patched.gb")));
    }

    [Fact]
    public async Task PatchExportPreservesSourcesAndRejectsLengthChangesWithoutReplacingOutput()
    {
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        File.WriteAllBytes(At("before.gb"), rom);
        File.WriteAllBytes(At("after.gb"), rom[..^1]);
        File.WriteAllText(At("changes.json"), "previous");
        var failed = await CliTests.Invoke("rom", "diff", At("before.gb"), At("after.gb"), "--patch-output", At("changes.json"), "--overwrite");
        Assert.Equal(1, failed.Status);
        Assert.Equal("previous", File.ReadAllText(At("changes.json")));
        var collision = await CliTests.Invoke("rom", "diff", At("before.gb"), At("after.gb"), "--patch-output", At("before.gb"), "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", collision.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(rom, File.ReadAllBytes(At("before.gb")));
    }

    [Fact]
    public async Task NamedAddressesAnnotateListingsAndPreserveSymbolInputs()
    {
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        File.WriteAllBytes(At("game.gb"), rom);
        const string symbols = "00:0150 Main\n00:0150 Main.alias\n01:4000 NextBank\n00:c000 PlayerX\n";
        File.WriteAllText(At("game.sym"), symbols);
        var listing = await CliTests.Invoke("asm", "decompile", At("game.gb"), "--symbols", At("game.sym"), "--label", "Main", "--count", "1");
        Assert.Equal(0, listing.Status);
        Assert.Equal(336, listing.Json["data"]!["offset"]!.GetValue<int>());
        Assert.Equal(2, listing.Json["data"]!["lines"]![0]!["labels"]!.AsArray().Count);
        var stopped = await CliTests.Invoke("run", At("game.gb"), "--symbols", At("game.sym"), "--breakpoint", "Main", "--frames", "1");
        Assert.Equal(0, stopped.Status);
        Assert.True(stopped.Json["data"]!["breakpointReached"]!.GetValue<bool>());
        Assert.Equal("Main", stopped.Json["data"]!["breakpointSymbol"]!["name"]!.GetValue<string>());
        File.WriteAllText(At("debug.json"), """{"version":1,"commands":[{"op":"breakpoints","addresses":["Main"]},{"op":"run","frames":1},{"op":"assert","registers":{"pc":336}}]}""");
        Assert.Equal(0, (await CliTests.Invoke("debug", At("game.gb"), "--symbols", At("game.sym"), "--script", At("debug.json"))).Status);
        Assert.Equal(1, (await CliTests.Invoke("asm", "decompile", At("game.gb"), "--symbols", At("game.sym"), "--output", At("game.sym"), "--overwrite")).Status);
        Assert.Equal(symbols, File.ReadAllText(At("game.sym")));
        Assert.Equal(2, (await CliTests.Invoke("asm", "decompile", At("game.gb"), "--symbols", At("game.sym"), "--label", "PlayerX")).Status);
    }

    [Fact]
    public async Task FourShadePngTileRoundTripPreservesOrderAndBadInputPreservesDestination()
    {
        var tiles = Enumerable.Range(0, 64).Select(index => (byte)(index * 37)).ToArray();
        File.WriteAllBytes(At("tiles.2bpp"), tiles);
        Assert.Equal(0, (await CliTests.Invoke("tiles", "export", At("tiles.2bpp"), "--output", At("sheet.png"), "--columns", "2")).Status);
        Assert.Equal(0, (await CliTests.Invoke("tiles", "import", At("sheet.png"), "--output", At("roundtrip.2bpp"))).Status);
        Assert.Equal(tiles, File.ReadAllBytes(At("roundtrip.2bpp")));
        Assert.Equal(1, (await CliTests.Invoke("tiles", "import", At("sheet.png"), "--output", At("sheet.png"), "--overwrite")).Status);
        Assert.Equal(2, (await CliTests.Invoke("tiles", "export", At("tiles.2bpp"), "--output", At("sheet.png"), "--columns", "3", "--overwrite")).Status);
        File.WriteAllBytes(At("bad.2bpp"), [1, 2, 3]);
        Assert.Equal(2, (await CliTests.Invoke("tiles", "export", At("bad.2bpp"), "--output", At("sheet.png"), "--overwrite")).Status);
        Assert.Equal(0, (await CliTests.Invoke("tiles", "import", At("sheet.png"), "--output", At("roundtrip.2bpp"), "--overwrite")).Status);
        Assert.Equal(tiles, File.ReadAllBytes(At("roundtrip.2bpp")));
    }

    [Fact]
    public async Task LintAggregatesDiagnosticsAndDoctorReportsEngineIdentity()
    {
        File.WriteAllText(At("game.json"), """{"title":"","mode":"invalid","objective":"","palette":{},"map":[]}""");
        var lint = await CliTests.Invoke("game", "lint", At("game.json"));
        Assert.Equal(3, lint.Status);
        Assert.Contains(lint.Json["data"]!["diagnostics"]!.AsArray(), item => item!["path"]!.GetValue<string>() == "/title");
        Assert.Contains(lint.Json["data"]!["diagnostics"]!.AsArray(), item => item!["path"]!.GetValue<string>() == "/map");
        var doctor = await CliTests.Invoke("doctor");
        Assert.Equal(0, doctor.Status);
        Assert.True(doctor.Json["data"]!["runtimeHealthy"]!.GetValue<bool>());
        Assert.Equal(2, doctor.Json["data"]!["tools"]!.AsArray().Count);
        Assert.Matches("^[0-9a-f]{64}$", doctor.Json["data"]!["engine"]!["engineSha256"]!.GetValue<string>());
        Assert.Equal(AutomationContracts.Version, doctor.Json["data"]!["toolkitVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProvenanceIsOptInAndStarterSuiteBuildsWithoutRgbds()
    {
        string project = At("game");
        await CliTests.Invoke("project", "init", project, "--kind", "game");
        var build = await CliTests.Invoke("build", project, "--provenance");
        Assert.Equal(0, build.Status);
        Assert.Equal(AutomationContracts.Version, build.Json["data"]!["provenance"]!["toolkitVersion"]!.GetValue<string>());
        Assert.Equal(2, build.Json["data"]!["provenance"]!["observedInputs"]!.AsArray().Count);
        var suite = await CliTests.Invoke("project", "test", project, "--build", "--overwrite");
        Assert.True(suite.Status == 0, suite.Json.ToJsonString());
        Assert.Equal(1, suite.Json["data"]!["passed"]!.GetValue<int>());
    }

    [Fact]
    public async Task BuildDeadlineKillsStalledAssemblerAndPreservesExistingArtifacts()
    {
        if (OperatingSystem.IsWindows()) return;
        string tools = At("fake-tools");
        Directory.CreateDirectory(tools);
        string assembler = Path.Combine(tools, "rgbasm");
        File.WriteAllText(assembler, "#!/bin/sh\nexec sleep 30\n");
        File.SetUnixFileMode(assembler, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(At("main.asm"), "SECTION \"ROM\", ROM0[$0000]\nds $8000\n");
        foreach (string name in new[] { "old.gb", "old.sym", "old.map" }) File.WriteAllText(At(name), "preserved " + name);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { typeof(CliApplication).Assembly.Location, "asm", "compile", At("main.asm"), "--output", At("old.gb"), "--overwrite", "--timeout-seconds", "1", "--json" }) start.ArgumentList.Add(arg);
        start.Environment["RGBDS_BIN"] = tools;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        Assert.Equal(1, process.ExitCode);
        using var envelope = JsonDocument.Parse(await stdout);
        Assert.Equal("BUILD_TIMEOUT", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("", await stderr);
        foreach (string name in new[] { "old.gb", "old.sym", "old.map" }) Assert.Equal("preserved " + name, File.ReadAllText(At(name)));
    }
}
