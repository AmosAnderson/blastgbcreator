using System.Text.Json.Nodes;
using BlastGb.Cli;
using BlastGb.Core;

namespace BlastGb.Cli.Tests;

public sealed class SuiteTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "blast suite " + Guid.NewGuid().ToString("N"));
    public SuiteTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);
    private void Write(string path, string contents)
    {
        var full = PathFor(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    [Fact]
    public async Task CasesUseIndependentSessionsAndRetainFailureEvidence()
    {
        var rom = GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom;
        File.WriteAllBytes(PathFor("game.gb"), rom);
        Write("fail.json", """{"version":1,"commands":[{"op":"step"},{"op":"assert","registers":{"pc":0}},{"op":"write","address":49152,"bytes":[42]}]}""");
        Write("fresh.json", """{"version":1,"commands":[{"op":"assert","registers":{"pc":256}}]}""");
        Write("checks.json", """{"registers":{"pc":256}}""");
        Write("gb-tests.json", """
            {"version":1,"cases":[
              {"name":"fails after stepping","rom":"game.gb","script":"fail.json"},
              {"name":"fresh session","rom":"game.gb","script":"fresh.json"},
              {"name":"assertion case","rom":"game.gb","assert":"checks.json","frames":1,"breakpoint":256}]}
            """);
        var result = await CliTests.Invoke("project", "test", directory);
        Assert.Equal(3, result.Status);
        Assert.False(result.Json["ok"]!.GetValue<bool>());
        var data = result.Json["data"]!;
        Assert.Equal(3, data["total"]!.GetValue<int>());
        Assert.Equal(2, data["passed"]!.GetValue<int>());
        Assert.Equal(1, data["failed"]!.GetValue<int>());
        Assert.Equal(2, data["results"]![0]!["data"]!["results"]!.AsArray().Count);
        Assert.False(data["results"]![0]!["ok"]!.GetValue<bool>());
        Assert.True(data["results"]![1]!["ok"]!.GetValue<bool>());
        Assert.True(data["results"]![2]!["ok"]!.GetValue<bool>());
        Assert.Equal(rom, File.ReadAllBytes(PathFor("game.gb")));
    }

    [Fact]
    public async Task BuildProducesRomThenRunsPreparedChecks()
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        File.WriteAllText(Path.Combine(project, "checks.json"), """{"registers":{"pc":256}}""");
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"startup","rom":"build/game.gb","assert":"checks.json","frames":1,"breakpoint":256}]}""");
        var result = await CliTests.Invoke("project", "test", Path.Combine(project, "gb-tests.json"), "--build");
        Assert.Equal(0, result.Status);
        Assert.True(result.Json["data"]!["assertionsPassed"]!.GetValue<bool>());
        Assert.Equal(1, result.Json["data"]!["passed"]!.GetValue<int>());
        Assert.Equal("builtin-gamespec", result.Json["data"]!["build"]!["engine"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(project, "build", "game.gb")));
    }

    [Theory]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"unknown\"}]}")]
    [InlineData("{\"version\":1,\"commands\":[{\"op\":\"frame\",\"count\":3600},{\"op\":\"frame\"}]}")]
    public async Task LateInvalidScriptIsRejectedBeforeBuild(string invalid)
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        File.WriteAllText(Path.Combine(project, "first.json"), """{"version":1,"commands":[{"op":"step"}]}""");
        File.WriteAllText(Path.Combine(project, "last.json"), invalid);
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"first","rom":"build/game.gb","script":"first.json"},{"name":"last","rom":"build/game.gb","script":"last.json"}]}""");
        var result = await CliTests.Invoke("project", "test", project, "--build", "--overwrite");
        Assert.NotEqual(0, result.Status);
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
    }

    [Fact]
    public async Task AggregateBudgetsAreRejectedBeforeBuild()
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        File.WriteAllText(Path.Combine(project, "long.json"), """{"version":1,"commands":[{"op":"frame","count":1801}]}""");
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"first","rom":"build/game.gb","script":"long.json"},{"name":"second","rom":"build/game.gb","script":"long.json"}]}""");
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Assert.Equal("INVALID_SUITE", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
    }

    [Fact]
    public async Task ExplicitUntilBudgetsAreAggregatedAcrossStandaloneCases()
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        File.WriteAllText(Path.Combine(project, "long.json"), """{"version":1,"commands":[{"op":"until","frames":1801,"maxInstructions":1,"registers":{"pc":256}}]}""");
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"first","rom":"build/game.gb","script":"long.json"},{"name":"second","rom":"build/game.gb","script":"long.json"}]}""");
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Assert.Equal("INVALID_SUITE", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
    }

    [Fact]
    public async Task NamedCaptureBudgetsAreAggregatedAcrossStandaloneCases()
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        var operations = new JsonArray(Enumerable.Range(0, 17).Select(index => (JsonNode?)new JsonObject
        { ["op"] = "capture", ["name"] = "frame" + index }).ToArray());
        File.WriteAllText(Path.Combine(project, "captures.json"), new JsonObject { ["version"] = 1, ["commands"] = operations }.ToJsonString());
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"first","rom":"build/game.gb","script":"captures.json"},{"name":"second","rom":"build/game.gb","script":"captures.json"}]}""");
        var result = await CliTests.Invoke("project", "test", project, "--build");
        Assert.Equal("INVALID_SUITE", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(project, "build")));
    }

    [Fact]
    public async Task BuildCannotReplaceAnAssertionInput()
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project, "--kind", "game")).Status);
        var manifestPath = Path.Combine(project, "gb-project.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["output"] = "checks.json";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        const string checks = """{"registers":{"pc":256}}""";
        File.WriteAllText(Path.Combine(project, "checks.json"), checks);
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"check","rom":"checks.json","assert":"checks.json"}]}""");
        var result = await CliTests.Invoke("project", "test", project, "--build", "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(checks, File.ReadAllText(Path.Combine(project, "checks.json")));
    }

    [Fact]
    public async Task SymbolArtifactCannotReplaceADebugScript()
    {
        var project = PathFor("project");
        Assert.Equal(0, (await CliTests.Invoke("project", "init", project)).Status);
        Directory.CreateDirectory(Path.Combine(project, "build"));
        const string script = """{"version":1,"commands":[{"op":"step"}]}""";
        File.WriteAllText(Path.Combine(project, "build", "game.sym"), script);
        File.WriteAllText(Path.Combine(project, "gb-tests.json"), """{"version":1,"cases":[{"name":"check","rom":"build/game.gb","script":"build/game.sym"}]}""");
        var result = await CliTests.Invoke("project", "test", project, "--build", "--overwrite");
        Assert.Equal("UNSAFE_OUTPUT", result.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(script, File.ReadAllText(Path.Combine(project, "build", "game.sym")));
        Assert.False(File.Exists(Path.Combine(project, "build", "game.gb")));
    }

    [Theory]
    [InlineData("{\"version\":1,\"cases\":[{\"name\":\"bad\",\"rom\":\"game.gb\",\"assert\":\"checks.json\",\"script\":\"debug.json\"}]}")]
    [InlineData("{\"version\":1,\"cases\":[{\"name\":\"bad\",\"rom\":\"game.gb\",\"assert\":\"checks.json\",\"frames\":1.1}]}")]
    [InlineData("{\"version\":1,\"cases\":[{\"name\":\"bad\",\"rom\":\"game.gb\",\"script\":\"debug.json\",\"frames\":1}]}")]
    [InlineData("{\"version\":1,\"cases\":[{\"name\":\"bad\",\"rom\":\"../game.gb\",\"assert\":\"checks.json\"}]}")]
    public async Task RejectsAmbiguousCasesAndInvalidOptions(string suite)
    {
        File.WriteAllBytes(PathFor("game.gb"), GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom);
        Write("checks.json", """{"registers":{"pc":256}}""");
        Write("debug.json", """{"version":1,"commands":[{"op":"step"}]}""");
        Write("gb-tests.json", suite);
        Assert.NotEqual(0, (await CliTests.Invoke("project", "test", directory)).Status);
    }
}
