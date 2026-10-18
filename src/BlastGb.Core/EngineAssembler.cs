namespace BlastGb.Core;

/// <summary>A deterministic assembler for the reviewed built-in engine; only data comes from a GameSpec.</summary>
internal sealed class EngineAssembler
{
    private readonly List<byte> _bytes = [];
    private readonly List<string> _source = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private readonly List<(int At, string Label)> _references = [];
    private static string Hex(int value, int width = 2) => "$" + value.ToString($"x{width}", System.Globalization.CultureInfo.InvariantCulture);

    public void Op(string instruction, params int[] bytes)
    {
        _source.Add($"    {instruction}");
        foreach (var value in bytes) _bytes.Add(checked((byte)value));
    }

    public void Label(string name)
    {
        if (!_labels.TryAdd(name, _bytes.Count + 0x150)) throw new InvalidOperationException($"Duplicate assembly label: {name}");
        _source.Add($"{name}:");
    }

    public void Comment(string text) => _source.Add($"\n; {text}");

    private void Reference(string instruction, int opcode, string label)
    {
        _references.Add((_bytes.Count + 1, label));
        Op(instruction, opcode, 0, 0);
    }

    public void Jump(string label, string? condition = null)
    {
        var opcode = condition switch { "z" => 0xca, "nz" => 0xc2, "c" => 0xda, "nc" => 0xd2, null => 0xc3, _ => throw new ArgumentException("Unknown condition.", nameof(condition)) };
        Reference($"jp {(condition is null ? "" : condition + ", ")}{label}", opcode, label);
    }

    public void Call(string label) => Reference($"call {label}", 0xcd, label);
    private static int WordOpcode(string register) => register switch { "hl" => 0x21, "de" => 0x11, "bc" => 0x01, "sp" => 0x31, _ => throw new ArgumentException("Unknown word register.", nameof(register)) };
    public void Word(string register, string label) => Reference($"ld {register}, {label}", WordOpcode(register), label);
    public void Word(string register, int value) => Op($"ld {register}, {Hex(value, 4)}", WordOpcode(register), value & 255, value >> 8);
    public void A(int value) => Op($"ld a, {Hex(value)}", 0x3e, value);
    public void Load(int address) => Op($"ld a, [{Hex(address, 4)}]", 0xfa, address & 255, address >> 8);
    public void Store(int address) => Op($"ld [{Hex(address, 4)}], a", 0xea, address & 255, address >> 8);
    public void ReadIo(int address) => Op($"ldh a, [{Hex(0xff00 + address, 4)}]", 0xf0, address);
    public void WriteIo(int address) => Op($"ldh [{Hex(0xff00 + address, 4)}], a", 0xe0, address);

    public void Data(string label, int[] bytes)
    {
        Label(label);
        for (var index = 0; index < bytes.Length; index += 16)
        {
            var row = bytes.AsSpan(index, Math.Min(16, bytes.Length - index)).ToArray();
            Op("db " + string.Join(", ", row.Select(value => Hex(value))), row);
        }
    }

    public (byte[] Bytes, string Source) Finish()
    {
        foreach (var (at, label) in _references)
        {
            if (!_labels.TryGetValue(label, out var target)) throw new InvalidOperationException($"Missing assembly label: {label}");
            _bytes[at] = (byte)(target & 255);
            _bytes[at + 1] = checked((byte)(target >> 8));
        }
        if (_bytes.Count + 0x150 > 0x4000) throw new InvalidOperationException("The game exceeds its 16KiB engine bank.");
        return (_bytes.ToArray(), string.Join('\n', _source));
    }
}
