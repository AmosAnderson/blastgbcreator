using System.Security.Cryptography;
using System.Text.Json;

namespace BlastGb.Cli;

internal sealed record BuildFileDigest(string Path, long Size, string Sha256);
internal sealed record BuildTool(string Name, string Path, string Version, string Sha256);
internal sealed record BuildEffectiveOptions(IReadOnlyList<string> Sources, IReadOnlyList<string> IncludeDirectories,
    IReadOnlyDictionary<string, string> Defines, bool FixChecksums, int? RomSize, string Output);
internal sealed record BuildReceiptChange(string Kind, string Path, string Reason, string? ExpectedSha256, string? ActualSha256);

/// <summary>A deterministic description of the files and tools used by one successful build.</summary>
internal sealed record BuildReceipt(int SchemaVersion, string ToolkitVersion, string Project, string Root, string Engine,
    BuildEffectiveOptions Options, IReadOnlyList<BuildTool> Tools, IReadOnlyList<BuildFileDigest> Inputs, IReadOnlyList<BuildFileDigest> Outputs)
{
    public static BuildFileDigest HashFile(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using var stream = File.OpenRead(path);
        return new BuildFileDigest(path, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    public static BuildFileDigest HashArtifact(FileOperations.Artifact artifact) =>
        new(System.IO.Path.GetFullPath(artifact.Path), artifact.Contents.LongLength, Convert.ToHexStringLower(SHA256.HashData(artifact.Contents)));

    public static IReadOnlyList<BuildFileDigest> CaptureInputs(IEnumerable<string> paths)
    {
        var distinct = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths) distinct.TryAdd(FileOperations.CanonicalPath(path), System.IO.Path.GetFullPath(path));
        return distinct.Values.Order(StringComparer.Ordinal).Select(HashFile).ToArray();
    }

    public static void AssertUnchanged(IEnumerable<BuildFileDigest> expected)
    {
        foreach (var file in expected)
        {
            var actual = HashFile(file.Path);
            if (actual.Size != file.Size || actual.Sha256 != file.Sha256)
                throw new CliException("BUILD_FAILED", $"Build input changed during compilation: {file.Path}. No output was replaced.");
        }
    }

    public static BuildReceipt Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new CliException("INVALID_RECEIPT", "Build receipt exceeds 16 MiB.");
        BuildReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<BuildReceipt>(File.ReadAllBytes(path), JsonDefaults.Options)
                ?? throw new JsonException("Expected an object.");
        }
        catch (JsonException exception) { throw new CliException("INVALID_RECEIPT", "Invalid build receipt: " + exception.Message, exception); }
        if (receipt.SchemaVersion != 1 || string.IsNullOrEmpty(receipt.ToolkitVersion)
            || !Absolute(receipt.Project) || !Absolute(receipt.Root) || receipt.Engine is not ("rgbds" or "builtin-gamespec")
            || receipt.Options is null || !Absolute(receipt.Options.Output)
            || receipt.Options.Sources is null || receipt.Options.IncludeDirectories is null || receipt.Options.Defines is null
            || receipt.Tools is null || receipt.Inputs is not { Count: > 0 } || receipt.Outputs is not { Count: > 0 }
            || receipt.Inputs.Concat(receipt.Outputs).Any(file => file is null || !Absolute(file.Path) || file.Size < 0 || !Digest(file.Sha256))
            || receipt.Tools.Any(tool => tool is null || string.IsNullOrEmpty(tool.Name) || !Absolute(tool.Path) || string.IsNullOrEmpty(tool.Version) || !Digest(tool.Sha256)))
            throw new CliException("INVALID_RECEIPT", "Expected a version-1 build receipt with absolute paths, effective options, tools, and SHA-256 file digests.");
        if (!receipt.Inputs.Any(file => SamePath(file.Path, receipt.Project)) || !receipt.Outputs.Any(file => SamePath(file.Path, receipt.Options.Output)))
            throw new CliException("INVALID_RECEIPT", "Build receipt must include its project input and ROM output.");
        return receipt;
    }

    public IReadOnlyList<BuildReceiptChange> Check(string project)
    {
        var changes = new List<BuildReceiptChange>();
        if (!SamePath(Project, project)) changes.Add(new("project", project, "different-project", null, null));
        if (ToolkitVersion != AutomationContracts.Version) changes.Add(new("toolkit", project, "version-changed", null, null));
        foreach (var input in Inputs) CheckFile(changes, "input", input.Path, input.Sha256, input.Size);
        foreach (var output in Outputs) CheckFile(changes, "output", output.Path, output.Sha256, output.Size);
        foreach (var tool in Tools) CheckFile(changes, "tool", tool.Path, tool.Sha256, null);
        return changes;
    }

    private static void CheckFile(List<BuildReceiptChange> changes, string kind, string path, string expected, long? size)
    {
        if (!File.Exists(path)) { changes.Add(new(kind, path, "missing", expected, null)); return; }
        try
        {
            var actual = HashFile(path);
            if ((size is not null && actual.Size != size) || !string.Equals(actual.Sha256, expected, StringComparison.OrdinalIgnoreCase))
                changes.Add(new(kind, path, "changed", expected, actual.Sha256));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            changes.Add(new(kind, path, "unreadable", expected, null));
        }
    }

    private static bool Absolute(string? path) => !string.IsNullOrEmpty(path) && System.IO.Path.IsPathFullyQualified(path);
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool SamePath(string first, string second) => string.Equals(FileOperations.CanonicalPath(first), FileOperations.CanonicalPath(second), StringComparison.Ordinal);
}
