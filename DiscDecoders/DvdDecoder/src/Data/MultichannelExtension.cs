
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Multichannel Extension <see cref="http://www.mpucoder.com/DVD/ifo.html#mcext"/> 
/// </summary>
public class MultichannelExtension
{
    public readonly bool ach0_gme; // 1 bit

    public readonly bool ach1_gme; // 1 bit

    public readonly bool ach2_gv1e; // 1 bit
    public readonly bool ach2_gv2e; // 1 bit
    public readonly bool ach2_gm1e; // 1 bit
    public readonly bool ach2_gm2e; // 1 bit

    public readonly bool ach3_gv1e; // 1 bit
    public readonly bool ach3_gv2e; // 1 bit
    public readonly bool ach3_gmAe; // 1 bit
    public readonly bool ach3_se2e; // 1 bit

    public readonly bool ach4_gv1e; // 1 bit
    public readonly bool ach4_gv2e; // 1 bit
    public readonly bool ach4_gmBe; // 1 bit
    public readonly bool ach4_seBe; // 1 bit

    internal MultichannelExtension(DvdStream file)
    {
        BitReader bits = new BitReader(file.Stream);

        Util.AssertZero(bits.ReadBits<byte>(7));
        ach0_gme = bits.ReadBit();

        Util.AssertZero(bits.ReadBits<byte>(7));
        ach1_gme = bits.ReadBit();

        Util.AssertZero(bits.ReadBits<byte>(4));
        ach2_gv1e = bits.ReadBit();
        ach2_gv2e = bits.ReadBit();
        ach2_gm1e = bits.ReadBit();
        ach2_gm2e = bits.ReadBit();

        Util.AssertZero(bits.ReadBits<byte>(4));
        ach3_gv1e = bits.ReadBit();
        ach3_gv2e = bits.ReadBit();
        ach3_gmAe = bits.ReadBit();
        ach3_se2e = bits.ReadBit();

        Util.AssertZero(bits.ReadBits<byte>(4));
        ach4_gv1e = bits.ReadBit();
        ach4_gv2e = bits.ReadBit();
        ach4_gmBe = bits.ReadBit();
        ach4_seBe = bits.ReadBit();

        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");

        file.ReadZeros(19);
    }
}