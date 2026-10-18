using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Core;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private sealed record WorkspaceManifest
    {
        public string? Format { get; init; }
        public int Version { get; init; }
        public string? Name { get; init; }
        public string? Kind { get; init; }
        public string? Source { get; init; }
        public string? Game { get; init; }
        public string? Rom { get; init; }
        public string? Output { get; init; }
        public string? Patches { get; init; }
        public bool? FixChecksums { get; init; }
        public int? RomSize { get; init; }
        public IEnumerable<(string Role, string Value)> InputPaths()
        {
            if (Source is not null) yield return ("source", Source);
            if (Game is not null) yield return ("game", Game);
            if (Rom is not null) yield return ("rom", Rom);
            if (Patches is not null) yield return ("patches", Patches);
        }
    }

    private sealed record LoadedProject(string Root, string Path, WorkspaceManifest Manifest);
    private sealed record ProjectInput(string Role, string Path, long Size, string Sha256);

    private static LoadedProject LoadManifest(string path)
    {
        var root = Path.GetDirectoryName(path)!;
        FileOperations.RejectSymbolicLink(root);
        FileOperations.RejectSymbolicLink(path);
        var value = JsonFile(path);
        if (value.ValueKind != JsonValueKind.Object)
            throw new CliException("INVALID_PROJECT", "The workspace manifest must be a JSON object.");
        foreach (var property in value.EnumerateObject())
            if (property.Name is "source" or "game" or "rom" or "patches" or "fixChecksums" or "romSize" && property.Value.ValueKind == JsonValueKind.Null)
                throw new CliException("INVALID_PROJECT", $"{property.Name} cannot be null; omit optional fields instead.");
        WorkspaceManifest? manifest;
        try { manifest = value.Deserialize<WorkspaceManifest>(JsonDefaults.Options); }
        catch (JsonException exception) { throw new CliException("INVALID_PROJECT", "The workspace manifest has invalid field types: " + exception.Message, exception); }
        if (manifest is null || manifest.Format != ManifestFormat || manifest.Version != 1
            || manifest.Kind is not ("source" or "rom" or "game") || string.IsNullOrWhiteSpace(manifest.Name))
            throw new CliException("INVALID_PROJECT", "Expected a blast-gb-workspace version 1 manifest with name and kind source, rom, or game.");
        FileOperations.WorkspacePath(root, manifest.Output, "output");
        foreach (var (role, input) in manifest.InputPaths()) FileOperations.WorkspacePath(root, input, role);
        if (manifest.RomSize is < 0x150 or > 8 * 1024 * 1024)
            throw new CliException("INVALID_PROJECT", "romSize must be an integer between 336 bytes and 8 MiB.");
        if (manifest.Kind == "game" ? string.IsNullOrEmpty(manifest.Game) : string.IsNullOrEmpty(manifest.Source))
            throw new CliException("INVALID_PROJECT", manifest.Kind == "game"
                ? "A game workspace needs game, the GameSpec JSON path."
                : "A source or ROM workspace needs source, the editable RGBDS assembly path.");
        return new LoadedProject(root, path, manifest);
    }

    private static JsonObject InitializeProject(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("name", "kind");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parsed.Positionals[0]));
        var kind = parsed.Option("kind") ?? "source";
        if (kind is not ("source" or "game")) throw new CliException("USAGE", "--kind must be source or game.");
        var name = parsed.Option("name") ?? Path.GetFileName(target);
        if (string.IsNullOrWhiteSpace(name)) throw new CliException("USAGE", "Project name must contain non-whitespace characters.");
        var spec = GameCompiler.CreateStarterSpec();
        var initial = GameCompiler.Build(spec);
        var manifest = new WorkspaceManifest
        {
            Format = ManifestFormat,
            Version = 1,
            Name = name,
            Kind = kind,
            Source = kind == "source" ? "source/main.asm" : null,
            Game = kind == "game" ? "game.json" : null,
            Output = "build/game.gb",
            FixChecksums = kind == "source"
        };
        var files = new Dictionary<string, byte[]>
        {
            [ManifestFile] = TextBytes(JsonText(manifest)),
            ["game.json"] = TextBytes(JsonText(spec)),
            ["README.md"] = TextBytes($"# {name}\n\n{(kind == "source" ? "Edit source/main.asm, then build with RGBDS installed." : "Edit game.json, then build with the built-in compiler.")}\n\nBuild: gb build \"{target}\" --json\nInspect: gb project resolve \"{target}\" --json\nRepeat builds with --overwrite. The project manifest is gb-project.json.\n")
        };
        if (kind == "source") files["source/main.asm"] = TextBytes(initial.Source);
        FileOperations.WriteWorkspace(target, files);
        return ToObject(new { workspace = target, manifest = Path.Combine(target, ManifestFile), kind, next = "gb build " + Serialize(target) });
    }

    private static JsonObject ImportProject(ParsedCommand parsed)
    {
        parsed.RequirePositionals(2);
        parsed.AllowOptions("name");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parsed.Positionals[1]));
        var rom = RomFile(input);
        var inspection = RomTools.Inspect(rom);
        var name = parsed.Option("name") ?? (string.IsNullOrEmpty(inspection.Title) ? Path.GetFileNameWithoutExtension(input) : inspection.Title);
        if (string.IsNullOrWhiteSpace(name)) throw new CliException("USAGE", "Project name must contain non-whitespace characters.");
        var manifest = new WorkspaceManifest
        {
            Format = ManifestFormat,
            Version = 1,
            Name = name,
            Kind = "rom",
            Source = "source/main.asm",
            Rom = "original.gb",
            Output = "build/game.gb",
            Patches = "patches.json",
            FixChecksums = false,
            RomSize = rom.Length
        };
        FileOperations.WriteWorkspace(target, new Dictionary<string, byte[]>
        {
            [ManifestFile] = TextBytes(JsonText(manifest)),
            ["original.gb"] = rom,
            ["source/main.asm"] = TextBytes(RomTools.ExportDisassembly(rom)),
            ["patches.json"] = TextBytes("[]\n"),
            ["README.md"] = TextBytes("# ROM workspace\n\noriginal.gb is the unchanged imported cartridge. Edit source/main.asm and build with RGBDS.\nThe byte-data source preserves the original ROM exactly; original labels, comments, and code/data boundaries cannot be recovered.\npatches.json records guarded byte edits and is not reapplied by build.\nHeader/checksum repair is opt-in with build --fix-checksums.\n")
        });
        return ToObject(new { workspace = target, manifest = Path.Combine(target, ManifestFile), original = Path.Combine(target, "original.gb"), sha256 = Sha256(rom), inspection });
    }

    private static JsonObject ResolveProject(ParsedCommand parsed)
    {
        parsed.RequirePositionals(0, 1);
        parsed.AllowOptions();
        var input = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? ".");
        var path = Directory.Exists(input) ? Path.Combine(input, ManifestFile) : input;
        var loaded = LoadManifest(path);
        var manifest = loaded.Manifest;
        var root = loaded.Root;
        var inputs = new List<ProjectInput>();
        foreach (var (role, value) in manifest.InputPaths())
        {
            var file = FileOperations.WorkspacePath(root, value, role);
            var info = new FileInfo(file);
            if (!info.Exists) throw new CliException("INVALID_PROJECT", $"{role} must be a regular file.");
            inputs.Add(new ProjectInput(role, file, info.Length, Sha256(File.ReadAllBytes(file))));
        }
        var output = FileOperations.WorkspacePath(root, manifest.Output, "output");
        var outputs = new Dictionary<string, string> { ["rom"] = output };
        if (manifest.Kind != "game")
        {
            outputs["symbols"] = Path.ChangeExtension(output, ".sym");
            outputs["map"] = Path.ChangeExtension(output, ".map");
        }
        foreach (var artifact in outputs.Values) FileOperations.ProtectDestination(artifact, [path, .. inputs.Select(file => file.Path)]);
        return ToObject(new { root, path, manifest, inputs, outputs, engine = manifest.Kind == "game" ? "builtin-gamespec" : "rgbds", externalTools = manifest.Kind == "game" ? Array.Empty<string>() : ["rgbasm", "rgblink"] });
    }
}
