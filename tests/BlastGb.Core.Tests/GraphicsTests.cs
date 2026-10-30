using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class GraphicsTests
{
    [Fact]
    public void PlanarTilesMatchAnIndependentBitplaneFixture()
    {
        string[] rows = ["00000000", "11111111", "22222222", "33333333", "01230123", "32103210", "30000003", "00233200"];
        var pixels = rows.SelectMany(row => row.Select(pixel => (byte)(pixel - '0'))).ToArray();
        byte[] planar = [0x00, 0x00, 0xff, 0x00, 0x00, 0xff, 0xff, 0xff, 0x55, 0x33, 0xaa, 0xcc, 0x81, 0x81, 0x18, 0x3c];
        Assert.Equal(planar, TileGraphics.Encode2Bpp(pixels));
        Assert.Equal(pixels, TileGraphics.Decode2Bpp(planar));
        var twoTiles = pixels.Concat(Enumerable.Repeat((byte)3, 64)).ToArray();
        var expected = planar.Concat(Enumerable.Repeat((byte)255, 16)).ToArray();
        Assert.Equal(expected, TileGraphics.Encode2Bpp(twoTiles));
        Assert.Equal(twoTiles, TileGraphics.Decode2Bpp(expected));
    }

    [Fact]
    public void PlanarTileBoundsAndInvalidPixelsAreEnforcedWithoutChangingInputs()
    {
        var pixels = Enumerable.Range(0, TileGraphics.MaximumTiles * 64).Select(index => (byte)((index / 64) % 4)).ToArray();
        Assert.Equal(pixels, TileGraphics.Decode2Bpp(TileGraphics.Encode2Bpp(pixels)));
        foreach (var length in new[] { 0, 63, 65, (TileGraphics.MaximumTiles + 1) * 64 })
            Assert.Throws<ArgumentException>(() => TileGraphics.Encode2Bpp(new byte[length]));
        foreach (var length in new[] { 0, 15, 17, (TileGraphics.MaximumTiles + 1) * 16 })
            Assert.Throws<ArgumentException>(() => TileGraphics.Decode2Bpp(new byte[length]));
        byte[] invalid = new byte[64];
        invalid[^1] = 4;
        var original = (byte[])invalid.Clone();
        Assert.Throws<ArgumentException>(() => TileGraphics.Encode2Bpp(invalid));
        Assert.Equal(original, invalid);
    }

    [Fact]
    public void PngChunksHaveCorrectDimensionsChecksumsAndLosslessRgbaRows()
    {
        byte[] pixels =
        [
            255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0,
            12, 34, 56, 78, 90, 123, 145, 167, 255, 255, 255, 255
        ];
        var original = (byte[])pixels.Clone();
        var png = PngEncoder.EncodeRgba(pixels, 3, 2);
        var chunks = ReadChunks(png);
        var header = Assert.Single(chunks, chunk => chunk.Type == "IHDR").Data;
        Assert.Equal(13, header.Length);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(header));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, header[8..]);
        using var compressed = new MemoryStream(chunks.Where(chunk => chunk.Type == "IDAT").SelectMany(chunk => chunk.Data).ToArray());
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        var scanlines = decoded.ToArray();
        Assert.Equal(26, scanlines.Length);
        Assert.Equal(0, scanlines[0]);
        Assert.Equal(0, scanlines[13]);
        Assert.Equal(pixels[..12], scanlines[1..13]);
        Assert.Equal(pixels[12..], scanlines[14..]);
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void PngHeaderMatchesKnownOnePixelRgbaChecksum()
    {
        var png = PngEncoder.EncodeRgba([0, 0, 0, 0], 1, 1);
        Assert.Equal(0x1f15c489u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(29, 4)));
        Assert.Empty(ReadChunks(png)[^1].Data);
    }

    [Fact]
    public void PngDimensionsAndPixelCountAreBounded()
    {
        Assert.NotEmpty(PngEncoder.EncodeRgba(new byte[4096 * 4], 4096, 1));
        Assert.NotEmpty(PngEncoder.EncodeRgba(new byte[4096 * 4], 1, 4096));
        foreach (var (width, height) in new[] { (0, 1), (1, 0), (-1, 1), (4097, 1), (1, 4097), (int.MaxValue, int.MaxValue), (2, 1) })
            Assert.Throws<ArgumentException>(() => PngEncoder.EncodeRgba(new byte[4], width, height));
    }

    private static List<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var chunks = new List<(string Type, byte[] Data)>();
        var offset = 8;
        while (offset < png.Length)
        {
            Assert.InRange(png.Length - offset, 12, int.MaxValue);
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            Assert.InRange(length, 0, png.Length - offset - 12);
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length).ToArray();
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
            Assert.Equal(expectedCrc, TableCrc(png.AsSpan(offset + 4, length + 4)));
            chunks.Add((type, data));
            offset += length + 12;
        }
        Assert.Equal(png.Length, offset);
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("IEND", chunks[^1].Type);
        return chunks;
    }

    // Independent table-driven CRC calculation, in contrast with the encoder's per-byte bit loop.
    private static uint TableCrc(ReadOnlySpan<byte> bytes)
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        var crc = 0xffffffffu;
        foreach (var value in bytes) crc = table[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc ^ 0xffffffffu;
    }
}
