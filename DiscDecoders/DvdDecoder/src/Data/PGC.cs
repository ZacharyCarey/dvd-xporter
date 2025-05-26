
using System.Collections;
using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Program chain information table <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#pgciut"/> 
/// </summary>
public class ProgamChainInformationTable : IEnumerable<ProgramChainInfoSearchPointer>
{
    private readonly ProgramChainInfoSearchPointer[] pgci_srp;

    public int Count => pgci_srp.Length;
    public ProgramChainInfoSearchPointer this[int index] => pgci_srp[index];

    IEnumerator<ProgramChainInfoSearchPointer> IEnumerable<ProgramChainInfoSearchPointer>.GetEnumerator()
    {
        return ((IEnumerable<ProgramChainInfoSearchPointer>)pgci_srp).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return pgci_srp.GetEnumerator();
    }

    internal ProgamChainInformationTable(DvdStream file, uint offset, bool isVMG)
    {
        file.Seek(offset);

        // Read data
        ushort nr_of_pgci_srp = file.Read<ushort>();
        file.ReadZeros(2);
        uint last_byte = file.Read<uint>();

        // Verify 
        /* assert(pgcit->nr_of_pgci_srp != 0);
            Magic Knight Rayearth Daybreak is mastered very strange and has
            Titles with 0 PTTs. */
        Util.Assert(nr_of_pgci_srp < 10000, "Invalid number of PGCI."); /* ?? seen max of 1338 */

        if (nr_of_pgci_srp == 0)
        {
            pgci_srp = Array.Empty<ProgramChainInfoSearchPointer>();
            return;
        }

        pgci_srp = new ProgramChainInfoSearchPointer[nr_of_pgci_srp];
        Dictionary<uint, PGC> pgc_index = new(); // Used to find duplicate PGCs
        for (int i = 0; i < pgci_srp.Length; i++)
        {
            pgci_srp[i] = new ProgramChainInfoSearchPointer(file, isVMG, offset, pgc_index);
            Util.Assert(pgci_srp[i].Offset + PGC.Size <= last_byte + 1, "Invalid PGCI offset.");
        }
    }

}

/// <summary>
/// Program chain information search pointer <see cref="http://www.mpucoder.com/DVD/ifo_vmg.html#pgciut"/>
/// </summary>
public class ProgramChainInfoSearchPointer
{
    public readonly PgcCategory Category;
    public readonly PGC Pgc;
    internal readonly uint Offset;

    internal ProgramChainInfoSearchPointer(DvdStream file, bool isVMG, uint base_offset, Dictionary<uint, PGC> pgc_index)
    {
        // Read data
        Category = PgcCategory.Parse(file, isVMG);
        Offset = file.Read<uint>();
        if (!pgc_index.TryGetValue(Offset, out Pgc!))
        {
            uint pos = file.Position;
            this.Pgc = new PGC(file, base_offset + Offset);
            file.Seek(pos);

            pgc_index[Offset] = this.Pgc;
        }
    }
}

/// <summary>
/// Program chain information <see cref="http://www.mpucoder.com/DVD/pgc.html"/>
/// </summary>
public class PGC
{
        internal const uint Size = 236;

        public readonly byte NumberOfPrograms;
        public readonly byte NumberOfCells;
        public readonly DiscTime PlaybackTime;
        public readonly double PlaybackFPS;
        public readonly UserOperations ProhibitedOperations;
        public readonly ReadOnlyCollection<AudioStreamControl> AudioStreams;
        public readonly ReadOnlyCollection<SubpictureStreamControl> SubpictureStreams;
        public readonly ushort NextPgcNumber;
        public readonly ushort PreviousPgcNumber;
        /// <summary>The PGC a menu's "go up" button goes to (0 for none).</summary>
        public readonly ushort GoUpPgcNumber;
        
        /// <summary>
        /// 0 = Sequential, otherwise bit7 indicated random (0) or shuffle (1), and 
        /// the program count-1 is in bits 6-0 (minimum program count is 2)
        /// </summary>
        public readonly byte PGPlaybackMode;

        /// <summary>
        /// 255 = infinite
        /// </summary>
        public readonly byte StillTime;
        public readonly ReadOnlyCollection<uint> Palette; // TODO: New type struct {zero_1, Y, Cr, Cb} ?
        public readonly PgcCommandTable? command_tbl = null;

        /// <summary>
        /// PGC Program map
        /// </summary>
        public readonly ReadOnlyCollection<byte> ProgramMap;
        public readonly ReadOnlyCollection<CellPlaybackInfo> CellPlayback;
        public readonly ReadOnlyCollection<CellPositionInfo> CellPosition;

        /// <summary>
        /// The detected number of angles in this PGC.
        /// </summary>
        public readonly uint NumberOfAngles;

    internal PGC(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        file.ReadZeros(2);
        NumberOfPrograms = file.Read<byte>();
        NumberOfCells = file.Read<byte>();
        PlaybackTime = file.ReadDuration(out PlaybackFPS);
        ProhibitedOperations = new UserOperations(file);

        List<AudioStreamControl> audio_streams = new();
        for (int i = 0; i < 8; i++)
        {
            audio_streams.Add(new AudioStreamControl(file));
        }
        AudioStreams = audio_streams.AsReadOnly();

        List<SubpictureStreamControl> sub_streams = new();
        for (int i = 0; i < 32; i++)
        {
            sub_streams.Add(new SubpictureStreamControl(file));
        }        
        SubpictureStreams = sub_streams.AsReadOnly();

        NextPgcNumber = file.Read<ushort>();
        PreviousPgcNumber = file.Read<ushort>();
        GoUpPgcNumber = file.Read<ushort>();
        PGPlaybackMode = file.Read<byte>();
        StillTime = file.Read<byte>();

        uint[] palette = new uint[16];
        for (int i = 0; i < 16; i++)
            palette[i] = file.Read<uint>();
        this.Palette = palette.AsReadOnly();

        ushort command_tbl_offset = file.Read<ushort>();
        ushort program_map_offset = file.Read<ushort>();
        ushort cell_playback_offset = file.Read<ushort>();
        ushort cell_position_offset = file.Read<ushort>();

        Util.Assert(NumberOfPrograms <= NumberOfCells, "Invalid number of programs.");

        /* Check that time is 0:0:0:0 also if nr_of_programs == 0 */
        if (NumberOfPrograms == 0)
        {
            Util.AssertZero(StillTime);
            Util.AssertZero(PGPlaybackMode); /* ?? */
            Util.Assert(program_map_offset == 0, "Invalid program map offset.");
            Util.Assert(cell_playback_offset == 0, "Invalid cell playback offset.");
            Util.Assert(cell_position_offset == 0, "Invalid cell position offset.");
        }
        else
        {
            Util.Assert(program_map_offset != 0, "Invalid program map offset.");
            Util.Assert(cell_playback_offset != 0, "Invalid cell playback offset.");
            Util.Assert(cell_position_offset != 0, "Invalid cell position offset.");
        }

        if (command_tbl_offset != 0)
        {
            command_tbl = new PgcCommandTable(file, offset + command_tbl_offset);
        }

        
        if (program_map_offset != 0 && NumberOfPrograms > 0)
        {
            file.Seek(offset + program_map_offset);
            byte[] programMap = new byte[NumberOfPrograms];
            for (int i = 0; i < NumberOfPrograms; i++)
                programMap[i] = file.Read<byte>();
            this.ProgramMap = programMap.AsReadOnly();
        } else
        {
            ProgramMap = Array.Empty<byte>().AsReadOnly();
        }

        if (cell_playback_offset != 0 && NumberOfCells > 0)
        {
            CellPlaybackInfo[] cellPlayback = new CellPlaybackInfo[NumberOfCells];
            CellPlaybackInfo.ifoRead_CELL_PLAYBACK_TBL(file, cellPlayback, offset + cell_playback_offset);
            this.CellPlayback = cellPlayback.AsReadOnly();
        } else
        {
            CellPlayback = Array.Empty<CellPlaybackInfo>().AsReadOnly();
        }

        if (cell_position_offset != 0 && NumberOfCells > 0)
        {
            CellPositionInfo[] cellPosition = new CellPositionInfo[NumberOfCells];
            CellPositionInfo.ifoRead_CELL_POSITION_TBL(file, cellPosition, offset + cell_position_offset);
            this.CellPosition = cellPosition.AsReadOnly();
        } else
        {
            CellPosition = Array.Empty<CellPositionInfo>().AsReadOnly();
        }

        // Find all angles
        NumberOfAngles = 1;
        for (int cell = 0; cell < NumberOfCells; cell++)
        {
            var info = CellPlayback[cell];
            if (info.IsFirstAngle)
                NumberOfAngles = 1;
            else if (info.IsMiddleAngle)
                NumberOfAngles++;
            else if (info.IsLastAngle)
            {
                NumberOfAngles++;
                break;
            }
        }
    }
}