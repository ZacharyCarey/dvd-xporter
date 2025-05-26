
namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Cell position information <see cref="http://www.mpucoder.com/DVD/pgc.html#pos"/>
/// </summary>
public class CellPositionInfo
{
    public readonly ushort VobID;
    public readonly byte CellID;

    private CellPositionInfo(DvdStream file)
    {
        // Read data
        VobID = file.Read<ushort>();
        Util.AssertZero(file.Read<byte>());
        CellID = file.Read<byte>();
    }

    internal static void ifoRead_CELL_POSITION_TBL(DvdStream file, CellPositionInfo[] cells, uint offset)
    {
        file.Seek(offset);
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = new CellPositionInfo(file);
        }
    }
}