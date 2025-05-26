
using System.Collections;
using System.Collections.ObjectModel;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Time Map Table <see cref="http://www.mpucoder.com/DVD/ifo_vts.html#tmap"/>
/// </summary>
public class TimeMapTable : IEnumerable<TimeMap>
{
    private readonly TimeMap[] tmap;
    public int Count => tmap.Length;
    public TimeMap this[int index] => tmap[index];

    IEnumerator<TimeMap> IEnumerable<TimeMap>.GetEnumerator()
    {
        return ((IEnumerable<TimeMap>)tmap).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return tmap.GetEnumerator();
    }

    internal TimeMapTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        ushort nr_of_tmaps = file.Read<ushort>();
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        uint[] tmap_offset = new uint[nr_of_tmaps];
        file.Read(tmap_offset);

        tmap = new TimeMap[nr_of_tmaps];
        for (int i = 0; i < nr_of_tmaps; i++)
        {
            tmap[i] = new TimeMap(file, offset + tmap_offset[i]);
        }
    }
}

/// <summary>
/// Time map
/// </summary>
public class TimeMap
{
    /// <summary>
    /// Time unit, in seconds
    /// </summary>
    public readonly byte TimeUnit;

    /// <summary>
    /// bit 31 is set if next time entry is for a different cell
    /// 
    /// sector offset within VOBS of vobu which begins on or before the time for this entry and ends after the time for this entry.
    /// bit 31 is set if the next time entry is for a different cell
    /// </summary>
    public readonly ReadOnlyCollection<uint> SectorOffsets; // TODO: should this be bit field at all or just the uint32_t?

    internal TimeMap(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        TimeUnit = file.Read<byte>();
        file.ReadZeros(1);
        ushort nr_of_entries = file.Read<ushort>();

        if (nr_of_entries == 0)
        {
            SectorOffsets = Array.Empty<uint>().AsReadOnly();
            return;
        }

        uint[] sectorOffsets = new uint[nr_of_entries];
        file.Read(sectorOffsets);
        SectorOffsets = sectorOffsets.AsReadOnly();
    }
}