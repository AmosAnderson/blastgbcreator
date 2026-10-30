using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BlastGb.Core;

/// <summary>A bank and mapped address supplied by RGBDS, not an inferred mapper state.</summary>
public sealed record RgbdsSymbol(string Name, int Bank, int Address)
{
    /// <summary>Physical ROM offset for conventional ROM0/ROMX labels, or null for other address spaces.</summary>
    public int? RomOffset => Address is >= 0 and < 0x4000 && Bank == 0 ? Address :
        Address is >= 0x4000 and < 0x8000 && Bank is >= 1 and < 512 ? Bank * RomTools.RomBankSize + Address - 0x4000 : null;
}

/// <summary>Ordinal, case-sensitive RGBDS symbols. Distinct names may alias one address.</summary>
public sealed class RgbdsSymbolTable
{
    private readonly IReadOnlyDictionary<string, RgbdsSymbol> byName;
    public IReadOnlyList<RgbdsSymbol> Symbols { get; }

    internal RgbdsSymbolTable(IReadOnlyList<RgbdsSymbol> symbols)
    {
        Symbols = symbols;
        byName = symbols.ToDictionary(symbol => symbol.Name, StringComparer.Ordinal);
    }

    public bool TryGetByName(string name, [NotNullWhen(true)] out RgbdsSymbol? symbol) => byName.TryGetValue(name, out symbol);

    public RgbdsSymbol GetByName(string name) => byName.TryGetValue(name, out var symbol) ? symbol :
        throw new ArgumentException($"Unknown RGBDS symbol {name}.", nameof(name));

    public IReadOnlyList<RgbdsSymbol> FindByRomOffset(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return Symbols.Where(symbol => symbol.RomOffset == offset).ToArray();
    }
}

/// <summary>Parse RGBDS address symbols, validating and skipping exported constants that have no bank or address space.</summary>
public static class RgbdsSymbols
{
    public static RgbdsSymbolTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var symbols = new List<RgbdsSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].AsSpan();
            if (index == 0 && line.StartsWith("\ufeff", StringComparison.Ordinal)) line = line[1..];
            var comment = line.IndexOf(';');
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.IsEmpty) continue;
            var separator = line.IndexOfAny(' ', '\t');
            if (separator < 0) throw Invalid(index);
            var location = line[..separator];
            var name = line[separator..].Trim();
            if (name.IsEmpty || HasWhitespace(name)) throw Invalid(index);
            var symbolName = name.ToString();
            if (!names.Add(symbolName)) throw new ArgumentException($"Duplicate RGBDS symbol {symbolName} at line {index + 1}.", nameof(text));
            var colon = location.IndexOf(':');
            if (colon < 0)
            {
                // RGBDS writes exported 32-bit constants as unsigned hexadecimal,
                // including negative values such as ffffffff. They are not mapped labels.
                if (location.Length is < 1 or > 8 || !IsHex(location) ||
                    !uint.TryParse(location, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                    throw Invalid(index);
                continue;
            }
            if (colon is < 2 or > 4 || location.Length - colon - 1 != 4 ||
                !IsHex(location[..colon]) || !IsHex(location[(colon + 1)..]) ||
                !int.TryParse(location[..colon], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bank) || bank >= 512 ||
                !int.TryParse(location[(colon + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
                throw Invalid(index);
            symbols.Add(new RgbdsSymbol(symbolName, bank, address));
        }
        return new RgbdsSymbolTable(symbols.AsReadOnly());
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    private static bool HasWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (char.IsWhiteSpace(character) || char.IsControl(character)) return true;
        return false;
    }

    private static ArgumentException Invalid(int index) => new($"Invalid RGBDS symbol record at line {index + 1}; expected hexadecimal bank:address or a 32-bit hexadecimal constant, followed by one symbol name.", "text");
}
