
using System.Collections;
using Iso639;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Menu PGCI Unit Table <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#pgciut"/>
/// </summary>
public class MenuProgramChainLanguageUnitTable : IEnumerable<MenuProgramChainLanguageUnit>
{
    private readonly MenuProgramChainLanguageUnit[] lu;

    public int Count => lu.Length;

    public MenuProgramChainLanguageUnit this[int index] => lu[index];

    IEnumerator<MenuProgramChainLanguageUnit> IEnumerable<MenuProgramChainLanguageUnit>.GetEnumerator()
    {
        return ((IEnumerable<MenuProgramChainLanguageUnit>)lu).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return lu.GetEnumerator();
    }

    internal MenuProgramChainLanguageUnitTable(DvdStream file, uint offset, bool isVMG)
    {
        file.Seek(offset);

        // Read data
        ushort nr_of_lus = file.Read<ushort>();
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        // verify
        Util.Assert(nr_of_lus != 0, "Number of LUs cant be zero.");
        Util.Assert(nr_of_lus < 100, "Too many LUs."); /* ?? 3-4 ? */
        Util.Assert((uint)nr_of_lus * MenuProgramChainLanguageUnit.Size < last_byte, "Number of LUs too large.");

        // Read table data
        lu = new MenuProgramChainLanguageUnit[nr_of_lus];
        Dictionary<uint, ProgamChainInformationTable> pgclu_offsets = new();
        for(int i = 0; i < nr_of_lus; i++)
        {
            lu[i] = new MenuProgramChainLanguageUnit(file, offset, pgclu_offsets, isVMG);
        }
    }
}

/// <summary>
/// Menu PGCI Language Unit <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#pgciut"/>
/// </summary>
public class MenuProgramChainLanguageUnit
{
    internal const uint Size = 8;

    /// <summary>
    /// ISO639 language code
    /// </summary>
    public readonly Language? Language;
    public readonly byte LanguageExtension;
    public readonly byte MenuExistenceFlag;
    public readonly ProgamChainInformationTable PgcTable;
    internal readonly uint lang_start_byte;

    internal MenuProgramChainLanguageUnit(DvdStream file, uint offset_base, Dictionary<uint, ProgamChainInformationTable> pgclu_offsets, bool isVMG)
    {
        // Read data
        string lang = file.ReadString(2);
        if (string.IsNullOrWhiteSpace(lang)) this.Language = null;
        else
        {
            try
            {
                this.Language = Iso639.Language.FromPart1(lang);
            } catch(Exception)
            {
                this.Language = null;
            }
        }
        LanguageExtension = file.Read<byte>();
        MenuExistenceFlag = file.Read<byte>();
        lang_start_byte = file.Read<uint>();

        /* Maybe this is only defined for v1.1 and later titles? */
        /* If the bits in 'lu[i].exists' are enumerated abcd efgh then:
            VTS_x_yy.IFO        VIDEO_TS.IFO
            a == 0x83 "Root"         0x82 "Title"
            b == 0x84 "Subpicture"
            c == 0x85 "Audio"
            d == 0x86 "Angle"
            e == 0x87 "PTT"
        */
        Util.Assert((MenuExistenceFlag & 0x07) == 0, "Invalid menu flag.");

        if (!pgclu_offsets.TryGetValue(lang_start_byte, out PgcTable!))
        {
            uint pos = file.Position;
            PgcTable = new ProgamChainInformationTable(file, offset_base + lang_start_byte, isVMG);
            pgclu_offsets[lang_start_byte] = this.PgcTable;
            file.Seek(pos);
        }
    }
}