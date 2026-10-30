using System.Text.Json;

namespace BlastGb.Core;

public static partial class GameCompiler
{
    /// <summary>Report independent specification problems without compiling or stopping at the first error.</summary>
    public static IReadOnlyList<GameDiagnostic> Lint(GameSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var diagnostics = new List<GameDiagnostic>();
        if (string.IsNullOrWhiteSpace(spec.Title) || spec.Title.Length > 40)
            diagnostics.Add(new("/title", "Give the game a title between 1 and 40 characters."));
        if (spec.Mode is not ("gb" or "gbc"))
            diagnostics.Add(new("/mode", "Choose a gb or gbc cartridge."));
        if (string.IsNullOrWhiteSpace(spec.Objective) || spec.Objective.Length > 240)
            diagnostics.Add(new("/objective", "The objective must be between 1 and 240 characters."));
        if (spec.Palette is null) diagnostics.Add(new("/palette", "A five-color palette is required."));
        else
        {
            foreach (var (name, value) in new[]
            {
                ("background", spec.Palette.Background), ("ground", spec.Palette.Ground),
                ("wall", spec.Palette.Wall), ("player", spec.Palette.Player), ("collectible", spec.Palette.Collectible)
            })
                if (value is null || !ColorPattern().IsMatch(value))
                    diagnostics.Add(new($"/palette/{name}", $"Palette {name} must be a six-digit hex color, such as #b8c982."));
        }
        if (spec.Map is null)
        {
            diagnostics.Add(new("/map", "The map must have 18 rows of exactly 20 characters, using # . P C only."));
            return diagnostics.AsReadOnly();
        }
        var validShape = spec.Map.Length == 18;
        var validCells = true;
        if (!validShape) diagnostics.Add(new("/map", "The map must have exactly 18 rows."));
        for (var row = 0; row < Math.Min(spec.Map.Length, 18); row++)
        {
            var line = spec.Map[row];
            if (line is null || line.Length != 20)
            {
                validShape = false;
                diagnostics.Add(new($"/map/{row}", "Each map row must be a string of exactly 20 characters.", row));
            }
            if (line is null) continue;
            for (var column = 0; column < Math.Min(line.Length, 20); column++)
                if (line[column] is not ('#' or '.' or 'P' or 'C'))
                {
                    validCells = false;
                    diagnostics.Add(new($"/map/{row}", "Map cells must be # . P or C.", row, column));
                }
        }
        if (!validShape) return diagnostics.AsReadOnly();
        var cells = string.Concat(spec.Map);
        var players = cells.Count(cell => cell == 'P');
        if (players != 1) diagnostics.Add(new("/map", "The map needs exactly one player start (P)."));
        var collectibles = cells.Count(cell => cell == 'C');
        if (collectibles is < 1 or > 99) diagnostics.Add(new("/map", "The map needs between 1 and 99 collectibles (C)."));
        if (!validCells || players != 1) return diagnostics.AsReadOnly();
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
            if (cells[index] == 'C' && !reachable.Contains(index))
                diagnostics.Add(new($"/map/{index / 20}", "This collectible is unreachable from the player start.", index / 20, index % 20));
        return diagnostics.AsReadOnly();
    }

    /// <summary>Lint untrusted JSON using strict types, with JSON Pointer paths and zero-based map coordinates.</summary>
    public static IReadOnlyList<GameDiagnostic> Lint(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return [new("", "The game must be a JSON object.")];
        var typeDiagnostics = new List<GameDiagnostic>();
        string ReadString(JsonElement parent, string name, string path)
        {
            if (parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString()!;
            typeDiagnostics.Add(new(path, "This field must be a string."));
            return "";
        }
        var title = ReadString(json, "title", "/title");
        var mode = ReadString(json, "mode", "/mode");
        var objective = ReadString(json, "objective", "/objective");
        GamePalette? palette = null;
        if (json.TryGetProperty("palette", out var paletteJson) && paletteJson.ValueKind == JsonValueKind.Object)
            palette = new GamePalette(
                ReadString(paletteJson, "background", "/palette/background"), ReadString(paletteJson, "ground", "/palette/ground"),
                ReadString(paletteJson, "wall", "/palette/wall"), ReadString(paletteJson, "player", "/palette/player"),
                ReadString(paletteJson, "collectible", "/palette/collectible"));
        string[]? map = null;
        if (json.TryGetProperty("map", out var mapJson) && mapJson.ValueKind == JsonValueKind.Array)
        {
            // One extra row proves an invalid size without materializing an unbounded JSON array.
            var inspectedRows = Math.Min(mapJson.GetArrayLength(), 19);
            var rows = new List<string>();
            foreach (var row in mapJson.EnumerateArray())
            {
                if (rows.Count == inspectedRows) break;
                if (row.ValueKind == JsonValueKind.String) rows.Add(row.GetString()!);
                else
                {
                    if (rows.Count < 18) typeDiagnostics.Add(new($"/map/{rows.Count}", "Each map row must be a string.", rows.Count));
                    rows.Add("");
                }
            }
            map = rows.ToArray();
        }
        var replacedPaths = typeDiagnostics.Select(diagnostic => diagnostic.Path).ToHashSet(StringComparer.Ordinal);
        return Lint(new GameSpec(title, mode, palette!, map!, objective))
            .Where(diagnostic => !replacedPaths.Contains(diagnostic.Path)).Concat(typeDiagnostics).ToArray();
    }
}
