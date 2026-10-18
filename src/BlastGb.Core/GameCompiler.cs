using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlastGb.Core;

/// <summary>Builds a complete deterministic cartridge and matching editable RGBDS source.</summary>
public static partial class GameCompiler
{
    public const string EngineVersion = "1.0.0";

    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
    [GeneratedRegex("[^A-Z0-9 &!?.-]", RegexOptions.CultureInvariant)]
    private static partial Regex TitleCharacters();
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex FilenameCharacters();

    private static bool IsWindowsDeviceName(string stem) =>
        stem is "con" or "prn" or "aux" or "nul" ||
        stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
        (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal));

    /// <summary>Parse and validate untrusted JSON without allowing number/string coercion.</summary>
    public static GameSpec Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("The game must be a JSON object.", nameof(json));
        static string Text(JsonElement parent, string key) => parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var map = json.TryGetProperty("map", out var mapJson) && mapJson.ValueKind == JsonValueKind.Array
            ? mapJson.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.String ? row.GetString()! : "").ToArray() : [];
        if (!json.TryGetProperty("palette", out var paletteJson) || paletteJson.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A five-color palette is required.", nameof(json));
        var palette = new GamePalette(Text(paletteJson, "background"), Text(paletteJson, "ground"), Text(paletteJson, "wall"), Text(paletteJson, "player"), Text(paletteJson, "collectible"));
        return Validate(new GameSpec(Text(json, "title"), Text(json, "mode"), palette, map, Text(json, "objective")));
    }

    /// <summary>Returns a normalized, independently owned specification after validating reachability.</summary>
    public static GameSpec Validate(GameSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.Title) || spec.Title.Length > 40) throw new ArgumentException("Give the game a title between 1 and 40 characters.", nameof(spec));
        if (spec.Mode is not ("gb" or "gbc")) throw new ArgumentException("Choose a gb or gbc cartridge.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Objective) || spec.Objective.Length > 240) throw new ArgumentException("The objective must be between 1 and 240 characters.", nameof(spec));
        if (spec.Map is null || spec.Map.Length != 18 || spec.Map.Any(row => row is null || row.Length != 20 || row.Any(c => c is not ('#' or '.' or 'P' or 'C'))))
            throw new ArgumentException("The map must have 18 rows of exactly 20 characters, using # . P C only.", nameof(spec));
        var cells = string.Concat(spec.Map);
        if (cells.Count(c => c == 'P') != 1) throw new ArgumentException("The map needs exactly one player start (P).", nameof(spec));
        var collectibleCount = cells.Count(c => c == 'C');
        if (collectibleCount is < 1 or > 99) throw new ArgumentException("The map needs between 1 and 99 collectibles (C).", nameof(spec));
        var reachable = new HashSet<int> { cells.IndexOf('P', StringComparison.Ordinal) };
        var queue = new Queue<int>(reachable);
        while (queue.TryDequeue(out var index))
        {
            var x = index % 20;
            var y = index / 20;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx is < 0 or >= 20 || ny is < 0 or >= 18) continue;
                var next = ny * 20 + nx;
                if (cells[next] != '#' && reachable.Add(next)) queue.Enqueue(next);
            }
        }
        for (var index = 0; index < cells.Length; index++)
            if (cells[index] == 'C' && !reachable.Contains(index)) throw new ArgumentException("Every collectible must be reachable from the player start.", nameof(spec));
        if (spec.Palette is null) throw new ArgumentException("A five-color palette is required.", nameof(spec));
        static string Color(string value, string name)
        {
            if (value is null || !ColorPattern().IsMatch(value)) throw new ArgumentException($"Palette {name} must be a six-digit hex color, such as #b8c982.");
            return value.ToLowerInvariant();
        }
        var palette = new GamePalette(Color(spec.Palette.Background, "background"), Color(spec.Palette.Ground, "ground"), Color(spec.Palette.Wall, "wall"), Color(spec.Palette.Player, "player"), Color(spec.Palette.Collectible, "collectible"));
        return new GameSpec(spec.Title.Trim(), spec.Mode, palette, [.. spec.Map], spec.Objective.Trim());
    }

    public static GameSpec CreateStarterSpec()
    {
        var map = Enumerable.Range(0, 18).Select(y => Enumerable.Range(0, 20).Select(x => x is 0 or 19 || y is 0 or 17 ? '#' : '.').ToArray()).ToArray();
        foreach (var (x, y, width, height) in new[] { (3, 3, 3, 3), (10, 2, 2, 3), (14, 6, 3, 3), (5, 10, 3, 3), (11, 12, 3, 3), (2, 14, 2, 1) })
            for (var dy = 0; dy < height; dy++)
                for (var dx = 0; dx < width; dx++) map[y + dy][x + dx] = '#';
        map[8][9] = 'P';
        foreach (var (x, y) in new[] { (7, 3), (15, 3), (3, 8), (11, 7), (16, 12), (8, 15) }) map[y][x] = 'C';
        return Validate(new GameSpec("MOSS & MOON", "gbc", new GamePalette("#dce6ad", "#b8c982", "#507453", "#fff2ad", "#f4c75b"), map.Select(row => new string(row)).ToArray(),
            "Find the six moonstones hidden in the forest. Use the D-pad to explore; Start begins again."));
    }

    public static RomBuild Build(GameSpec input)
    {
        var spec = Validate(input);
        var program = RomProgram.Create(spec);
        var rom = new byte[32768];
        new byte[] { 0, 0xc3, 0x50, 1 }.CopyTo(rom, 0x100);
        RomTools.Logo.CopyTo(rom.AsSpan(0x104));
        var headerTitle = TitleCharacters().Replace(spec.Title.ToUpperInvariant(), "");
        if (headerTitle.Length > 15) headerTitle = headerTitle[..15];
        if (headerTitle.Length == 0) headerTitle = "BLAST GAME";
        Encoding.ASCII.GetBytes(headerTitle).CopyTo(rom, 0x134);
        rom[0x143] = spec.Mode == "gbc" ? (byte)0x80 : (byte)0;
        rom[0x14a] = 1;
        program.Bytes.CopyTo(rom, 0x150);
        rom = RomTools.RepairChecksums(rom);
        var header = string.Join('\n', Enumerable.Range(0, 5).Select(index => "    db " + string.Join(", ", rom.AsSpan(0x100 + index * 16, 16).ToArray().Select(value => $"${value:x2}"))));
        var stem = FilenameCharacters().Replace(spec.Title.ToLowerInvariant(), "-").Trim('-');
        if (stem.Length == 0) stem = "blast-game";
        // Windows reserves device names even with an extension. Normalize on
        // every host so exported filenames remain portable and deterministic.
        if (IsWindowsDeviceName(stem)) stem = "blast-" + stem;
        var filename = $"{stem}.{spec.Mode}";
        var source = $"; BLAST game engine {EngineVersion}\n; Generated LR35902 source; no external game assets.\n; D-pad: move. Start: restart. Collect every moonstone to win.\n; Rebuild from the exported project kit root with RGBDS:\n; rgbasm -o game.o source/main.asm\n; rgblink -o {filename} game.o\n; After editing, repair header/checksums: rgbfix -v {filename}\n; For a separately downloaded source, replace source/main.asm with its filename.\n; Hardware reference: https://gbdev.io/pandocs/\n\nSECTION \"Cartridge\", ROM0[$0000]\n    ds $100, $00\n{header}\n\n{program.Source}\n    ds $4000 - @, $00\nSECTION \"Second ROM bank\", ROMX[$4000], BANK[1]\n    ds $4000, $00\n";
        string[] warnings = spec.Mode == "gb" ? ["The original Game Boy displays four shades; custom colors are used in Game Boy Color mode."] : [];
        return new RomBuild(rom, source, warnings, spec.Title, filename);
    }
}
