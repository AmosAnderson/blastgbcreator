using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Core;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private static GameSpec ReadGameSpec(string path, out byte[] original)
    {
        original = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(Utf8.GetString(original));
            return GameCompiler.Parse(document.RootElement);
        }
        catch (JsonException exception) { throw new CliException("INVALID_JSON", $"{path}: {exception.Message}", exception); }
        catch (DecoderFallbackException exception) { throw new CliException("INVALID_JSON", $"{path} is not valid UTF-8.", exception); }
        catch (ArgumentException exception) { throw new CliException("INVALID_GAME", exception.Message, exception); }
    }

    private static JsonObject ValidateGame(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions();
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var game = ReadGameSpec(input, out var original);
        return ToObject(new
        {
            input,
            sha256 = Sha256(original),
            valid = true,
            game,
            collectibles = game.Map.Sum(row => row.Count(cell => cell == 'C')),
            engineVersion = GameCompiler.EngineVersion
        });
    }

    private static JsonObject ExportGameSource(ParsedCommand parsed)
    {
        parsed.RequirePositionals(2);
        parsed.AllowOptions("name");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parsed.Positionals[1]));
        var game = ReadGameSpec(input, out var original);
        var name = parsed.Option("name") ?? game.Title;
        if (string.IsNullOrWhiteSpace(name)) throw new CliException("USAGE", "Project name must contain non-whitespace characters.");
        var build = GameCompiler.Build(game);
        var manifest = new WorkspaceManifest
        {
            Format = ManifestFormat,
            Version = 1,
            Name = name,
            Kind = "source",
            Source = "source/main.asm",
            Game = "game.json",
            Output = "build/" + build.Filename,
            FixChecksums = true
        };
        FileOperations.WriteWorkspace(target, new Dictionary<string, byte[]>
        {
            [ManifestFile] = TextBytes(JsonText(manifest)),
            ["game.json"] = original,
            ["source/main.asm"] = TextBytes(build.Source),
            ["README.md"] = TextBytes($"# {name}\n\nThis editable RGBDS project was exported from a collection-game specification.\nThe original game.json is preserved as a reference; source/main.asm controls future builds.\nEdit the assembly to extend the implemented mechanics. Changes to game.json are not reapplied.\n\nBuild with RGBDS installed: gb build . --json\nRepeat builds with --overwrite.\n")
        });
        return ToObject(new
        {
            input,
            workspace = target,
            manifest = Path.Combine(target, ManifestFile),
            source = Path.Combine(target, "source", "main.asm"),
            game = Path.Combine(target, "game.json"),
            kind = "source",
            expectedRomSha256 = Sha256(build.Rom),
            warnings = build.Warnings
        });
    }

    private static JsonObject CreatePatch(ParsedCommand parsed)
    {
        parsed.RequirePositionals(2);
        parsed.AllowOptions("output", "overwrite");
        var beforePath = Path.GetFullPath(parsed.Positionals[0]);
        var afterPath = Path.GetFullPath(parsed.Positionals[1]);
        var output = Path.GetFullPath(parsed.Option("output")
            ?? throw new CliException("USAGE", "rom patch-create requires --output PATCHES.json."));
        FileOperations.ProtectDestination(output, [beforePath, afterPath]);
        FileOperations.AssertWritable(output, parsed.Has("overwrite"));
        var before = RomFile(beforePath);
        var after = RomFile(afterPath);
        IReadOnlyList<RomPatch> patches;
        try { patches = RomPatches.Create(before, after); }
        catch (ArgumentException exception) { throw new CliException("INVALID_PATCH", exception.Message, exception); }
        var changedBytes = 0;
        for (var index = 0; index < before.Length; index++)
            if (before[index] != after[index]) changedBytes++;
        FileOperations.AtomicWrite(output, JsonText(patches), parsed.Has("overwrite"));
        return ToObject(new
        {
            before = new { path = beforePath, sha256 = Sha256(before) },
            after = new { path = afterPath, sha256 = Sha256(after) },
            output,
            patches = patches.Count,
            changedBytes,
            guardedBytes = patches.Sum(patch => patch.Bytes.Length)
        });
    }

    private static IReadOnlyList<RgbdsSymbol> LoadSymbols(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > RgbdsSymbols.MaxFileBytes)
            throw new CliException("INVALID_SYMBOLS", "Symbol file exceeds the 16 MiB limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new CliException("INVALID_SYMBOLS", "Symbol file changed while it was being read.");
        try { return RgbdsSymbols.Parse(Utf8.GetString(bytes)); }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new CliException("INVALID_SYMBOLS", exception.Message, exception);
        }
    }

    private static RgbdsSymbol FindSymbol(IReadOnlyList<RgbdsSymbol> symbols, string name) =>
        symbols.FirstOrDefault(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal))
            ?? throw new CliException("SYMBOL_NOT_FOUND", $"No symbol named {name} exists in the symbol file.");

    private static JsonObject InspectSymbols(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("symbol");
        var path = Path.GetFullPath(parsed.Positionals[0]);
        var all = LoadSymbols(path);
        IReadOnlyList<RgbdsSymbol> symbols = parsed.Option("symbol") is { } name ? [FindSymbol(all, name)] : all;
        return ToObject(new { path, count = symbols.Count, totalSymbols = all.Count, symbols });
    }
}
