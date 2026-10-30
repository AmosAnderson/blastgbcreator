using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Runtime;
using BlastGb.Serialization;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private const int MaximumSuiteCases = 100;
    private const long MaximumSuiteInstructions = 10_000_000;
    private sealed record SuiteCase(string Name, string Rom, JsonElement? Script, RunOptions? Options);

    private static async Task<JsonObject> TestSuiteAsync(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        parsed.RequirePositionals(0, 1);
        parsed.AllowOptions("build", "overwrite", "timeout-seconds");
        if (!parsed.Has("build") && (parsed.Has("overwrite") || parsed.Has("timeout-seconds")))
            throw new CliException("USAGE", "--overwrite and --timeout-seconds require --build for project test.");
        if (parsed.Option("timeout-seconds") is { } timeout && Integer(timeout, "timeout-seconds", 600) == 0)
            throw new CliException("USAGE", "timeout-seconds must be between 1 and 600.");
        var input = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? ".");
        var suitePath = Directory.Exists(input) ? Path.Combine(input, "gb-tests.json") : input;
        var root = Path.GetDirectoryName(suitePath)!;
        FileOperations.RejectSymbolicLink(root);
        FileOperations.RejectSymbolicLink(suitePath);
        var suite = SuiteJson(suitePath);
        SuiteFields(suite, ["version", "cases"], "suite");
        _ = SuiteInteger(suite, "version", 0, 1, 1);
        if (!suite.TryGetProperty("cases", out var caseValues) || caseValues.ValueKind != JsonValueKind.Array || caseValues.GetArrayLength() is < 1 or > MaximumSuiteCases)
            throw new CliException("INVALID_SUITE", $"Suite cases must contain between 1 and {MaximumSuiteCases} named cases.");

        string? buildRom = null;
        if (parsed.Has("build"))
        {
            var project = LoadManifest(Path.Combine(root, ManifestFile));
            buildRom = FileOperations.WorkspacePath(project.Root, project.Manifest.Output, "output");
        }
        var protectedInputs = new HashSet<string>(StringComparer.Ordinal) { suitePath };
        var cachedRoms = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cases = new List<SuiteCase>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long frames = 0, instructions = 0, memoryBytes = 0, conditionMemoryReads = 0, traceEntries = 0, commands = 0;
        foreach (var value in caseValues.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuiteFields(value, ["name", "rom", "assert", "script", "frames", "buttons", "breakpoint", "maxInstructions"], "suite case");
            var name = SuiteString(value, "name");
            if (name.Length > 256 || !names.Add(name)) throw new CliException("INVALID_SUITE", "Case names must be unique and at most 256 characters.");
            var rom = FileOperations.WorkspacePath(root, SuiteString(value, "rom"), "case ROM");
            bool rebuilt = buildRom is not null && FileOperations.CanonicalPath(rom) == FileOperations.CanonicalPath(buildRom);
            if (!rebuilt)
            {
                protectedInputs.Add(rom);
                if (!cachedRoms.ContainsKey(rom)) cachedRoms.Add(rom, RomFile(rom));
            }
            bool hasAssert = value.TryGetProperty("assert", out _), hasScript = value.TryGetProperty("script", out _);
            if (hasAssert == hasScript) throw new CliException("INVALID_SUITE", $"Case {name} requires exactly one of assert or script.");
            if (hasScript)
            {
                SuiteFields(value, ["name", "rom", "script"], "script case");
                var scriptPath = FileOperations.WorkspacePath(root, SuiteString(value, "script"), "debug script");
                protectedInputs.Add(scriptPath);
                var scriptValue = SuiteJson(scriptPath);
                var script = RuntimeRunner.ValidateDebugScript(scriptValue, cancellationToken);
                commands += script.Commands.Count;
                foreach (var command in script.Commands)
                {
                    switch (command)
                    {
                        case AdvanceCommand advance when advance.Op == "frame": frames += advance.Count; break;
                        case AdvanceCommand advance: instructions += advance.Count; break;
                        case ReadCommand read: memoryBytes += read.Length; break;
                        case WriteCommand write: memoryBytes += write.Bytes.Length; break;
                        case AssertCommand assertion: memoryBytes += assertion.Expected.Memory.Sum(item => (long)item.Bytes.Length); break;
                        case RunCommand run:
                            frames += run.Frames;
                            instructions += run.MaxInstructions;
                            traceEntries += run.TraceCapacity;
                            if (run.Until is not null)
                            {
                                long bytes = run.Until.Memory.Sum(item => (long)item.Bytes.Length);
                                memoryBytes += bytes;
                                conditionMemoryReads += bytes * (run.MaxInstructions + 1L);
                            }
                            break;
                    }
                }
                cases.Add(new(name, rom, scriptValue, null));
            }
            else
            {
                var assertionPath = FileOperations.WorkspacePath(root, SuiteString(value, "assert"), "assertions");
                protectedInputs.Add(assertionPath);
                var assertionValue = SuiteJson(assertionPath);
                var expected = RuntimeRunner.NormalizeAssertions(assertionValue, cancellationToken);
                int caseFrames = SuiteInteger(value, "frames", 60, 1, 3600);
                int maximumInstructions = SuiteInteger(value, "maxInstructions", 1_000_000, 1, 10_000_000);
                int? breakpoint = value.TryGetProperty("breakpoint", out _) ? SuiteInteger(value, "breakpoint", 0, 0, 65535) : null;
                var buttons = new List<string>();
                if (value.TryGetProperty("buttons", out var pressed))
                {
                    if (pressed.ValueKind != JsonValueKind.Array || pressed.GetArrayLength() > 8) throw new CliException("INVALID_SUITE", "Case buttons must be an array of at most eight button names.");
                    foreach (var button in pressed.EnumerateArray())
                    {
                        if (button.ValueKind != JsonValueKind.String || button.GetString() is not ("up" or "down" or "left" or "right" or "A" or "B" or "start" or "select"))
                            throw new CliException("INVALID_SUITE", "Unknown case button; use up, down, left, right, A, B, start, or select.");
                        if (!buttons.Contains(button.GetString()!, StringComparer.Ordinal)) buttons.Add(button.GetString()!);
                    }
                }
                frames += caseFrames;
                if (breakpoint is not null) instructions += maximumInstructions;
                memoryBytes += expected.Memory.Sum(item => (long)item.Bytes.Length);
                cases.Add(new(name, rom, null, new RunOptions { Frames = caseFrames, MaxInstructions = maximumInstructions, Breakpoint = breakpoint, Buttons = buttons, Assertions = assertionValue }));
            }
        }
        if (frames > 3600 || instructions > MaximumSuiteInstructions || memoryBytes > DebugLimits.MemoryBytes || commands > DebugLimits.Commands ||
            conditionMemoryReads > RuntimeRunner.MaximumConditionMemoryReads || traceEntries > 4096)
            throw new CliException("INVALID_SUITE", "Suite aggregate budgets exceed 3600 frames, 10000000 instruction steps, 1048576 memory bytes, 1000 script commands, 16777216 condition memory reads, or 4096 trace entries.");

        cancellationToken.ThrowIfCancellationRequested();
        JsonObject? build = null;
        if (parsed.Has("build"))
        {
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (parsed.Has("overwrite")) options.Add("overwrite", null);
            if (parsed.Option("timeout-seconds") is { } seconds) options.Add("timeout-seconds", seconds);
            build = await BuildAsync(new ParsedCommand("build", "build", [root], options), cancellationToken, protectedInputs.ToArray());
            cachedRoms[buildRom!] = RomFile(buildRom!);
        }
        var results = new JsonArray();
        int passed = 0;
        foreach (var testCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rom = cachedRoms.TryGetValue(testCase.Rom, out var cached) ? cached : cachedRoms[buildRom!];
                var evidence = testCase.Script is { } script
                    ? ToObject(RuntimeRunner.Debug(rom, script, cancellationToken))
                    : ToObject(RuntimeRunner.Run(rom, testCase.Options!, cancellationToken));
                bool ok = !IsFalse(evidence["assertionsPassed"]) && !IsFalse(evidence["breakpointReached"]) && !IsFalse(evidence["conditionsPassed"]);
                if (ok) passed++;
                results.Add(new JsonObject { ["name"] = testCase.Name, ["rom"] = testCase.Rom, ["sha256"] = Sha256(rom), ["ok"] = ok, ["data"] = evidence });
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                results.Add(new JsonObject { ["name"] = testCase.Name, ["rom"] = testCase.Rom, ["ok"] = false, ["error"] = ToObject(new { code = exception is CliException cli ? cli.Code : "OPERATION_FAILED", message = exception.Message }) });
            }
        }
        var result = ToObject(new { suite = suitePath, total = cases.Count, passed, failed = cases.Count - passed, assertionsPassed = passed == cases.Count, budgets = new { frames, instructions, memoryBytes, commands, conditionMemoryReads, traceEntries } });
        result["results"] = results;
        if (build is not null) result["build"] = build;
        return result;
    }

    private static JsonElement SuiteJson(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new CliException("INVALID_SUITE", "Suite input files may not exceed 16 MiB.");
        return JsonFile(path);
    }

    private static void SuiteFields(JsonElement value, string[] allowed, string label)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CliException("INVALID_SUITE", $"{label} must be an object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !fields.Add(property.Name))
                throw new CliException("INVALID_SUITE", $"Unknown or duplicate {label} field {property.Name}.");
    }

    private static string SuiteString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw new CliException("INVALID_SUITE", $"{name} must be a nonempty string.");
        return field.GetString()!;
    }

    private static int SuiteInteger(JsonElement value, string name, int fallback, int minimum, int maximum)
    {
        if (!value.TryGetProperty(name, out var field))
        {
            if (fallback >= minimum && fallback <= maximum) return fallback;
            throw new CliException("INVALID_SUITE", $"Missing {name}.");
        }
        if (!JsonInteger.TryGetInt32(field, out int number) || number < minimum || number > maximum)
            throw new CliException("INVALID_SUITE", $"{name} must be an integer between {minimum} and {maximum}.");
        return number;
    }
}
