namespace BlastGb.Core;

/// <summary>Game Boy 8×8 tiles: row-major color indices 0..3 and interleaved low/high bitplanes.</summary>
public static class Tile2Bpp
{
    public const int Width = 8;
    public const int Height = 8;
    public const int PixelCount = Width * Height;
    public const int ByteCount = Height * 2;

    public static byte[] Encode(ReadOnlySpan<byte> pixels)
    {
        var result = new byte[ByteCount];
        Encode(pixels, result);
        return result;
    }

    /// <summary>Encode exactly 64 pixels. Invalid input leaves the destination unchanged; spans may overlap.</summary>
    public static void Encode(ReadOnlySpan<byte> pixels, Span<byte> destination)
    {
        if (pixels.Length != PixelCount) throw new ArgumentException("A tile must contain exactly 64 row-major pixels.", nameof(pixels));
        if (destination.Length < ByteCount) throw new ArgumentException("A tile destination needs at least 16 bytes.", nameof(destination));
        Span<byte> encoded = stackalloc byte[ByteCount];
        for (var row = 0; row < Height; row++)
        {
            var low = 0;
            var high = 0;
            for (var column = 0; column < Width; column++)
            {
                var pixel = pixels[row * Width + column];
                if (pixel > 3) throw new ArgumentException("Tile pixels must be color indices between 0 and 3.", nameof(pixels));
                low = (low << 1) | (pixel & 1);
                high = (high << 1) | (pixel >> 1);
            }
            encoded[row * 2] = (byte)low;
            encoded[row * 2 + 1] = (byte)high;
        }
        encoded.CopyTo(destination);
    }

    public static byte[] Decode(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[PixelCount];
        Decode(bytes, result);
        return result;
    }

    /// <summary>Decode exactly 16 bytes into 64 row-major color indices. Spans may overlap.</summary>
    public static void Decode(ReadOnlySpan<byte> bytes, Span<byte> destination)
    {
        if (bytes.Length != ByteCount) throw new ArgumentException("An encoded tile must contain exactly 16 bytes.", nameof(bytes));
        if (destination.Length < PixelCount) throw new ArgumentException("A tile destination needs at least 64 pixels.", nameof(destination));
        Span<byte> decoded = stackalloc byte[PixelCount];
        for (var row = 0; row < Height; row++)
            for (var column = 0; column < Width; column++)
            {
                var shift = Width - 1 - column;
                decoded[row * Width + column] = (byte)(((bytes[row * 2] >> shift) & 1) | (((bytes[row * 2 + 1] >> shift) & 1) << 1));
            }
        decoded.CopyTo(destination);
    }
}
