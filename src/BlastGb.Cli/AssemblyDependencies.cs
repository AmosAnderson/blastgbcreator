using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlastGb.Cli;

/// <summary>Reads RGBDS's literal dependency report, failing closed on ambiguous filenames.</summary>
internal static partial class AssemblyDependencies
{
    private const int MaximumReportBytes = 16 * 1024 * 1024;
    public static string CreateTarget() => "blast-gb-dependencies-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> report, string target, string cwd, string source)
    {
        if (!TargetPattern().IsMatch(target)) throw Failure("invalid dependency target");
        if (report.Length > MaximumReportBytes) throw Failure("dependency report exceeds 16 MiB");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(report); }
        catch (DecoderFallbackException) { throw Failure("dependency report is not valid UTF-8"); }
        if (OperatingSystem.IsWindows()) text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.EndsWith('\n')) throw Failure("dependency report is empty or incomplete");
        var prefix = target + ": ";
        var inputs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text[..^1].Split('\n'))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) throw Failure("unsupported or ambiguous dependency rule");
            var path = line[prefix.Length..];
            if (string.IsNullOrEmpty(path) || path.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw Failure("empty or ambiguous dependency path");
            var input = Path.GetFullPath(path, cwd);
            if (!File.Exists(input)) throw Failure($"dependency is missing or not a regular file: {input}");
            if (seen.Add(input)) inputs.Add(input);
        }
        if (inputs.Count == 0 || inputs[0] != Path.GetFullPath(source, cwd))
            throw Failure("dependency report does not begin with the source file");
        return inputs;
    }

    private static CliException Failure(string detail) => new("BUILD_FAILED", $"Cannot confirm assembler inputs: {detail}. No output was replaced.");

    [GeneratedRegex("^blast-gb-dependencies-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();
}
