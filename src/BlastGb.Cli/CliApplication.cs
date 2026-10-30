using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlastGb.Core;

namespace BlastGb.Cli;

/// <summary>Host orchestration for the reusable core and runtime. All output is explicitly routed.</summary>
public static partial class CliApplication
{
    private const string ManifestFile = "gb-project.json";
    private const string ManifestFormat = "blast-gb-workspace";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions PrettyJson = new(JsonDefaults.Options) { WriteIndented = true };
    private static readonly HashSet<string> BooleanOptions = ["json", "overwrite", "fix-checksums", "allow-unguarded", "help", "version", "build", "provenance"];
    private static readonly HashSet<string> OptionNames = [.. BooleanOptions, "output", "offset", "count", "name", "kind", "frames", "memory", "assert", "buttons", "breakpoint", "max-instructions", "script", "symbols", "symbol", "screenshot", "capture-dir", "receipt", "suite", "report", "tilemap", "palette", "attrmap", "patch-output", "label", "until", "trace-capacity", "timeout-seconds", "columns", "save-input", "save-output"];
    private static readonly Dictionary<string, Dictionary<string, string>> Groups = new(StringComparer.Ordinal)
    {
        ["rom"] = new() { ["info"] = "inspect", ["verify"] = "verify", ["patch"] = "patch", ["diff"] = "diff", ["patch-create"] = "patch-create" },
        ["asm"] = new() { ["compile"] = "build", ["decompile"] = "disassemble", ["symbols"] = "symbols" },
        ["project"] = new() { ["init"] = "init", ["import"] = "import", ["resolve"] = "resolve", ["info"] = "resolve", ["status"] = "project-status", ["test"] = "suite" },
        ["tiles"] = new() { ["import"] = "tiles-import", ["export"] = "tiles-export" },
        ["game"] = new() { ["lint"] = "lint", ["validate"] = "game-validate", ["export"] = "game-export" },
        ["asset"] = new() { ["convert"] = "asset-convert", ["encode"] = "asset-encode", ["decode"] = "asset-decode" },
        ["mcp"] = new() { ["serve"] = "mcp" }
    };

    private sealed record ParsedCommand(string Command, string Invocation, IReadOnlyList<string> Positionals, Dictionary<string, string?> Options)
    {
        public bool Has(string name) => Options.ContainsKey(name);
        public string? Option(string name) => Options.GetValueOrDefault(name);
        public void RequirePositionals(int minimum, int? maximum = null)
        {
            var upper = maximum ?? minimum;
            if (Positionals.Count < minimum || Positionals.Count > upper)
                throw new CliException("USAGE", $"{Command} expects {(minimum == upper ? minimum.ToString(CultureInfo.InvariantCulture) : $"{minimum}–{upper}")} path argument(s). Use {Command} --help.");
        }
        public void AllowOptions(params string[] names)
        {
            var allowed = new HashSet<string>(names, StringComparer.Ordinal) { "json", "help" };
            foreach (var key in Options.Keys)
                if (!allowed.Contains(key)) throw new CliException("USAGE", $"--{key} is not supported by {Command}.");
        }
    }

    private static ParsedCommand Parse(string[] args)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument == "--") { positionals.AddRange(args[(i + 1)..]); break; }
            if (argument == "-h") { options["help"] = null; continue; }
            if (!argument.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(argument); continue; }
            var separator = argument.IndexOf('=');
            var key = separator < 0 ? argument[2..] : argument[2..separator];
            if (!OptionNames.Contains(key)) throw new CliException("USAGE", $"Unknown option --{key}. Use --help for supported options.");
            if (options.ContainsKey(key)) throw new CliException("USAGE", $"Option --{key} may be supplied only once.");
            if (BooleanOptions.Contains(key))
            {
                if (separator >= 0) throw new CliException("USAGE", $"--{key} does not take a value.");
                options.Add(key, null);
            }
            else
            {
                var value = separator < 0 ? ++i < args.Length ? args[i] : null : argument[(separator + 1)..];
                if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal))
                    throw new CliException("USAGE", $"--{key} needs a value.");
                options.Add(key, value);
            }
        }
        var command = positionals.Count > 0 ? positionals[0] : options.ContainsKey("version") ? "version" : "help";
        if (positionals.Count > 0) positionals.RemoveAt(0);
        var invocation = command;
        if (Groups.TryGetValue(command, out var subcommands) && positionals.Count > 0)
        {
            var subcommand = positionals[0];
            positionals.RemoveAt(0);
            invocation = command + " " + subcommand;
            if (!subcommands.TryGetValue(subcommand, out var operation))
                throw new CliException("USAGE", $"Unknown command {invocation}. Use {command} --help.");
            command = operation;
        }
        return new ParsedCommand(command, invocation, positionals, options);
    }

    /// <summary>Uses the same parser as execution, including option values and terminators.</summary>
    public static bool SelectsMcpCommand(string[] args)
    {
        try { return Parse(args).Command == "mcp"; }
        catch (CliException) { return false; }
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var command = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? "help";
        var wantsJson = args.TakeWhile(argument => argument != "--").Contains("--json", StringComparer.Ordinal);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = Parse(args);
            command = parsed.Invocation;
            wantsJson = parsed.Has("json");
            if (parsed.Command == "mcp" && !parsed.Has("help"))
            {
                parsed.RequirePositionals(0);
                if (parsed.Options.Count != 0)
                    throw new CliException("USAGE", "mcp serve accepts no options. Stdout is reserved for protocol messages.");
                return await McpServer.ServeAsync(RunAsync, Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken);
            }
            if (parsed.Command == "help" || parsed.Has("help") || Groups.ContainsKey(parsed.Command))
            {
                var text = AutomationContracts.GetHelp(parsed.Command == "help" ? string.Join(' ', parsed.Positionals) : parsed.Invocation);
                await output.WriteAsync(wantsJson
                    ? Serialize(new { schemaVersion = 1, command = "help", ok = true, data = new { text, commands = AutomationContracts.CommandNames } }) + "\n"
                    : text);
                return 0;
            }
            var data = await ExecuteAsync(parsed, cancellationToken);
            var failed = IsFalse(data["valid"]) || IsFalse(data["assertionsPassed"]) || IsFalse(data["breakpointReached"]) || IsFalse(data["untilReached"]) || IsFalse(data["conditionsPassed"]);
            if (wantsJson)
            {
                var envelope = new JsonObject { ["schemaVersion"] = 1, ["command"] = command, ["ok"] = !failed, ["data"] = data };
                if (failed) envelope["error"] = ToObject(new { code = "VALIDATION_FAILED", message = "Verification, an assertion, or a requested breakpoint failed. Inspect data for details." });
                await output.WriteLineAsync(envelope.ToJsonString(JsonDefaults.Options));
            }
            else if (parsed.Command == "version") await output.WriteLineAsync(AutomationContracts.Version);
            else if (parsed.Command == "disassemble" && data["lines"] is JsonArray lines && data["output"] is null)
                await output.WriteLineAsync(FormatInstructionListing(lines));
            else await output.WriteLineAsync(data.ToJsonString(PrettyJson));
            return failed ? 3 : 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var code = exception switch
            {
                CliException cli => cli.Code,
                OperationCanceledException => "CANCELLED",
                FileNotFoundException or DirectoryNotFoundException => "ENOENT",
                UnauthorizedAccessException => "EACCES",
                _ => "OPERATION_FAILED"
            };
            if (wantsJson) await output.WriteLineAsync(Serialize(new { schemaVersion = 1, command, ok = false, error = new { code, message = exception.Message } }));
            else await error.WriteLineAsync($"{code}: {exception.Message}");
            return code == "USAGE" ? 2 : 1;
        }
    }

    private static bool IsFalse(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && !result;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
    private static JsonObject ToObject<T>(T value) => JsonSerializer.SerializeToNode(value, JsonDefaults.Options)!.AsObject();
    private static JsonObject Merge<T>(T value, object additions)
    {
        var result = ToObject(value);
        foreach (var property in ToObject(additions)) result[property.Key] = property.Value?.DeepClone();
        return result;
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] TextBytes(string text) => Utf8.GetBytes(text);
    private static string JsonText<T>(T value) => JsonSerializer.Serialize(value, PrettyJson) + "\n";

    private static JsonElement JsonFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(Utf8.GetString(File.ReadAllBytes(path)));
            return document.RootElement.Clone();
        }
        catch (JsonException exception) { throw new CliException("INVALID_JSON", $"{path}: {exception.Message}", exception); }
        catch (DecoderFallbackException exception) { throw new CliException("INVALID_JSON", $"{path} is not valid UTF-8.", exception); }
    }

    private static byte[] RomFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Cannot find cartridge file.", path);
        if (info.Length is < 0x150 or > 8 * 1024 * 1024)
            throw new CliException("INVALID_ROM", "Choose a regular Game Boy ROM file between 336 bytes and 8 MiB.");
        var bytes = File.ReadAllBytes(path);
        RomTools.Inspect(bytes);
        return bytes;
    }

    private static int Integer(string value, string label, int maximum = int.MaxValue)
    {
        var hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var digits = hexadecimal ? value[2..] : value;
        if (!IntegerPattern().IsMatch(value) || !int.TryParse(digits, hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0 || number > maximum)
            throw new CliException("USAGE", $"{label} must be a nonnegative integer between 0 and {maximum} (decimal or 0x hexadecimal).");
        return number;
    }

    [GeneratedRegex("^(?:0x[0-9a-f]+|[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    private static string FormatInstructionListing(JsonArray lines) => string.Join('\n', lines.Select(line =>
        $"{line!["offset"]!.GetValue<int>():X6}  {string.Join(' ', line["bytes"]!.AsArray().Select(value => value!.GetValue<int>().ToString("X2", CultureInfo.InvariantCulture))).PadRight(8)}  {line["text"]!.GetValue<string>()}" +
        (line["labels"] is JsonArray { Count: > 0 } labels ? " ; " + string.Join(", ", labels.Select(label => label!.GetValue<string>())) : "")));
}
