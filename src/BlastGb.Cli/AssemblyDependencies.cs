using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlastGb.Cli;

/// <summary>Reads RGBDS's literal dependency report, failing closed on ambiguous filenames.</summary>
internal static partial class AssemblyDependencies
{
    private const int MaximumReportBytes = 16 * 1024 * 1024;
    public static string CreateTarget() => "blast-gb-dependencies-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> report, string target, string cwd, string source) =>
        Parse(report, target, cwd, source, OperatingSystem.IsWindows()
            ? WindowsEncoding((int)GetACP())
            : null);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetACP();

    internal static Encoding WindowsEncoding(int codePage) => codePage == 65001
        ? new UTF8Encoding(false, true)
        : CodePagesEncodingProvider.Instance.GetEncoding(codePage)
            ?? throw Failure("the active Windows code page is unavailable");

    internal static IReadOnlyList<string> Parse(ReadOnlySpan<byte> report, string target, string cwd, string source, Encoding? windowsEncoding)
    {
        if (!TargetPattern().IsMatch(target)) throw Failure("invalid dependency target");
        if (report.Length > MaximumReportBytes) throw Failure("dependency report exceeds 16 MiB");
        string? windowsText = null;
        if (windowsEncoding is not null)
        {
            // Native Windows RGBDS uses narrow paths. Check the ACP interpretation even
            // when the same bytes form valid UTF-8: the two may name different inputs.
            var strict = (Encoding)windowsEncoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;
            try
            {
                windowsText = strict.GetString(report);
                if (!report.SequenceEqual(strict.GetBytes(windowsText)))
                    throw Failure("dependency report does not round-trip through the active Windows code page");
            }
            catch (ArgumentException exception) when (exception is DecoderFallbackException or EncoderFallbackException)
            {
                // Some code pages accept noncanonical byte aliases in native file APIs.
                // Discarding this interpretation could protect the wrong UTF-8 filename.
                throw Failure("dependency report cannot be decoded exactly in the active Windows code page");
            }
        }
        IReadOnlyList<string>? utf8Inputs = null;
        CliException? utf8Failure = null;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(report);
            utf8Inputs = ParseText(text, target, cwd, source, windowsEncoding is not null);
        }
        catch (DecoderFallbackException) { utf8Failure = Failure("dependency report is not valid UTF-8"); }
        catch (CliException exception) { utf8Failure = exception; }
        if (windowsText is null) return utf8Inputs ?? throw utf8Failure!;
        IReadOnlyList<string>? windowsInputs = null;
        try { windowsInputs = ParseText(windowsText, target, cwd, source, true); }
        catch (CliException) { }
        if (utf8Inputs is not null && windowsInputs is not null && !utf8Inputs.SequenceEqual(windowsInputs, StringComparer.Ordinal))
            throw Failure("dependency report has ambiguous UTF-8 and Windows code-page paths");
        return utf8Inputs ?? windowsInputs ?? throw utf8Failure!;
    }

    private static IReadOnlyList<string> ParseText(string text, string target, string cwd, string source, bool windows)
    {
        if (windows) text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
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
