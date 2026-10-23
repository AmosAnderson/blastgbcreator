using System.Buffers.Binary;
using System.IO.Compression;

namespace BlastGb.Core;

/// <summary>Lossless RGBA PNG encoding without platform graphics dependencies.</summary>
public static class PngEncoder
{
    public static byte[] EncodeRgba(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height * 4 != pixels.Length)
            throw new ArgumentException("RGBA pixels must match dimensions between 1 and 4096.");
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels.Slice(y * width * 4, width * 4));
            }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> bytes)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(bytes);
        var crc = uint.MaxValue;
        foreach (var value in type) crc = UpdateCrc(crc, value);
        foreach (var value in bytes) crc = UpdateCrc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        return crc;
    }
}
