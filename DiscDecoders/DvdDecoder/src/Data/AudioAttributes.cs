
using Iso639;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public enum AudioEncoding : byte
{
    AC3 = 0,
    Unknown = 1,
    Mpeg1 = 2,
    Mpeg2 = 3,
    LPCM = 4,
    SDDS = 5,
    DTS = 6,
    Invalid = 7
}

public enum AudioMode : byte
{
    Unspecified = 0,
    Karaoke = 1,
    Surround = 2
}

public enum QuantizationType : byte
{
    _16bps = 0,
    _20bps = 1,
    _24bps = 2,
    DRC = 3
}

public enum AudioContentType : byte
{
    None = 0,
    Normal = 1,
    VisuallyImpaired = 2,
    DirectorsCommentary = 3,
    AlternateCommentary = 4
}

/// <summary>
/// Audio attributes
/// </summary>
public class AudioAttributes : IStreamAttributes
{
    internal const uint Size = 8;

    public readonly AudioEncoding Format; // 3 bits
    public readonly bool MultichannelExtensionPresent; // 1 bit
    public readonly AudioMode Mode; // 2 bits

    public readonly QuantizationType Quantization; // 2 bits
    public readonly int SampleFrequency; // 2 bits
    //public bool unknown1; // 1 bit
    /// <summary>
    /// Number of audio channels, including LFE (e.g. 2 for stereo, 6 for 5.1). Stored on disc as channels - 1.
    /// </summary>
    public readonly byte Channels; // 3 bits

    /// <summary>
    /// 2 bytes
    /// </summary>
    public readonly Language? Language;
    public readonly byte LanguageExtension;
    public readonly AudioContentType ContentType;
    //public byte unknown3;

    internal byte app_info;
    public KaraokeData Karaoke => new KaraokeData(this);
    public SurroundData Surround => new SurroundData(this);

    

    private static readonly byte[] StreamIDBase = { 0x80, 0, 0xC0, 0xC0, 0xA0, 0, 0x88 };
    public readonly byte StreamID;
    byte IStreamAttributes.StreamID => this.StreamID;

    internal AudioAttributes(DvdStream file, int index)
    {
        BitReader bits = new BitReader(file.Stream);

        Format = (AudioEncoding)bits.ReadBits<byte>(3);
        Util.Assert((byte)Format < 7, "Invalid format.");

        MultichannelExtensionPresent = bits.ReadBit();
        byte lang_type = bits.ReadBits<byte>(2);
        Mode = (AudioMode)bits.ReadBits<byte>(2);
        Quantization = (QuantizationType)bits.ReadBits<byte>(2);
        SampleFrequency = GetFrequency(bits.ReadBits<byte>(2));
        bool unknown1 = bits.ReadBit();
        Channels = (byte)(bits.ReadBits<byte>(3) + 1);
        string lang = file.ReadString(2);
        if (string.IsNullOrWhiteSpace(lang)) this.Language = null;
        else
        {
            try
            {
                this.Language = Iso639.Language.FromPart1(lang);
            }
            catch (Exception)
            {
                this.Language = null;
            }
        }
        LanguageExtension = bits.ReadBits<byte>(8);
        ContentType = (AudioContentType)bits.ReadBits<byte>(8);
        Util.Assert((byte)ContentType <= 4, "Invalid content type.");

        byte unknown3 = bits.ReadBits<byte>(8);
        app_info = bits.ReadBits<byte>(8);

        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");

        StreamID = (byte)(StreamIDBase[(byte)Format] + index);
    }

    private int GetFrequency(byte val)
    {
        switch(val)
        {
            case 0: return 48000;
            case 1: return 96000;
            default: throw new Exception("Invalid sample frequency.");
        }
    }

    public class KaraokeData
    {
        private AudioAttributes source;
        public bool Unknown4 => ((source.app_info >> 7) & 0b1) != 0;
        public byte ChannelAssignment => (byte)((source.app_info >> 4) & 0b111);
        public byte Version => (byte)((source.app_info >> 2) & 0b11);

        /// <summary>
        /// probably 0: true, 1:false
        /// </summary>
        public bool McIntro => ((source.app_info >> 1) & 0b1) != 0;

        /// <summary>
        /// 0=solo, 1=duet
        /// </summary>
        public bool Mode => (source.app_info & 0b1) != 0; // 
        internal KaraokeData(AudioAttributes data)
        {
            this.source = data;
        }
    }
    public class SurroundData
    {
        private AudioAttributes source;
        public byte Unknown5 => (byte)((source.app_info >> 4) & 0b1111);
        public bool DolbyEncoded => ((source.app_info >> 3) & 0b1) != 0; // Suitable for surround decoding
        public byte Unknown6 => (byte)(source.app_info & 0b111);
        internal SurroundData(AudioAttributes data)
        {
            this.source = data;
        }
    }
}