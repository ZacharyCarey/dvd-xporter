
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public class AudioStreamControl
{
    public readonly bool Available;

    /// <summary>
    /// Stream Number for MPEG audio, or Substream Number for all others.
    /// </summary>
    public readonly int StreamNumber;

    internal AudioStreamControl(DvdStream file)
    {
        BitReader bits = new BitReader(file.Stream);
        Available = bits.ReadBit();
        Util.AssertZero(bits.ReadBits<byte>(4));
        StreamNumber = bits.ReadBits<int>(3);
        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");

        Util.AssertZero(file.Read<byte>());

        if (!Available) 
            Util.AssertZero(StreamNumber);
    }

    /// <summary>
    /// The MPEG stream ID this audio stream is stored under in the VOBs: the private stream 1 substream ID
    /// for AC-3, DTS and LPCM, or the MPEG audio stream ID's low byte.
    /// </summary>
    public byte GetStreamId(AudioEncoding format) => format switch
    {
        AudioEncoding.AC3 => (byte)(0x80 + StreamNumber),
        AudioEncoding.DTS => (byte)(0x88 + StreamNumber),
        AudioEncoding.SDDS => (byte)(0x90 + StreamNumber),
        AudioEncoding.LPCM => (byte)(0xA0 + StreamNumber),
        AudioEncoding.Mpeg1 or AudioEncoding.Mpeg2 => (byte)(0xC0 + StreamNumber),
        _ => throw new NotSupportedException($"Unsupported audio format: {format}."),
    };
}

public class SubpictureStreamControl
{
    public readonly bool Available;

    /// <summary>
    /// Standard = 4:3 ratio
    /// </summary>
    public readonly int StreamNumberForStandard;
    public readonly int StreamNumberForWide;
    public readonly int StreamNumberForLetterbox;
    public readonly int StreamNumberForPanAndScan;

    internal SubpictureStreamControl(DvdStream file)
    {
        BitReader bits = new BitReader(file.Stream);

        Available = bits.ReadBit();
        Util.AssertZero(bits.ReadBits<byte>(2));
        StreamNumberForStandard = bits.ReadBits<int>(5);

        Util.AssertZero(bits.ReadBits<byte>(3));
        StreamNumberForWide = bits.ReadBits<int>(5);

        Util.AssertZero(bits.ReadBits<byte>(3));
        StreamNumberForLetterbox = bits.ReadBits<int>(5);

        Util.AssertZero(bits.ReadBits<byte>(3));
        StreamNumberForPanAndScan = bits.ReadBits<int>(5);

        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");

        if (!Available)
        {
            Util.AssertZero(StreamNumberForStandard);
            Util.AssertZero(StreamNumberForWide);
            Util.AssertZero(StreamNumberForLetterbox);
            Util.AssertZero(StreamNumberForPanAndScan);
        }
    }

    /// <summary>
    /// The private stream 1 substream ID to play for video of the given aspect ratio.
    /// 16:9 video uses the widescreen stream; 4:3 video uses the standard one. (The letterbox and
    /// pan-and-scan streams are only for showing 16:9 video on a 4:3 display.)
    /// </summary>
    public byte GetStreamId(AspectRatio videoAspect) =>
        (byte)(0x20 + (videoAspect == AspectRatio.WideScreen ? StreamNumberForWide : StreamNumberForStandard));

}