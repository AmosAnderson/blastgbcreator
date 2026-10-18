using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Runtime;

namespace BlastGb.Runtime.Tests;

/// <summary>One-time migration oracle captured from the original host, using original homebrew ROMs.</summary>
public sealed class ParityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void RunMatchesOriginalHostExactly(string mode)
    {
        var rom = File.ReadAllBytes(Fixture($"{mode}.gb"));
        var actual = RuntimeRunner.Run(rom, new() { Frames = 60, Memory = [new(0xc000, 32)], Buttons = ["A", "right"] });
        var expected = JsonNode.Parse(File.ReadAllText(Fixture($"{mode}-run.json")));
        var serialized = JsonSerializer.SerializeToNode(actual, JsonOptions);
        Assert.True(JsonNode.DeepEquals(expected, serialized), $"Expected: {expected}\nActual: {serialized}");
    }

    [Theory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void DebugMatchesOriginalHostExactly(string mode)
    {
        var rom = File.ReadAllBytes(Fixture($"{mode}.gb"));
        using var script = JsonDocument.Parse(File.ReadAllText(Fixture("debug.json")));
        var actual = RuntimeRunner.Debug(rom, script.RootElement);
        var expected = JsonNode.Parse(File.ReadAllText(Fixture($"{mode}-debug.json")));
        var serialized = JsonSerializer.SerializeToNode(actual, JsonOptions);
        Assert.True(JsonNode.DeepEquals(expected, serialized), $"Expected: {expected}\nActual: {serialized}");
    }
}
