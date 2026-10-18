using System.Buffers;
using System.Text.Json;

namespace BlastGb.Serialization;

/// <summary>Reads integral JSON numbers without rounding decimal or exponent forms.</summary>
internal static class JsonInteger
{
    public static bool TryGetInt32(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetInt32(out value)) return true;
        if (!TryGetInt64(element, out var integer) || integer is < int.MinValue or > int.MaxValue) return false;
        value = (int)integer;
        return true;
    }

    public static bool TryGetInt32(ref Utf8JsonReader reader, out int value)
    {
        value = 0;
        if (reader.TokenType != JsonTokenType.Number) return false;
        if (reader.TryGetInt32(out value)) return true;
        if (!reader.TryGetInt64(out var integer))
        {
            var token = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            if (!TryParse(new NumberToken(token), out integer)) return false;
        }
        if (integer is < int.MinValue or > int.MaxValue) return false;
        value = (int)integer;
        return true;
    }

    public static bool TryGetInt64(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number) return false;
        if (element.TryGetInt64(out value)) return true;
        return TryParse(new NumberToken(element.GetRawText()), out value);
    }

    private static bool TryParse(NumberToken token, out long value)
    {
        value = 0;
        var negative = token[0] == '-';
        var position = negative ? 1 : 0;
        var significantDigits = 0;
        var trailingZeroes = 0;
        var fractionalDigits = 0;
        var fractional = false;
        ulong prefix = 0;
        // JsonElement and Utf8JsonReader already validate JSON number grammar.
        // Keep at most 19 significant digits, but count the entire token so a
        // long coefficient followed by a cancelling exponent remains exact.
        for (; position < token.Length && token[position] is not ('e' or 'E'); position++)
        {
            var character = token[position];
            if (character == '.') { fractional = true; continue; }
            if (fractional) fractionalDigits++;
            var digit = character - '0';
            if (digit == 0 && significantDigits == 0) continue;
            significantDigits++;
            trailingZeroes = digit == 0 ? trailingZeroes + 1 : 0;
            if (significantDigits <= 19) prefix = prefix * 10 + (uint)digit;
        }
        if (significantDigits == 0) return true;

        long exponent = 0;
        if (position < token.Length)
        {
            position++;
            var exponentNegative = token[position] == '-';
            if (token[position] is '-' or '+') position++;
            // Larger exponents cannot be cancelled by any coefficient in this
            // token. Saturation prevents overflow even for arbitrarily long exponents.
            var limit = (long)token.Length + 20;
            for (; position < token.Length; position++)
                exponent = Math.Min(limit, exponent * 10 + token[position] - '0');
            if (exponentNegative) exponent = -exponent;
        }

        var coefficientDigits = significantDigits - trailingZeroes;
        var shift = exponent - fractionalDigits + trailingZeroes;
        if (shift < 0 || coefficientDigits + shift > 19) return false;

        var capturedDigits = Math.Min(significantDigits, 19);
        for (var index = coefficientDigits; index < capturedDigits; index++) prefix /= 10;
        for (long index = 0; index < shift; index++) prefix *= 10;
        var magnitudeLimit = negative ? 9223372036854775808UL : long.MaxValue;
        if (prefix > magnitudeLimit) return false;
        value = negative ? prefix == 9223372036854775808UL ? long.MinValue : -(long)prefix : (long)prefix;
        return true;
    }

    private readonly ref struct NumberToken
    {
        private readonly ReadOnlySpan<char> characters;
        private readonly ReadOnlySpan<byte> bytes;

        public NumberToken(ReadOnlySpan<char> value) => characters = value;
        public NumberToken(ReadOnlySpan<byte> value) => bytes = value;
        public int Length => characters.IsEmpty ? bytes.Length : characters.Length;
        public char this[int index] => characters.IsEmpty ? (char)bytes[index] : characters[index];
    }
}
