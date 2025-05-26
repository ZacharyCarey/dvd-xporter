
namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Video Title Set Attribute Table <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#atrt"/>
/// Supposedly just copies of VTS attributes, not sure if it is usefull
/// </summary>
public class VtsAttributeTable
{
    internal const uint Size = 8;

    public readonly ushort NumberOfVTSs;
    //public vts_attributes_t[] vts;

    internal VtsAttributeTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        NumberOfVTSs = file.Read<ushort>();
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        // Verify
        Util.Assert(NumberOfVTSs != 0, "Invalid number of VTSs.");
        Util.Assert(NumberOfVTSs < 100, "Invalid number of VTSs."); /* ?? */
        Util.Assert((uint)NumberOfVTSs * (4 + vts_attributes_t.MIN_SIZE) + VtsAttributeTable.Size < last_byte + 1, "Invalid size.");

        // Offsets table for each vts_attributes
        uint[] vts_atrt_offsets = new uint[NumberOfVTSs];
        file.Read(vts_atrt_offsets);

        for (int i = 0; i < NumberOfVTSs; i++)
        {
            Util.Assert(vts_atrt_offsets[i] + vts_attributes_t.MIN_SIZE < last_byte + 1, "Invalid size."); // Verify
        }

        /*vts = new vts_attributes_t[nr_of_vtss];
        for (int i = 0; i < nr_of_vtss; i++) {
            uint offset = vts_atrt_offsets[i];
            vts[i] = new vts_attributes_t(file, (sector * DvdUtils.DVD_BLOCK_LEN) + offset);

            // This assert can't be in ifoRead_VTS_ATTRIBUTES 
            DvdUtils.CHECK_VALUE(offset + vts[i].last_byte <= last_byte + 1);
            // Is this check correct? 
        }*/
    }

}

/// <summary>
/// Video Title Set Attributes.
/// </summary>
internal class vts_attributes_t
{
    internal const uint MIN_SIZE = 356;

    /*internal uint last_byte;
    internal uint vts_cat;

    internal video_attr_t vtsm_vobs_attr = new();
    internal byte zero_1;

    /// <summary>
    /// should be 0 or 1
    /// </summary>
    internal byte nr_of_vtsm_audio_streams;
    internal audio_attr_t vtsm_audio_attr = new();
    internal audio_attr_t[] zero_2 = DvdUtils.CreateClassArray<audio_attr_t>(7);
    internal byte[] zero_3 = new byte[16];
    internal byte zero_4;

    /// <summary>
    /// should be 0 or 1
    /// </summary>
    internal byte nr_of_vtsm_subp_streams;
    internal subp_attr_t vtsm_subp_attr = new();
    internal subp_attr_t[] zero_5 = DvdUtils.CreateClassArray<subp_attr_t>(27);

    internal byte[] zero_6 = new byte[2];

    internal video_attr_t vtstt_vobs_video_attr = new();
    internal byte zero_7;
    internal byte nr_of_vtstt_audio_streams;
    internal audio_attr_t[] vtstt_audio_attr = DvdUtils.CreateClassArray<audio_attr_t>(8);
    internal byte[] zero_8 = new byte[16];
    internal byte zero_9;
    internal byte nr_of_vtstt_subp_streams;
    internal subp_attr_t[] vtstt_subp_attr = DvdUtils.CreateClassArray<subp_attr_t>(32);*/

    internal vts_attributes_t(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        // TODO parse data ifoRead_VTS_ATTRIBUTES
        byte[] data = new byte[542];
        file.Read(data);
    }
}