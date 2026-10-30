using System.Text.Json;

namespace BlastGb.Runtime;

public sealed record ExecutionTraceEntry(double Ticks, CpuRegisters Registers);
public sealed record RuntimeProvenance(string Engine, string EngineSha256);
public sealed record DebugOptions
{
    public bool CaptureFramebuffer { get; init; }
    public bool IncludeProvenance { get; init; }
    public byte[]? BatterySave { get; init; }
    public bool CaptureBatterySave { get; init; }
}

internal sealed class ExecutionTrace(int capacity)
{
    private readonly Queue<ExecutionTraceEntry> entries = new(capacity);
    public void Add(EmulatorSession session)
    {
        if (capacity == 0) return;
        if (entries.Count == capacity) entries.Dequeue();
        entries.Enqueue(new(session.Ticks, session.Registers));
    }
    public ExecutionTraceEntry[] ToArray() => entries.ToArray();
}

public static partial class RuntimeRunner
{
    public const int MaximumTraceCapacity = 1024;
    public const long MaximumConditionMemoryReads = 16 * 1024 * 1024;

    private static void ValidateTraceCapacity(int capacity)
    {
        if (capacity is < 0 or > MaximumTraceCapacity)
            throw new ArgumentException($"traceCapacity must be between 0 and {MaximumTraceCapacity}.");
    }

    private static Assertions NormalizeUntil(JsonElement value, int instructions, CancellationToken cancellationToken)
    {
        ValidateFields(value, ["memory", "registers"], "run condition");
        var condition = NormalizeAssertions(value, cancellationToken);
        long bytes = condition.Memory.Sum(item => (long)item.Bytes.Length);
        if (bytes * ((long)instructions + 1) > MaximumConditionMemoryReads)
            throw new ArgumentException($"Run-condition memory reads may total at most {MaximumConditionMemoryReads} bytes across the instruction budget.");
        return condition;
    }

    private static string[] NormalizeButtons(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > ButtonNames.Count)
            throw new ArgumentException("pressed must be an array of at most eight button names.");
        var pressed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            string? button = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (button is null || !ButtonNames.Contains(button)) throw new ArgumentException($"Unknown button {item.GetRawText()}.");
            if (!pressed.Add(button)) throw new ArgumentException($"Duplicate pressed button {button}.");
        }
        return pressed.Order(StringComparer.Ordinal).ToArray();
    }
}
