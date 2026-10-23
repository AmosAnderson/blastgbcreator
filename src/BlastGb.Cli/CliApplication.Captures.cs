using System.Text.Json.Nodes;
using BlastGb.Core;
using BlastGb.Runtime;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private static IEnumerable<string> DebugCaptureNames(DebugScript script) => script.Commands
        .Select((command, index) => command is CaptureCommand capture ? capture.Name
            : command is AssertCommand { Expected.FramebufferSha256: not null } ? "failure-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : null)
        .OfType<string>();

    private static void PreflightCaptures(string? screenshot, string? directory, IEnumerable<string> names,
        IReadOnlyList<string> inputs, bool overwrite)
    {
        if (directory is not null)
        {
            FileOperations.RejectSymbolicLink(directory);
            if (File.Exists(directory)) throw new CliException("OUTPUT_EXISTS", "Capture directory names an existing file.");
        }
        var paths = new List<string>();
        if (screenshot is not null) paths.Add(Path.GetFullPath(screenshot));
        if (directory is not null) paths.AddRange(names.Select(name => FileOperations.WorkspacePath(Path.GetFullPath(directory), name + ".png", "capture")));
        foreach (var path in paths)
        {
            FileOperations.ProtectDestination(path, inputs);
            FileOperations.AssertWritable(path, overwrite);
        }
        if (paths.Select(FileOperations.CanonicalPath).Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw new CliException("UNSAFE_OUTPUT", "Screenshot and capture paths must be distinct.");
    }

    // Callers can combine these artifacts with reports before committing one atomic batch.
    private static List<FileOperations.Artifact> CaptureArtifacts(byte[] rgba, IReadOnlyList<FrameCapture> captures,
        string? screenshot, string? directory, IReadOnlyList<string> inputs, bool overwrite, JsonObject result)
    {
        PreflightCaptures(screenshot, directory, captures.Select(capture => capture.Name), inputs, overwrite);
        var artifacts = new List<FileOperations.Artifact>();
        if (screenshot is not null)
        {
            var path = Path.GetFullPath(screenshot);
            artifacts.Add(new(path, PngEncoder.EncodeRgba(rgba, 160, 144)));
            result["screenshot"] = path;
        }
        if (directory is not null)
        {
            var images = new JsonArray();
            foreach (var capture in captures)
            {
                var path = FileOperations.WorkspacePath(Path.GetFullPath(directory), capture.Name + ".png", "capture");
                artifacts.Add(new(path, PngEncoder.EncodeRgba(capture.Rgba, 160, 144)));
                images.Add(ToObject(new { capture.Name, path, framebufferSha256 = capture.Sha256 }));
            }
            result["captures"] = images;
        }
        return artifacts;
    }
}
