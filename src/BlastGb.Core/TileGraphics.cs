namespace BlastGb.Core;

/// <summary>Game Boy planar tiles. Pixels are tile-major, then row-major, with values 0–3.</summary>
public static class TileGraphics
{
    public const int MaximumTiles = 4096;

    public static byte[] Encode2Bpp(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length == 0 || pixels.Length % 64 != 0 || pixels.Length / 64 > MaximumTiles)
            throw new ArgumentException("Supply between 1 and 4096 complete 8×8 tiles.", nameof(pixels));
        var bytes = new byte[pixels.Length / 4];
        for (var row = 0; row < pixels.Length / 8; row++)
            for (var x = 0; x < 8; x++)
            {
                var pixel = pixels[row * 8 + x];
                if (pixel > 3) throw new ArgumentException("Tile pixels must be palette indices 0–3.", nameof(pixels));
                bytes[row * 2] |= (byte)((pixel & 1) << (7 - x));
                bytes[row * 2 + 1] |= (byte)((pixel >> 1) << (7 - x));
            }
        return bytes;
    }

    public static byte[] Decode2Bpp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % 16 != 0 || bytes.Length / 16 > MaximumTiles)
            throw new ArgumentException("Supply between 1 and 4096 complete 16-byte tiles.", nameof(bytes));
        var pixels = new byte[bytes.Length * 4];
        for (var row = 0; row < bytes.Length / 2; row++)
            for (var x = 0; x < 8; x++)
                pixels[row * 8 + x] = (byte)(((bytes[row * 2] >> (7 - x)) & 1) | (((bytes[row * 2 + 1] >> (7 - x)) & 1) << 1));
        return pixels;
    }
}
