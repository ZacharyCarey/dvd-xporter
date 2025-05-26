
using System.Numerics;
using System.Text;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

internal class DvdStream : IDisposable
{
    /// <summary>The file, for reading bit fields with a <see cref="BitReader"/>.</summary>
    public Stream Stream { get; }
    public uint Position
    {
        get
        {
            checked
            {
                return (uint)Stream.Position;
            }
        }
    }

    public void Dispose()
    {
        Stream.Dispose();
    }

    /// <summary>Receives non-fatal problems found while parsing (the data is still usable).</summary>
    internal Action<string>? OnWarning;

    public DvdStream(Stream stream)
    {
        this.Stream = stream;
    }

    public string ReadString(int length)
    {
        byte[] data = new byte[length];
        int bytes = Stream.Read(data);
        Util.Assert(bytes == length, "Early EOF.");
        return Encoding.ASCII.GetString(data).TrimEnd('\0');
    }

    public T Read<T>() where T : IBinaryInteger<T>, IShiftOperators<T, int, T>, IBitwiseOperators<T, T, T>
    {
        T result = T.Zero;
        for (int i = 0; i < result.GetByteCount(); i++)
        {
            int data = Stream.ReadByte();
            Util.Assert(data >= 0, "Early EOF.");
            Util.Assert(data <= 0xFF, "Invalid data read.");
            byte b = (byte)data;
            result = (result << 8) | T.CreateChecked(b);
        }
        return result;
    }

    public void Read<T>(T[] buffer) where T : IBinaryInteger<T>, IShiftOperators<T, int, T>, IBitwiseOperators<T, T, T>
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = this.Read<T>();
        }
    }

    public void ReadZeros(int count)
    {
        for (int i = 0; i < count; i++)
        {
            int data = Stream.ReadByte();
            Util.Assert(data >= 0, "Early EOF.");
            Util.Assert(data <= 0xFF, "Invalid data read.");
            Util.Assert(data == 0, "Non-zero data read when zeros were expected.");
        }
    }

    public DiscTime ReadDuration(out double fps)
    {
        return DiscTime.Parse(Read<uint>(), out fps);
    }

    public void Seek(uint offset)
    {
        long pos = Stream.Seek(offset, SeekOrigin.Begin);
        Util.Assert(pos == offset, "Seeking failed");
    }
}