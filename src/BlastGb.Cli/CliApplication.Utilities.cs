using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using BlastGb.Core;
using BlastGb.Runtime;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private static string[] RuntimeInputPaths(ParsedCommand parsed, string input) => [input, .. new[] { "assert", "script", "symbols", "until", "save-input" }
        .Select(parsed.Option).Where(path => path is not null).Select(path => Path.GetFullPath(path!))];

    private static byte[]? ReadBatterySave(ParsedCommand parsed)
    {
        if (parsed.Option("save-input") is not { } path) return null;
        if (new FileInfo(path).Length > 131072) throw new CliException("INVALID_SAVE", "Battery RAM saves may total at most 128 KiB.");
        return File.ReadAllBytes(path);
    }

    private static void ValidateRuntimeOutputs(ParsedCommand parsed, string[] inputs)
    {
        string[] outputs = new[] { "screenshot", "save-output" }.Select(parsed.Option).Where(path => path is not null).Select(path => Path.GetFullPath(path!)).ToArray();
        if (parsed.Has("overwrite") && outputs.Length == 0 && !parsed.Has("capture-dir")) throw new CliException("USAGE", "--overwrite requires --screenshot, --capture-dir, or --save-output.");
        foreach (string output in outputs)
        {
            FileOperations.ProtectDestination(output, inputs);
            FileOperations.AssertWritable(output, parsed.Has("overwrite"));
        }
        if (outputs.Select(FileOperations.CanonicalPath).Distinct(StringComparer.Ordinal).Count() != outputs.Length)
            throw new CliException("UNSAFE_OUTPUT", "Runtime outputs must use distinct destinations.");
    }

    private static void WriteRuntimeOutputs(ParsedCommand parsed, byte[] framebuffer, byte[]? save, JsonObject data, CancellationToken cancellationToken,
        IReadOnlyList<FrameCapture>? captures = null)
    {
        var artifacts = CaptureArtifacts(framebuffer, captures ?? [], parsed.Option("screenshot"), parsed.Option("capture-dir"),
            RuntimeInputPaths(parsed, Path.GetFullPath(parsed.Positionals[0])), parsed.Has("overwrite"), data);
        if (parsed.Option("save-output") is { } savePath)
        {
            string output = Path.GetFullPath(savePath);
            artifacts.Add(new(output, save ?? throw new InvalidOperationException("Missing battery RAM capture.")));
            data["saveOutput"] = output;
        }
        if (parsed.Has("provenance")) data["toolkitVersion"] = AutomationContracts.Version;
        cancellationToken.ThrowIfCancellationRequested();
        if (artifacts.Count != 0) FileOperations.AtomicWriteBatch(artifacts, parsed.Has("overwrite"));
    }

    private static JsonObject LintGame(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions();
        string input = Path.GetFullPath(parsed.Positionals[0]);
        var diagnostics = GameCompiler.Lint(JsonFile(input));
        return ToObject(new { input, valid = diagnostics.Count == 0, diagnostics });
    }

    private static JsonObject ConvertTiles(ParsedCommand parsed)
    {
        parsed.RequirePositionals(1);
        parsed.AllowOptions("output", "overwrite", "columns");
        string input = Path.GetFullPath(parsed.Positionals[0]);
        string output = Path.GetFullPath(parsed.Option("output") ?? throw new CliException("USAGE", "Tile conversion requires --output FILE."));
        FileOperations.ProtectDestination(output, [input]);
        FileOperations.AssertWritable(output, parsed.Has("overwrite"));
        if (new FileInfo(input).Length > 16 * 1024 * 1024) throw new CliException("USAGE", "Tile inputs may total at most 16 MiB.");
        byte[] result;
        int count, width, height;
        if (parsed.Command == "tiles-export")
        {
            byte[] tiles = File.ReadAllBytes(input);
            if (tiles.Length == 0 || tiles.Length % 16 != 0 || tiles.Length > 262144)
                throw new CliException("USAGE", "2bpp data must contain 1–16384 complete 16-byte tiles.");
            count = tiles.Length / 16;
            int columns = Integer(parsed.Option("columns") ?? "1", "columns", 128);
            if (columns == 0 || count % columns != 0) throw new CliException("USAGE", "columns must be positive and divide the tile count exactly.");
            width = columns * 8;
            height = count / columns * 8;
            byte[] rgba = new byte[width * height * 4];
            for (int tile = 0; tile < count; tile++)
            {
                byte[] pixels = Tile2Bpp.Decode(tiles.AsSpan(tile * 16, 16));
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int at = (((tile / columns * 8 + y) * width) + tile % columns * 8 + x) * 4;
                        rgba.AsSpan(at, 3).Fill((byte)(255 - pixels[y * 8 + x] * 85));
                        rgba[at + 3] = 255;
                    }
            }
            result = new PngImage(width, height, rgba).Encode();
        }
        else
        {
            if (parsed.Has("columns")) throw new CliException("USAGE", "--columns applies to tiles export; import uses the image dimensions.");
            var image = PngImage.Decode(File.ReadAllBytes(input));
            width = image.Width;
            height = image.Height;
            if (width % 8 != 0 || height % 8 != 0) throw new CliException("USAGE", "Tile-sheet dimensions must be multiples of eight.");
            count = width / 8 * (height / 8);
            result = new byte[count * 16];
            for (int tile = 0; tile < count; tile++)
            {
                byte[] pixels = new byte[64];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int at = (((tile / (width / 8) * 8 + y) * width) + tile % (width / 8) * 8 + x) * 4;
                        byte shade = image.Rgba[at];
                        if (image.Rgba[at + 1] != shade || image.Rgba[at + 2] != shade || image.Rgba[at + 3] != 255 || shade % 85 != 0)
                            throw new CliException("USAGE", "Tile PNG pixels must be opaque grayscale shades 255, 170, 85, or 0 (indices 0–3).");
                        pixels[y * 8 + x] = (byte)((255 - shade) / 85);
                    }
                Tile2Bpp.Encode(pixels).CopyTo(result, tile * 16);
            }
        }
        FileOperations.AtomicWrite(output, result, parsed.Has("overwrite"));
        return ToObject(new { input, output, tiles = count, width, height, size = result.Length, sha256 = Sha256(result) });
    }

    private static async Task<JsonObject> DiagnoseAsync(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        parsed.RequirePositionals(0);
        parsed.AllowOptions();
        var tools = new List<object>();
        foreach (string name in new[] { "rgbasm", "rgblink" }) tools.Add(await ToolIdentityAsync(name, cancellationToken));
        using var session = new EmulatorSession(GameCompiler.Build(GameCompiler.CreateStarterSpec()).Rom);
        session.Advance(1, cancellationToken: cancellationToken);
        return ToObject(new
        {
            toolkitVersion = AutomationContracts.Version,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            engine = EmulatorSession.Provenance,
            runtimeHealthy = session.Ticks > 0,
            tools
        });
    }

    private static string ToolExecutable(string tool)
    {
        string name = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        string? bin = Environment.GetEnvironmentVariable("RGBDS_BIN");
        if (!string.IsNullOrEmpty(bin)) return Path.GetFullPath(Path.Combine(bin, name));
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(directory))
            {
                string path = Path.GetFullPath(Path.Combine(directory, name));
                if (File.Exists(path)) return path;
            }
        return name;
    }

    private static async Task<object> ToolIdentityAsync(string tool, CancellationToken cancellationToken)
    {
        string executable = ToolExecutable(tool);
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            process.Start();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            Task<string> stdout = ReadBoundedOutputAsync(process.StandardOutput, deadline), stderr = ReadBoundedOutputAsync(process.StandardError, deadline);
            try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr); }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                return new { name = tool, executable, available = false, diagnostic = "Version probe exceeded its time or output limit." };
            }
            return new { name = tool, executable, available = process.ExitCode == 0, version = (stdout.Result + stderr.Result).Trim() };
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new { name = tool, executable, available = false, diagnostic = exception.Message };
        }
    }
}
