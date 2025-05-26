
using System.Collections;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// PartOfTitle Search Pointer Table <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#tt"/>
/// </summary>
public class ChapterSearchPointerTable : IEnumerable<ChapterInfo>
{
    internal const uint Size = 8; // Only the header, doesnt include table data

    private readonly ChapterInfo[] title;

    public int Count => title.Length;

    public ChapterInfo this[int index] => title[index];

    IEnumerator<ChapterInfo> IEnumerable<ChapterInfo>.GetEnumerator()
    {
        return ((IEnumerable<ChapterInfo>)title).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return title.GetEnumerator();
    }

    internal ChapterSearchPointerTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read Data
        ushort nr_of_srpts = file.Read<ushort>();
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        /* E-One releases don't fill this field */
        if (last_byte == 0)
        {
            last_byte = nr_of_srpts * ChapterInfo.Size + ChapterSearchPointerTable.Size - 1;
        }

        // Read table data
        var info_length = last_byte + 1 - ChapterSearchPointerTable.Size;
        if (nr_of_srpts > info_length / ChapterInfo.Size)
        {
            file.OnWarning?.Invoke($"Title table lists {nr_of_srpts} titles but only has room for {info_length / ChapterInfo.Size}. Using the first {info_length / ChapterInfo.Size}.");
            nr_of_srpts = (ushort)(info_length / ChapterInfo.Size);
        }

        title = new ChapterInfo[nr_of_srpts];
        for (int i = 0; i < title.Length; i++)
            title[i] = new ChapterInfo(file);

        Util.Assert(nr_of_srpts != 0, "Invalid number of SRPTS");
        Util.Assert(nr_of_srpts < 100, "Invalid number of SRPTS."); /* ?? */
        Util.Assert(nr_of_srpts * ChapterInfo.Size <= info_length, "Invalid size of SRPTS");
    }
}

public class PlaybackType
{
    public readonly bool IsSequential; // 1 bit. 0 = one sequential pgc title
    public readonly bool CommandsValidInCell; // 1 bit
    public readonly bool CommandsValidInPrePost; // 1 bit
    public readonly bool CommandsValidInButton; // 1 bit
    public readonly bool jlc_exists_in_tt_dom; // 1 bit
    public readonly bool ChapterSearchOrPlay; // 1 bit. UOP 1
    public readonly bool ChapterOrTimePlay; // 1 bit. UOP 0

    internal PlaybackType(DvdStream file)
    {
        BitReader bits = new BitReader(file.Stream);

        Util.AssertZero(bits.ReadBit() ? 1 : 0);
        IsSequential = bits.ReadBit();
        CommandsValidInCell = bits.ReadBit();
        CommandsValidInPrePost = bits.ReadBit();
        CommandsValidInButton = bits.ReadBit();
        jlc_exists_in_tt_dom = bits.ReadBit();
        ChapterSearchOrPlay = bits.ReadBit();
        ChapterOrTimePlay = bits.ReadBit();

        Util.Assert(bits.IsByteAligned, "Bit fields don't end on a byte boundary.");
    }
}

/// <summary>
/// Title information <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#tt"/>
/// </summary>
public class ChapterInfo
{
    internal const uint Size = 12;

    public readonly PlaybackType ChapterType;
    public readonly byte NumberOfAngles;
    public readonly ushort NumberOfChapters;
    public readonly ushort ParentalManagementMask;
    public readonly byte TitleSetNumber;
    public readonly byte ChapterNumber;
    public readonly uint TitleSetSector;

    internal ChapterInfo(DvdStream file)
    {
        // Read data
        ChapterType = new PlaybackType(file);
        NumberOfAngles = file.Read<byte>();
        NumberOfChapters = file.Read<ushort>();
        ParentalManagementMask = file.Read<ushort>();
        TitleSetNumber = file.Read<byte>();
        ChapterNumber = file.Read<byte>();
        TitleSetSector = file.Read<uint>();

        Util.Assert(NumberOfAngles != 0, "Invalid number of angles.");
        Util.Assert(NumberOfAngles < 10, "Invalid number of angles.");
        // CHECK_VALUE(tt_srpt->title[i].nr_of_ptts != 0); 
        // XXX: this assertion breaks Ghostbusters: 
        Util.Assert(NumberOfChapters < 1000, "Invalid number of chapters."); // ?? 
        Util.Assert(TitleSetNumber != 0, "Invalid title set number.");
        Util.Assert(TitleSetNumber < 100, "Invalid title set number."); // ?? 
        Util.Assert(ChapterNumber != 0, "Invalid chapter number.");
        Util.Assert(ChapterNumber < 100, "Invalid chapter number."); // ?? 
        // CHECK_VALUE(tt_srpt->title[i].title_set_sector != 0); 
    }
}