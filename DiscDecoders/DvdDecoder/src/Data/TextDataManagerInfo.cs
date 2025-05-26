
namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Text data manager information (Incomplete)
/// </summary>
public class TextDataManagerInfo
{
    /// <summary>
    /// 12 chars
    /// </summary>
    public readonly string DiscName;
    public readonly ushort NumberOfLanguageUnits;
    //public txtdt_lu_t? lu = null;

    internal TextDataManagerInfo(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        DiscName = file.ReadString(12);
        ushort unknown1 = file.Read<ushort>();
        NumberOfLanguageUnits = file.Read<ushort>();
        uint last_byte = file.Read<uint>();

        // TODO: What about lu???
    }
}