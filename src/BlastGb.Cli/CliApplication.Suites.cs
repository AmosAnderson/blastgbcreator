using System.Text.Json;
using System.Text.Json.Nodes;
using BlastGb.Runtime;
using BlastGb.Serialization;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private sealed record SuiteCase(string Name, JsonElement? Script, DebugScript? ValidatedScript,
        RunOptions? Run, Assertions? Expected);

    private static async Task<JsonObject> TestProjectAsync(ParsedCommand parsed, CancellationToken cancellationToken)
    {
        parsed.RequirePositionals(0, 1);
        parsed.AllowOptions("suite", "build", "overwrite", "report", "capture-dir", "symbols");
        var selected = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? ".");
        var projectPath = Directory.Exists(selected) ? Path.Combine(selected, ManifestFile) : selected;
        var project = LoadManifest(projectPath);
        var suitePath = parsed.Option("suite") is { } selectedSuite ? Path.GetFullPath(selectedSuite)
            : FileOperations.WorkspacePath(project.Root, project.Manifest.Tests ?? "tests.json", "tests");
        var suiteRoot = Path.GetDirectoryName(suitePath)!;
        var suiteInputs = new List<string> { suitePath };
        var symbolsPath = parsed.Option("symbols") is { } symbolsFile ? Path.GetFullPath(symbolsFile) : null;
        var symbols = symbolsPath is null ? null : LoadSymbols(symbolsPath);
        if (symbolsPath is not null) suiteInputs.Add(symbolsPath);
        long jsonBytes = 0;
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement ReadDocument(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileOperations.RejectSymbolicLink(path);
            if (documents.TryGetValue(path, out var cached)) return cached;
            var length = new FileInfo(path).Length;
            if (length > 16 * 1024 * 1024 - jsonBytes)
                throw new CliException("INVALID_SUITE", "Suite JSON files may total at most 16 MiB.");
            var value = JsonFile(path);
            jsonBytes += length;
            documents.Add(path, value);
            return value;
        }
        var suite = ReadDocument(suitePath);
        SuiteFields(suite, ["version", "cases"]);
        _ = SuiteInteger(SuiteRequired(suite, "version"), "version", 1, 1);
        var casesValue = SuiteRequired(suite, "cases");
        if (casesValue.ValueKind != JsonValueKind.Array || casesValue.GetArrayLength() is < 1 or > 32)
            throw new CliException("INVALID_SUITE", "A suite must contain between 1 and 32 cases.");
        var cases = new List<SuiteCase>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long frames = 0, instructions = 0, memory = 0, commands = 0, captures = 0;
        foreach (var item in casesValue.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SuiteFields(item, ["name", "script", "frames", "assert", "buttons"]);
            var name = SuiteString(SuiteRequired(item, "name"), "case name");
            if (!SuiteCaseName(name) || !names.Add(name))
                throw new CliException("INVALID_SUITE", "Case names must be unique safe 1–64 character labels, ignoring case; Windows device names are reserved.");
            try
            {
                if (item.TryGetProperty("script", out var scriptPathValue))
                {
                    if (item.TryGetProperty("frames", out _) || item.TryGetProperty("assert", out _) || item.TryGetProperty("buttons", out _))
                        throw new CliException("INVALID_SUITE", "A script case cannot also specify frames, assert, or buttons.");
                    var scriptPath = FileOperations.WorkspacePath(suiteRoot, SuiteString(scriptPathValue, "script"), "script");
                    suiteInputs.Add(scriptPath);
                    var script = ResolveRuntimeSymbols(ReadDocument(scriptPath), symbols);
                    var validated = RuntimeRunner.ValidateDebugScript(script, cancellationToken);
                    commands += validated.Commands.Count;
                    foreach (var command in validated.Commands)
                    {
                        switch (command)
                        {
                            case AdvanceCommand advance when advance.Op == "frame": frames += advance.Count; break;
                            case AdvanceCommand advance: instructions += advance.Count; break;
                            case RunCommand run: frames += run.Frames; instructions += run.MaxInstructions; break;
                            case UntilCommand until:
                                frames += until.Frames;
                                instructions += until.MaxInstructions;
                                memory += until.Expected.Memory.Sum(check => (long)check.Bytes.Length) * (until.MaxInstructions + 2L);
                                break;
                            case ReadCommand read: memory += read.Length; break;
                            case WriteCommand write: memory += write.Bytes.Length; break;
                            case AssertCommand assert: memory += assert.Expected.Memory.Sum(check => (long)check.Bytes.Length); break;
                            case CaptureCommand: captures++; break;
                        }
                    }
                    cases.Add(new(name, script, validated, null, null));
                }
                else
                {
                    var caseFrames = SuiteInteger(SuiteRequired(item, "frames"), "frames", 1, DebugLimits.Frames);
                    var assertionPath = FileOperations.WorkspacePath(suiteRoot, SuiteString(SuiteRequired(item, "assert"), "assert"), "assert");
                    suiteInputs.Add(assertionPath);
                    var assertion = ResolveRuntimeSymbols(ReadDocument(assertionPath), symbols);
                    var expected = RuntimeRunner.NormalizeAssertions(assertion, cancellationToken);
                    var buttons = Array.Empty<string>();
                    if (item.TryGetProperty("buttons", out var pressed))
                    {
                        var buttonScript = JsonSerializer.SerializeToElement(new { version = 1, commands = new[] { new { op = "buttons", pressed } } });
                        buttons = ((ButtonsCommand)RuntimeRunner.ValidateDebugScript(buttonScript, cancellationToken).Commands[0]).Pressed;
                    }
                    frames += caseFrames;
                    memory += expected.Memory.Sum(check => (long)check.Bytes.Length);
                    cases.Add(new(name, null, null, new RunOptions { Frames = caseFrames, Assertions = assertion, Buttons = buttons }, expected));
                }
            }
            catch (ArgumentException exception)
            {
                throw new CliException("INVALID_SUITE", $"Case {name}: {exception.Message}", exception);
            }
        }
        if (frames > DebugLimits.Frames || instructions > DebugLimits.Instructions || memory > DebugLimits.MemoryBytes || commands > DebugLimits.Commands || captures > DebugLimits.Captures)
            throw new CliException("INVALID_SUITE", "Suite aggregate budgets exceed 3600 frames, 1000000 instructions, 1048576 memory bytes, 1000 script commands, or 32 explicit captures.");

        var input = FileOperations.WorkspacePath(project.Root, project.Manifest.Output, "output");
        var protectedInputs = new List<string> { project.Path, input };
        protectedInputs.AddRange(project.Manifest.InputPaths().Select(path => FileOperations.WorkspacePath(project.Root, path.Value, path.Role)));
        foreach (var asset in project.Manifest.Assets ?? [])
            protectedInputs.AddRange(asset.Outputs().Select(output => FileOperations.WorkspacePath(project.Root, output.Path, "asset " + output.Role)));
        protectedInputs.AddRange(suiteInputs);
        if (project.Manifest.Kind != "game") protectedInputs.AddRange([Path.ChangeExtension(input, ".sym"), Path.ChangeExtension(input, ".map")]);
        if (project.Manifest.Receipt is { } receipt) protectedInputs.Add(FileOperations.WorkspacePath(project.Root, receipt, "receipt"));
        var overwrite = parsed.Has("overwrite");
        var reportPath = parsed.Option("report") is { } report ? Path.GetFullPath(report) : null;
        var captureRoot = parsed.Option("capture-dir") is { } captureDirectory ? Path.GetFullPath(captureDirectory) : null;
        var existingOutputsConfirmed = false;
        string? CaptureDirectory(SuiteCase item) => captureRoot is null ? null : FileOperations.WorkspacePath(captureRoot, item.Name, "case capture directory");
        void PreflightOutputs()
        {
            var planned = new List<string>();
            if (reportPath is not null)
            {
                FileOperations.ProtectDestination(reportPath, protectedInputs);
                FileOperations.AssertWritable(reportPath, overwrite);
                planned.Add(reportPath);
            }
            foreach (var item in cases)
            {
                var directory = CaptureDirectory(item);
                if (directory is null) continue;
                var screenshot = Path.Combine(directory, "_final.png");
                var captureNames = item.ValidatedScript is { } script ? DebugCaptureNames(script).ToArray()
                    : item.Expected?.FramebufferSha256 is not null ? new[] { "failure" } : [];
                PreflightCaptures(screenshot, directory, captureNames, protectedInputs, overwrite);
                planned.Add(screenshot);
                planned.AddRange(captureNames.Select(name => Path.Combine(directory, name + ".png")));
            }
            if (planned.Select(FileOperations.CanonicalPath).Distinct(StringComparer.Ordinal).Count() != planned.Count)
                throw new CliException("UNSAFE_OUTPUT", "Suite report and capture destinations must be distinct.");
            if (!parsed.Has("build") && project.Manifest.Kind != "game" && !existingOutputsConfirmed && planned.Any(File.Exists))
            {
                var receiptPath = project.Manifest.Receipt is { } relative ? FileOperations.WorkspacePath(project.Root, relative, "receipt") : null;
                if (receiptPath is null || !File.Exists(receiptPath))
                    throw new CliException("UNSAFE_OUTPUT", "Replacing suite reports or captures for an assembly workspace requires --build or a fresh manifest receipt to protect assembler inputs.");
                var previous = BuildReceipt.Read(receiptPath);
                if (previous.Check(project.Path).Count != 0)
                    throw new CliException("UNSAFE_OUTPUT", "The manifest build receipt is stale. Use --build before replacing suite reports or captures so assembler inputs can be protected.");
                protectedInputs.AddRange(previous.Inputs.Concat(previous.Outputs).Select(file => file.Path));
                existingOutputsConfirmed = true;
            }
            foreach (var path in planned) FileOperations.ProtectDestination(path, protectedInputs);
        }
        PreflightOutputs();
        JsonObject? build = null;
        if (parsed.Has("build"))
        {
            var options = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (overwrite) options.Add("overwrite", null);
            build = await BuildAsync(new ParsedCommand("build", "build", [project.Path], options), cancellationToken, suiteInputs,
                discovered => { protectedInputs.AddRange(discovered); PreflightOutputs(); });
        }
        var rom = RomFile(input);
        var artifacts = new List<FileOperations.Artifact>();
        var evidence = new JsonArray();
        var passed = 0;
        foreach (var item in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new JsonObject { ["name"] = item.Name, ["kind"] = item.Script is null ? "run" : "script" };
            JsonObject? data = null;
            byte[] pixels = [];
            IReadOnlyList<FrameCapture> caseCaptures = [];
            bool valid;
            try
            {
                if (item.Script is { } script)
                {
                    var result = RuntimeRunner.Debug(rom, script, cancellationToken);
                    data = ToObject(result);
                    pixels = result.FramebufferRgba;
                    caseCaptures = result.Captures;
                    valid = result.AssertionsPassed && result.Completed;
                }
                else
                {
                    var result = RuntimeRunner.Run(rom, item.Run!, cancellationToken);
                    data = ToObject(result);
                    pixels = result.FramebufferRgba;
                    valid = result.AssertionsPassed;
                    if (result.Assertions.Any(check => check.Kind == "framebuffer" && !check.Passed))
                        caseCaptures = [new FrameCapture("failure", pixels, result.FramebufferSha256)];
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                valid = false;
                entry["error"] = ToObject(new { code = exception is CliException cli ? cli.Code : "OPERATION_FAILED", message = exception.Message });
            }
            if (data is not null)
            {
                var directory = CaptureDirectory(item);
                artifacts.AddRange(CaptureArtifacts(pixels, caseCaptures, directory is null ? null : Path.Combine(directory, "_final.png"),
                    directory, protectedInputs, overwrite, data));
                entry["data"] = data;
            }
            entry["valid"] = valid;
            if (valid) passed++;
            evidence.Add(entry);
        }
        var summary = ToObject(new { project = project.Path, suite = suitePath, input, sha256 = Sha256(rom), valid = passed == cases.Count, total = cases.Count, passed, failed = cases.Count - passed });
        summary["cases"] = evidence;
        if (build is not null) summary["build"] = build;
        if (reportPath is not null)
        {
            summary["report"] = reportPath;
            artifacts.Add(new(reportPath, JsonText(summary)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var artifact in artifacts) FileOperations.ProtectDestination(artifact.Path, protectedInputs);
        FileOperations.AtomicWriteBatch(artifacts, overwrite);
        return summary;
    }

    private static void SuiteFields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CliException("INVALID_SUITE", "Suite and case definitions must be objects.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new CliException("INVALID_SUITE", $"Unknown or duplicate suite field {property.Name}.");
    }

    private static JsonElement SuiteRequired(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        ? property : throw new CliException("INVALID_SUITE", $"Missing suite field {name}.");

    private static string SuiteString(JsonElement value, string label) => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : throw new CliException("INVALID_SUITE", $"Suite {label} must be a nonempty string.");

    private static int SuiteInteger(JsonElement value, string label, int minimum, int maximum) => JsonInteger.TryGetInt32(value, out var number) && number >= minimum && number <= maximum
        ? number : throw new CliException("INVALID_SUITE", $"Suite {label} must be an integer between {minimum} and {maximum}.");

    private static bool SuiteCaseName(string name)
    {
        if (name.Length > 64 || !char.IsAsciiLetterOrDigit(name[0]) || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return false;
        var upper = name.ToUpperInvariant();
        return upper is not ("CON" or "PRN" or "AUX" or "NUL") && !(upper.Length == 4 &&
            (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] is >= '1' and <= '9');
    }
}
