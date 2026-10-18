using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("BlastGb.Cli.Tests")]

namespace BlastGb.Cli;

/// <summary>All CLI writes are staged next to their destination and committed as a batch.</summary>
internal static class FileOperations
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal sealed record Artifact(string Path, byte[] Contents)
    {
        public Artifact(string path, string contents) : this(path, Utf8.GetBytes(contents)) { }
    }

    internal sealed class StagedArtifact(string path, string temporary, string backup)
    {
        public string Path { get; } = path;
        public string Temporary { get; } = temporary;
        public string Backup { get; } = backup;
        public bool OriginalMoved { get; set; }
        public bool Committed { get; set; }
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static void RejectSymbolicLink(string path)
    {
        // LinkTarget detects dangling links as well; Exists intentionally does not.
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new CliException("UNSAFE_PATH", $"{path} is a symbolic link. Use an ordinary workspace file or directory.");
    }

    public static void AssertWritable(string path, bool overwrite)
    {
        RejectSymbolicLink(path);
        if (Directory.Exists(path))
            throw new CliException("OUTPUT_EXISTS", $"{path} is not a regular file.");
        if (File.Exists(path) && !overwrite)
            throw new CliException("OUTPUT_EXISTS", $"{path} already exists. Choose a new output or add --overwrite.");
    }

    /// <summary>Resolve existing ancestors, links, and the actual casing of existing names.</summary>
    public static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(true);
                if (target is null || !target.Exists)
                    throw new CliException("UNSAFE_PATH", $"Cannot resolve symbolic link {next}.");
                current = CanonicalPath(target.FullName);
                continue;
            }
            if (Exists(next))
            {
                // GetFullPath does not normalize casing. Enumerating an existing parent does,
                // without assuming every macOS or Windows volume is case insensitive.
                var names = Directory.EnumerateFileSystemEntries(current).ToArray();
                var normalizedPart = part.Normalize(NormalizationForm.FormD);
                next = names.FirstOrDefault(name => Path.GetFileName(name) == part)
                    ?? names.FirstOrDefault(name => string.Equals(Path.GetFileName(name), part, StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault(name => Path.GetFileName(name).Normalize(NormalizationForm.FormD) == normalizedPart)
                    ?? names.FirstOrDefault(name => string.Equals(Path.GetFileName(name).Normalize(NormalizationForm.FormD), normalizedPart, StringComparison.OrdinalIgnoreCase))
                    ?? next;
            }
            current = next;
        }
        return Path.GetFullPath(current);
    }

    public static void ProtectDestination(string output, IEnumerable<string> inputs)
    {
        var destination = CanonicalPath(output);
        if (inputs.Any(input => string.Equals(CanonicalPath(input), destination, StringComparison.Ordinal)))
            throw new CliException("UNSAFE_OUTPUT", "Choose an output different from the original ROM, project manifest, and source files.");
    }

    public static string WorkspacePath(string root, string? value, string label)
    {
        if (string.IsNullOrEmpty(value) || Path.IsPathRooted(value))
            throw new CliException("INVALID_PROJECT", $"{label} must be a relative path inside the workspace.");
        var path = Path.GetFullPath(value, root);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new CliException("INVALID_PROJECT", $"{label} must stay inside the workspace.");
        RejectSymbolicLink(root);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectSymbolicLink(current);
        }
        return path;
    }

    public static void AtomicWrite(string path, byte[] contents, bool overwrite) =>
        AtomicWriteBatch([new Artifact(path, contents)], overwrite);

    public static void AtomicWrite(string path, string contents, bool overwrite) =>
        AtomicWriteBatch([new Artifact(path, contents)], overwrite);

    public static void AtomicWriteBatch(IReadOnlyList<Artifact> files, bool overwrite)
    {
        if (files.Select(file => Path.GetFullPath(file.Path)).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new CliException("UNSAFE_OUTPUT", "Build artifact paths must be distinct.");
        var staged = new List<StagedArtifact>();
        var complete = false;
        try
        {
            foreach (var file in files)
            {
                AssertWritable(file.Path, overwrite);
                var directory = Path.GetDirectoryName(file.Path)!;
                Directory.CreateDirectory(directory);
                var record = new StagedArtifact(file.Path,
                    Path.Combine(directory, $".{Path.GetFileName(file.Path)}.{Guid.NewGuid():N}.é.tmp"),
                    Path.Combine(directory, $".{Path.GetFileName(file.Path)}.{Guid.NewGuid():N}.bak"));
                staged.Add(record);
                using var stream = new FileStream(record.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(file.Contents);
                stream.Flush(true);
            }
            var destinations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in staged)
            {
                var canonical = CanonicalPath(record.Path);
                // Probe this exact destination volume with a file we own. The composed
                // marker guarantees a distinct decomposed probe even for ASCII paths.
                var caseInsensitive = File.Exists(record.Temporary[..^4] + ".TMP");
                var decomposed = Path.Combine(Path.GetDirectoryName(record.Temporary)!,
                    Path.GetFileName(record.Temporary).Normalize(NormalizationForm.FormD));
                var normalizationInsensitive = File.Exists(decomposed);
                var key = normalizationInsensitive ? canonical.Normalize(NormalizationForm.FormD) : canonical;
                if (caseInsensitive) key = key.ToUpperInvariant();
                if (!destinations.Add(key))
                    throw new CliException("UNSAFE_OUTPUT", "Build artifact paths must be distinct on this filesystem.");
            }
            foreach (var record in staged) AssertWritable(record.Path, overwrite);
            CommitStagedArtifacts(staged, overwrite);
            complete = true;
        }
        finally
        {
            foreach (var record in staged)
            {
                File.Delete(record.Temporary);
                if (complete) File.Delete(record.Backup);
            }
        }
    }

    /// <summary>Commit prepared files and restore the whole batch if a later move fails.</summary>
    internal static void CommitStagedArtifacts(IReadOnlyList<StagedArtifact> staged, bool overwrite)
    {
        try
        {
            foreach (var record in staged)
            {
                // An unexpected destination must never be moved aside without permission.
                // File.Move itself enforces create-only semantics after preflight races.
                if (overwrite && File.Exists(record.Path))
                {
                    File.Move(record.Path, record.Backup);
                    record.OriginalMoved = true;
                }
                File.Move(record.Temporary, record.Path);
                record.Committed = true;
            }
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            foreach (var record in staged.Reverse())
            {
                try
                {
                    if (record.Committed) File.Delete(record.Path);
                    if (record.OriginalMoved) File.Move(record.Backup, record.Path);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    failures.Add(record.OriginalMoved ? record.Backup : record.Path);
                }
            }
            if (failures.Count != 0)
                throw new CliException("WRITE_FAILED", $"Could not commit all build artifacts or restore all previous files. Recovery files remain at {string.Join(", ", failures)}. Original error: {error.Message}", error);
            throw;
        }
    }

    public static void WriteWorkspace(string target, IReadOnlyDictionary<string, byte[]> files)
    {
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        RejectSymbolicLink(target);
        if (Exists(target))
            throw new CliException("OUTPUT_EXISTS", $"{target} already exists. Choose a new workspace directory.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".gb-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, contents) in files)
            {
                var path = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                stream.Write(contents);
            }
            if (Exists(target))
                throw new CliException("OUTPUT_EXISTS", $"{target} was created by another process. Choose a new directory.");
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}

internal sealed class CliException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
