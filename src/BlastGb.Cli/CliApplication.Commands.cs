using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Core;
using BlastGb.Runtime;
using BlastGb.Serialization;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private static async Task<JsonObject> ExecuteAsync(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        switch (parsed.Command)
        {
            case "init": return InitializeProject(parsed);
            case "import": return ImportProject(parsed);
            case "resolve": return ResolveProject(parsed);
            case "project-status": return CheckProjectFreshness(parsed);
            case "project-test": return await TestProjectAsync(parsed, cancellationToken);
            case "game-validate": return ValidateGame(parsed);
            case "game-export": return ExportGameSource(parsed);
            case "patch-create": return CreatePatch(parsed);
            case "symbols": return InspectSymbols(parsed);
            case "asset-convert": return await ConvertAssetAsync(parsed, cancellationToken);
            case "asset-encode":
            case "asset-decode": return ConvertTileBytes(parsed);
            case "build":
                if (parsed.Invocation == "asm compile")
                {
                    parsed.RequirePositionals(1);
                    if (!string.Equals(Path.GetExtension(parsed.Positionals[0]), ".asm", StringComparison.OrdinalIgnoreCase))
                        throw new CliException("USAGE", "asm compile expects an RGBDS .asm source file. Use build for project manifests.");
                }
                return await BuildAsync(parsed, cancellationToken);
            case "inspect":
            case "verify": return InspectCartridge(parsed);
            case "disassemble": return DisassembleCartridge(parsed);
            case "diff": return DiffCartridges(parsed);
            case "patch": return PatchCartridge(parsed);
            case "debug": return DebugCartridge(parsed, cancellationToken);
            case "run":
            case "test": return RunCartridge(parsed, cancellationToken);
            case "capabilities":
                parsed.RequirePositionals(0);
                parsed.AllowOptions();
                return ToObject(AutomationContracts.GetCapabilities());
            case "schema":
                parsed.RequirePositionals(0, 1);
                parsed.AllowOptions();
                if (parsed.Positionals.Count == 0) return ToObject(new { schemas = AutomationContracts.SchemaNames });
                var name = parsed.Positionals[0];
                var schema = AutomationContracts.GetSchema(name)
                    ?? throw new CliException("USAGE", $"Unknown schema {name}. Available schemas: {string.Join(", ", AutomationContracts.SchemaNames)}.");
                return ToObject(new { name, schema });
            case "version":
                parsed.RequirePositionals(0);
                parsed.AllowOptions("version");
                return ToObject(new { name = "blast-gb-tools", version = AutomationContracts.Version });
            default: throw new CliException("USAGE", $"Unknown command {parsed.Command}. Use --help.");
        }
    }

    private static JsonObject InspectCartridge(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions();
        var path = Path.GetFullPath(parsed.Positionals[0]);
        var rom = RomFile(path);
        var inspection = RomTools.Inspect(rom);
        var data = Merge(inspection, new { path, sha256 = Sha256(rom) });
        if (parsed.Command == "verify")
            data["valid"] = inspection.LogoValid && inspection.HeaderChecksumValid && inspection.GlobalChecksumValid && inspection.DeclaredRomBanks is { } banks && banks * 0x4000 == inspection.Size;
        return data;
    }

    private static JsonObject DisassembleCartridge(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("output", "overwrite", "offset", "count", "symbols", "symbol");
        var path = Path.GetFullPath(parsed.Positionals[0]);
        var rom = RomFile(path);
        var symbolPath = parsed.Option("symbols") is { } sym ? Path.GetFullPath(sym) : null;
        var symbols = symbolPath is null ? null : LoadSymbols(symbolPath);
        var offset = Integer(parsed.Option("offset") ?? "0x100", "offset", rom.Length - 1);
        if (parsed.Option("symbol") is { } name)
        {
            if (symbols is null || parsed.Has("offset")) throw new CliException("USAGE", "--symbol requires --symbols and cannot be combined with --offset.");
            offset = FindSymbol(symbols, name).RomOffset ?? throw new CliException("INVALID_SYMBOL", "The selected symbol does not identify a physical ROM offset.");
            if (offset >= rom.Length) throw new CliException("INVALID_SYMBOL", "The selected symbol lies beyond this ROM.");
        }
        var count = Integer(parsed.Option("count") ?? "64", "count", 1_000_000);
        if (count == 0) throw new CliException("USAGE", "count must be at least 1.");
        var lines = RomTools.Disassemble(rom, offset, count);
        if (parsed.Option("output") is { } destination)
        {
            var output = Path.GetFullPath(destination);
            FileOperations.ProtectDestination(output, symbolPath is null ? [path] : [path, symbolPath]);
            var exact = !parsed.Has("offset") && !parsed.Has("count") && !parsed.Has("symbol");
            var text = exact ? RomTools.ExportDisassembly(rom) : FormatInstructionListing(JsonSerializer.SerializeToNode(lines, JsonDefaults.Options)!.AsArray()) + "\n";
            FileOperations.AtomicWrite(output, text, parsed.Has("overwrite"));
            return exact
                ? ToObject(new { output, format = "rgbds-byte-exact", size = rom.Length })
                : ToObject(new { output, format = "instruction-listing", lines });
        }
        var result = ToObject(new { path, offset, count = lines.Count, lines });
        if (symbols is not null)
        {
            var offsets = lines.Select(line => line.Offset).ToHashSet();
            result["symbols"] = System.Text.Json.JsonSerializer.SerializeToNode(symbols.Where(symbol => symbol.RomOffset is { } at && offsets.Contains(at)), JsonDefaults.Options);
        }
        return result;
    }

    private static JsonObject PatchCartridge(ParsedCommand parsed)
    {
        parsed.RequirePositionals(2);
        parsed.AllowOptions("output", "overwrite", "fix-checksums", "allow-unguarded");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var patchPath = Path.GetFullPath(parsed.Positionals[1]);
        var output = Path.GetFullPath(parsed.Option("output")
            ?? throw new CliException("USAGE", "patch requires --output NEW_ROM to preserve the input cartridge."));
        FileOperations.ProtectDestination(output, [input, patchPath]);
        FileOperations.AssertWritable(output, parsed.Has("overwrite"));
        var source = JsonFile(patchPath);
        if (source.ValueKind != JsonValueKind.Array)
            throw new CliException("INVALID_PATCH", "The patch file must be an array of {offset, bytes, expected} records.");
        if (source.GetArrayLength() > 65536)
            throw new CliException("INVALID_PATCH", "The patch file may contain at most 65536 records.");
        var patches = new List<RomPatch>();
        foreach (var record in source.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
                throw new CliException("INVALID_PATCH", "Each patch needs an integer offset and byte array.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in record.EnumerateObject())
            {
                if (property.Name is not ("offset" or "bytes" or "expected"))
                    throw new CliException("INVALID_PATCH", $"Unknown patch property: {property.Name}.");
                if (!names.Add(property.Name))
                    throw new CliException("INVALID_PATCH", $"Duplicate patch property: {property.Name}.");
            }
            if (!record.TryGetProperty("offset", out var offsetElement) || !JsonInteger.TryGetInt32(offsetElement, out var offset)
                || !record.TryGetProperty("bytes", out var bytesElement))
                throw new CliException("INVALID_PATCH", "Each patch needs an integer offset and byte array.");
            var bytes = ParsePatchBytes(bytesElement, "bytes");
            byte[]? expected = record.TryGetProperty("expected", out var expectedElement) ? ParsePatchBytes(expectedElement, "expected") : null;
            if (!parsed.Has("allow-unguarded") && expected is null)
                throw new CliException("INVALID_PATCH", "Each patch needs expected bytes to guard against the wrong ROM revision. Use --allow-unguarded only when intentionally omitting these guards.");
            patches.Add(new RomPatch(offset, bytes, expected));
        }
        var original = RomFile(input);
        var rom = RomTools.Patch(original, patches, parsed.Has("fix-checksums"));
        FileOperations.AtomicWrite(output, rom, parsed.Has("overwrite"));
        return ToObject(new { input, output, patches = patches.Count, originalSha256 = Sha256(original), sha256 = Sha256(rom), inspection = RomTools.Inspect(rom) });
    }

    private static byte[] ParsePatchBytes(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new CliException("INVALID_PATCH", $"Patch {label} must be a byte array.");
        if (element.GetArrayLength() is < 1 or > 65536)
            throw new CliException("INVALID_PATCH", $"Patch {label} must contain between 1 and 65536 bytes.");
        var bytes = new byte[element.GetArrayLength()];
        var index = 0;
        foreach (var value in element.EnumerateArray())
        {
            if (!JsonInteger.TryGetInt32(value, out var item) || item is < 0 or > 255) throw new CliException("INVALID_PATCH", $"Patch {label} must contain only integers between 0 and 255.");
            bytes[index++] = (byte)item;
        }
        return bytes;
    }

    private sealed record DifferenceRange(int Offset, int Length, byte[] Before, byte[] After, bool PreviewTruncated);

    private static JsonObject DiffCartridges(ParsedCommand parsed)
    {
        parsed.RequirePositionals(2);
        parsed.AllowOptions();
        var beforePath = Path.GetFullPath(parsed.Positionals[0]);
        var afterPath = Path.GetFullPath(parsed.Positionals[1]);
        var before = RomFile(beforePath);
        var after = RomFile(afterPath);
        var ranges = new List<DifferenceRange>();
        var changedBytes = 0;
        var rangeCount = 0;
        var offset = 0;
        var maximum = Math.Max(before.Length, after.Length);
        bool Equal(int at) => at < before.Length && at < after.Length && before[at] == after[at];
        while (offset < maximum)
        {
            if (Equal(offset)) { offset++; continue; }
            var start = offset;
            while (offset < maximum && !Equal(offset)) offset++;
            var length = offset - start;
            changedBytes += length;
            rangeCount++;
            if (ranges.Count < 64)
                ranges.Add(new DifferenceRange(start, length,
                    before.AsSpan(Math.Min(start, before.Length), Math.Max(0, Math.Min(Math.Min(32, length), before.Length - start))).ToArray(),
                    after.AsSpan(Math.Min(start, after.Length), Math.Max(0, Math.Min(Math.Min(32, length), after.Length - start))).ToArray(), length > 32));
        }
        return ToObject(new
        {
            before = new { path = beforePath, size = before.Length, sha256 = Sha256(before) },
            after = new { path = afterPath, size = after.Length, sha256 = Sha256(after) },
            identical = changedBytes == 0,
            changedBytes,
            addedBytes = Math.Max(0, after.Length - before.Length),
            removedBytes = Math.Max(0, before.Length - after.Length),
            rangeCount,
            ranges,
            rangesTruncated = rangeCount > ranges.Count,
            previewLimits = new { ranges = 64, bytesPerRange = 32 }
        });
    }

    private static JsonObject DebugCartridge(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("script", "symbols", "screenshot", "capture-dir", "overwrite");
        var script = parsed.Option("script") ?? throw new CliException("USAGE", "debug requires --script FILE.json. Use gb schema debug --json for its contract.");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var scriptPath = Path.GetFullPath(script);
        var inputs = new List<string> { input, scriptPath };
        var symbolFile = parsed.Option("symbols");
        var symbols = symbolFile is not null ? LoadSymbols(Path.GetFullPath(symbolFile)) : null;
        if (symbolFile is not null) inputs.Add(Path.GetFullPath(symbolFile));
        var scriptJson = ResolveRuntimeSymbols(JsonFile(scriptPath), symbols);
        var validated = RuntimeRunner.ValidateDebugScript(scriptJson, cancellationToken);
        PreflightCaptures(parsed.Option("screenshot"), parsed.Option("capture-dir"), DebugCaptureNames(validated), inputs, parsed.Has("overwrite"));
        var rom = RomFile(input);
        var execution = RuntimeRunner.Debug(rom, scriptJson, cancellationToken);
        var result = Merge(execution, new { input, sha256 = Sha256(rom) });
        var artifacts = CaptureArtifacts(execution.FramebufferRgba, execution.Captures, parsed.Option("screenshot"), parsed.Option("capture-dir"), inputs, parsed.Has("overwrite"), result);
        cancellationToken.ThrowIfCancellationRequested();
        FileOperations.AtomicWriteBatch(artifacts, parsed.Has("overwrite"));
        return result;
    }

    private static JsonObject RunCartridge(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("frames", "memory", "assert", "buttons", "breakpoint", "max-instructions", "symbols", "screenshot", "overwrite");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var symbolPath = parsed.Option("symbols") is { } symbolFile ? Path.GetFullPath(symbolFile) : null;
        var symbols = symbolPath is null ? null : LoadSymbols(symbolPath);
        var frames = Integer(parsed.Option("frames") ?? "60", "frames", 3600);
        if (frames == 0) throw new CliException("USAGE", "frames must be at least 1.");
        var ranges = new List<MemoryRange>();
        foreach (var part in (parsed.Option("memory") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split(':');
            if (fields.Length > 2) throw new CliException("USAGE", "Memory ranges use address:length, for example --memory 0xc000:16,0xff40:1.");
            var address = ResolveCpuAddress(fields[0], symbols);
            var length = Integer(fields.Length == 1 || fields[1].Length == 0 ? "1" : fields[1], "memory length", 0x10000 - address);
            if (length == 0) throw new CliException("USAGE", "Memory range length must be positive.");
            ranges.Add(new MemoryRange(address, length));
        }
        if (ranges.Sum(range => (long)range.Length) > 65536) throw new CliException("USAGE", "Requested memory ranges may total at most 65536 bytes.");
        var assertionPath = parsed.Option("assert");
        if (parsed.Command == "test" && assertionPath is null)
            throw new CliException("USAGE", "test requires --assert FILE.json. Use run for execution without assertions.");
        var maxInstructions = Integer(parsed.Option("max-instructions") ?? "1000000", "max-instructions", 10_000_000);
        if (maxInstructions == 0) throw new CliException("USAGE", "max-instructions must be at least 1.");
        var rom = RomFile(input);
        var inputs = new List<string> { input };
        if (assertionPath is not null) inputs.Add(Path.GetFullPath(assertionPath));
        if (symbolPath is not null) inputs.Add(symbolPath);
        PreflightCaptures(parsed.Option("screenshot"), null, [], inputs, parsed.Has("overwrite"));
        var result = RuntimeRunner.Run(rom, new RunOptions
        {
            Frames = frames,
            Memory = ranges,
            Assertions = assertionPath is null ? null : ResolveRuntimeSymbols(JsonFile(Path.GetFullPath(assertionPath)), symbols),
            Buttons = parsed.Option("buttons")?.Split(',') ?? [],
            Breakpoint = parsed.Option("breakpoint") is { } breakpoint ? ResolveCpuAddress(breakpoint, symbols) : null,
            MaxInstructions = maxInstructions
        }, cancellationToken);
        var data = Merge(result, new { input, sha256 = Sha256(rom) });
        var artifacts = CaptureArtifacts(result.FramebufferRgba, [], parsed.Option("screenshot"), null, inputs, parsed.Has("overwrite"), data);
        cancellationToken.ThrowIfCancellationRequested();
        FileOperations.AtomicWriteBatch(artifacts, parsed.Has("overwrite"));
        return data;
    }
}
