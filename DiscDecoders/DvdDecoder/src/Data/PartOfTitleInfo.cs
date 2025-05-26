
using System.Collections;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// PartOfTitle Search Pointer Table <see cref="http://www.mpucoder.com/DVD/ifo_vts.html#ptt"/>
/// Same as VMG?
/// </summary>
public class PartOfTitleSearchPointerTable : IEnumerable<PartOfTitleTable>
{
    internal const uint Size = 8;

    public int Count => titles.Length;
    public PartOfTitleTable this[int index] => titles[index];

    private readonly PartOfTitleTable[] titles;

    IEnumerator<PartOfTitleTable> IEnumerable<PartOfTitleTable>.GetEnumerator()
    {
        return ((IEnumerable<PartOfTitleTable>)titles).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return titles.GetEnumerator();
    }

    internal PartOfTitleSearchPointerTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        ushort nr_of_srpts = file.Read<ushort>();
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        // Verify
        Util.Assert(nr_of_srpts != 0, "Invalid number of SRPTS.");
        Util.Assert(nr_of_srpts < 100, "Invalid number of SRPTS."); /* ?? */

        /* E-One releases don't fill this field */
        if (last_byte == 0)
        {
            last_byte = nr_of_srpts * (uint)sizeof(uint) + PartOfTitleSearchPointerTable.Size - 1;
        }

        // Read offsets
        uint info_length = last_byte - PartOfTitleSearchPointerTable.Size + 1;
        if (nr_of_srpts > info_length / sizeof(uint)) throw new IOException("PTT search table too small");
        if (nr_of_srpts == 0) throw new IOException("Zero entries in PTT search table.");

        uint[] ttu_offset = new uint[nr_of_srpts];
        file.Read(ttu_offset);

        for (int i = 0; i < nr_of_srpts; i++)
        {

            /* Transformers 3 has PTT start bytes that point outside the SRPT PTT */
            /* assert(data[i] + sizeof(ptt_info_t) <= vts_ptt_srpt->last_byte + 1);
                Magic Knight Rayearth Daybreak is mastered very strange and has
                Titles with 0 PTTs. They all have a data[i] offsets beyond the end of
                of the vts_ptt_srpt structure. */
            Util.Assert(ttu_offset[i] + PartOfTitleInfo.Size <= last_byte + 1 + 4, "Invalid size.");
        }

        titles = new PartOfTitleTable[nr_of_srpts];
        for (int i = 0; i < nr_of_srpts; i++)
        {
            uint n; // The number of PTT entries in this PTT table
            if (i < nr_of_srpts - 1)
                n = (ttu_offset[i + 1] - ttu_offset[i]);
            else
                n = (last_byte + 1 - ttu_offset[i]);

            /* assert(n > 0 && (n % 4) == 0);
                Magic Knight Rayearth Daybreak is mastered very strange and has
                Titles with 0 PTTs. */
            if (n < 0) n = 0;

            /* DVDs created by the VDR-to-DVD device LG RC590M violate the following requirement */
            Util.Assert(n % 4 == 0, "Invalid size.");
            if (n / 4 > 0)
            {
                /* The assert placed here because of Magic Knight Rayearth Daybreak */
                Util.Assert(ttu_offset[i] + PartOfTitleInfo.Size <= last_byte + 1, "Invalid size.");
            }
            titles[i] = new PartOfTitleTable(file, offset + ttu_offset[i], n / 4);
        }
    }
}

/// <summary>
/// PartOfTitle Unit Information <see cref="http://www.mpucoder.com/DVD/ifo_vts.html#ptt"/> 
/// </summary>
public class PartOfTitleTable : IEnumerable<PartOfTitleInfo>
{
    private readonly PartOfTitleInfo[] ptt;

    public int Count => ptt.Length;
    public PartOfTitleInfo this[int index] => ptt[index];

    IEnumerator<PartOfTitleInfo> IEnumerable<PartOfTitleInfo>.GetEnumerator()
    {
        return ((IEnumerable<PartOfTitleInfo>)ptt).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ptt.GetEnumerator();
    }

    internal PartOfTitleTable(DvdStream file, uint offset, uint numEntries)
    {
        Util.Assert(numEntries < 1000, "Invalud number of entries."); // ??
        file.Seek(offset);
        ptt = new PartOfTitleInfo[numEntries];
        for (int j = 0; j < numEntries; j++)
        {
            ptt[j] = new PartOfTitleInfo(file);
        }
    }
}

/// <summary>
/// PartOfTitle Unit Information <see cref="http://www.mpucoder.com/DVD/ifo_vts.html#ptt"/> 
/// </summary>
public class PartOfTitleInfo
{
    internal const uint Size = 4;

    public readonly ushort ProgramChainNumber;
    public readonly ushort ProgramNumber;

    internal PartOfTitleInfo(DvdStream file)
    {
        ProgramChainNumber = file.Read<ushort>();
        ProgramNumber = file.Read<ushort>();

        // ??
        Util.Assert(ProgramChainNumber != 0, "Invalid PCN number.");
        Util.Assert(ProgramChainNumber < 1000, "Invalid PCN number."); /* ?? */
        Util.Assert(ProgramNumber != 0, "Invalud PN number.");
        Util.Assert(ProgramNumber < 100, "Invalid PN number."); /* ?? */
        //don't abort here. E-One DVDs contain PTT with pgcn or pgn == 0
    }
}