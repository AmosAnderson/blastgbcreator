using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace BlastGb.Core;

/// <summary>A label with a bank and CPU address, or an exported signed 32-bit constant.</summary>
public sealed record RgbdsSymbol(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Bank,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Address,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RomOffset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ConstantValue = null)
{
    public RgbdsSymbol(string name, int bank, int address)
        : this(name, bank, address, bank == 0 && address is >= 0 and < 0x4000 ? address
            : bank is >= 1 and <= 511 && address is >= 0x4000 and < 0x8000
                ? bank * RomTools.RomBankSize + address - 0x4000 : null)
    { }

    public string Kind => ConstantValue.HasValue ? "constant" : "label";
}

/// <summary>All exported records, with ordinal lookup restricted to mapped-address labels.</summary>
public sealed class RgbdsSymbolTable : IReadOnlyList<RgbdsSymbol>
{
    private readonly IReadOnlyList<RgbdsSymbol> records;
    private readonly IReadOnlyDictionary<string, RgbdsSymbol> byName;
    public IReadOnlyList<RgbdsSymbol> Symbols { get; }
    public int Count => records.Count;
    public RgbdsSymbol this[int index] => records[index];

    internal RgbdsSymbolTable(IReadOnlyList<RgbdsSymbol> records)
    {
        this.records = records;
        Symbols = Array.AsReadOnly(records.Where(symbol => symbol.ConstantValue is null).ToArray());
        byName = Symbols.ToDictionary(symbol => symbol.Name, StringComparer.Ordinal);
    }

    public bool TryGetByName(string name, [NotNullWhen(true)] out RgbdsSymbol? symbol) => byName.TryGetValue(name, out symbol);
    public RgbdsSymbol GetByName(string name) => byName.TryGetValue(name, out var symbol) ? symbol :
        throw new ArgumentException($"Unknown RGBDS address symbol {name}.", nameof(name));

    public IReadOnlyList<RgbdsSymbol> FindByRomOffset(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return Symbols.Where(symbol => symbol.RomOffset == offset).ToArray();
    }

    public IEnumerator<RgbdsSymbol> GetEnumerator() => records.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Bounded RGBDS symbol parsing with explicit, conservative ROM bank mapping.</summary>
public static class RgbdsSymbols
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxSymbols = 65536;
    public const int MaxNameBytes = 1024;
    private const int MaxLineLength = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static RgbdsSymbolTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxFileBytes || Utf8.GetByteCount(text) > MaxFileBytes)
            throw new ArgumentException("Symbol text exceeds the 16 MiB limit.", nameof(text));
        var symbols = new List<RgbdsSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text.Length > 0 && text[0] == '\ufeff' ? text[1..] : text);
        var lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            if (raw.Length > MaxLineLength) throw Invalid(lineNumber, "line exceeds the supported length");
            var comment = raw.IndexOf(';');
            var line = (comment < 0 ? raw.AsSpan() : raw.AsSpan(0, comment)).Trim(" \t");
            if (line.IsEmpty) continue;
            var separator = line.IndexOfAny(' ', '\t');
            if (separator < 0) throw Invalid(lineNumber, "expected a value and symbol name");
            var location = line[..separator];
            var encodedName = line[(separator + 1)..].Trim(" \t");
            if (encodedName.IsEmpty || encodedName.IndexOfAny(' ', '\t') >= 0)
                throw Invalid(lineNumber, "expected one symbol name");
            var name = DecodeName(encodedName, lineNumber);
            if (!names.Add(name)) throw Invalid(lineNumber, $"duplicate symbol name {name}");
            if (symbols.Count == MaxSymbols) throw Invalid(lineNumber, "symbol count exceeds 65536");
            var colon = location.IndexOf(':');
            if (colon < 0)
            {
                if (location.Length is < 1 or > 8 || !uint.TryParse(location, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var constant))
                    throw Invalid(lineNumber, "constant must be a 32-bit hexadecimal value");
                symbols.Add(new RgbdsSymbol(name, null, null, null, unchecked((int)constant)));
                continue;
            }
            if (colon is < 1 or > 4 || location.Length - colon - 1 != 4
                || !int.TryParse(location[..colon], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bank)
                || !int.TryParse(location[(colon + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
                throw Invalid(lineNumber, "label must use a hexadecimal bank and four-digit CPU address");
            int? romOffset = bank == 0 && address < 0x4000 ? address
                : bank is >= 1 and <= 511 && address is >= 0x4000 and < 0x8000
                    ? bank * RomTools.RomBankSize + address - 0x4000 : null;
            symbols.Add(new RgbdsSymbol(name, bank, address, romOffset));
        }
        return new RgbdsSymbolTable(symbols.AsReadOnly());
    }

    private static string DecodeName(ReadOnlySpan<char> value, int lineNumber)
    {
        var name = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\')
            {
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                    throw Invalid(lineNumber, "symbol names must escape whitespace and control characters");
                name.Append(character);
                continue;
            }
            var digits = index + 1 < value.Length ? value[index + 1] switch { 'u' => 4, 'U' => 8, _ => 0 } : 0;
            if (digits == 0 || value.Length - index - 2 < digits
                || !uint.TryParse(value.Slice(index + 2, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var scalar)
                || scalar > 0x10ffff || !Rune.IsValid((int)scalar))
                throw Invalid(lineNumber, "invalid Unicode escape in symbol name");
            var rune = new Rune((int)scalar);
            if (Rune.IsControl(rune)) throw Invalid(lineNumber, "control characters are not supported in symbol names");
            name.Append(rune.ToString());
            index += digits + 1;
        }
        var result = name.ToString();
        if (Utf8.GetByteCount(result) > MaxNameBytes) throw Invalid(lineNumber, "symbol name exceeds 1024 UTF-8 bytes");
        return result;
    }

    private static FormatException Invalid(int line, string message) =>
        new($"Invalid RGBDS symbol record on line {line.ToString(CultureInfo.InvariantCulture)}: {message}.");
}
