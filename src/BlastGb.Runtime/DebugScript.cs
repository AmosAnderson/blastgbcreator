using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlastGb.Runtime;

public static class DebugLimits
{
    public const int Commands = 1000;
    public const int Frames = 3600;
    public const int Instructions = 1_000_000;
    public const int MemoryBytes = 1_048_576;
    public const int Breakpoints = 256;
}

public abstract record DebugCommand(string Op);
public sealed record AdvanceCommand(string Operation, int Count) : DebugCommand(Operation);
public sealed record ReadCommand(int Address, int Length) : DebugCommand("read");
public sealed record WriteCommand(int Address, int[] Bytes) : DebugCommand("write");
public sealed record BreakpointsCommand(int[] Addresses) : DebugCommand("breakpoints");
public sealed record RunCommand(int Frames, int MaxInstructions) : DebugCommand("run");
public sealed record AssertCommand(Assertions Expected) : DebugCommand("assert");
public sealed record DebugScript(int Version, IReadOnlyList<DebugCommand> Commands);
public sealed record DebugResult(
    string Engine, double Ticks, CpuRegisters Registers, int[] Breakpoints,
    double ElapsedTicks, string StopReason, int RequestedCommands, bool Completed,
    IReadOnlyList<JsonObject> Results, bool AssertionsPassed, string FramebufferSha256,
    IReadOnlyList<string> Diagnostics);

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
        long memoryBytes = 0;
        var commands = new List<DebugCommand>();
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
                case "run":
                    ValidateFields(command, ["op", "frames", "maxInstructions"], op);
                    int frames = OptionalInteger(command, "frames", 60, 1, DebugLimits.Frames);
                    int instructions = OptionalInteger(command, "maxInstructions", 100_000, 1, DebugLimits.Instructions);
                    frameBudget += frames;
                    instructionBudget += instructions;
                    commands.Add(new RunCommand(frames, instructions));
                    break;
                case "assert":
                    ValidateFields(command, ["op", "memory", "registers"], op);
                    var assertion = new JsonObject();
                    if (command.TryGetProperty("memory", out var memory)) assertion["memory"] = JsonNode.Parse(memory.GetRawText());
                    if (command.TryGetProperty("registers", out var registers)) assertion["registers"] = JsonNode.Parse(registers.GetRawText());
                    var expected = NormalizeAssertions(JsonSerializer.SerializeToElement(assertion), cancellationToken);
                    memoryBytes += expected.Memory.Sum(item => (long)item.Bytes.Length);
                    commands.Add(new AssertCommand(expected));
                    break;
                default:
                    throw new ArgumentException($"Unknown debug operation {op ?? operation.GetRawText()} at command {commands.Count}.");
            }
        }
        if (frameBudget > DebugLimits.Frames) throw new ArgumentException($"The script frame budgets may total at most {DebugLimits.Frames}.");
        if (instructionBudget > DebugLimits.Instructions) throw new ArgumentException($"The script instruction budgets may total at most {DebugLimits.Instructions}.");
        if (memoryBytes > DebugLimits.MemoryBytes) throw new ArgumentException($"The script read, write, and assertion memory may total at most {DebugLimits.MemoryBytes} bytes.");
        return new(1, commands);
    }

    /// <summary>Runs an ordered script in one isolated session, stopping at failed expectations.</summary>
    public static DebugResult Debug(byte[] rom, JsonElement scriptValue, CancellationToken cancellationToken = default)
    {
        var script = ValidateDebugScript(scriptValue, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var session = new EmulatorSession(rom);
        double initialTicks = session.Ticks;
        var results = new List<JsonObject>();
        bool assertionsPassed = true;
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
                case BreakpointsCommand points:
                    session.SetBreakpoints(points.Addresses);
                    resumeBreakpoint = null;
                    data["addresses"] = JsonSerializer.SerializeToNode(session.Snapshot().Breakpoints);
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
                        bool stopped = StopAtCurrentBreakpoint();
                        stopReason = stopped ? "breakpoint" : "frame-budget";
                        while (!stopped && session.Ticks < target)
                        {
                            if (instructions >= run.MaxInstructions) { stopReason = "instruction-budget"; break; }
                            var state = session.Advance(1, () => frames++, cancellationToken: cancellationToken);
                            instructions++;
                            if (state.StopReason == "breakpoint")
                            {
                                stopReason = "breakpoint";
                                resumeBreakpoint = state.Registers.PC;
                                break;
                            }
                        }
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
                    if (!passed) stopReason = "assertion-failed";
                    break;
            }
            data["elapsedTicks"] = session.Ticks - startTicks;
            results.Add(data);
            cancellationToken.ThrowIfCancellationRequested();
            if (!assertionsPassed) break;
        }
        var snapshot = session.Snapshot();
        return new("binjgb", snapshot.Ticks, snapshot.Registers, snapshot.Breakpoints, session.Ticks - initialTicks,
            stopReason, script.Commands.Count, results.Count == script.Commands.Count, results, assertionsPassed,
            session.FramebufferSha256(), session.Diagnostics.ToArray());
    }
}
