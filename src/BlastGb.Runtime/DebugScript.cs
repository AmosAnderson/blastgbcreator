using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BlastGb.Runtime;

public static class DebugLimits
{
    public const int Commands = 1000;
    public const int Frames = 3600;
    public const int Instructions = 1_000_000;
    public const int MemoryBytes = 1_048_576;
    public const int Breakpoints = 256;
    public const int Captures = 32;
}

public abstract record DebugCommand(string Op);
public sealed record AdvanceCommand(string Operation, int Count) : DebugCommand(Operation);
public sealed record ReadCommand(int Address, int Length) : DebugCommand("read");
public sealed record WriteCommand(int Address, int[] Bytes) : DebugCommand("write");
public sealed record BreakpointsCommand(int[] Addresses) : DebugCommand("breakpoints");
public sealed record RunCommand(int Frames, int MaxInstructions) : DebugCommand("run")
{
    public Assertions? Until { get; init; }
    public int TraceCapacity { get; init; }
}
public sealed record ButtonsCommand(string[] Pressed) : DebugCommand("buttons");
public sealed record AssertCommand(Assertions Expected) : DebugCommand("assert");
public sealed record UntilCommand(int Frames, int MaxInstructions, Assertions Expected) : DebugCommand("until");
public sealed record CaptureCommand(string Name) : DebugCommand("capture");
public sealed record DebugScript(int Version, IReadOnlyList<DebugCommand> Commands);
public sealed record DebugResult(
    string Engine, double Ticks, CpuRegisters Registers, int[] Breakpoints,
    double ElapsedTicks, string StopReason, int RequestedCommands, bool Completed,
    IReadOnlyList<JsonObject> Results, bool AssertionsPassed, string FramebufferSha256,
    IReadOnlyList<string> Diagnostics)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConditionsPassed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeProvenance? Provenance { get; init; }
    [JsonIgnore]
    public byte[]? Framebuffer { get; init; }
    [JsonIgnore]
    public byte[]? BatterySave { get; init; }
    [JsonIgnore]
    public IReadOnlyList<FrameCapture> Captures { get; init; } = [];
    [JsonIgnore]
    public byte[] FramebufferRgba { get; init; } = [];
}

public static partial class RuntimeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Validates the entire script and aggregate budgets before creating a CPU.</summary>
    public static DebugScript ValidateDebugScript(JsonElement input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFields(input, ["version", "commands"], "debug script");
        _ = Integer(Required(input, "version"), "Debug script version", 1, 1);
        var operations = Required(input, "commands");
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > DebugLimits.Commands)
            throw new ArgumentException($"Debug script commands must contain between 1 and {DebugLimits.Commands} operations.");
        int frameBudget = 0, instructionBudget = 0;
        long memoryBytes = 0, conditionMemoryReads = 0;
        int traceEntries = 0;
        var commands = new List<DebugCommand>();
        var captureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in operations.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Command {commands.Count} must be an object.");
            var operation = Required(command, "op");
            string? op = operation.ValueKind == JsonValueKind.String ? operation.GetString() : null;
            switch (op)
            {
                case "step":
                case "frame":
                    ValidateFields(command, ["op", "count"], op);
                    int count = OptionalInteger(command, "count", 1, 1, op == "step" ? DebugLimits.Instructions : DebugLimits.Frames);
                    if (op == "step") instructionBudget += count;
                    else frameBudget += count;
                    commands.Add(new AdvanceCommand(op, count));
                    break;
                case "read":
                case "write":
                    ValidateFields(command, op == "read" ? ["op", "address", "length"] : ["op", "address", "bytes"], op);
                    int address = Integer(Required(command, "address"), $"{op} address", 0, 65535);
                    if (op == "read")
                    {
                        int length = OptionalInteger(command, "length", 1, 1, 65536 - address);
                        memoryBytes += length;
                        commands.Add(new ReadCommand(address, length));
                    }
                    else
                    {
                        var bytes = ByteArray(Required(command, "bytes"), "write bytes", cancellationToken);
                        EmulatorSession.ValidateMemoryRange(address, bytes.Length);
                        memoryBytes += bytes.Length;
                        commands.Add(new WriteCommand(address, bytes));
                    }
                    break;
                case "breakpoints":
                    ValidateFields(command, ["op", "addresses"], op);
                    var addresses = Required(command, "addresses");
                    if (addresses.ValueKind != JsonValueKind.Array || addresses.GetArrayLength() > DebugLimits.Breakpoints)
                        throw new ArgumentException($"breakpoints addresses must be an array of at most {DebugLimits.Breakpoints} mapped CPU addresses.");
                    commands.Add(new BreakpointsCommand(addresses.EnumerateArray()
                        .Select(value => Integer(value, "breakpoint address", 0, 65535)).Distinct().ToArray()));
                    break;
                case "buttons":
                    ValidateFields(command, ["op", "pressed"], op);
                    commands.Add(new ButtonsCommand(NormalizeButtons(Required(command, "pressed"))));
                    break;
                case "run":
                    ValidateFields(command, ["op", "frames", "maxInstructions", "until", "traceCapacity"], op);
                    int frames = OptionalInteger(command, "frames", 60, 1, DebugLimits.Frames);
                    int instructions = OptionalInteger(command, "maxInstructions", 100_000, 1, DebugLimits.Instructions);
                    frameBudget += frames;
                    instructionBudget += instructions;
                    var until = command.TryGetProperty("until", out var condition) ? NormalizeUntil(condition, instructions, cancellationToken) : null;
                    int traceCapacity = OptionalInteger(command, "traceCapacity", 0, 0, MaximumTraceCapacity);
                    traceEntries += traceCapacity;
                    if (until is not null)
                    {
                        memoryBytes += until.Memory.Sum(item => (long)item.Bytes.Length);
                        conditionMemoryReads += until.Memory.Sum(item => (long)item.Bytes.Length) * (instructions + 1L);
                    }
                    commands.Add(new RunCommand(frames, instructions) { Until = until, TraceCapacity = traceCapacity });
                    break;
                case "until":
                    ValidateFields(command, ["op", "frames", "maxInstructions", "memory", "registers"], op);
                    int untilFrames = OptionalInteger(command, "frames", 60, 1, DebugLimits.Frames);
                    int untilInstructions = OptionalInteger(command, "maxInstructions", 100_000, 1, DebugLimits.Instructions);
                    var conditions = CommandAssertions(command, cancellationToken);
                    frameBudget += untilFrames;
                    instructionBudget += untilInstructions;
                    // Reserve every possible condition read, including initial and final evidence checks.
                    // Otherwise a small script could perform billions of mapped bus reads.
                    long untilMemoryReads = conditions.Memory.Sum(item => (long)item.Bytes.Length) * (untilInstructions + 2L);
                    memoryBytes += untilMemoryReads;
                    conditionMemoryReads += untilMemoryReads;
                    commands.Add(new UntilCommand(untilFrames, untilInstructions, conditions));
                    break;
                case "capture":
                    ValidateFields(command, ["op", "name"], op);
                    var nameValue = Required(command, "name");
                    var name = nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
                    if (!ValidCaptureName(name)) throw new ArgumentException("Capture name must be a safe 1–64 character label; device names and failure-N labels are reserved.");
                    if (!captureNames.Add(name!)) throw new ArgumentException($"Duplicate capture name {name}.");
                    if (captureNames.Count > DebugLimits.Captures) throw new ArgumentException($"A script may capture at most {DebugLimits.Captures} frames.");
                    commands.Add(new CaptureCommand(name!));
                    break;
                case "assert":
                    ValidateFields(command, ["op", "memory", "registers", "framebufferSha256"], op);
                    var expected = CommandAssertions(command, cancellationToken);
                    memoryBytes += expected.Memory.Sum(item => (long)item.Bytes.Length);
                    commands.Add(new AssertCommand(expected));
                    break;
                default:
                    throw new ArgumentException($"Unknown debug operation {op ?? operation.GetRawText()} at command {commands.Count}.");
            }
        }
        if (traceEntries > 4096) throw new ArgumentException("The script trace capacities may total at most 4096 entries.");
        if (conditionMemoryReads > MaximumConditionMemoryReads) throw new ArgumentException($"The script run-condition memory reads may total at most {MaximumConditionMemoryReads} bytes.");
        if (frameBudget > DebugLimits.Frames) throw new ArgumentException($"The script frame budgets may total at most {DebugLimits.Frames}.");
        if (instructionBudget > DebugLimits.Instructions) throw new ArgumentException($"The script instruction budgets may total at most {DebugLimits.Instructions}.");
        if (memoryBytes > DebugLimits.MemoryBytes) throw new ArgumentException($"The script read, write, and assertion memory may total at most {DebugLimits.MemoryBytes} bytes.");
        return new(1, commands);
    }

    private static Assertions CommandAssertions(JsonElement command, CancellationToken cancellationToken)
    {
        var assertion = new JsonObject();
        foreach (var name in new[] { "memory", "registers", "framebufferSha256" })
            if (command.TryGetProperty(name, out var value)) assertion[name] = JsonNode.Parse(value.GetRawText());
        return NormalizeAssertions(JsonSerializer.SerializeToElement(assertion), cancellationToken);
    }

    private static bool ValidCaptureName(string? name)
    {
        if (name is null || name.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(name[0]) ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return false;
        var upper = name.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || upper.Length == 4 &&
            (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] is >= '1' and <= '9') return false;
        return !upper.StartsWith("FAILURE-", StringComparison.Ordinal) || upper.Length == 8 || !upper[8..].All(char.IsAsciiDigit);
    }

    /// <summary>Runs an ordered script in one isolated session, stopping at failed expectations.</summary>
    public static DebugResult Debug(byte[] rom, JsonElement scriptValue, CancellationToken cancellationToken = default) =>
        Debug(rom, scriptValue, new DebugOptions(), cancellationToken);

    public static DebugResult Debug(byte[] rom, JsonElement scriptValue, DebugOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var script = ValidateDebugScript(scriptValue, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = new EmulatorSession(rom);
        if (options.BatterySave is not null) session.ImportBatterySave(options.BatterySave, cancellationToken);
        if (options.CaptureBatterySave) _ = session.ExportBatterySave(cancellationToken);
        double initialTicks = session.Ticks;
        var results = new List<JsonObject>();
        var captures = new List<FrameCapture>();
        bool assertionsPassed = true;
        bool? conditionsPassed = null;
        var heldButtons = new HashSet<string>(StringComparer.Ordinal);
        string stopReason = "ready";
        int? resumeBreakpoint = null;
        bool StopAtCurrentBreakpoint()
        {
            var state = session.Snapshot();
            int pc = state.Registers.PC;
            if (state.Breakpoints.Contains(pc) && resumeBreakpoint != pc)
            {
                resumeBreakpoint = pc;
                return true;
            }
            resumeBreakpoint = null;
            return false;
        }
        void AddSnapshot(JsonObject data)
        {
            var state = session.Snapshot();
            data["ticks"] = state.Ticks;
            data["registers"] = JsonSerializer.SerializeToNode(state.Registers, JsonOptions);
            data["breakpoints"] = JsonSerializer.SerializeToNode(state.Breakpoints);
        }
        FrameCapture Capture(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rgba = session.Framebuffer();
            var capture = new FrameCapture(name, rgba, FrameHash(rgba));
            captures.Add(capture);
            return capture;
        }
        foreach (var command in script.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double startTicks = session.Ticks;
            var data = new JsonObject { ["index"] = results.Count, ["op"] = command.Op };
            switch (command)
            {
                case ReadCommand read:
                    data["address"] = read.Address;
                    data["bytes"] = JsonSerializer.SerializeToNode(session.ReadMemory(read.Address, read.Length, cancellationToken).Select(value => (int)value));
                    break;
                case WriteCommand write:
                    session.WriteMemory(write.Address, write.Bytes.Select(value => (byte)value).ToArray(), cancellationToken);
                    data["address"] = write.Address;
                    data["bytes"] = JsonSerializer.SerializeToNode(write.Bytes);
                    break;
                case ButtonsCommand buttons:
                    foreach (string button in heldButtons.Except(buttons.Pressed, StringComparer.Ordinal)) session.SetButton(button, false);
                    foreach (string button in buttons.Pressed.Except(heldButtons, StringComparer.Ordinal)) session.SetButton(button, true);
                    heldButtons = buttons.Pressed.ToHashSet(StringComparer.Ordinal);
                    data["pressed"] = JsonSerializer.SerializeToNode(buttons.Pressed);
                    break;
                case BreakpointsCommand points:
                    session.SetBreakpoints(points.Addresses);
                    resumeBreakpoint = null;
                    data["addresses"] = JsonSerializer.SerializeToNode(session.Snapshot().Breakpoints);
                    break;
                case CaptureCommand capture:
                    var captured = Capture(capture.Name);
                    data["name"] = captured.Name;
                    data["framebufferSha256"] = captured.Sha256;
                    AddSnapshot(data);
                    break;
                case AdvanceCommand advance:
                    {
                        int count = 0, frames = 0;
                        bool stopped = advance.Op == "frame" && StopAtCurrentBreakpoint();
                        if (advance.Op == "step") resumeBreakpoint = null;
                        stopReason = stopped ? "breakpoint" : "target";
                        while (!stopped && count < advance.Count)
                        {
                            var state = session.Advance(advance.Op == "step" ? 1 : EmulatorSession.FrameTicks, () => frames++, cancellationToken: cancellationToken);
                            count++;
                            if (state.StopReason == "breakpoint")
                            {
                                stopReason = "breakpoint";
                                resumeBreakpoint = state.Registers.PC;
                                break;
                            }
                        }
                        data["requestedCount"] = advance.Count;
                        data["count"] = count;
                        data["frames"] = frames;
                        data["stopReason"] = stopReason;
                        AddSnapshot(data);
                        break;
                    }
                case RunCommand run:
                    {
                        double target = startTicks + run.Frames * EmulatorSession.FrameTicks;
                        int instructions = 0, frames = 0;
                        var trace = new ExecutionTrace(run.TraceCapacity);
                        bool untilReached = run.Until is not null && CheckAssertions(session, run.Until, cancellationToken).All(check => check.Passed);
                        bool stopped = !untilReached && StopAtCurrentBreakpoint();
                        stopReason = untilReached ? "condition" : stopped ? "breakpoint" : "frame-budget";
                        while (!stopped && !untilReached && session.Ticks < target)
                        {
                            if (instructions >= run.MaxInstructions) { stopReason = "instruction-budget"; break; }
                            var state = session.Advance(1, () => frames++, cancellationToken: cancellationToken);
                            instructions++;
                            trace.Add(session);
                            if (run.Until is not null && CheckAssertions(session, run.Until, cancellationToken).All(check => check.Passed))
                            {
                                untilReached = true;
                                stopReason = "condition";
                            }
                            if (state.StopReason == "breakpoint")
                            {
                                stopReason = "breakpoint";
                                resumeBreakpoint = state.Registers.PC;
                                break;
                            }
                        }
                        if (run.Until is not null)
                        {
                            conditionsPassed = (conditionsPassed ?? true) && untilReached;
                            data["untilReached"] = untilReached;
                        }
                        if (run.TraceCapacity > 0) data["trace"] = JsonSerializer.SerializeToNode(trace.ToArray(), JsonOptions);
                        data["requestedFrames"] = run.Frames;
                        data["frames"] = frames;
                        data["instructionSteps"] = instructions;
                        data["stopReason"] = stopReason;
                        AddSnapshot(data);
                        break;
                    }
                case AssertCommand assert:
                    var checks = CheckAssertions(session, assert.Expected, cancellationToken);
                    bool passed = checks.All(check => check.Passed);
                    assertionsPassed &= passed;
                    data["assertions"] = JsonSerializer.SerializeToNode(checks, JsonOptions);
                    data["assertionsPassed"] = passed;
                    if (!passed)
                    {
                        stopReason = "assertion-failed";
                        if (checks.Any(check => check.Kind == "framebuffer" && !check.Passed) && captures.Count < DebugLimits.Captures)
                        {
                            var failure = Capture($"failure-{results.Count}");
                            data["capture"] = failure.Name;
                        }
                    }
                    break;
                case UntilCommand until:
                    {
                        double target = startTicks + until.Frames * EmulatorSession.FrameTicks;
                        int instructions = 0, frames = 0;
                        bool matched = ConditionsMatch(session, until.Expected, cancellationToken);
                        bool stopped = !matched && StopAtCurrentBreakpoint();
                        stopReason = matched ? "condition" : stopped ? "breakpoint" : "frame-budget";
                        while (!matched && !stopped && session.Ticks < target)
                        {
                            if (instructions >= until.MaxInstructions) { stopReason = "instruction-budget"; break; }
                            var state = session.Advance(1, () => frames++, cancellationToken: cancellationToken);
                            instructions++;
                            matched = ConditionsMatch(session, until.Expected, cancellationToken);
                            if (state.StopReason == "breakpoint")
                            {
                                resumeBreakpoint = state.Registers.PC;
                                stopped = true;
                                stopReason = "breakpoint";
                            }
                            if (matched) stopReason = "condition";
                        }
                        assertionsPassed &= matched;
                        data["requestedFrames"] = until.Frames;
                        data["frames"] = frames;
                        data["instructionSteps"] = instructions;
                        data["conditionReached"] = matched;
                        data["assertions"] = JsonSerializer.SerializeToNode(CheckAssertions(session, until.Expected, cancellationToken), JsonOptions);
                        data["assertionsPassed"] = matched;
                        data["stopReason"] = stopReason;
                        AddSnapshot(data);
                        break;
                    }
            }
            data["elapsedTicks"] = session.Ticks - startTicks;
            results.Add(data);
            cancellationToken.ThrowIfCancellationRequested();
            if (!assertionsPassed || conditionsPassed == false) break;
        }
        var snapshot = session.Snapshot();
        var framebufferRgba = session.Framebuffer();
        return new("binjgb", snapshot.Ticks, snapshot.Registers, snapshot.Breakpoints, session.Ticks - initialTicks,
            stopReason, script.Commands.Count, results.Count == script.Commands.Count && conditionsPassed != false, results, assertionsPassed,
            FrameHash(framebufferRgba), session.Diagnostics.ToArray())
        {
            ConditionsPassed = conditionsPassed,
            Provenance = options.IncludeProvenance ? EmulatorSession.Provenance : null,
            Framebuffer = options.CaptureFramebuffer ? framebufferRgba : null,
            BatterySave = options.CaptureBatterySave ? session.ExportBatterySave(cancellationToken) : null,
            Captures = captures,
            FramebufferRgba = framebufferRgba
        };
    }
}
