
using System.Collections;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Parental management information table <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#mait"/>
/// </summary>
public class ParentalManagementInfoTable : IEnumerable<ParentalManagementInfo>
{
    internal const uint Size = 8;
    internal const uint PTL_MAIT_NUM_LEVEL = 8;

    private readonly ParentalManagementInfo[] countries;
    public int Count => countries.Length;

    public ParentalManagementInfo this[int index] => countries[index];

    IEnumerator<ParentalManagementInfo> IEnumerable<ParentalManagementInfo>.GetEnumerator()
    {
        return ((IEnumerable<ParentalManagementInfo>)countries).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return countries.GetEnumerator();
    }

    internal ParentalManagementInfoTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        ushort nr_of_countries = file.Read<ushort>();
        ushort nr_of_vtss = file.Read<ushort>();
        uint last_byte = file.Read<uint>();

        // Verify
        Util.Assert(nr_of_countries != 0, "Invalid number of countries.");
        Util.Assert(nr_of_countries < 100, "Invalid number of countries."); /* ?? */
        Util.Assert(nr_of_vtss != 0, "Invalid number of VTSS.");
        Util.Assert(nr_of_vtss < 100, "Invalid number of VTSS."); /* ?? */
        Util.Assert(nr_of_countries * ParentalManagementInfo.Size <= last_byte + 1 - ParentalManagementInfoTable.Size, "Invalid size.");

        // Parse countries
        countries = new ParentalManagementInfo[nr_of_countries];
        for (int i = 0; i < countries.Length; i++)
        {
            var pos = file.Position;
            countries[i] = new ParentalManagementInfo(file, nr_of_vtss, last_byte, offset);
            file.Seek(pos + ParentalManagementInfo.Size);
        }

    }
}

/// <summary>
/// Parental management information unit table
/// </summary>
public class ParentalManagementInfo
{
    internal const uint Size = 8;

    public readonly ushort CountryCode;
    internal readonly ushort pf_ptl_mai_start_byte;

    /// <summary>
    /// Parental Management Information Unit Table.
    /// Level 1 (US: G), ..., 7 (US: NC-17), 8
    /// </summary>
    public readonly ushort[][] pf_ptl_mai;

    internal ParentalManagementInfo(DvdStream file, ushort nr_of_vtss, uint last_byte, uint offset)
    {
        // Read data
        CountryCode = file.Read<ushort>();
        file.ReadZeros(2);
        pf_ptl_mai_start_byte = file.Read<ushort>();
        file.ReadZeros(2);

        // Verify
        Util.Assert(pf_ptl_mai_start_byte + 2 * (nr_of_vtss + 1) <= last_byte + 1, "Invalid size.");

        file.Seek(offset + pf_ptl_mai_start_byte);
        ushort[] pf_temp = new ushort[(nr_of_vtss + 1) * 8];
        file.Read(pf_temp); ;

        pf_ptl_mai = new ushort[nr_of_vtss + 1U][];
        for (int j = 0; j < pf_ptl_mai.Length; j++)
            pf_ptl_mai[j] = new ushort[8]; // one ushort for each level of the PTL_MAIT

        // Transpose the array so we can use C indexing. 
        int level, vts;
        for (level = 0; level < ParentalManagementInfoTable.PTL_MAIT_NUM_LEVEL; level++)
        {
            for (vts = 0; vts <= nr_of_vtss; vts++)
            {
                pf_ptl_mai[vts][level] =
                    pf_temp[(7 - level) * (nr_of_vtss + 1) + vts];
            }
        }
    }
}