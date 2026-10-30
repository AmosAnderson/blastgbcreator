using BlastGb.Core;

namespace BlastGb.Core.Tests;

public sealed class Tile2BppTests
{
    [Fact]
    public void KnownBitplanesPreserveColorOrderAndRowOrientation()
    {
        byte[] encoded = [0x55, 0x33, 0xff, 0x00, 0x00, 0xff, 0xff, 0xff, 0x80, 0x00, 0x01, 0x00, 0x00, 0x80, 0x00, 0x01];
        byte[] pixels =
        [
            0, 1, 2, 3, 0, 1, 2, 3,
            1, 1, 1, 1, 1, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2,
            3, 3, 3, 3, 3, 3, 3, 3,
            1, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1,
            2, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 2
        ];
        Assert.Equal(encoded, Tile2Bpp.Encode(pixels));
        Assert.Equal(pixels, Tile2Bpp.Decode(encoded));
        for (var value = 0; value < 256; value++)
        {
            var bytes = Enumerable.Range(0, 16).Select(index => (byte)(value + index)).ToArray();
            Assert.Equal(bytes, Tile2Bpp.Encode(Tile2Bpp.Decode(bytes)));
        }
    }

    [Fact]
    public void SpanOperationsSupportOverlapAndPreserveDestinationOnFailure()
    {
        var buffer = Enumerable.Range(0, 70).Select(index => (byte)(index % 4)).ToArray();
        var expectedPixels = buffer[..64];
        var expectedBytes = Tile2Bpp.Encode(expectedPixels);
        Tile2Bpp.Encode(buffer.AsSpan(0, 64), buffer.AsSpan(8));
        Assert.Equal(expectedBytes, buffer[8..24]);
        Tile2Bpp.Decode(buffer.AsSpan(8, 16), buffer);
        Assert.Equal(expectedPixels, buffer[..64]);
        buffer[63] = 4;
        var retained = buffer.ToArray();
        Assert.Throws<ArgumentException>(() => Tile2Bpp.Encode(buffer.AsSpan(0, 64), buffer));
        Assert.Equal(retained, buffer);
        Assert.Throws<ArgumentException>(() => Tile2Bpp.Encode(new byte[63]));
        Assert.Throws<ArgumentException>(() => Tile2Bpp.Encode(new byte[64], new byte[15]));
        Assert.Throws<ArgumentException>(() => Tile2Bpp.Decode(new byte[15]));
        Assert.Throws<ArgumentException>(() => Tile2Bpp.Decode(new byte[16], new byte[63]));
    }
}
