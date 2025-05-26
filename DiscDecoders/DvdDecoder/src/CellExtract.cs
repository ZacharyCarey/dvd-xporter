
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

internal class CellExtract
{
    readonly byte[] Buffer = new byte[Util.SECTOR_SIZE];
    readonly Action<string>? onWarning;

    // Problems with individual sectors are counted and reported once per extraction rather than once each,
    // since a damaged backup can have thousands.
    int invalidPackSectors;
    int invalidPackHeaders;
    int invalidStartCodes;

    /// <param name="onWarning">Receives non-fatal problems, such as damaged sectors that were skipped.</param>
    internal CellExtract(Action<string>? onWarning = null)
    {
        this.onWarning = onWarning;
    }

    private class CellExtraction
    {
        public ExtractedFileInfo Info = new();
        public Stream? Stream;
        /// <summary>The next sector's position in this cell's output file, used to rewrite nav pack addresses.</summary>
        public long LBA = 0;
        public DiscTime? StartTime;
        public DiscTime Duration = new DiscTime(0);
    }

    void Warn(string msg) => onWarning?.Invoke(msg);

    public List<ExtractedFileInfo> ExtractFiles(IDiscFileSystem reader, IfoFile title, bool isMenu, string demux_folder, Action<long>? progress = null)
    {
        progress?.Invoke(0);
        invalidPackSectors = invalidPackHeaders = invalidStartCodes = 0;
        string source = $"VTS_{title.TitleNumber:00} {(isMenu ? "menu" : "title")} VOBs";

        CellID? current_cell = null;

        Dictionary<CellID, CellExtraction> cells = new();
        try
        {
            using (VobStream dvd = new VobStream(reader, title, isMenu))
            {
                long totalBytes = dvd.Length;
                uint max_sector = (uint)(dvd.Length / Util.SECTOR_SIZE);
                if (dvd.Length % Util.SECTOR_SIZE != 0)
                    Warn($"{source}: {dvd.Length % Util.SECTOR_SIZE} bytes past the last whole sector were ignored.");

                for (uint sector = 0; sector < max_sector; sector++)
                {
                    long pos = (long)sector * Util.SECTOR_SIZE;
                    progress?.Invoke(pos);
                    if (dvd.Read(Buffer) != Util.SECTOR_SIZE)
                        break;

                    // Verify we are reading valid MPEG-2 stream
                    var header = ParseHeader();
                    if (header == null)
                    {
                        invalidPackSectors++;
                        continue;
                    }

                    Packet? packet = ParsePacket(this);
                    if (packet == null)
                        continue;

                    if (packet is NavPacket nav)
                        current_cell = new CellID(nav.VID, nav.CID);

                    if (current_cell != null)
                    { 
                        CellExtraction context;
                        if (!cells.TryGetValue(current_cell.Value, out context!))
                        {
                            context = new();
                            context.Info.VobID = current_cell.Value.VID;
                            context.Info.CellID = current_cell.Value.CID;
                            context.Info.IsMenu = isMenu;
                            context.Info.Title = title.TitleNumber;
                            context.Info.Size = 0;
                            cells[current_cell.Value] = context;
                        }

                        if (packet is NavPacket nav2)
                        {
                            // Nav packs record their own sector address. Point it at this pack's position in the
                            // output file rather than its original position on the disc.
                            NavPacket.ModifyLBA(this, context.LBA);
                            if(context.StartTime == null)
                                context.StartTime = nav2.VobuStartPTM;
                            if (nav2.VobuEndPTM.Ticks > context.Duration.Ticks)
                                context.Duration = nav2.VobuEndPTM;
                        }

                        if (packet.Type is (PacketType.Navigation or PacketType.Video or PacketType.Audio or PacketType.Subtitle))
                        {
                            // Write packet
                            if (context.Stream == null)
                            {
                                string output_name = $"VTS_{title.TitleNumber:00}_{(isMenu ? "Menu" : "Title")}Cell_{current_cell}.VOB";
                                context.Info.FileName = output_name;
                                context.Stream = File.Open(Path.Combine(demux_folder, output_name), FileMode.Create);
                            }
                            context.Stream.Write(Buffer);
                            context.Info.Size += Buffer.Length; // Size is the bytes written to the output file
                            context.LBA++;

                            if (!context.Info.Streams.Select(x => x.StreamID).Contains(packet.StreamID))
                            {
                                MpegStream stream;
                                stream.StreamID = packet.StreamID;
                                switch(packet.Type)
                                {
                                    case PacketType.Navigation:
                                        stream.Type = MpegStreamType.Navigation;
                                        break;
                                    case PacketType.Video:
                                        stream.Type = MpegStreamType.Video;
                                        break;
                                    case PacketType.Audio:
                                        stream.Type = MpegStreamType.Audio;
                                        break;
                                    case PacketType.Subtitle:
                                        stream.Type = MpegStreamType.Subpicture;
                                        break;
                                    default:
                                        throw new Exception("Invalid type.");
                                }
                                context.Info.Streams.Add(stream);
                            }
                        }
                    }
                } 
            }

            // Dispose flushes and closes each output file
            foreach (var cell in cells.Values)
            {
                cell.Stream?.Dispose();
                cell.Stream = null;
            }
        }
        catch
        {
            // Close every output file, and delete what this call wrote: a partial cell file must never be
            // mistaken for a complete one.
            foreach (var cell in cells.Values)
            {
                try
                {
                    cell.Stream?.Dispose();
                    if (cell.Info.FileName != null)
                        File.Delete(Path.Combine(demux_folder, cell.Info.FileName));
                }
                catch (Exception) { }
            }
            throw;
        }

        if (invalidPackSectors > 0)
            Warn($"{source}: skipped {invalidPackSectors} sectors that aren't MPEG-2 packs. The backup may be damaged or incorrectly made.");
        if (invalidStartCodes > 0)
            Warn($"{source}: skipped {invalidStartCodes} sectors with an invalid packet start code. The backup may be damaged.");
        if (invalidPackHeaders > 0)
            Warn($"{source}: {invalidPackHeaders} pack headers had invalid marker bits; they were used anyway.");

        foreach (var cell in cells.Values)
        {
            if (cell.StartTime != null)
                cell.Info.StartTime = cell.StartTime.Value;
            if (cell.Duration.Ticks < cell.Info.StartTime.Ticks)
                    cell.Info.Duration = new DiscTime(0);
                else
                    cell.Info.Duration = new DiscTime(cell.Duration.Ticks - cell.Info.StartTime.Ticks);
        }

        return cells.Values.Select(x => x.Info).ToList();
    }

    T ReadBytes<T>(int pos, int length) where T : IBinaryInteger<T>
    {
        T result = T.Zero;
        for (int i = 0; i < length; i++)
        {
            result = (result << 8) | T.CreateChecked(Buffer[pos + i]);
        }

        return result;
    }


    /// <summary>
    /// <see cref="http://www.mpucoder.com/DVD/packhdr.html"/> 
    /// </summary>
    struct PackHeader
    {
        public uint SCR;
        public ushort SCR_ext;
        public uint ProgramMuxRate;
        public byte PackStuffingLength; 
    }

    
    /// <see cref="http://www.mpucoder.com/DVD/packhdr.html"/>
    PackHeader? ParseHeader()
    {
        // Check for header identifier
        uint id = ReadBytes<uint>(0, 4);
        if (id != 0x01BA)
            return null;

        bool warn = false;
        var result = new PackHeader();
        UInt64 temp = ReadBytes<UInt64>(4, 6);
        if ((temp & 0xC40004000401u) != 0x440004000401u)
            warn = true;
        result.SCR = (UInt32)(((temp >> 14) & 0xE0000000) | ((temp >> 13) & 0x1FFFC000) | ((temp >> 11) & 0x7FFF));
        result.SCR_ext = (UInt16)((temp >> 1) & 0x1FF);

        temp = ReadBytes<UInt32>(10, 4);
        if ((temp & 0x0300) != 0x0300)
            warn = true;
        result.ProgramMuxRate = (UInt32)((temp >>10) & 0x3FFFFF);
        result.PackStuffingLength = (byte)(temp & 0x7);

        if (warn)
            invalidPackHeaders++;
        return result;
    }

    internal enum PacketType
    {
        Navigation,
        Video,
        Audio,
        Subtitle
    }

    interface Packet
    {
        public PacketType Type { get; }
        public byte StreamID { get; }
    }

    readonly struct NavPacket : Packet
    {
        public PacketType Type => PacketType.Navigation;

        public byte StreamID { get; }
        public readonly ushort VID;
        public readonly byte CID;
        public readonly uint FirstRef;

        // Cell time at the start of the packet
        public readonly DiscTime CellElapsedTimeStart;

        // Cell time at the end of the packet
        public readonly DiscTime CellElapsedTimeEnd;

        public readonly DiscTime VobuStartPTM;
        public readonly DiscTime VobuEndPTM;

        public NavPacket(CellExtract cell, byte id)
        {
            StreamID = id;
            VID = cell.ReadBytes<ushort>(0x41F, 2);
            CID = cell.Buffer[0x422];
            FirstRef = cell.ReadBytes<uint>(0x413, 4);
            VobuStartPTM = new DiscTime(cell.ReadBytes<uint>(0x39, 4));
            VobuEndPTM = new DiscTime(cell.ReadBytes<uint>(0x3d, 4));

            CellElapsedTimeStart = DiscTime.Parse(cell.ReadBytes<uint>(0x45, 4), out _);
            CellElapsedTimeEnd = DiscTime.Parse(cell.ReadBytes<uint>(0x423, 4), out _);
        }

        public static void ModifyLBA(CellExtract cell, long lba)
        {
            // 1st lba number
            cell.Buffer[0x30] = (byte)(lba % 256);
            cell.Buffer[0x2F] = (byte)((lba / 256) % 256);
            cell.Buffer[0x2E] = (byte)((lba / 256 / 256) % 256);
            cell.Buffer[0x2D] = (byte)(lba / 256 / 256 / 256);

            // 2nd lba number
            cell.Buffer[0x040E] = cell.Buffer[0x30];
            cell.Buffer[0x040D] = cell.Buffer[0x2F];
            cell.Buffer[0x040C] = cell.Buffer[0x2E];
            cell.Buffer[0x040B] = cell.Buffer[0x2D];
        }
    }

    readonly struct VideoPacket : Packet
    {
        public PacketType Type => PacketType.Video;
        public byte StreamID { get; }
        public readonly Mpeg2Header Header;

        public VideoPacket(byte id)
        {
            StreamID = id;
            Header = new();
        }
    }

    readonly struct AudioPacket : Packet
    {
        public PacketType Type => PacketType.Audio;
        public byte StreamID { get; }
        public readonly Mpeg2Header Header;

        public AudioPacket(byte id)
        {
            StreamID = id;
            Header = new();
        }
    }

    readonly struct SubpicturePacket : Packet
    {
        public PacketType Type => PacketType.Subtitle;
        public byte StreamID { get; }
        public readonly Mpeg2Header Header;

        public SubpicturePacket(byte id)
        {
            StreamID = id;
            Header = new();
        }
    }

    enum PES_ScramblingControl : byte
    {
        NotScrambled = 0,
        UserDefined1 = 1,
        UserDefined2 = 2,
        UserDefined3 = 3
    }

    enum PTS_DTS_Flags
    {
        None = 0,
        Undefined = 1,
        PTS_Only = 2,
        Both = 3
    }

    /// <summary>
    /// See http://www.mpucoder.com/DVD/pes-hdr.html
    /// </summary>
    readonly struct Mpeg2Header
    {
        public readonly PES_ScramblingControl PES_ScramblingControl;
        public readonly bool PES_Priority;
        public readonly bool DataAlignmentIndicator;
        public readonly bool Copyright;
        public readonly bool IsOriginal;
        public readonly PTS_DTS_Flags PTS_DTS_Flags;
        public readonly bool ESCR_Flag;
        public readonly bool ES_Rate_Flag;
        public readonly bool DSM_TrickModeFlag;
        public readonly bool AdditionalCopyInfoFlag;
        public readonly bool PES_CRC_Flag;
        public readonly bool PES_ExtensionFlag;
        public readonly byte PES_HeaderDataLength;

        public readonly DiscTime? PTS;
        public readonly DiscTime? DTS;
        public readonly ulong? ESCR;
        public readonly byte? ESCR_Ext;
        public readonly uint? ES_Rate;
        public readonly byte? AdditionalCopyInfo;

        public Mpeg2Header(CellExtract cell)
        {
            PES_ScramblingControl = (PES_ScramblingControl)((cell.Buffer[0x14] >> 4) & 0x3);
            PES_Priority = ((cell.Buffer[0x14] >> 3) & 0x1) != 0;
            DataAlignmentIndicator = ((cell.Buffer[0x14] >> 2) & 0x1) != 0;
            Copyright = ((cell.Buffer[0x14] >> 1) & 0x1) != 0;
            IsOriginal = (cell.Buffer[0x14] & 0x1) != 0;

            PTS_DTS_Flags = (PTS_DTS_Flags)((cell.Buffer[0x15] >> 6) & 0x3);
            ESCR_Flag = ((cell.Buffer[0x15] >> 5) & 0x1) != 0;
            ES_Rate_Flag = ((cell.Buffer[0x15] >> 4) & 0x1) != 0;
            DSM_TrickModeFlag = ((cell.Buffer[0x15] >> 3) & 0x1) != 0;
            AdditionalCopyInfoFlag = ((cell.Buffer[0x15] >> 2) & 0x1) != 0;
            PES_CRC_Flag = ((cell.Buffer[0x15] >> 1) & 0x1) != 0;
            PES_ExtensionFlag = (cell.Buffer[0x15] & 0x1) != 0;

            PES_HeaderDataLength = cell.Buffer[0x16];

            int index = 0x17;
            if (PTS_DTS_Flags is PTS_DTS_Flags.PTS_Only or PTS_DTS_Flags.Both)
            {
                ulong ticks = 0;
                ticks |= (ulong)(cell.Buffer[index++] & 0xE) << 29;
                ticks |= (ulong)cell.Buffer[index++] << 22;
                ticks |= (ulong)(cell.Buffer[index++] & 0xFE) << 14;
                ticks |= (ulong)cell.Buffer[index++] << 7;
                ticks |= (ulong)(cell.Buffer[index++] & 0xFE) >> 1;
                PTS = new DiscTime(ticks);
            }else
            {
                PTS = null;
            }

            if (PTS_DTS_Flags == PTS_DTS_Flags.Both)
            {
                ulong ticks = 0;
                ticks |= (ulong)(cell.Buffer[index++] & 0xE) << 29;
                ticks |= (ulong)cell.Buffer[index++] << 22;
                ticks |= (ulong)(cell.Buffer[index++] & 0xFE) << 14;
                ticks |= (ulong)cell.Buffer[index++] << 7;
                ticks |= (ulong)(cell.Buffer[index++] & 0xFE) >> 1;
                DTS = new DiscTime(ticks);
            } else
            {
                DTS = null;
            }

            if (ESCR_Flag)
            {
                ulong escr = 0;
                escr |= (ulong)(cell.Buffer[index] & 0x38) << 27;
                escr |= (ulong)(cell.Buffer[index++] & 0x3) << 28;
                escr |= (ulong)cell.Buffer[index++] << 20;
                escr |= (ulong)(cell.Buffer[index] & 0xF8) << 12;
                escr |= (ulong)(cell.Buffer[index++] & 0x3) << 13;
                escr |= (ulong)cell.Buffer[index++] << 5;
                escr |= (ulong)(cell.Buffer[index] & 0xF8) >> 3;
                this.ESCR = escr;

                byte ext = 0;
                ext |= (byte)((cell.Buffer[index++] & 0x3) << 6);
                ext |= (byte)((cell.Buffer[index++] & 0xFE) >> 1);
            } else
            {
                ESCR = null;
                ESCR_Ext = null;
            }

            if (ES_Rate_Flag)
            {
                uint rate = 0;
                rate |= (uint)(cell.Buffer[index++] & 0x7F) << 15;
                rate |= (uint)cell.Buffer[index++] << 7;
                rate |= (uint)(cell.Buffer[index++] & 0xFE) >> 1;
                this.ES_Rate = rate;
            } else
            {
                ES_Rate = null;
            }

            if (DSM_TrickModeFlag)
                throw new Exception("This field should not be used by DVD");
            
            if (AdditionalCopyInfoFlag)
            {
                AdditionalCopyInfo = (byte)(cell.Buffer[index++] & 0x7F);
            } else
            {
                AdditionalCopyInfo = null;
            }
        }
    }

    /// <see cref="http://www.mpucoder.com/DVD/sys_hdr.html"/>
    /// <see cref="http://www.mpucoder.com/DVD/pes-hdr.html"/>
    /// Must call ParseHeader() first
    static Packet? ParsePacket(CellExtract cell)
    {
        // Pack header is 14 bytes, so the packet starts after that
        UInt32 code = cell.ReadBytes<UInt32>(14, 4);
        //Assert.AreEqual(0x0100u, code & 0xFFFFFF00, "Invalid packet start code.");
        if ((code & 0xFFFFFF00) != 0x0100u)
        {
            cell.invalidStartCodes++;
            return null;
        }
        byte ID = (byte)(code & 0xFF);
        switch(ID)
        {
            case 0xBB: return new NavPacket(cell, ID);
            case >=0xE0 and <=0xEF: return new VideoPacket(ID);
            case >=0xC0 and <=0xC7: // MPEG-1
            case >=0xD0 and <=0xD7:  // MPEG-2
                return new AudioPacket(ID); // Mpeg Audio
            case 0xBD: // MPEG private stream. Could be Audio or subpicture
                byte header_length = cell.Buffer[0x16];
                byte subid = cell.Buffer[0x17 + header_length];
                switch(subid)
                {
                    case >=0x80 and <= 0x87: // ac3
                    case >=0x88 and <= 0x8F: // dts
                    case >=0x90 and <= 0x97: // dds
                    case >=0xA0 and <= 0xA7: // lpcm
                        return new AudioPacket(subid);
                    case >=0x20 and <=0x3F:
                        return new SubpicturePacket(subid);
                    default:
                        return null;
                }
            default: 
                return null;
        }
    }
}

internal record struct CellID
{
    public ushort VID;
    public byte CID;

    public CellID(){}
    public CellID(ushort vid, byte cid)
    {
        this.VID = vid;
        this.CID = cid;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(this.VID, this.CID);
    }

    public override string ToString()
    {
        return $"{this.VID:00}-{this.CID:00}";
    }
}