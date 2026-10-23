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
    private static async Task<JsonObject> BuildAsync(ParsedCommand parsed, CancellationToken cancellationToken,
        IReadOnlyList<string>? additionalProtectedInputs = null, Action<IReadOnlyList<string>>? validateAdditionalOutputs = null)
    {
        parsed.RequirePositionals(0, 1);
        parsed.AllowOptions("output", "overwrite", "fix-checksums", "receipt");
        var input = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? ".");
        var projectPath = Directory.Exists(input) ? Path.Combine(input, ManifestFile) : input;
        var initialProject = BuildReceipt.HashFile(projectPath);
        var root = Path.GetDirectoryName(projectPath)!;
        var sources = new List<string>();
        string? game = null;
        WorkspaceManifest? manifest = null;
        string output;
        if (string.Equals(Path.GetExtension(projectPath), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            sources.Add(projectPath);
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
                else sources.AddRange(manifest.AssemblySources().Select(source => FileOperations.WorkspacePath(root, source, "source")));
                output = FileOperations.WorkspacePath(root, manifest.Output, "output");
            }
            else
            {
                game = projectPath;
                output = Path.Combine(root, "build", GameCompiler.Build(GameCompiler.Parse(value)).Filename);
            }
        }
        if (parsed.Option("output") is { } destination) output = Path.GetFullPath(destination);
        var receiptPath = parsed.Option("receipt") is { } selectedReceipt ? Path.GetFullPath(selectedReceipt)
            : manifest?.Receipt is { } relativeReceipt ? FileOperations.WorkspacePath(root, relativeReceipt, "receipt") : null;
        var includeDirectories = new[] { root }.Concat(sources.Select(source => Path.GetDirectoryName(source)!))
            .Concat((manifest?.IncludeDirectories ?? []).Select(include => FileOperations.WorkspacePath(root, include, "includeDirectories")))
            .DistinctBy(FileOperations.CanonicalPath, StringComparer.Ordinal).ToArray();
        var defines = (manifest?.Defines ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var protectedInputs = new List<string> { projectPath };
        protectedInputs.AddRange(sources);
        if (manifest is not null)
            protectedInputs.AddRange(manifest.InputPaths().Select(inputPath => FileOperations.WorkspacePath(root, inputPath.Value, inputPath.Role)));
        if (additionalProtectedInputs is not null) protectedInputs.AddRange(additionalProtectedInputs);
        var consumedInputs = new List<string> { projectPath };
        consumedInputs.AddRange(sources);
        if (game is not null) { protectedInputs.Add(game); consumedInputs.Add(game); }
        if (manifest?.Rom is not null) protectedInputs.Add(FileOperations.WorkspacePath(root, manifest.Rom, "rom"));
        if (manifest?.Patches is not null) protectedInputs.Add(FileOperations.WorkspacePath(root, manifest.Patches, "patches"));
        if (manifest?.Tests is not null) protectedInputs.Add(FileOperations.WorkspacePath(root, manifest.Tests, "tests"));
        foreach (var asset in manifest?.Assets ?? [])
        {
            var assetInput = FileOperations.WorkspacePath(root, asset.Input, "asset input");
            protectedInputs.Add(assetInput);
            consumedInputs.Add(assetInput);
        }
        var overwrite = parsed.Has("overwrite");
        var fixChecksums = parsed.Has("fix-checksums") || manifest?.FixChecksums == true;
        var symbolPath = Path.ChangeExtension(output, ".sym");
        var mapPath = Path.ChangeExtension(output, ".map");
        var outputs = game is not null ? new List<string> { output } : [output, symbolPath, mapPath];
        if (receiptPath is not null) outputs.Add(receiptPath);
        foreach (var asset in manifest?.Assets ?? [])
            outputs.AddRange(asset.Outputs().Select(item => FileOperations.WorkspacePath(root, item.Path, "asset " + item.Role)));
        foreach (var artifact in outputs)
        {
            FileOperations.ProtectDestination(artifact, protectedInputs);
            FileOperations.AssertWritable(artifact, overwrite);
        }
        byte[] rom;
        string? symbols = null;
        string? map = null;
        var tools = new List<BuildTool>();
        var generatedArtifacts = new List<FileOperations.Artifact>();
        var generatedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<BuildFileDigest> generatedInputSnapshot = [];
        IReadOnlyList<BuildFileDigest>? inputSnapshot = null;
        var exactSize = manifest?.RomSize;
        // Asset compilation may supply an overlay working directory before assembly.
        var assemblyRoot = root;
        if (game is not null)
        {
            if (receiptPath is not null) inputSnapshot = BuildReceipt.CaptureInputs(consumedInputs);
            rom = GameCompiler.Build(GameCompiler.Parse(JsonFile(game))).Rom;
        }
        else
        {
            var staging = Path.Combine(Path.GetTempPath(), "blast-gb-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var objectPaths = sources.Select((_, index) => Path.Combine(staging, "game-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".o")).ToArray();
                var cartridge = Path.Combine(staging, "game.gb");
                var symbolFile = Path.Combine(staging, "game.sym");
                var mapFile = Path.Combine(staging, "game.map");
                if (exactSize is null && manifest?.Rom is not null)
                {
                    var original = FileOperations.WorkspacePath(root, manifest.Rom, "rom");
                    consumedInputs.Add(original);
                    var originalSize = new FileInfo(original).Length;
                    if (originalSize < 32768 || originalSize % 16384 != 0)
                    {
                        if (originalSize is < 0x150 or > 8 * 1024 * 1024) throw new CliException("INVALID_ROM", "Original ROM size must be between 336 bytes and 8 MiB.");
                        exactSize = (int)originalSize;
                    }
                }
                var declaredSnapshot = receiptPath is null ? null : BuildReceipt.CaptureInputs(consumedInputs);
                if (manifest?.Assets is { Length: > 0 })
                {
                    assemblyRoot = Path.Combine(staging, "assets");
                    Directory.CreateDirectory(assemblyRoot);
                    if (receiptPath is not null) tools.Add(await GetRgbdsToolAsync("rgbgfx", root, cancellationToken));
                    foreach (var asset in manifest.Assets)
                    {
                        var converted = await GenerateAssetAsync(asset, root, assemblyRoot, cancellationToken, tools.FirstOrDefault(tool => tool.Name == "rgbgfx")?.Path);
                        generatedArtifacts.AddRange(converted);
                        foreach (var artifact in converted)
                        {
                            var relative = Path.GetRelativePath(root, artifact.Path);
                            generatedPaths[FileOperations.CanonicalPath(Path.Combine(assemblyRoot, relative))] = artifact.Path;
                        }
                    }
                }
                if (receiptPath is not null)
                {
                    tools.Add(await GetRgbdsToolAsync("rgbasm", assemblyRoot, cancellationToken));
                    tools.Add(await GetRgbdsToolAsync("rgblink", assemblyRoot, cancellationToken));
                }
                async Task<IReadOnlyList<string>> AssembleAsync()
                {
                    var dependencies = new List<string>();
                    for (var index = 0; index < sources.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var source = sources[index];
                        var dependencyFile = Path.ChangeExtension(objectPaths[index], ".d");
                        var dependencyTarget = AssemblyDependencies.CreateTarget();
                        var arguments = new List<string>();
                        if (assemblyRoot != root) arguments.AddRange(["-I", assemblyRoot + Path.DirectorySeparatorChar]);
                        foreach (var include in includeDirectories)
                            arguments.AddRange(["-I", Path.TrimEndingDirectorySeparator(include) + Path.DirectorySeparatorChar]);
                        foreach (var (name, value) in defines) arguments.AddRange(["-D", name + "=" + value]);
                        arguments.AddRange(["-M", dependencyFile, "-MT", dependencyTarget, "-o", objectPaths[index], source]);
                        await RunRgbdsAsync("rgbasm", [.. arguments], assemblyRoot, cancellationToken, tools.FirstOrDefault(tool => tool.Name == "rgbasm")?.Path);
                        var dependencyInfo = new FileInfo(dependencyFile);
                        if (!dependencyInfo.Exists || dependencyInfo.Length > 16 * 1024 * 1024)
                            throw new CliException("BUILD_FAILED", "Cannot confirm assembler inputs: missing or oversized dependency report. No output was replaced.");
                        dependencies.AddRange(AssemblyDependencies.Parse(File.ReadAllBytes(dependencyFile), dependencyTarget, assemblyRoot, source));
                    }
                    foreach (var artifact in outputs) FileOperations.ProtectDestination(artifact, dependencies);
                    return dependencies;
                }
                var dependencies = await AssembleAsync();
                consumedInputs.AddRange(dependencies);
                protectedInputs.AddRange(dependencies);
                if (receiptPath is not null)
                {
                    BuildReceipt.AssertUnchanged(declaredSnapshot!);
                    inputSnapshot = BuildReceipt.CaptureInputs(consumedInputs);
                    // Discover dependencies first, then assemble against their captured hashes.
                    // A changing include graph or input must not produce a misleading receipt.
                    var confirmedDependencies = await AssembleAsync();
                    var expected = dependencies.Select(FileOperations.CanonicalPath).ToHashSet(StringComparer.Ordinal);
                    if (!expected.SetEquals(confirmedDependencies.Select(FileOperations.CanonicalPath)))
                        throw new CliException("BUILD_FAILED", "Assembler inputs changed between dependency discovery and compilation. No output was replaced.");
                    BuildReceipt.AssertUnchanged(inputSnapshot);
                }
                await RunRgbdsAsync("rgblink", ["-n", symbolFile, "-m", mapFile, "-o", cartridge, .. objectPaths], assemblyRoot, cancellationToken, tools.FirstOrDefault(tool => tool.Name == "rgblink")?.Path);
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
                if (inputSnapshot is not null)
                {
                    BuildReceipt.AssertUnchanged(inputSnapshot);
                    generatedInputSnapshot = inputSnapshot.Where(file => generatedPaths.ContainsKey(FileOperations.CanonicalPath(file.Path)))
                        .Select(file => file with { Path = generatedPaths[FileOperations.CanonicalPath(file.Path)] }).ToArray();
                    inputSnapshot = inputSnapshot.Where(file => !generatedPaths.ContainsKey(FileOperations.CanonicalPath(file.Path))).ToArray();
                }
            }
            finally { Directory.Delete(staging, true); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (fixChecksums) rom = RomTools.RepairChecksums(rom);
        var artifacts = new List<FileOperations.Artifact> { new(output, rom) };
        if (symbols is not null) artifacts.Add(new FileOperations.Artifact(symbolPath, symbols));
        if (map is not null) artifacts.Add(new FileOperations.Artifact(mapPath, map));
        artifacts.AddRange(generatedArtifacts);
        var engine = game is not null ? "builtin-gamespec" : "rgbds";
        if (receiptPath is not null)
        {
            BuildReceipt.AssertUnchanged([initialProject, .. inputSnapshot!]);
            foreach (var tool in tools)
                if (BuildReceipt.HashFile(tool.Path).Sha256 != tool.Sha256)
                    throw new CliException("BUILD_FAILED", $"Build tool changed during compilation: {tool.Path}. No output was replaced.");
            var receipt = new BuildReceipt(1, AutomationContracts.Version, projectPath, root, engine,
                new BuildEffectiveOptions(sources, includeDirectories, defines, fixChecksums, exactSize, output),
                tools, inputSnapshot!.Concat(generatedInputSnapshot).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), artifacts.Select(BuildReceipt.HashArtifact).ToArray());
            artifacts.Add(new FileOperations.Artifact(receiptPath, JsonText(receipt)));
        }
        foreach (var artifact in artifacts) FileOperations.ProtectDestination(artifact.Path, protectedInputs);
        validateAdditionalOutputs?.Invoke(protectedInputs);
        cancellationToken.ThrowIfCancellationRequested();
        FileOperations.AtomicWriteBatch(artifacts, overwrite);
        var result = ToObject(new { output, size = rom.Length, sha256 = Sha256(rom), inspection = RomTools.Inspect(rom), engine });
        if (symbols is not null) { result["symbols"] = symbolPath; result["map"] = mapPath; }
        if (receiptPath is not null) result["receipt"] = receiptPath;
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

    private static string ResolveRgbdsExecutable(string tool)
    {
        var bin = Environment.GetEnvironmentVariable("RGBDS_BIN");
        var filename = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        if (!string.IsNullOrEmpty(bin))
        {
            var configured = Path.GetFullPath(Path.Combine(bin, filename));
            if (File.Exists(configured)) return configured;
        }
        else
        {
            var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
            foreach (var directory in directories)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), filename));
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new CliException("RGBDS_MISSING", $"Cannot find {tool}. Install RGBDS and put its tools on PATH, or set RGBDS_BIN to their directory. GameSpec JSON builds use the built-in compiler.");
    }

    private static async Task<BuildTool> GetRgbdsToolAsync(string tool, string cwd, CancellationToken cancellationToken)
    {
        var executable = ResolveRgbdsExecutable(tool);
        var fingerprint = BuildReceipt.HashFile(executable);
        var version = (await RunRgbdsAsync(tool, ["--version"], cwd, cancellationToken, executable)).Trim();
        if (string.IsNullOrWhiteSpace(version) || version.Length > 4096)
            throw new CliException("BUILD_FAILED", $"Cannot record {tool} version: expected at most 4096 nonempty characters.");
        return new BuildTool(tool, executable, version, fingerprint.Sha256);
    }

    private static async Task<string> RunRgbdsAsync(string tool, string[] arguments, string cwd, CancellationToken cancellationToken, string? executable = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executable ??= ResolveRgbdsExecutable(tool);
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
        var stdout = ReadBoundedOutputAsync(process.StandardOutput, linked);
        var stderr = ReadBoundedOutputAsync(process.StandardError, linked);
        try { await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr); }
        catch (Exception exception)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            throw new CliException("BUILD_FAILED", $"{tool} failed: {exception.Message}", exception);
        }
        if (process.ExitCode != 0)
            throw new CliException("BUILD_FAILED", $"{tool} failed:\n{(string.IsNullOrWhiteSpace(stderr.Result) ? stdout.Result : stderr.Result).Trim()}");
        return string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result : stdout.Result;
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
