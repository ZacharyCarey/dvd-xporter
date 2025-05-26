
using System.Collections;
using System.Collections.ObjectModel;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Cell address table <see cref="http://www.mpucoder.com/DVD/ifo.html#c_adt"/>
/// </summary>
public class CellAddressTable : IEnumerable<CellAddress>
{
    private const uint Size = 8;

    /// <summary>
    /// This value has been found to be inconsistant across discs.
    /// The <see cref="Cells.Count"/> array length should be used intead, but
    /// this field is still provided in case it's needed.
    /// </summary>
    [Obsolete]
    public readonly ushort NumberOfVOBs;

    /// <summary>
    /// A list of cells in this table
    /// </summary>
    public readonly ReadOnlyCollection<CellAddress> Cells;

    public CellAddress this[int index]
    {
        get => Cells[index];
    }

    IEnumerator<CellAddress> IEnumerable<CellAddress>.GetEnumerator()
    {
        return ((IEnumerable<CellAddress>)Cells).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return Cells.GetEnumerator();
    }

    internal CellAddressTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
#pragma warning disable CS0612 // Type or member is obsolete
        NumberOfVOBs = file.Read<ushort>();
#pragma warning restore CS0612 // Type or member is obsolete
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        // Verify
        if (last_byte + 1 < CellAddressTable.Size)
            throw new IOException();

        uint info_length = last_byte + 1 - CellAddressTable.Size;

        /* assert(c_adt->nr_of_vobs > 0);
            Magic Knight Rayearth Daybreak is mastered very strange and has
            Titles with a VOBS that has no cells. */
        Util.Assert(info_length % CellAddress.Size == 0, "Invalid info length.");

        /* assert(info_length / sizeof(cell_adr_t) >= c_adt->nr_of_vobs);
            Enemy of the State region 2 (de) has Titles where nr_of_vobs field
            is to high, they high ones are never referenced though. */

        // nr_of_vobs field appears to be unreliable, so calculate the actual value from the size of the table
        int nr_of_vobs = (ushort)(info_length / CellAddress.Size);

        CellAddress[] cells = new CellAddress[nr_of_vobs];
        for (int i = 0; i < nr_of_vobs; i++)
            cells[i] = new CellAddress(file);
        this.Cells = cells.AsReadOnly();

        // Verify
        foreach (var cell in Cells)
        {
            Util.Assert(cell.VobID <= nr_of_vobs, "Invalid cell Vob ID.");
        }
    }
}

/// <summary>
/// Cell address information <see cref="http://www.mpucoder.com/DVD/ifo.html#c_adt"/>
/// </summary>
public class CellAddress
{
    internal const uint Size = 12;

    public readonly ushort VobID;
    public readonly byte CellID;
    public readonly uint StartSector;
    public readonly uint LastSector;

    internal CellAddress(DvdStream file)
    {
        // Read data
        VobID = file.Read<ushort>();
        CellID = file.Read<byte>();
        file.ReadZeros(1);
        StartSector = file.Read<uint>();
        LastSector = file.Read<uint>();

        // Verify
        Util.Assert(VobID > 0, "Invalid Vob ID.");
        Util.Assert(CellID > 0, "Invalid Cell ID");
        Util.Assert(StartSector < LastSector, "Invalid start sector.");
    }

}