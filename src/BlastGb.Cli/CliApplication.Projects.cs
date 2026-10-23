using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        public string[]? Sources { get; init; }
        public string[]? IncludeDirectories { get; init; }
        public Dictionary<string, string>? Defines { get; init; }
        public string? Receipt { get; init; }
        public string? Tests { get; init; }
        public AssetDefinition[]? Assets { get; init; }
        public string? Game { get; init; }
        public string? Rom { get; init; }
        public string? Output { get; init; }
        public string? Patches { get; init; }
        public bool? FixChecksums { get; init; }
        public int? RomSize { get; init; }
        public IEnumerable<(string Role, string Value)> InputPaths()
        {
            if (Source is not null) yield return ("source", Source);
            if (Sources is not null)
                foreach (var source in Sources) yield return ("source", source);
            if (Game is not null) yield return ("game", Game);
            if (Rom is not null) yield return ("rom", Rom);
            if (Patches is not null) yield return ("patches", Patches);
            if (Tests is not null) yield return ("tests", Tests);
            if (Assets is not null)
                foreach (var asset in Assets)
                    if (asset is not null) yield return ("asset", asset.Input!);
        }

        public IEnumerable<string> AssemblySources() => Sources ?? (Source is null ? [] : new[] { Source });
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
            if (property.Name is "source" or "sources" or "includeDirectories" or "defines" or "receipt" or "tests" or "assets" or "game" or "rom" or "patches" or "fixChecksums" or "romSize" && property.Value.ValueKind == JsonValueKind.Null)
                throw new CliException("INVALID_PROJECT", $"{property.Name} cannot be null; omit optional fields instead.");
        if (value.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (asset.ValueKind == JsonValueKind.Object)
                    foreach (var property in asset.EnumerateObject())
                        if (property.Name is "input" or "output" or "tilemap" or "palette" or "attrmap" && property.Value.ValueKind == JsonValueKind.Null)
                            throw new CliException("INVALID_PROJECT", $"Asset {property.Name} cannot be null; omit optional fields instead.");
        WorkspaceManifest? manifest;
        try { manifest = value.Deserialize<WorkspaceManifest>(JsonDefaults.Options); }
        catch (JsonException exception) { throw new CliException("INVALID_PROJECT", "The workspace manifest has invalid field types: " + exception.Message, exception); }
        if (manifest is null || manifest.Format != ManifestFormat || manifest.Version != 1
            || manifest.Kind is not ("source" or "rom" or "game") || string.IsNullOrWhiteSpace(manifest.Name))
            throw new CliException("INVALID_PROJECT", "Expected a blast-gb-workspace version 1 manifest with name and kind source, rom, or game.");
        FileOperations.WorkspacePath(root, manifest.Output, "output");
        if (manifest.Receipt is not null) FileOperations.WorkspacePath(root, manifest.Receipt, "receipt");
        foreach (var (role, input) in manifest.InputPaths()) FileOperations.WorkspacePath(root, input, role);
        if (manifest.Source is not null && manifest.Sources is not null)
            throw new CliException("INVALID_PROJECT", "Use source or sources, never both.");
        if (manifest.Sources is { Length: 0 or > 256 })
            throw new CliException("INVALID_PROJECT", "sources must contain between 1 and 256 assembly paths in link order.");
        var sourcePaths = manifest.AssemblySources().Select(source => FileOperations.WorkspacePath(root, source, "source")).ToArray();
        if (sourcePaths.Select(FileOperations.CanonicalPath).Distinct(StringComparer.Ordinal).Count() != sourcePaths.Length)
            throw new CliException("INVALID_PROJECT", "sources must identify distinct assembly files.");
        if (manifest.IncludeDirectories is { Length: > 256 })
            throw new CliException("INVALID_PROJECT", "includeDirectories may contain at most 256 paths.");
        foreach (var include in manifest.IncludeDirectories ?? [])
        {
            var directory = FileOperations.WorkspacePath(root, include, "includeDirectories");
            if (!Directory.Exists(directory))
                throw new CliException("INVALID_PROJECT", $"Include directory {include} must be an existing directory inside the workspace.");
        }
        if (manifest.Defines is { Count: > 1024 })
            throw new CliException("INVALID_PROJECT", "defines may contain at most 1024 entries.");
        foreach (var (name, definition) in manifest.Defines ?? [])
            if (!DefineName().IsMatch(name) || definition is null || definition.Length > 4096 || definition.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new CliException("INVALID_PROJECT", "Each define needs an ASCII identifier and a single-line string value of at most 4096 characters.");
        if (manifest.Assets is { Length: > 256 } || manifest.Kind == "game" && manifest.Assets is { Length: > 0 })
            throw new CliException("INVALID_PROJECT", "assets may contain at most 256 conversions and require an assembly workspace.");
        if (manifest.Kind == "game" && (manifest.Sources is not null || manifest.IncludeDirectories is { Length: > 0 } || manifest.Defines is { Count: > 0 }))
            throw new CliException("INVALID_PROJECT", "sources, includeDirectories, and defines configure assembly workspaces, not the built-in GameSpec compiler.");
        foreach (var asset in manifest.Assets ?? [])
        {
            if (asset is null || asset.Output is null)
                throw new CliException("INVALID_PROJECT", "Each asset needs input and output paths.");
            FileOperations.WorkspacePath(root, asset.Input, "asset input");
            foreach (var (role, assetOutput, _) in asset.Outputs()) FileOperations.WorkspacePath(root, assetOutput, "asset " + role);
        }
        if (manifest.RomSize is < 0x150 or > 8 * 1024 * 1024)
            throw new CliException("INVALID_PROJECT", "romSize must be an integer between 336 bytes and 8 MiB.");
        if (manifest.Kind == "game" ? string.IsNullOrEmpty(manifest.Game) : sourcePaths.Length == 0)
            throw new CliException("INVALID_PROJECT", manifest.Kind == "game"
                ? "A game workspace needs game, the GameSpec JSON path."
                : "A source or ROM workspace needs source or sources, the editable RGBDS assembly paths.");
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
        if (manifest.Receipt is not null) outputs["receipt"] = FileOperations.WorkspacePath(root, manifest.Receipt, "receipt");
        for (var index = 0; index < (manifest.Assets?.Length ?? 0); index++)
            foreach (var (role, assetOutput, _) in manifest.Assets![index].Outputs())
                outputs[$"asset{index}.{role}"] = FileOperations.WorkspacePath(root, assetOutput, "asset " + role);
        foreach (var artifact in outputs.Values) FileOperations.ProtectDestination(artifact, [path, .. inputs.Select(file => file.Path)]);
        var externalTools = manifest.Kind == "game" ? Array.Empty<string>()
            : manifest.Assets is { Length: > 0 } ? ["rgbasm", "rgblink", "rgbgfx"] : new[] { "rgbasm", "rgblink" };
        return ToObject(new { root, path, manifest, inputs, outputs, engine = manifest.Kind == "game" ? "builtin-gamespec" : "rgbds", externalTools });
    }

    private static JsonObject CheckProjectFreshness(ParsedCommand parsed)
    {
        parsed.RequirePositionals(0, 1);
        parsed.AllowOptions("receipt");
        var input = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? ".");
        var path = Directory.Exists(input) ? Path.Combine(input, ManifestFile) : input;
        var loaded = LoadManifest(path);
        var receiptPath = parsed.Option("receipt") is { } selected ? Path.GetFullPath(selected)
            : loaded.Manifest.Receipt is { } relative ? FileOperations.WorkspacePath(loaded.Root, relative, "receipt")
            : throw new CliException("INVALID_PROJECT", "project status needs a manifest receipt path or --receipt FILE from a previous build.");
        if (!File.Exists(receiptPath))
            return ToObject(new { path, receipt = receiptPath, stale = true, valid = false, changes = new[] { new BuildReceiptChange("receipt", receiptPath, "missing", null, null) } });
        var receipt = BuildReceipt.Read(receiptPath);
        var changes = receipt.Check(path);
        return ToObject(new { path, receipt = receiptPath, engine = receipt.Engine, stale = changes.Count != 0, valid = changes.Count == 0, changes });
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DefineName();
}
