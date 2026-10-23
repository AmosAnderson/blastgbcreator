using System.Text.Json.Nodes;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class ProjectSuiteTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast gameplay suites " + Guid.NewGuid().ToString("N"));
    private static readonly byte[] GameRom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
    public ProjectSuiteTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string path) => Path.GetFullPath(Path.Combine(directory, path));
    private string Write(string relative, string contents)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
    private async Task<string> Project(string kind = "game", bool rom = true)
    {
        var path = At("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", path, "--kind", kind)).Status);
        if (rom)
        {
            Directory.CreateDirectory(At("project/build"));
            File.WriteAllBytes(At("project/build/game.gb"), GameRom);
        }
        return path;
    }
    private void InitialScript(string relative = "project/initial.json") => Write(relative,
        """{"version":1,"commands":[{"op":"assert","registers":{"pc":256}}]}""");
    private void Suite(string cases, string relative = "project/tests.json") => Write(relative, "{\"version\":1,\"cases\":" + cases + "}");
    private void ChangeManifest(Action<JsonObject> update)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(At("project/gb-project.json")))!.AsObject();
        update(manifest);
        Write("project/gb-project.json", manifest.ToJsonString());
    }
    private static string Error((int Status, JsonNode Json) result) => result.Json["error"]!["code"]!.GetValue<string>();
    private static void Success((int Status, JsonNode Json) result) => Assert.True(result.Status == 0, result.Json.ToJsonString());

    [Fact]
    public async Task DefaultSuiteRunsMixedCasesInFreshSessions()
    {
        var project = await Project();
        Write("project/advance.json", """{"version":1,"commands":[{"op":"step"}]}""");
        InitialScript();
        var expected = new JsonObject { ["memory"] = new JsonArray(new JsonObject { ["address"] = 256, ["bytes"] = new JsonArray(GameRom.Skip(256).Take(4).Select(value => (JsonNode?)JsonValue.Create((int)value)).ToArray()) }) };
        Write("project/assert.json", expected.ToJsonString());
        Suite("""[{"name":"advance","script":"advance.json"},{"name":"fresh","script":"initial.json"},{"name":"run","frames":1,"assert":"assert.json","buttons":["A","right"]}]""");
        var result = await CliTests.Invoke("project", "test", project);
        Success(result);
        Assert.Equal(3, result.Json["data"]!["passed"]!.GetValue<int>());
        Assert.Equal(0, result.Json["data"]!["cases"]![1]!["data"]!["ticks"]!.GetValue<double>());
        Assert.Equal(GameRom, File.ReadAllBytes(At("project/build/game.gb")));
    }

    [Fact]
    public async Task ManifestSuitePathResolvesCasePathsFromItsDirectoryAndBuildsFirst()
    {
        var project = await Project(rom: false);
        ChangeManifest(manifest => manifest["tests"] = "checks/suite.json");
        InitialScript("project/checks/initial.json");
        Suite("""[{"name":"initial","script":"initial.json"}]""", "project/checks/suite.json");
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Success(result);
        Assert.Equal(At("project/checks/suite.json"), result.Json["data"]!["suite"]!.GetValue<string>());
        Assert.Equal("builtin-gamespec", result.Json["data"]!["build"]!["engine"]!.GetValue<string>());
        Assert.Equal(GameRom, File.ReadAllBytes(At("project/build/game.gb")));
    }

    [Fact]
    public async Task FailedCaseDoesNotPreventIndependentCaseAndEvidenceCommitsTogether()
    {
        var project = await Project();
        Write("project/visual.json", """{"version":1,"commands":[{"op":"capture","name":"before"},{"op":"assert","framebufferSha256":"0000000000000000000000000000000000000000000000000000000000000000"},{"op":"step"}]}""");
        InitialScript();
        Suite("""[{"name":"visual","script":"visual.json"},{"name":"fresh","script":"initial.json"}]""");
        var report = At("report.json");
        var result = await CliTests.Invoke("project", "test", project, "--report", report, "--capture-dir", At("captures"));
        Assert.Equal(3, result.Status);
        Assert.Equal(1, result.Json["data"]!["passed"]!.GetValue<int>());
        Assert.Equal(1, result.Json["data"]!["failed"]!.GetValue<int>());
        Assert.False(result.Json["data"]!["cases"]![0]!["data"]!["completed"]!.GetValue<bool>());
        Assert.True(result.Json["data"]!["cases"]![1]!["valid"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(result.Json["data"], JsonNode.Parse(File.ReadAllText(report))));
        foreach (var name in new[] { "visual/_final.png", "visual/before.png", "visual/failure-1.png", "fresh/_final.png" })
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(At("captures/" + name))[..8]);
        Assert.Equal(2, result.Json["data"]!["cases"]![0]!["data"]!["captures"]!.AsArray().Count);
    }

    [Fact]
    public async Task FailedRunVisualAssertionRetainsFrame()
    {
        var project = await Project();
        Write("project/visual.json", """{"framebufferSha256":"0000000000000000000000000000000000000000000000000000000000000000"}""");
        Suite("""[{"name":"visual","frames":1,"assert":"visual.json"}]""");
        var result = await CliTests.Invoke("project", "test", project, "--capture-dir", At("captures"));
        Assert.Equal(3, result.Status);
        Assert.Equal("failure", result.Json["data"]!["cases"]![0]!["data"]!["captures"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(File.ReadAllBytes(At("captures/visual/_final.png")), File.ReadAllBytes(At("captures/visual/failure.png")));
    }

    [Fact]
    public async Task InvalidLaterScriptPreventsBuildExecutionAndOutputReplacement()
    {
        var project = await Project();
        InitialScript();
        Write("project/bad.json", """{"version":1,"commands":[{"op":"buttons","pressed":["invalid"]}]}""");
        Suite("""[{"name":"first","script":"initial.json"},{"name":"bad","script":"bad.json"}]""");
        var report = Write("report.json", "keep report");
        var frame = Write("captures/first/_final.png", "keep capture");
        var result = await CliTests.Invoke("project", "test", project, "--build", "--overwrite", "--report", report, "--capture-dir", At("captures"));
        Assert.Equal(1, result.Status);
        Assert.Equal("INVALID_SUITE", Error(result));
        Assert.Equal("keep report", File.ReadAllText(report));
        Assert.Equal("keep capture", File.ReadAllText(frame));
        Assert.Equal(GameRom, File.ReadAllBytes(At("project/build/game.gb")));
    }

    [Theory]
    [InlineData("{\"op\":\"frame\",\"count\":1801}")]
    [InlineData("{\"op\":\"step\",\"count\":500001}")]
    [InlineData("{\"op\":\"until\",\"maxInstructions\":300000,\"memory\":[{\"address\":49152,\"bytes\":[0,0]}]}")]
    public async Task AggregateBudgetsAreValidatedAcrossCasesBeforeBuild(string command)
    {
        var project = await Project(rom: false);
        Write("project/script.json", "{\"version\":1,\"commands\":[" + command + "]}");
        Suite("""[{"name":"one","script":"script.json"},{"name":"two","script":"script.json"}]""");
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Assert.Equal(1, result.Status);
        Assert.Equal("INVALID_SUITE", Error(result));
        Assert.False(File.Exists(At("project/build/game.gb")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandAndCaptureLimitsApplyAcrossCases(bool captures)
    {
        var project = await Project(rom: false);
        var operations = new JsonArray(Enumerable.Range(0, captures ? 17 : 501).Select(index => (JsonNode?)(captures
            ? new JsonObject { ["op"] = "capture", ["name"] = "frame" + index }
            : new JsonObject { ["op"] = "buttons", ["pressed"] = new JsonArray() })).ToArray());
        Write("project/script.json", new JsonObject { ["version"] = 1, ["commands"] = operations }.ToJsonString());
        Suite("""[{"name":"one","script":"script.json"},{"name":"two","script":"script.json"}]""");
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Assert.Equal(1, result.Status);
        Assert.Equal("INVALID_SUITE", Error(result));
        Assert.False(File.Exists(At("project/build/game.gb")));
    }

    [Fact]
    public async Task ExplicitSuiteResolvesSymbolsAndProtectsSymbolFile()
    {
        var project = await Project();
        Write("custom/initial.json", """{"version":1,"commands":[{"op":"assert","registers":{"pc":"Entry"}}]}""");
        Suite("""[{"name":"one","script":"initial.json"}]""", "custom/suite.json");
        var symbols = Write("custom/game.sym", "00:0100 Entry\n");
        Success(await CliTests.Invoke("project", "test", project, "--suite", At("custom/suite.json"), "--symbols", symbols));
        var result = await CliTests.Invoke("project", "test", project, "--suite", At("custom/suite.json"), "--symbols", symbols, "--report", symbols, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Equal("00:0100 Entry\n", File.ReadAllText(symbols));
    }

    [Theory]
    [InlineData("[{\"name\":\"same\",\"script\":\"initial.json\"},{\"name\":\"SAME\",\"script\":\"initial.json\"}]")]
    [InlineData("[{\"name\":\"../escape\",\"script\":\"initial.json\"}]")]
    [InlineData("[{\"name\":\"CON\",\"script\":\"initial.json\"}]")]
    [InlineData("[{\"name\":\"one\",\"script\":\"initial.json\",\"frames\":1}]")]
    [InlineData("[{\"name\":\"one\",\"frames\":1,\"assert\":\"assert.json\",\"buttons\":[\"A\",\"A\"]}]")]
    [InlineData("[{\"name\":\"one\",\"frames\":1.00000000000000000001,\"assert\":\"assert.json\"}]")]
    [InlineData("[{\"name\":\"one\",\"frames\":1,\"assert\":\"assert.json\",\"frames\":1}]")]
    [InlineData("[]")]
    public async Task InvalidCaseDefinitionsFailBeforeBuild(string cases)
    {
        var project = await Project(rom: false);
        InitialScript();
        Write("project/assert.json", """{"registers":{"pc":256}}""");
        Suite(cases);
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Assert.Equal(1, result.Status);
        Assert.Equal("INVALID_SUITE", Error(result));
        Assert.False(File.Exists(At("project/build/game.gb")));
    }

    [Fact]
    public async Task ReferencedFilesCannotEscapeSuiteDirectory()
    {
        var project = await Project(rom: false);
        InitialScript();
        Suite("""[{"name":"one","script":"../initial.json"}]""", "project/checks/suite.json");
        var result = await CliTests.Invoke("project", "test", project, "--suite", At("project/checks/suite.json"), "--build");
        Assert.Equal(1, result.Status);
        Assert.False(File.Exists(At("project/build/game.gb")));
    }

    [Theory]
    [InlineData("tests.json")]
    [InlineData("initial.json")]
    [InlineData("build/game.gb")]
    [InlineData("game.json")]
    public async Task ReportsCannotOverwriteConsumedOrProjectInputs(string destination)
    {
        var project = await Project();
        InitialScript();
        Suite("""[{"name":"one","script":"initial.json"}]""");
        var path = At("project/" + destination);
        var before = File.ReadAllBytes(path);
        var result = await CliTests.Invoke("project", "test", project, "--report", path, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ReportAndCaptureCollisionFailsBeforeBuild()
    {
        var project = await Project(rom: false);
        InitialScript();
        Suite("""[{"name":"one","script":"initial.json"}]""");
        var report = At("captures/one/_final.png");
        var result = await CliTests.Invoke("project", "test", project, "--build", "--report", report, "--capture-dir", At("captures"));
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.False(File.Exists(At("project/build/game.gb")));
        Assert.False(File.Exists(report));
    }

    [Fact]
    public async Task BuildCannotReplaceSuiteCaseFile()
    {
        var project = await Project(rom: false);
        InitialScript("project/build/game.gb");
        Suite("""[{"name":"one","script":"build/game.gb"}]""");
        var before = File.ReadAllBytes(At("project/build/game.gb"));
        var result = await CliTests.Invoke("project", "test", project, "--build", "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Equal(before, File.ReadAllBytes(At("project/build/game.gb")));
    }

    [Fact]
    public async Task SourceSuiteProtectsGeneratedAssetDestinationsWithoutBuilding()
    {
        var project = await Project("source");
        ChangeManifest(manifest => manifest["assets"] = new JsonArray(new JsonObject { ["input"] = "image.png", ["output"] = "generated/tiles.2bpp" }));
        InitialScript();
        Suite("""[{"name":"one","script":"initial.json"}]""");
        var path = Write("project/generated/tiles.2bpp", "asset bytes");
        var result = await CliTests.Invoke("project", "test", project, "--report", path, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Equal("asset bytes", File.ReadAllText(path));
    }

    [Fact]
    public async Task SourceSuiteWithoutReceiptCanCreateButCannotReplaceEvidence()
    {
        var project = await Project("source");
        InitialScript();
        Suite("""[{"name":"one","script":"initial.json"}]""");
        var report = At("report.json");
        Success(await CliTests.Invoke("project", "test", project, "--report", report));
        var before = File.ReadAllBytes(report);
        var result = await CliTests.Invoke("project", "test", project, "--report", report, "--overwrite");
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Contains("--build", result.Json["error"]!["message"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(report));
    }

    [NativeRgbdsFact]
    public async Task BuildDiscoversIncludeBeforeCommittingRomOrSuiteArtifacts()
    {
        var project = await Project("source");
        var include = Write("project/dependency.inc", "; retain include\n");
        File.AppendAllText(At("project/source/main.asm"), "\nINCLUDE \"dependency.inc\"\n");
        InitialScript();
        Suite("""[{"name":"one","script":"initial.json"}]""");
        var result = await CliTests.Invoke("project", "test", project, "--build", "--overwrite", "--report", include);
        Assert.Equal(1, result.Status);
        Assert.Equal("UNSAFE_OUTPUT", Error(result));
        Assert.Equal("; retain include\n", File.ReadAllText(include));
        Assert.Equal(GameRom, File.ReadAllBytes(At("project/build/game.gb")));
        Assert.False(File.Exists(At("project/build/game.sym")));
        Assert.False(File.Exists(At("project/build/game.map")));
    }

    [NativeRgbdsFact]
    public async Task FreshReceiptPermitsEvidenceReplacementAndProtectsRecordedInputs()
    {
        var project = await Project("source", rom: false);
        ChangeManifest(manifest => manifest["receipt"] = "build/receipt.json");
        var include = Write("project/dependency.inc", "; retain include\n");
        File.AppendAllText(At("project/source/main.asm"), "\nINCLUDE \"dependency.inc\"\n");
        InitialScript();
        Suite("""[{"name":"one","script":"initial.json"}]""");
        Success(await CliTests.Invoke("project", "test", project, "--build"));
        var report = Write("report.json", "replace evidence");
        Success(await CliTests.Invoke("project", "test", project, "--report", report, "--overwrite"));
        var rejected = await CliTests.Invoke("project", "test", project, "--report", include, "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", Error(rejected));
        Assert.Equal("; retain include\n", File.ReadAllText(include));
        var before = File.ReadAllBytes(report);
        File.AppendAllText(At("project/source/main.asm"), "\n; source edited after build\n");
        rejected = await CliTests.Invoke("project", "test", project, "--report", report, "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", Error(rejected));
        Assert.Contains("stale", rejected.Json["error"]!["message"]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(report));
    }
}
