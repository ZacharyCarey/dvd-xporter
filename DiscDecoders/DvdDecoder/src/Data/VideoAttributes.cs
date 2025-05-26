
using System.Drawing;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public enum MpegVersion : byte
{
    Mpeg1 = 0,
    Mpeg2 = 1
}

public enum RegionStandard : byte
{
    NTSC = 0,
    PAL = 1
}

public enum AspectRatio : byte
{
    /// <summary>
    /// 4:3 ratio
    /// </summary>
    Fullscreen = 0,

    /// <summary>
    /// 16:9 ratio
    /// </summary>
    WideScreen = 3
}

public enum VideoResolution
{
    NTSC_720x480,
    NTSC_704x480,
    NTSC_352x480,
    NTSC_352x240,

    PAL_720x576,
    PAL_704x576,
    PAL_352x576,
    PAL_352x288
}

/// <summary>
/// Video attributes
/// </summary>
public class VideoAttributes : IStreamAttributes
{
    public readonly MpegVersion Format; // 2 bits
    public readonly RegionStandard Region; // 2 bits
    public readonly AspectRatio DisplayAspectRatio; // 2 bits
    public readonly bool AutomaticPanAllowed; // 1 bits
    public readonly bool AutomaticLetterboxAllowed; // 1 bits

    public readonly bool Line21ClosedCaptioningField1; // 1 bit
    public readonly bool Line21ClosedCaptioningField2; // 1 bit
    public readonly bool BitRate; // 1 bit
    public readonly VideoResolution Resolution; // 2 bits
    public byte StreamID => 0xE0;
    public Size Size
    {
        get
        {
            switch(Resolution)
            {
                case VideoResolution.NTSC_720x480: return new Size(720, 480);
                case VideoResolution.NTSC_704x480: return new Size(704, 480);
                case VideoResolution.NTSC_352x480: return new Size(352, 480);
                case VideoResolution.NTSC_352x240: return new Size(352, 240);
                case VideoResolution.PAL_720x576: return new Size(720, 576);
                case VideoResolution.PAL_704x576: return new Size(704, 576);
                case VideoResolution.PAL_352x576: return new Size(352, 576);
                case VideoResolution.PAL_352x288: return new Size(352, 288);
                default: throw new Exception("Invalid resolution.");
            }
        }
    }

    /// <summary>
    /// When false, picture is in full screen mode.
    /// When true, top and bottom are cropped (only in 4:3 mode)
    /// </summary>
    public readonly bool Letterboxed; // 1 bit

    /// <summary>
    /// 0 = Camera,
    /// 1 = Film
    /// </summary>
    public readonly bool FilmMode; // 1 bit

    internal VideoAttributes(DvdStream file)
    {
        BitReader bits = new BitReader(file.Stream);

        Format = (MpegVersion)bits.ReadBits<byte>(2);
        Util.Assert((byte)Format <= 1, "Invalid format.");

        Region = (RegionStandard)bits.ReadBits<byte>(2);
        Util.Assert((byte)Region <= 1, "Invalid region.");

        DisplayAspectRatio = (AspectRatio)bits.ReadBits<byte>(2);
        Util.Assert((byte)DisplayAspectRatio == 0 || (byte)DisplayAspectRatio == 3, "Invalid aspect ratio.");

        AutomaticPanAllowed = !bits.ReadBit();
        AutomaticLetterboxAllowed = !bits.ReadBit();

        Line21ClosedCaptioningField1 = bits.ReadBit();
        Line21ClosedCaptioningField2 = bits.ReadBit();
        BitRate = bits.ReadBit();
        
        byte picture_size = bits.ReadBits<byte>(2);
        Resolution = ParseResolution(picture_size, Region);

        Letterboxed = bits.ReadBit();
        Util.AssertZero(bits.ReadBit() ? 1 : 0);
        FilmMode = bits.ReadBit();
        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");
    }

    private static VideoResolution ParseResolution(byte size, RegionStandard region)
    {
        if (region == RegionStandard.NTSC)
        {
            switch (size)
            {
                case 0: return VideoResolution.NTSC_720x480;
                case 1: return VideoResolution.NTSC_704x480;
                case 2: return VideoResolution.NTSC_352x480;
                case 3: return VideoResolution.NTSC_352x240;
                default: throw new Exception("Invalid resolution.");
            }
        }
        else if (region == RegionStandard.PAL)
        {
            switch(size)
            {
                case 0: return VideoResolution.PAL_720x576;
                case 1: return VideoResolution.PAL_704x576;
                case 2: return VideoResolution.PAL_352x576;
                case 3: return VideoResolution.PAL_352x288;
                default: throw new Exception("Invalid resolution.");
            }
        }

        throw new Exception("Invalid region.");
    }
}