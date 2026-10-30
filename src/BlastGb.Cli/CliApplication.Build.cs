using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlastGb.Core;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private static async Task<JsonObject> BuildAsync(ParsedCommand parsed, CancellationToken cancellationToken, IReadOnlyList<string>? additionalProtectedInputs = null)
    {
        parsed.RequirePositionals(0, 1);
        parsed.AllowOptions("output", "overwrite", "fix-checksums", "provenance", "timeout-seconds");
        int timeoutSeconds = Integer(parsed.Option("timeout-seconds") ?? "120", "timeout-seconds", 600);
        if (timeoutSeconds == 0) throw new CliException("USAGE", "timeout-seconds must be between 1 and 600.");
        var input = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? ".");
        var projectPath = Directory.Exists(input) ? Path.Combine(input, ManifestFile) : input;
        var root = Path.GetDirectoryName(projectPath)!;
        string? source = null;
        string? game = null;
        WorkspaceManifest? manifest = null;
        string output;
        if (string.Equals(Path.GetExtension(projectPath), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            source = projectPath;
            output = Path.Combine(root, "build", Path.GetFileNameWithoutExtension(projectPath) + ".gb");
        }
        else
        {
            var value = JsonFile(projectPath);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == ManifestFormat)
            {
                var loaded = LoadManifest(projectPath);
                manifest = loaded.Manifest;
                root = loaded.Root;
                if (manifest.Kind == "game") game = FileOperations.WorkspacePath(root, manifest.Game, "game");
                else source = FileOperations.WorkspacePath(root, manifest.Source, "source");
                output = FileOperations.WorkspacePath(root, manifest.Output, "output");
            }
            else
            {
                game = projectPath;
                output = Path.Combine(root, "build", GameCompiler.Build(GameCompiler.Parse(value)).Filename);
            }
        }
        if (parsed.Option("output") is { } destination) output = Path.GetFullPath(destination);
        var protectedInputs = new List<string> { projectPath };
        if (additionalProtectedInputs is not null) protectedInputs.AddRange(additionalProtectedInputs);
        if (source is not null) protectedInputs.Add(source);
        if (game is not null) protectedInputs.Add(game);
        if (manifest?.Rom is not null) protectedInputs.Add(FileOperations.WorkspacePath(root, manifest.Rom, "rom"));
        if (manifest?.Patches is not null) protectedInputs.Add(FileOperations.WorkspacePath(root, manifest.Patches, "patches"));
        var overwrite = parsed.Has("overwrite");
        var symbolPath = Path.ChangeExtension(output, ".sym");
        var mapPath = Path.ChangeExtension(output, ".map");
        var outputs = source is null ? new[] { output } : [output, symbolPath, mapPath];
        foreach (var artifact in outputs)
        {
            FileOperations.ProtectDestination(artifact, protectedInputs);
            FileOperations.AssertWritable(artifact, overwrite);
        }
        byte[] rom;
        string? symbols = null;
        string? map = null;
        if (game is not null) rom = GameCompiler.Build(GameCompiler.Parse(JsonFile(game))).Rom;
        else
        {
            var staging = Path.Combine(Path.GetTempPath(), "blast-gb-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var objectPath = Path.Combine(staging, "game.o");
                var cartridge = Path.Combine(staging, "game.gb");
                var symbolFile = Path.Combine(staging, "game.sym");
                var mapFile = Path.Combine(staging, "game.map");
                var dependencyFile = Path.Combine(staging, "game.d");
                var dependencyTarget = AssemblyDependencies.CreateTarget();
                var exactSize = manifest?.RomSize;
                if (exactSize is null && manifest?.Rom is not null)
                {
                    var originalSize = new FileInfo(FileOperations.WorkspacePath(root, manifest.Rom, "rom")).Length;
                    if (originalSize < 32768 || originalSize % 16384 != 0)
                    {
                        if (originalSize is < 0x150 or > 8 * 1024 * 1024) throw new CliException("INVALID_ROM", "Original ROM size must be between 336 bytes and 8 MiB.");
                        exactSize = (int)originalSize;
                    }
                }
                await RunRgbdsAsync("rgbasm", ["-I", root + Path.DirectorySeparatorChar, "-I", Path.GetDirectoryName(source!) + Path.DirectorySeparatorChar, "-M", dependencyFile, "-MT", dependencyTarget, "-o", objectPath, source!], root, timeoutSeconds, cancellationToken);
                var dependencyInfo = new FileInfo(dependencyFile);
                if (!dependencyInfo.Exists || dependencyInfo.Length > 16 * 1024 * 1024)
                    throw new CliException("BUILD_FAILED", "Cannot confirm assembler inputs: missing or oversized dependency report. No output was replaced.");
                protectedInputs.AddRange(AssemblyDependencies.Parse(File.ReadAllBytes(dependencyFile), dependencyTarget, root, source!));
                foreach (var artifact in outputs) FileOperations.ProtectDestination(artifact, protectedInputs);
                await RunRgbdsAsync("rgblink", ["-n", symbolFile, "-m", mapFile, "-o", cartridge, objectPath], root, timeoutSeconds, cancellationToken);
                var cartridgeSize = new FileInfo(cartridge).Length;
                if (cartridgeSize is < 0x150 or > 8 * 1024 * 1024)
                    throw new CliException("BUILD_FAILED", "RGBDS output must be between 336 bytes and 8 MiB.");
                rom = File.ReadAllBytes(cartridge);
                symbols = File.ReadAllText(symbolFile, Utf8);
                map = File.ReadAllText(mapFile, Utf8);
                if (exactSize is { } size)
                {
                    if (rom.Length < size)
                        throw new CliException("BUILD_FAILED", $"RGBDS produced {rom.Length} bytes but the manifest requires romSize {size}. Update the assembly or romSize; no output was replaced.");
                    if (rom.Length > size)
                    {
                        ValidateRomExtent(map, size);
                        rom = rom[..size];
                    }
                }
                RomTools.Inspect(rom);
            }
            finally { Directory.Delete(staging, true); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (parsed.Has("fix-checksums") || manifest?.FixChecksums == true) rom = RomTools.RepairChecksums(rom);
        var artifacts = new List<FileOperations.Artifact> { new(output, rom) };
        if (symbols is not null) artifacts.Add(new FileOperations.Artifact(symbolPath, symbols));
        if (map is not null) artifacts.Add(new FileOperations.Artifact(mapPath, map));
        object? provenance = null;
        if (parsed.Has("provenance"))
        {
            var tools = new List<object>();
            if (source is not null)
                foreach (string tool in new[] { "rgbasm", "rgblink" }) tools.Add(await ToolIdentityAsync(tool, cancellationToken));
            provenance = new
            {
                toolkitVersion = AutomationContracts.Version,
                engine = game is not null ? (object)new { name = "builtin-gamespec", version = GameCompiler.EngineVersion } : new { name = "rgbds", tools },
                // These identify files observed after compilation, not an atomic source snapshot.
                observedInputs = protectedInputs.Distinct(StringComparer.Ordinal).Select(path => new { path, sha256 = Sha256(File.ReadAllBytes(path)) }).ToArray()
            };
        }
        cancellationToken.ThrowIfCancellationRequested();
        FileOperations.AtomicWriteBatch(artifacts, overwrite);
        var result = ToObject(new { output, size = rom.Length, sha256 = Sha256(rom), inspection = RomTools.Inspect(rom), engine = game is not null ? "builtin-gamespec" : "rgbds" });
        if (symbols is not null) { result["symbols"] = symbolPath; result["map"] = mapPath; }
        if (provenance is not null) result["provenance"] = JsonSerializer.SerializeToNode(provenance, JsonDefaults.Options);
        return result;
    }

    /// <summary>Only discard padding whose absence from every ROM section is proven by the map.</summary>
    internal static void ValidateRomExtent(string map, int exactSize)
    {
        int? bank = null;
        var sections = 0;
        var complete = true;
        foreach (var line in map.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            var heading = RomBankHeading().Match(line);
            if (heading.Success)
            {
                if (!int.TryParse(heading.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBank)) { complete = false; bank = null; }
                else bank = parsedBank;
                continue;
            }
            if (AnyBankHeading().IsMatch(line)) bank = null;
            if (bank is null || !SectionHeading().IsMatch(line)) continue;
            var section = RomSection().Match(line);
            if (!section.Success) { complete = false; continue; }
            var start = int.Parse(section.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var end = section.Groups[2].Success ? int.Parse(section.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : start;
            var length = int.Parse(section.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (length == 0) continue;
            sections++;
            if (end - start + 1 != length || start < (bank == 0 ? 0 : 0x4000) || end > (bank == 0 ? 0x3fff : 0x7fff)) { complete = false; continue; }
            var fileEnd = (long)bank * 0x4000 + end - (bank == 0 ? 0 : 0x4000) + 1;
            if (fileEnd > exactSize)
                throw new CliException("BUILD_FAILED", $"An assembled section ends at file offset {fileEnd}, beyond manifest romSize {exactSize}. Update the assembly or romSize; no output was replaced.");
        }
        if (!complete || sections == 0)
            throw new CliException("BUILD_FAILED", "Cannot confirm ROM section bounds from the RGBDS map. Refusing to trim potentially assembled content; update RGBDS or remove romSize deliberately.");
    }

    private static async Task RunRgbdsAsync(string tool, string[] arguments, string cwd, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var executable = ToolExecutable(tool);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new CliException("RGBDS_MISSING", $"Cannot find {tool}. Install RGBDS and put its tools on PATH, or set RGBDS_BIN to their directory. GameSpec JSON builds use the built-in compiler.", exception);
        }
        process.StandardInput.Close();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stdout = ReadBoundedOutputAsync(process.StandardOutput, linked);
        var stderr = ReadBoundedOutputAsync(process.StandardError, linked);
        try { await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr); }
        catch (Exception exception)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (exception is OperationCanceledException)
                throw new CliException("BUILD_TIMEOUT", $"{tool} exceeded its {timeoutSeconds}-second deadline. No output was replaced.", exception);
            throw new CliException("BUILD_FAILED", $"{tool} failed: {exception.Message}", exception);
        }
        if (process.ExitCode != 0)
            throw new CliException("BUILD_FAILED", $"{tool} failed:\n{(string.IsNullOrWhiteSpace(stderr.Result) ? stdout.Result : stderr.Result).Trim()}");
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, CancellationTokenSource cancellation)
    {
        try
        {
            var builder = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, cancellation.Token)) != 0)
            {
                if (builder.Length + count > 4 * 1024 * 1024) throw new IOException("RGBDS output exceeds 4 MiB.");
                builder.Append(buffer, 0, count);
            }
            return builder.ToString();
        }
        catch { cancellation.Cancel(); throw; }
    }

    [GeneratedRegex("^(ROM[0X]) bank #(\\d+):\\s*$", RegexOptions.CultureInvariant)] private static partial Regex RomBankHeading();
    [GeneratedRegex("^[A-Z][A-Z0-9]* bank #", RegexOptions.CultureInvariant)] private static partial Regex AnyBankHeading();
    [GeneratedRegex("^\\s+SECTION:", RegexOptions.CultureInvariant)] private static partial Regex SectionHeading();
    [GeneratedRegex("^\\s+SECTION: \\$([0-9a-f]+)(?:-\\$([0-9a-f]+))? \\(\\$([0-9a-f]+) bytes?\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex RomSection();
}
