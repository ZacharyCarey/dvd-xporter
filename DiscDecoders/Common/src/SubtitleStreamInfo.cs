using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

public class SubtitleStreamInfo : StreamInfo
{
    public override StreamType Type => StreamType.Subtitle;

    [JsonPropertyName("Codec"), JsonInclude]
    public SubtitleCodec Codec;

    /// <summary>Blu-ray text subtitles: the character set their text is in. Null for image subtitles.</summary>
    [JsonPropertyName("Character Set"), JsonInclude]
    public TextCharacterSet? CharacterSet = null;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubtitleCodec
{
    Unknown = -1,

    /// <summary>ARIB STD-B24 captions</summary>
    arib_caption,

    /// <summary>ASS (Advanced SSA) subtitle</summary>
    ass,

    /// <summary>DVB subtitles</summary>
    dvb_subtitle,

    /// <summary>DVB teletext</summary>
    dvb_teletext,

    /// <summary>DVD subtitles</summary>
    dvd_subtitle,

    /// <summary>EIA-608 closed captions</summary>
    eia_608,

    /// <summary>HDMV Presentation Graphic Stream subtitles</summary>
    hdmv_pgs_subtitle,

    /// <summary>HDMV Text subtitle</summary>
    hdmv_text_subtitle,

    /// <summary>JACOsub subtitle</summary>
    jacosub,              

    /// <summary>MicroDVD subtitle</summary>
    microdvd,             
 
    /// <summary>MOV text</summary>
    mov_text,

    /// <summary>MPL2 subtitle</summary>
    mpl2,

    /// <summary>PJS (Phoenix Japanimation Society) subtitle</summary>                 
    pjs,

    /// <summary>RealText subtitle</summary>                  
    realtext,

    /// <summary>SAMI subtitle</summary>             
    sami,   

    /// <summary>SubRip subtitle with embedded timing</summary>              
    srt,        

    /// <summary>SSA (SubStation Alpha) subtitle</summary>          
    ssa, 

    /// <summary>Spruce subtitle format</summary>                 
    stl,  

    /// <summary>SubRip subtitle</summary>                
    subrip,

    /// <summary>SubViewer subtitle</summary>
    subviewer,       

    /// <summary><SubViewer v1 subtitle/summary>     
    subviewer1,  

    /// <summary>raw UTF-8 text</summary>         
    text,  

    /// <summary>Timed Text Markup Language</summary>               
    ttml,  

    /// <summary>VPlayer subtitle</summary>               
    vplayer,

    /// <summary>WebVTT subtitle</summary>              
    webvtt,               
    
    /// <summary>XSUB</summary>
    xsub
}

public enum SubtitleType
{
    Unknown = -1,
    Text,
    Image
}

public static class SubtitleExtensions
{
    /// <summary>Whether a subtitle format is text or images.</summary>
    /// <remarks>Not called GetType: object.GetType() would always be chosen instead, so it could never be called.</remarks>
    public static SubtitleType GetSubtitleType(this SubtitleCodec codec)
    {
        switch(codec)
        {
            case SubtitleCodec.hdmv_pgs_subtitle:
            case SubtitleCodec.dvd_subtitle:
            case SubtitleCodec.dvb_subtitle:
            case SubtitleCodec.xsub:
                return SubtitleType.Image;
            case SubtitleCodec.hdmv_text_subtitle:
            case SubtitleCodec.subrip:
            case SubtitleCodec.srt:
            case SubtitleCodec.ass:
            case SubtitleCodec.ssa:
            case SubtitleCodec.mov_text:
            case SubtitleCodec.webvtt:
            case SubtitleCodec.text:
            case SubtitleCodec.ttml:
            case SubtitleCodec.sami:
            case SubtitleCodec.microdvd:
            case SubtitleCodec.mpl2:
            case SubtitleCodec.pjs:
            case SubtitleCodec.realtext:
            case SubtitleCodec.stl:
            case SubtitleCodec.subviewer:
            case SubtitleCodec.subviewer1:
            case SubtitleCodec.vplayer:
            case SubtitleCodec.jacosub:
                return SubtitleType.Text;
            default:
                return SubtitleType.Unknown;
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextCharacterSet
{
    UTF8,
    UTF16BE,
    ShiftJIS,
    EUCKR,
    GB18030,
    CNGB,
    Big5,
}
