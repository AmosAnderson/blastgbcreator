using System.Text.Json;
using System.Text.Json.Serialization;
using BlastGb.Serialization;

namespace BlastGb.Runtime;

public sealed record MemoryRange(int Address, int Length);
public sealed record MemoryAssertion(int Address, int[] Bytes);
public sealed record Assertions(IReadOnlyList<MemoryAssertion> Memory, IReadOnlyDictionary<string, int> Registers);
public sealed record RunOptions
{
    public int Frames { get; init; } = 60;
    public IReadOnlyList<MemoryRange> Memory { get; init; } = [];
    public JsonElement? Assertions { get; init; }
    public IReadOnlyList<string> Buttons { get; init; } = [];
    public int? Breakpoint { get; init; }
    public int MaxInstructions { get; init; } = 1_000_000;
}

public sealed record AssertionCheck(string Kind, object Expected, object Actual, bool Passed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Register { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Address { get; init; }
}
public sealed record MemoryResult(int Address, int Length, int[] Bytes);
public sealed record RunResult(
    string Engine, int RequestedFrames, int Frames, double Ticks, double ElapsedTicks,
    CpuRegisters Registers, string StopReason, IReadOnlyList<MemoryResult> Memory,
    string FramebufferSha256, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<AssertionCheck> Assertions, bool AssertionsPassed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Breakpoint { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BreakpointReached { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InstructionSteps { get; init; }
}

/// <summary>Bounded execution and assertions with no filesystem or console dependencies.</summary>
public static partial class RuntimeRunner
{
    private static readonly HashSet<string> RegisterNames = new(StringComparer.Ordinal) { "a", "f", "af", "bc", "de", "hl", "sp", "pc" };
    private static readonly HashSet<string> ButtonNames = new(StringComparer.Ordinal) { "up", "down", "left", "right", "A", "B", "start", "select" };

    public static Assertions NormalizeAssertions(JsonElement? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null) return new([], new Dictionary<string, int>());
        var input = value.Value;
        ValidateFields(input, ["memory", "registers"], "assertion");
        var memory = new List<MemoryAssertion>();
        if (input.TryGetProperty("memory", out var expectedMemory))
        {
            if (expectedMemory.ValueKind != JsonValueKind.Array) throw new ArgumentException("Assertion memory must be an array.");
            foreach (var item in expectedMemory.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFields(item, ["address", "bytes"], "memory assertion");
                int address = Integer(Required(item, "address"), "assertion address", 0, 65535);
                int[] bytes = ByteArray(Required(item, "bytes"), "assertion bytes", cancellationToken);
                EmulatorSession.ValidateMemoryRange(address, bytes.Length);
                memory.Add(new(address, bytes));
            }
        }
        var registers = new Dictionary<string, int>(StringComparer.Ordinal);
        if (input.TryGetProperty("registers", out var expectedRegisters))
        {
            if (expectedRegisters.ValueKind != JsonValueKind.Object) throw new ArgumentException("Assertion registers must be an object.");
            foreach (var register in expectedRegisters.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = register.Name.ToLowerInvariant();
                if (!RegisterNames.Contains(key)) throw new ArgumentException($"Invalid register assertion {register.Name}. Use A, F, AF, BC, DE, HL, SP, or PC.");
                int number = Integer(register.Value, $"register assertion {register.Name}", 0, key is "a" or "f" ? 255 : 65535);
                if (!registers.TryAdd(key, number)) throw new ArgumentException($"Duplicate register assertion {register.Name}.");
            }
        }
        if (memory.Count == 0 && registers.Count == 0) throw new ArgumentException("An assertion file must contain at least one memory or register assertion.");
        return new(memory, registers);
    }

    public static IReadOnlyList<AssertionCheck> CheckAssertions(EmulatorSession session, Assertions expected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expected);
        var registers = session.Registers;
        var checks = new List<AssertionCheck>();
        foreach (var (name, value) in expected.Registers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checks.Add(new("register", value, registers[name], registers[name] == value) { Register = name });
        }
        foreach (var item in expected.Memory)
        {
            int[] actual = session.ReadMemory(item.Address, item.Bytes.Length, cancellationToken).Select(value => (int)value).ToArray();
            checks.Add(new("memory", item.Bytes, actual, actual.SequenceEqual(item.Bytes)) { Address = item.Address });
        }
        return checks;
    }

    public static RunResult Run(byte[] rom, RunOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        var expected = NormalizeAssertions(options.Assertions, cancellationToken);
        if (options.Frames is < 1 or > 3600) throw new ArgumentException("frames must be an integer between 1 and 3600.");
        if (options.MaxInstructions is < 1 or > 10_000_000) throw new ArgumentException("max-instructions must be an integer between 1 and 10000000.");
        if (options.Breakpoint is < 0 or > 65535) throw new ArgumentException("Breakpoints require a mapped 16-bit CPU address.");
        ArgumentNullException.ThrowIfNull(options.Buttons);
        ArgumentNullException.ThrowIfNull(options.Memory);
        foreach (string button in options.Buttons)
            if (!ButtonNames.Contains(button)) throw new ArgumentException($"Unknown button {button}. Choose {string.Join(", ", ButtonNames)}.");
        long memoryBytes = 0;
        foreach (var range in options.Memory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(range);
            EmulatorSession.ValidateMemoryRange(range.Address, range.Length);
            memoryBytes += range.Length;
        }
        if (memoryBytes > 65536) throw new ArgumentException("Memory ranges may total at most 65536 bytes.");
        cancellationToken.ThrowIfCancellationRequested();
        using var session = new EmulatorSession(rom);
        foreach (string button in options.Buttons) session.SetButton(button, true);
        double initialTicks = session.Ticks, targetTicks = initialTicks + options.Frames * EmulatorSession.FrameTicks;
        int frames = 0, instructions = 0;
        bool breakpointReached = options.Breakpoint is int address && session.Registers.PC == address;
        string stopReason = breakpointReached ? "breakpoint" : "frame-budget";
        while (session.Ticks < targetTicks && !breakpointReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Breakpoint is not null && instructions >= options.MaxInstructions)
            {
                stopReason = "instruction-budget";
                break;
            }
            double previousTicks = session.Ticks;
            int events = session.RunUntil(options.Breakpoint is null ? targetTicks : previousTicks + 1, cancellationToken);
            if (session.Ticks > previousTicks) instructions++;
            if ((events & 1) != 0) frames++;
            if (options.Breakpoint is int breakpoint && session.Registers.PC == breakpoint)
            {
                breakpointReached = true;
                stopReason = "breakpoint";
            }
        }
        var checks = CheckAssertions(session, expected, cancellationToken);
        return new("binjgb", options.Frames, frames, session.Ticks, session.Ticks - initialTicks, session.Registers, stopReason,
            options.Memory.Select(range => new MemoryResult(range.Address, range.Length,
                session.ReadMemory(range.Address, range.Length, cancellationToken).Select(value => (int)value).ToArray())).ToArray(),
            session.FramebufferSha256(), session.Diagnostics.ToArray(), checks, checks.All(check => check.Passed))
        {
            Breakpoint = options.Breakpoint,
            BreakpointReached = options.Breakpoint is null ? null : breakpointReached,
            InstructionSteps = options.Breakpoint is null ? null : instructions
        };
    }

    internal static void ValidateFields(JsonElement input, string[] allowed, string label)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{label} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ArgumentException($"Unknown {label} field {property.Name}.");
            if (!seen.Add(property.Name)) throw new ArgumentException($"Duplicate {label} field {property.Name}.");
        }
    }
    internal static JsonElement Required(JsonElement input, string name) => input.TryGetProperty(name, out var value)
        ? value : throw new ArgumentException($"Missing required field {name}.");
    internal static int Integer(JsonElement value, string label, int minimum, int maximum)
    {
        if (!JsonInteger.TryGetInt32(value, out int number) || number < minimum || number > maximum)
            throw new ArgumentException($"{label} must be an integer between {minimum} and {maximum}.");
        return number;
    }
    internal static int OptionalInteger(JsonElement input, string name, int fallback, int minimum, int maximum) =>
        input.TryGetProperty(name, out var value) ? Integer(value, name, minimum, maximum) : fallback;
    internal static int[] ByteArray(JsonElement value, string label, CancellationToken cancellationToken = default)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 65536)
            throw new ArgumentException($"{label} must be a nonempty array of at most 65536 bytes.");
        var bytes = new int[value.GetArrayLength()];
        int index = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes[index++] = Integer(item, label, 0, 255);
        }
        return bytes;
    }
}
