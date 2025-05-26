
using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public enum IfoCategory
{
    /// <summary>
    /// Unknown differs from <see cref="IfoCategory.Unspecified"/>.
    /// Unknown is when the value is not a known decodable type,
    /// whereas <see cref="IfoCategory.Unspecified"/> is a type defined
    /// in the DVD standard.
    /// </summary>
    Unknown = -1,
    Unspecified,
    Karaoke,
}

public record struct VobFileInfo
{
    public readonly string FileName;

    /// <summary>
    /// Size in bytes
    /// </summary>
    public readonly long Size;

    internal VobFileInfo(string file, IDiscFileSystem reader)
    {
        this.FileName = file;
        this.Size = reader.GetFileSize(file);
    }
}

/// <summary>
/// IFO file information <see cref="http://www.mpucoder.com/DVD/ifo.html"/>
/// </summary>
public class IfoFile
{
    /// <summary>
    /// The VTS title number.
    /// Zero represents the VMG.
    /// </summary>
    public readonly uint TitleNumber;

    /// <summary>
    /// The IFO version
    /// </summary>
    public readonly Version Version;
    public readonly IfoCategory Category;
    public readonly CellAddressTable? MenuCellAddressTable;
    public readonly VobuAddressMap? MenuVobuAddressMap;
    public readonly VideoAttributes MenuVobVideoAttributes;
    public readonly AudioAttributes? MenuVobAudioAttributes;
    public readonly SubpictureAttributes? MenuSubpictureAttributes;
    public readonly MenuProgramChainLanguageUnitTable? MenuProgramChainTable;

    public readonly PartOfTitleSearchPointerTable? TitlesAndChapters;
    public readonly ProgamChainInformationTable? TitleProgramChainTable;
    public readonly TimeMapTable? TimeMap;
    public readonly CellAddressTable? TitleCellAddressTable;
    public readonly VobuAddressMap? TitleVobuAddressMap;
    public readonly VideoAttributes? TitlesVobVideoAttributes; 
    public readonly ReadOnlyCollection<AudioAttributes>? TitlesVobAudioAttributes; 
    public readonly ReadOnlyCollection<SubpictureAttributes>? TitlesVobSubpictureAttributes; 
    public readonly ReadOnlyCollection<MultichannelExtension>? MultichannelExtensions;

    public readonly ReadOnlyCollection<VobFileInfo> VobFiles;

    internal IfoFile(IDiscFileSystem reader, DvdStream file, uint title)
    {
        // Common values shared between VMG and VTS
        this.TitleNumber = title;
        string ID = file.ReadString(12);
        if (title == 0)
            Util.Assert(ID == "DVDVIDEO-VMG", "Invalid identifier.");
        else
            Util.Assert(ID == "DVDVIDEO-VTS", "Invalid identifier.");

        uint LastBUPSector = file.Read<uint>();
        file.ReadZeros(12);
        uint LastIFOSector = file.Read<uint>();
        Util.Assert(LastBUPSector != 0, "Invalid last BUP sector number.");
        Util.Assert(LastIFOSector != 0, "Invalid last IFO sector.");
        Util.Assert(LastIFOSector * 2 < LastBUPSector, "Invalid last IFO sector.");

        // Read version number
        file.ReadZeros(1);
        byte version = file.Read<byte>();
        this.Version = new Version((version >> 4) & 0xFF, version & 0xFF);

        // Read category
        uint category = file.Read<uint>();
        if (title == 0)
            this.Category = IfoCategory.Unknown;
        else
        {
            switch(category)
            {
                case 0:
                    this.Category = IfoCategory.Unspecified;
                    break;
                case 1:
                    this.Category = IfoCategory.Karaoke;
                    break;
                default:
                    this.Category = IfoCategory.Unknown;
                    break;
            }
        }

        // Verify locations of all zeros
        file.Seek(0x2B);
        file.ReadZeros(19);

        file.Seek(0x68);
        file.ReadZeros(24);

        // Get MAT end address
        file.Seek(0x80);
        uint LastByteIndex = file.Read<uint>();
        Util.Assert(LastByteIndex >= 341, "Invalid MAT length.");
        Util.Assert(LastByteIndex / Util.SECTOR_SIZE <= LastIFOSector, "Invalid MAT size.");

        // Another zero location
        file.Seek(0x88);
        file.ReadZeros(56);

        // VOB start sector
        uint MenuVobStartSector = file.Read<uint>();
        Util.Assert(MenuVobStartSector == 0 || (MenuVobStartSector > LastIFOSector && MenuVobStartSector < LastBUPSector), "Invalid Menu VOB sector.");

        // Menu cell address table
        file.Seek(0xD8);
        uint menuADTSector = file.Read<uint>();
        Util.Assert(menuADTSector <= LastIFOSector, "Invalid menu cell address table sector.");
        if (menuADTSector != 0) 
            MenuCellAddressTable = new CellAddressTable(file, menuADTSector * Util.SECTOR_SIZE);

        // Menu VOBU address map
        file.Seek(0xDC);
        uint vobuAddressMapSector = file.Read<uint>();
        Util.Assert(vobuAddressMapSector <= LastIFOSector, "Invalid menu VOBU address map");
        if (vobuAddressMapSector != 0) 
            MenuVobuAddressMap = new VobuAddressMap(file, vobuAddressMapSector * Util.SECTOR_SIZE);

        // More zeros
        file.Seek(0xE8);
        file.ReadZeros(24);

        // Video attributes
        MenuVobVideoAttributes = new VideoAttributes(file);

        // Audio attributes
        ushort numAudioStreams = file.Read<ushort>();
        Util.Assert(numAudioStreams <= 1, "Too many audio streams.");
        if (numAudioStreams == 0)
            file.ReadZeros(8);
        else
            MenuVobAudioAttributes = new AudioAttributes(file, 0);

        // Zero padding / reserved
        file.ReadZeros(56 + 16);

        // Subpictures
        ushort numSubpictureStreams = file.Read<ushort>();
        Util.Assert(numSubpictureStreams <= 1, "Too many subtitle streams.");
        if (numSubpictureStreams == 0)
            file.ReadZeros(6);
        else
            MenuSubpictureAttributes = new SubpictureAttributes(file, 0);

        // Final shared padding
        file.ReadZeros(164);

        // Menu Program Chain Table
        file.Seek((title == 0) ? 0xC8u : 0xD0u);
        uint menuPGCSector = file.Read<uint>();
        Util.Assert(menuPGCSector <= LastIFOSector, "Invalid menu PGC table sector.");
        if (menuPGCSector != 0)
            MenuProgramChainTable = new MenuProgramChainLanguageUnitTable(file, menuPGCSector * Util.SECTOR_SIZE, title == 0);

        TitlesAndChapters = null;
        TitleProgramChainTable = null;
        TimeMap = null;
        TitleCellAddressTable = null;
        TitleVobuAddressMap = null;
        TitlesVobVideoAttributes = null;
        TitlesVobAudioAttributes = null;
        TitlesVobSubpictureAttributes = null;
        MultichannelExtensions = null;

        if (title > 0)
        { // VTS Only
            // Read zeros/padding
            file.Seek(0x26);
            file.ReadZeros(5);

            file.Seek(0x3E);
            file.ReadZeros(47);

            file.Seek(0x84);
            file.ReadZeros(4);

            // Title VOB
            file.Seek(0xC4);
            uint TitleVobStartSector = file.Read<uint>();
            Util.Assert(TitleVobStartSector == 0 || (TitleVobStartSector > LastIFOSector && TitleVobStartSector < LastBUPSector), "Invalid title VOB start sector.");

            // Titles and Chapters
            uint titlesSector = file.Read<uint>();
            Util.Assert(titlesSector <= LastIFOSector, "Invalid TitlesAndChaptersPtrTable sector.");
            if (titlesSector != 0) TitlesAndChapters = new PartOfTitleSearchPointerTable(file, titlesSector * Util.SECTOR_SIZE);

            // Title Program Chain Table
            file.Seek(0xCC);
            uint titlePgcSector = file.Read<uint>();
            Util.Assert(titlePgcSector <= LastIFOSector, "Invalid title PGC table sector.");
            if (titlePgcSector != 0) TitleProgramChainTable = new ProgamChainInformationTable(file, titlePgcSector * Util.SECTOR_SIZE, false);

            // Time map table
            file.Seek(0xD4);
            uint timeMapSector = file.Read<uint>();
            Util.Assert(timeMapSector <= LastIFOSector, "Invalid time map table sector.");
            if (timeMapSector != 0) TimeMap = new TimeMapTable(file, timeMapSector *  Util.SECTOR_SIZE);

            // Title cell address table
            file.Seek(0xE0);
            uint cellAddrSector = file.Read<uint>();
            Util.Assert(cellAddrSector <= LastIFOSector, "Invalid title cell addr table sector.");
            if (cellAddrSector != 0) TitleCellAddressTable = new CellAddressTable(file, cellAddrSector *  Util.SECTOR_SIZE);

            // Title Vobu address map
            file.Seek(0xE4);
            uint vobuSector = file.Read<uint>();
            Util.Assert(vobuSector <= LastIFOSector, "Invalid title VOB addr map.");
            if (vobuSector != 0) TitleVobuAddressMap = new VobuAddressMap(file, vobuSector *  Util.SECTOR_SIZE);

            // Title Video attributes
            file.Seek(0x200);
            TitlesVobVideoAttributes = new VideoAttributes(file);

            // Title audio attributes
            numAudioStreams = file.Read<ushort>();
            Util.Assert(numAudioStreams <= 8, "Too many audio streams.");
            AudioAttributes[] audio = new AudioAttributes[numAudioStreams];
            for (int i = 0; i < audio.Length; i++)
                audio[i] = new AudioAttributes(file, i);
            TitlesVobAudioAttributes = audio.AsReadOnly();
            file.ReadZeros((8 - (int)numAudioStreams) * 8);

            // padding
            file.ReadZeros(16);

            // Title subtitle attributes
            ushort numSubpStreams = file.Read<ushort>();
            Util.Assert(numSubpStreams <= 32, "Too many subtitle streams.");
            SubpictureAttributes[] subs = new SubpictureAttributes[numSubpStreams];
            for (int i = 0; i < subs.Length; i++)
                subs[i] = new SubpictureAttributes(file, i);
            TitlesVobSubpictureAttributes = subs.AsReadOnly();
            file.ReadZeros((32 - (int)numSubpStreams) * 6);

            // Padding
            file.ReadZeros(2);

            // Extension
            MultichannelExtension[] extensions = new MultichannelExtension[8];
            for (int i = 0; i < 8; i++)
                extensions[i] = new MultichannelExtension(file);
            this.MultichannelExtensions = extensions.AsReadOnly();

            // Padding
            file.ReadZeros(40);
        }

        List<VobFileInfo> files = new();
        if (title == 0)
        {
            files.Add(new VobFileInfo("VIDEO_TS.VOB", reader));
        } else
        {
            for (int i = 0; i <= 9; i++)
            {
                files.Add(new VobFileInfo($"VTS_{title:00}_{i}.VOB", reader));
            }
        }
        this.VobFiles = files.AsReadOnly();
    }
}