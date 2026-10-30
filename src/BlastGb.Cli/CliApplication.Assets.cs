using System.Buffers.Binary;
using System.Text.Json.Nodes;
using BlastGb.Core;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private sealed record AssetDefinition
    {
        public string? Input { get; init; }
        public string? Output { get; init; }
        public string? Tilemap { get; init; }
        public string? Palette { get; init; }
        public string? Attrmap { get; init; }

        public IEnumerable<(string Role, string Path, string Flag)> Outputs()
        {
            if (Output is not null) yield return ("tiles", Output, "-o");
            if (Tilemap is not null) yield return ("tilemap", Tilemap, "-t");
            if (Palette is not null) yield return ("palette", Palette, "-p");
            if (Attrmap is not null) yield return ("attrmap", Attrmap, "-a");
        }
    }

    private static void ValidatePngAsset(string input)
    {
        using var stream = File.OpenRead(input);
        if (stream.Length is < 33 or > 16 * 1024 * 1024) throw new CliException("INVALID_ASSET", "PNG input must be at most 16 MiB.");
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header[12..16].SequenceEqual("IHDR"u8))
            throw new CliException("INVALID_ASSET", "Graphics conversion requires a PNG image.");
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        if (width is < 8 or > 4096 || height is < 8 or > 4096 || width % 8 != 0 || height % 8 != 0 || (long)width * height > TileGraphics.MaximumTiles * 64)
            throw new CliException("INVALID_ASSET", "PNG dimensions must be multiples of 8, with at most 4096 tiles.");
    }

    // A staged mirror of workspace-relative outputs lets assemblers consume generated assets
    // without changing any existing output until the complete build succeeds.
    private static async Task<List<FileOperations.Artifact>> GenerateAssetAsync(AssetDefinition asset, string root,
        string staging, CancellationToken cancellationToken, string? executable = null, int timeoutSeconds = 120)
    {
        var input = FileOperations.WorkspacePath(root, asset.Input, "asset input");
        var outputs = asset.Outputs().ToArray();
        if (asset.Output is null) throw new CliException("INVALID_ASSET", "An asset needs a tile output path.");
        ValidatePngAsset(input);
        var arguments = new List<string> { "-d", "2" };
        foreach (var (role, relative, flag) in outputs)
        {
            var destination = FileOperations.WorkspacePath(root, relative, role);
            FileOperations.ProtectDestination(destination, [input]);
            var temporary = FileOperations.WorkspacePath(staging, relative, role);
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            arguments.AddRange([flag, temporary]);
        }
        arguments.Add(input);
        await RunRgbdsAsync("rgbgfx", arguments.ToArray(), root, cancellationToken, executable, timeoutSeconds);
        var artifacts = new List<FileOperations.Artifact>();
        foreach (var (role, relative, _) in outputs)
        {
            var temporary = FileOperations.WorkspacePath(staging, relative, role);
            artifacts.Add(new(FileOperations.WorkspacePath(root, relative, role), ReadAssetOutput(temporary, role)));
        }
        return artifacts;
    }

    internal static byte[] ReadAssetOutput(string path, string role)
    {
        var (limit, alignment) = role switch
        {
            "tiles" => (TileGraphics.MaximumTiles * 16, 16),
            "palette" => (64, 8),
            "tilemap" or "attrmap" => (TileGraphics.MaximumTiles, 1),
            _ => throw new CliException("INVALID_ASSET", $"Unknown generated asset role {role}.")
        };
        using var stream = File.OpenRead(path);
        var size = stream.Length;
        if (size < 1 || size > limit || size % alignment != 0)
            throw new CliException("INVALID_ASSET", $"Generated {role} exceeds Game Boy asset limits or is incomplete.");
        var contents = new byte[(int)size];
        try { stream.ReadExactly(contents); }
        catch (EndOfStreamException exception)
        {
            throw new CliException("INVALID_ASSET", $"Generated {role} changed while being read.", exception);
        }
        if (stream.ReadByte() != -1)
            throw new CliException("INVALID_ASSET", $"Generated {role} changed while being read.");
        return contents;
    }

    private static async Task<JsonObject> ConvertAssetAsync(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("output", "tilemap", "palette", "attrmap", "overwrite");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var output = Path.GetFullPath(parsed.Option("output") ?? throw new CliException("USAGE", "asset convert requires --output TILES.2bpp."));
        var destinations = new List<(string Flag, string Role, string Path)> { ("-o", "tiles", output) };
        foreach (var (name, flag) in new[] { ("tilemap", "-t"), ("palette", "-p"), ("attrmap", "-a") })
            if (parsed.Option(name) is { } path) destinations.Add((flag, name, Path.GetFullPath(path)));
        foreach (var item in destinations)
        {
            FileOperations.ProtectDestination(item.Path, [input]);
            FileOperations.AssertWritable(item.Path, parsed.Has("overwrite"));
        }
        ValidatePngAsset(input);
        var staging = Path.Combine(Path.GetTempPath(), "blast-gb-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var arguments = new List<string> { "-d", "2" };
            foreach (var item in destinations) arguments.AddRange([item.Flag, Path.Combine(staging, item.Role)]);
            arguments.Add(input);
            await RunRgbdsAsync("rgbgfx", arguments.ToArray(), staging, cancellationToken);
            var artifacts = destinations.Select(item => new FileOperations.Artifact(item.Path, ReadAssetOutput(Path.Combine(staging, item.Role), item.Role))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            FileOperations.AtomicWriteBatch(artifacts, parsed.Has("overwrite"));
            return ToObject(new { input, outputs = destinations.Select((item, index) => new { item.Role, path = item.Path, size = artifacts[index].Contents.Length, sha256 = Sha256(artifacts[index].Contents) }) });
        }
        finally { Directory.Delete(staging, true); }
    }

    private static JsonObject ConvertTileBytes(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("output", "overwrite");
        var input = Path.GetFullPath(parsed.Positionals[0]);
        var output = Path.GetFullPath(parsed.Option("output") ?? throw new CliException("USAGE", "Tile conversion requires --output FILE."));
        FileOperations.ProtectDestination(output, [input]);
        FileOperations.AssertWritable(output, parsed.Has("overwrite"));
        if (new FileInfo(input).Length > 2 * 1024 * 1024) throw new CliException("INVALID_ASSET", "Tile input exceeds 2 MiB.");
        byte[] contents;
        int tiles;
        if (parsed.Command == "asset-encode")
        {
            var value = JsonFile(input);
            if (value.ValueKind != System.Text.Json.JsonValueKind.Array || value.GetArrayLength() > TileGraphics.MaximumTiles * 64)
                throw new CliException("INVALID_ASSET", "Tile pixels must be a numeric array of at most 262144 indices in tile order.");
            var pixels = new byte[value.GetArrayLength()];
            var index = 0;
            foreach (var pixel in value.EnumerateArray())
            {
                if (!BlastGb.Serialization.JsonInteger.TryGetInt32(pixel, out var number) || number is < 0 or > 3)
                    throw new CliException("INVALID_ASSET", "Tile pixel indices must be integers 0–3.");
                pixels[index++] = (byte)number;
            }
            contents = TileGraphics.Encode2Bpp(pixels);
            tiles = contents.Length / 16;
        }
        else
        {
            var pixels = TileGraphics.Decode2Bpp(File.ReadAllBytes(input));
            contents = TextBytes(JsonText(pixels));
            tiles = pixels.Length / 64;
        }
        FileOperations.AtomicWrite(output, contents, parsed.Has("overwrite"));
        return ToObject(new { input, output, tiles, size = contents.Length, sha256 = Sha256(contents) });
    }
}
