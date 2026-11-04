using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DvdXporter.Core.Storage;

/// <summary>Writes uncomplicated PNGs: 8-bit greyscale or RGBA, rows unfiltered.</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <param name="pixels">Row by row: 1 byte a pixel for greyscale, 4 for RGBA.</param>
    public static byte[] Encode(int width, int height, byte[] pixels, bool rgba)
    {
        int bytesPerPixel = rgba ? 4 : 1;
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)(rgba ? 6 : 0);
        WriteChunk(png, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * bytesPerPixel;
                for (int row = 0; row < height; row++)
                {
                    // Filter type 0: the row as it is
                    zlib.WriteByte(0);
                    zlib.Write(pixels, row * stride, stride);
                }
            }

            WriteChunk(png, "IDAT", compressed.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
