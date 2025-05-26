
using System.Numerics;
using System.Text;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

internal static class Util
{
    public const int SECTOR_SIZE = 2048;

    public static void Assert(bool condition, string msg)
    {
        if (!condition)
            throw new DvdException(msg);
    }

    public static void AssertZero<T>(T value) where T : INumberBase<T>
    {
        Assert(value == T.Zero, "Expected value to be zero.");
    }

    extension(DiscTime)
    {
        internal static DiscTime Parse(uint data, out double fps)
        {
            // BCD values
            byte hour = (byte)((data >> 24) & 0xFF);
            byte hours = (byte)(((hour & 0xF0) >> 3) * 5 + (hour & 0x0F)); // Max 165

            byte minute = (byte)((data >> 16) & 0xFF);
            byte minutes = (byte)(((minute & 0xF0) >> 3) * 5 + (minute & 0x0F)); // Max 165

            byte second = (byte)((data >> 8) & 0xFF);
            byte seconds = (byte)(((second & 0xF0) >> 3) * 5 + (second & 0x0F)); // Max 165

            byte frame_u = (byte)(data & 0xFF);
            int fpsIndex = (frame_u & 0xC0) >> 6;

            // Store as 90khz ticks
            ulong ticks = 0;
            ticks += hours * 324000000ul;
            ticks += minutes * 5400000ul;
            ticks += seconds * 90000ul;
            // max 54365850000

            byte frames = (byte)(((frame_u & 0x30) >> 3) * 5 + (frame_u & 0x0F)); // max 45
            if (fpsIndex == 0) fps = 0.0;
            else if (fpsIndex == 1)
            {
                fps = 25.0;
                ticks += frames * 3600ul;
            }
            else if (fpsIndex == 3) { 
                fps = 30000.0 / 1001.0;
                ticks += frames * 3003ul;
            }
            else throw new IOException("Invalid FPS.");

            return new DiscTime(ticks);
        }
    }
}