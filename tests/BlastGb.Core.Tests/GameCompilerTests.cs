using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class GameCompilerTests
{
    // Recorded from the former TypeScript compiler before the C# migration.
    // Check ROM bytes and full exported source independently of this implementation.
    [Theory]
    [InlineData("gb", "5873501e91ffaadaf214f43efad382cceab8d7083094f81bdd53d47824d006f0", "f2dc106aad094f40ab776551d297d635df346c197dbb97612c7b4a83919012f9")]
    [InlineData("gbc", "36fb24f1da2f7df14db677753890855e50f27f2f03b594955acb48df438b6e65", "15758dc5822e1da9b6a16f8d7acb52ca67e47a3d96894c88fac5a2627173e79b")]
    public void StarterMatchesLegacyGoldenBytesAndSource(string mode, string romHash, string sourceHash)
    {
        var spec = GameCompiler.CreateStarterSpec() with { Mode = mode };
        var result = GameCompiler.Build(spec);
        Assert.Equal(romHash, Convert.ToHexStringLower(SHA256.HashData(result.Rom)));
        Assert.Equal(sourceHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Source))));
        Assert.Equal(result.Rom, GameCompiler.Build(spec).Rom);
        Assert.Equal(32768, result.Rom.Length);
        var metadata = RomTools.Inspect(result.Rom);
        Assert.True(metadata.HeaderChecksumValid);
        Assert.True(metadata.GlobalChecksumValid);
        Assert.True(metadata.LogoValid);
        Assert.Equal("MOSS & MOON", metadata.Title);
        Assert.Equal(mode, metadata.Mode);
        Assert.Equal(mode == "gb" ? 1 : 0, result.Warnings.Length);
    }

    [Fact]
    public void ValidationNormalizesAndCopiesCallerData()
    {
        var input = GameCompiler.CreateStarterSpec();
        var validated = GameCompiler.Validate(input with { Title = "  HELLO  ", Objective = "  Goal  ", Palette = input.Palette with { Ground = "#ABCDEF" } });
        Assert.Equal("HELLO", validated.Title);
        Assert.Equal("Goal", validated.Objective);
        Assert.Equal("#abcdef", validated.Palette.Ground);
        Assert.NotSame(input.Map, validated.Map);
        input.Map[0] = "changed";
        Assert.Equal("####################", validated.Map[0]);
    }

    [Fact]
    public void RejectsMalformedAndUnwinnableMaps()
    {
        var spec = GameCompiler.CreateStarterSpec();
        Assert.Contains("18 rows", Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Map = ["P"] })).Message);
        Assert.Contains("six-digit", Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Palette = spec.Palette with { Ground = "url(x)" } })).Message);
        Assert.Contains("six-digit", Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Palette = spec.Palette with { Ground = "#123456\n" } })).Message);
        var map = Enumerable.Repeat(new string('#', 20), 18).ToArray();
        map[1] = "#P##################";
        map[3] = "#C##################";
        Assert.Contains("reachable", Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Map = map })).Message);
        Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Title = new string('x', 41) }));
        Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Objective = " " }));
        Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Mode = "GBC" }));
        Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Map = spec.Map.Select(row => row.Replace('C', '.')).ToArray() }));
        Assert.Throws<ArgumentException>(() => GameCompiler.Validate(spec with { Map = spec.Map.Select(row => row.Replace('P', '.')).ToArray() }));
    }

    [Fact]
    public void ParseHandlesUntrustedJsonWithStrictTypes()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var spec = GameCompiler.CreateStarterSpec();
        var parsed = GameCompiler.Parse(JsonSerializer.SerializeToElement(spec, options));
        Assert.Equal(GameCompiler.Build(spec).Rom, GameCompiler.Build(parsed).Rom);
        foreach (var json in new[] { "null", "[]", "{}", "{\"title\":1}", "{\"palette\":null}" })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<ArgumentException>(() => GameCompiler.Parse(document.RootElement));
        }
    }

    [Fact]
    public void TitlesCannotInjectAssemblyAndFilenameIsPortable()
    {
        var result = GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Title = "../🎮\nGame: ONE!" });
        Assert.Equal("game-one.gbc", result.Filename);
        Assert.Equal("..GAME ONE!", RomTools.Inspect(result.Rom).Title);
        Assert.DoesNotContain("../🎮", result.Source);
    }

    [Theory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void ReservedDeviceTitlesProducePortableFilenamesWithoutChangingTitles(string mode)
    {
        string[] deviceNames =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];
        var starter = GameCompiler.CreateStarterSpec();
        foreach (var title in deviceNames)
        {
            var result = GameCompiler.Build(starter with { Title = title, Mode = mode });
            var expected = $"blast-{title.ToLowerInvariant()}.{mode}";
            Assert.Equal(expected, result.Filename);
            Assert.Equal(title, result.Title);
            Assert.Equal(title, RomTools.Inspect(result.Rom).Title);
            Assert.Contains($"; rgblink -o {expected} game.o\n", result.Source);
        }
    }

    [Theory]
    [InlineData(" .nUl. ", "blast-nul.gbc")]
    [InlineData("Aux!", "blast-aux.gbc")]
    [InlineData("CONSOLE", "console.gbc")]
    [InlineData("COM10", "com10.gbc")]
    [InlineData("LPT10", "lpt10.gbc")]
    [InlineData("🎮", "blast-game.gbc")]
    public void DeviceNameGuardRunsAfterSanitizationAndPreservesOtherFilenames(string title, string filename)
    {
        var result = GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Title = title });
        Assert.Equal(filename, result.Filename);
    }

    [RgbdsTheory]
    [InlineData("gb")]
    [InlineData("gbc")]
    public void RgbdsRebuildsExportedSourceByteForByte(string mode)
    {
        using var rgbds = new RgbdsFixture();
        var build = GameCompiler.Build(GameCompiler.CreateStarterSpec() with { Mode = mode });
        Assert.Equal(build.Rom, rgbds.Assemble(build.Source));
    }
}
