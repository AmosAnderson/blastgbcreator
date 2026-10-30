using System.Text.Json;
using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class GameLintTests
{
    [Fact]
    public void ReportsIndependentFieldsAndExactInvalidCellCoordinates()
    {
        var spec = GameCompiler.CreateStarterSpec();
        var map = spec.Map.ToArray();
        map[2] = map[2][..3] + "?" + map[2][4..];
        var diagnostics = GameCompiler.Lint(spec with
        {
            Title = "",
            Mode = "GBC",
            Objective = "",
            Map = map,
            Palette = spec.Palette with { Wall = "red", Player = "#123456\n" }
        });
        Assert.Equal(6, diagnostics.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/title");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/mode");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/objective");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/palette/wall");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/palette/player");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/map/2" && diagnostic.Row == 2 && diagnostic.Column == 3);
        Assert.Equal('?', map[2][3]);
        Assert.Empty(GameCompiler.Lint(spec));
    }

    [Fact]
    public void ReportsEveryUnreachableCollectibleAndSkipsDependentChecksForMalformedMaps()
    {
        var spec = GameCompiler.CreateStarterSpec();
        var map = Enumerable.Repeat(new string('#', 20), 18).ToArray();
        map[1] = "#P##################";
        map[2] = "##C#################";
        map[3] = "###C################";
        var diagnostics = GameCompiler.Lint(spec with { Map = map });
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Row == 2 && diagnostic.Column == 2);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Row == 3 && diagnostic.Column == 3);
        diagnostics = GameCompiler.Lint(spec with { Map = ["?"], Palette = null! });
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/palette");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/map");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonLintKeepsStrictTypesAndDoesNotRequireACompilation()
    {
        using var document = JsonDocument.Parse("""{"title":42,"mode":"gbc","objective":false,"palette":{"background":null},"map":[23]}""");
        var diagnostics = GameCompiler.Lint(document.RootElement);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/title" && diagnostic.Message.Contains("string", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/objective");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/palette/background");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/palette/wall");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Path == "/map/0" && diagnostic.Row == 0);
        using var invalidRoot = JsonDocument.Parse("null");
        Assert.Equal("", Assert.Single(GameCompiler.Lint(invalidRoot.RootElement)).Path);
        var json = JsonSerializer.SerializeToElement(GameCompiler.CreateStarterSpec(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Empty(GameCompiler.Lint(json));
        Assert.Equal(GameCompiler.Build(GameCompiler.Parse(json)).Rom, GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom);
    }
}
