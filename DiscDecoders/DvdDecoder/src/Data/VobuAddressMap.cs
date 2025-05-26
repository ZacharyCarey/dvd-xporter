
using System.Collections;
using System.Collections.ObjectModel;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// VOBU address map.
/// Contains the start sectors for VOBUs
/// </summary>
public class VobuAddressMap : IEnumerable<uint>
{
    internal const uint Size = 4;

    public ReadOnlyCollection<uint> VobuStartSectors;
    public int Count => VobuStartSectors.Count;
    public uint this[int index] => VobuStartSectors[index];

    IEnumerator<uint> IEnumerable<uint>.GetEnumerator()
    {
        return VobuStartSectors.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return VobuStartSectors.GetEnumerator();
    }

    internal VobuAddressMap(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        uint last_byte = file.Read<uint>();

        uint info_length = last_byte + 1 - VobuAddressMap.Size;
        /* assert(info_length > 0);
            Magic Knight Rayearth Daybreak is mastered very strange and has
            Titles with a VOBS that has no VOBUs. */
        Util.Assert(info_length % sizeof(uint) == 0, "Invalid length.");

        uint[] vobu_start_sectors = new uint[info_length / sizeof(uint)];
        file.Read<uint>(vobu_start_sectors);
        VobuStartSectors = vobu_start_sectors.AsReadOnly();
    }
}