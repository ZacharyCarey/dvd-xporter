using System.Text;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>What the BDMV navigation files (index.bdmv, .mpls, .clpi, id.bdmv) share: their header and extension data.</summary>
internal static class NavigationFile
{
    // The format versions this decoder reads (Blu-ray 1.0 to UHD)
    private static readonly string[] Versions = ["0100", "0200", "0240", "0300"];

    /// <summary>A time from a navigation file, which counts at 45 kHz.</summary>
    public static DiscTime ToDiscTime(uint time45k) => new((ulong)time45k * 2);

    /// <summary>Throws unless <paramref name="bytes"/> more bytes are left: a length the file gives that runs past its end.</summary>
    public static void Require(this BitReader reader, long bytes, string what)
    {
        if (reader.AvailableBytes < bytes)
            throw new BlurayException($"The {what} runs past the end of the file.");
    }

    /// <summary>Checks the file's 4-character type and its version, and returns the version.</summary>
    public static string ReadHeader(BitReader reader, string type)
    {
        reader.Seek(0);
        reader.Require(8, "header");
        var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var version = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (tag != type)
            throw new BlurayException($"Its header says \"{tag}\" rather than \"{type}\".");
        if (!Versions.Contains(version))
            throw new BlurayException($"Unsupported file version {version}.");
        return version;
    }

    /// <summary>
    /// Calls <paramref name="read"/> with each entry of the extension data at <paramref name="start"/> (its two IDs),
    /// positioned at the entry's data. An entry that can't be read is left out; one that runs past the end of the
    /// file ends the list.
    /// </summary>
    public static void ReadExtensions(BitReader reader, long start, Action<int, int> read)
    {
        if (start < 1 || start > reader.Length - 12)
            return;

        reader.Seek(start);
        uint length = reader.ReadUInt32();
        if (length < 1)
            return;
        reader.Skip(32); // relative start address of extension data
        reader.Skip(24);
        int count = reader.ReadByte();
        if (start > reader.Length - 12 - count * 12)
            return;

        for (int i = 0; i < count; i++)
        {
            int id1 = reader.ReadUInt16();
            int id2 = reader.ReadUInt16();
            long entryStart = reader.ReadUInt32();
            long entryLength = reader.ReadUInt32();
            long next = reader.Position;
            if (entryStart + start + entryLength > reader.Length)
                return;

            reader.Seek(start + entryStart);
            try
            {
                read(id1, id2);
            }
            catch (Exception ex) when (ex is BlurayException or EndOfStreamException)
            {
                // Extensions are optional
            }
            reader.Seek(next);
        }
    }
}
