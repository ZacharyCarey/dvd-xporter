using System.Numerics;
using System.Text;

namespace DiscDecoders.Common;

/// <summary>
/// Reads big-endian fields, any number of bits at a time with the most significant bit first, as disc navigation files
/// (DVD IFOs, Blu-ray .mpls and .clpi files) store them. Bytes are taken from the stream only as they're needed, so it
/// can read a few bit fields in the middle of a stream that's otherwise read directly, ending on a byte boundary (see
/// <see cref="IsByteAligned"/>).
/// </summary>
/// <exception cref="EndOfStreamException">Reading past the end of the data.</exception>
public sealed class BitReader
{
    private readonly Stream _stream;
    private int _byte;
    private int _bitsLeft;

    public BitReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Reads a whole file's data.</summary>
    public BitReader(byte[] data) : this(new MemoryStream(data, writable: false))
    {
    }

    /// <summary>The data's length in bytes.</summary>
    public long Length => _stream.Length;

    /// <summary>The byte being read.</summary>
    public long Position => _stream.Position - (_bitsLeft > 0 ? 1 : 0);

    /// <summary>Whether the bits read so far end on a byte boundary.</summary>
    public bool IsByteAligned => _bitsLeft == 0;

    public long AvailableBits => (_stream.Length - _stream.Position) * 8 + _bitsLeft;

    public long AvailableBytes => AvailableBits / 8;

    /// <summary>Moves to a byte offset. It may be past the end, though reading from there fails.</summary>
    public void Seek(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _stream.Position = offset;
        _bitsLeft = 0;
    }

    public void Skip(int bits)
    {
        if (bits <= _bitsLeft)
        {
            _bitsLeft -= bits;
            return;
        }

        bits -= _bitsLeft;
        _bitsLeft = 0;
        _stream.Position += bits / 8;
        if (bits % 8 != 0)
        {
            NextByte();
            _bitsLeft = 8 - bits % 8;
        }
    }

    public bool ReadBit() => ReadBits(1) != 0;

    /// <summary>Reads up to 32 bits.</summary>
    public uint ReadBits(int count)
    {
        uint result = 0;
        while (count > 0)
        {
            if (_bitsLeft == 0)
            {
                NextByte();
                _bitsLeft = 8;
            }

            int take = Math.Min(_bitsLeft, count);
            _bitsLeft -= take;
            result = (result << take) | ((uint)(_byte >> _bitsLeft) & ((1u << take) - 1));
            count -= take;
        }
        return result;
    }

    /// <summary>Reads up to 32 bits as <typeparamref name="T"/>.</summary>
    public T ReadBits<T>(int count) where T : IBinaryInteger<T> => T.CreateTruncating(ReadBits(count));

    public byte ReadByte() => (byte)ReadBits(8);

    public ushort ReadUInt16() => (ushort)ReadBits(16);

    public uint ReadUInt32() => ReadBits(32);

    public byte[] ReadBytes(int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
            bytes[i] = ReadByte();
        return bytes;
    }

    /// <summary>Reads ASCII text of a fixed length, without its trailing NULs.</summary>
    public string ReadString(int length) => Encoding.ASCII.GetString(ReadBytes(length)).TrimEnd('\0');

    private void NextByte()
    {
        _byte = _stream.ReadByte();
        if (_byte < 0)
            throw new EndOfStreamException("Unexpected end of file.");
    }
}
