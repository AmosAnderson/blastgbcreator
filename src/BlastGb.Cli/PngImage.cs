using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BlastGb.Cli;

/// <summary>Bounded 8-bit, non-interlaced PNG files for captures and four-shade tile sheets.</summary>
internal sealed record PngImage(int Width, int Height, byte[] Rgba)
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const int MaximumPixels = 1_048_576;

    public byte[] Encode()
    {
        ValidateSize(Width, Height);
        if (Rgba.Length != Width * Height * 4) throw new ArgumentException("Incorrect RGBA pixel length.");
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            for (var row = 0; row < Height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(Rgba.AsSpan(row * Width * 4, Width * 4));
            }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static PngImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length > 16 * 1024 * 1024 || !png.StartsWith(Signature)) throw new ArgumentException("Choose a PNG file up to 16 MiB.");
        int width = 0, height = 0, color = -1, offset = 8;
        byte[]? palette = null;
        bool ended = false, sawData = false, dataEnded = false;
        using var compressed = new MemoryStream();
        while (offset < png.Length)
        {
            if (png.Length - offset < 12) throw new ArgumentException("Truncated PNG chunk.");
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || length > png.Length - offset - 12) throw new ArgumentException("Invalid PNG chunk length.");
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + length, 4));
            if (Checksum(png.Slice(offset + 4, length + 4)) != crc) throw new ArgumentException("PNG chunk checksum mismatch.");
            string name = Encoding.ASCII.GetString(type);
            if (color < 0 && name != "IHDR") throw new ArgumentException("PNG must start with IHDR.");
            switch (name)
            {
                case "IHDR":
                    if (color >= 0 || length != 13) throw new ArgumentException("Invalid PNG header.");
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    ValidateSize(width, height);
                    color = data[9];
                    if (data[8] != 8 || color is not (0 or 2 or 3 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                        throw new ArgumentException("PNG must use non-interlaced 8-bit grayscale, RGB, indexed, or RGBA pixels.");
                    break;
                case "PLTE":
                    if (palette is not null || sawData || length is < 3 or > 768 || length % 3 != 0) throw new ArgumentException("Invalid PNG palette.");
                    palette = data.ToArray();
                    break;
                case "tRNS": throw new ArgumentException("Tile PNGs must be opaque; remove transparency first.");
                case "IDAT":
                    if (dataEnded || (color == 3 && palette is null)) throw new ArgumentException("Invalid PNG image data order.");
                    sawData = true;
                    compressed.Write(data);
                    break;
                case "IEND":
                    if (length != 0 || !sawData || offset + 12 != png.Length) throw new ArgumentException("Invalid PNG end chunk.");
                    ended = true;
                    break;
                default:
                    if ((type[0] & 32) == 0) throw new ArgumentException($"Unsupported critical PNG chunk {name}.");
                    break;
            }
            if (sawData && name != "IDAT") dataEnded = true;
            offset += length + 12;
        }
        if (!ended) throw new ArgumentException("PNG is missing its end chunk.");
        int channels = color == 6 ? 4 : color == 2 ? 3 : 1, stride = width * channels;
        byte[] filtered = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, true))
        {
            zlib.ReadExactly(filtered);
            if (zlib.ReadByte() != -1) throw new ArgumentException("PNG decompressed data exceeds its dimensions.");
        }
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[y * (stride + 1)];
            if (filter > 4) throw new ArgumentException("Invalid PNG scanline filter.");
            for (int x = 0; x < stride; x++)
            {
                int left = x >= channels ? pixels[y * stride + x - channels] : 0;
                int up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int corner = y > 0 && x >= channels ? pixels[(y - 1) * stride + x - channels] : 0;
                int predictor = filter switch { 1 => left, 2 => up, 3 => (left + up) / 2, 4 => Paeth(left, up, corner), _ => 0 };
                pixels[y * stride + x] = unchecked((byte)(filtered[y * (stride + 1) + x + 1] + predictor));
            }
        }
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int source = i * channels, target = i * 4;
            if (color == 3)
            {
                int index = pixels[source] * 3;
                if (palette is null || index + 2 >= palette.Length) throw new ArgumentException("PNG palette index is out of range.");
                palette.AsSpan(index, 3).CopyTo(rgba.AsSpan(target));
            }
            else if (color == 0) rgba.AsSpan(target, 3).Fill(pixels[source]);
            else pixels.AsSpan(source, 3).CopyTo(rgba.AsSpan(target));
            rgba[target + 3] = color == 6 ? pixels[source + 3] : (byte)255;
        }
        return new(width, height, rgba);
    }

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new ArgumentException($"PNG dimensions must be positive and total at most {MaximumPixels} pixels.");
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string name, ReadOnlySpan<byte> data)
    {
        byte[] bytes = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, data.Length);
        Encoding.ASCII.GetBytes(name, bytes.AsSpan(4, 4));
        data.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(data.Length + 8), Checksum(bytes.AsSpan(4, data.Length + 4)));
        output.Write(bytes);
    }

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
