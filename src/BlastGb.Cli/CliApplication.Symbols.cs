using System.Text.Json;
using BlastGb.Core;

namespace BlastGb.Cli;

public static partial class CliApplication
{
    private static int ResolveCpuAddress(string value, IReadOnlyList<RgbdsSymbol>? symbols)
    {
        if (IntegerPattern().IsMatch(value)) return Integer(value, "CPU address", 65535);
        if (symbols is null) throw new CliException("USAGE", "A symbolic address requires --symbols FILE.sym.");
        var symbol = FindSymbol(symbols, value);
        if (symbol.Address is not { } address || symbol.Bank is not { } bank)
            throw new CliException("INVALID_SYMBOL", $"{value} is a constant, not an address label.");
        if (bank != 0 || address is >= 0x4000 and < 0xc000 or >= 0xd000 and < 0xff80)
            throw new CliException("AMBIGUOUS_SYMBOL", $"{value} names banked or mirrored memory. Runtime symbol references support ROM0, WRAM0 and HRAM bank 0 only; use an explicit mapped CPU address after selecting the bank in a script.");
        return address;
    }

    /// <summary>Resolve address labels while retaining duplicate fields for the strict runtime validator.</summary>
    private static JsonElement ResolveRuntimeSymbols(JsonElement source, IReadOnlyList<RgbdsSymbol>? symbols)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(source, null, writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();

        void Write(JsonElement element, string? property, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.String && (property is "address" or "addresses" || string.Equals(property, "pc", StringComparison.OrdinalIgnoreCase)))
            {
                writer.WriteNumberValue(ResolveCpuAddress(element.GetString()!, symbols));
                return;
            }
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var item in element.EnumerateObject())
                {
                    writer.WritePropertyName(item.Name);
                    Write(item.Value, item.Name, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(item, property, writer);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }
}
