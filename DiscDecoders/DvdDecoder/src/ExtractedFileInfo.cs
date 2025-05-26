
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public class ExtractedFileInfo
{
    public string FileName = null!;
    public long Size; // Size in bytes
    public uint Title;
    public bool IsMenu;
    public int VobID;
    public int CellID;
    public List<MpegStream> Streams = new();
    public DiscTime StartTime = new DiscTime(0);
    public DiscTime Duration = new DiscTime(0);
}

public struct MpegStream
{
    public byte StreamID;
    public MpegStreamType Type;
}

public enum MpegStreamType
{
    Navigation,
    Video,
    Audio,
    Subpicture
}
