
using Iso639;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public enum SubpictureCodeMode
{
    RunLength = 0,
    Extended = 1,
    Other = 2
}

/// <summary>
/// Subpicture attributes
/// </summary>
public class SubpictureAttributes : IStreamAttributes
{
    internal const uint Size = 6;

    public readonly SubpictureCodeMode Mode; // 3 bits

    /// <summary>
    /// only if type == 1/language
    /// 2 bytes
    /// </summary>
    public readonly Language? Language;

    /// <summary>
    /// only if type == 1/language
    /// </summary>
    public readonly byte LanguageExtension;

    public readonly byte CodeExtension;

    public readonly byte StreamID;
    byte IStreamAttributes.StreamID => this.StreamID;
    internal SubpictureAttributes(DvdStream file, int index)
    {
        BitReader bits = new BitReader(file.Stream);

        Mode = (SubpictureCodeMode)bits.ReadBits<byte>(3);
        Util.AssertZero(bits.ReadBits<byte>(3));
        byte type = bits.ReadBits<byte>(2);
        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");
        file.ReadZeros(1);
        string lang = file.ReadString(2);
        if (string.IsNullOrWhiteSpace(lang)) this.Language = null;
        else
        {
            try
            {
                this.Language = Iso639.Language.FromPart1(lang);
            }catch(Exception)
            {
                this.Language = null;
            }
        }
        LanguageExtension = file.Read<byte>();
        CodeExtension = file.Read<byte>();
        StreamID = (byte)(0x20 + index);
    }
}