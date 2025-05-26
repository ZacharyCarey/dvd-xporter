
using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public enum DiscSides
{
    Front,
    Back
}

/// <summary>
/// DVD information <see cref="http://www.mpucoder.com/DVD/ifo.html"/>
/// </summary>
public class Dvd
{
    public readonly string Path;
    public readonly ushort NumberOfVolumes;
    public readonly ushort VolumeNumber;
    public readonly DiscSides DiscSide;
    public readonly string ProviderID;
    public readonly ReadOnlyCollection<IfoFile> Titles;
    public readonly PGC? FirstPlayPGC = null;
    public readonly ChapterSearchPointerTable? Chapters = null; 
    public readonly ParentalManagementInfoTable? ParentalManagementMasks = null;
    public readonly VtsAttributeTable? VtsStreamAttributes = null;
    public readonly TextDataManagerInfo? TextData = null;


    internal Dvd(string path, IDiscFileSystem file_reader, Action<string>? onWarning)
    {
        this.Path = path;
        List<IfoFile> titles = new();

        // First parse VMG to check for basic errors
        using DvdStream vmg = new DvdStream(file_reader.OpenFile("VIDEO_TS.IFO")) { OnWarning = onWarning };
        titles.Add(ParseIfo("VIDEO_TS.IFO", () => new IfoFile(file_reader, vmg, 0)));

        // Now parse general DVD info from VMG file
        // These values should have already been checked by the Ifo parser
        vmg.Seek(0x1C);
        uint LastIFOSector = vmg.Read<uint>();
        vmg.Seek(0x80);
        uint LastByteIndex = vmg.Read<uint>();

        vmg.Seek(0x26);
        NumberOfVolumes = vmg.Read<ushort>();
        Util.Assert(NumberOfVolumes != 0, "Invalid number of volumes.");
        VolumeNumber = vmg.Read<ushort>();
        Util.Assert(VolumeNumber != 0, "Invalid volume number.");
        Util.Assert(VolumeNumber <= NumberOfVolumes, "Invalid volume number.");
        byte side = vmg.Read<byte>();
        switch(side)
        {
            case 1:
                DiscSide = DiscSides.Front;
                break;
            case 2:
                DiscSide = DiscSides.Back;
                break;
            default:
                throw new Exception("Invalid disc side.");
        }

        // First play address
        vmg.Seek(0x84);
        uint firstPlayAddr = vmg.Read<uint>(); // Absolute, not sector
        Util.Assert(firstPlayAddr < LastByteIndex, "Invalid first play PGC addr.");
        if (firstPlayAddr != 0) FirstPlayPGC = new PGC(vmg, firstPlayAddr);

        // Table of Titles
        vmg.Seek(0xC4);
        uint tableOfTitlesSector = vmg.Read<uint>();
        Util.Assert(tableOfTitlesSector <= LastIFOSector, "Invalid ChapterSearchPtrTable sector.");
        if (tableOfTitlesSector != 0) Chapters = new ChapterSearchPointerTable(vmg, tableOfTitlesSector * Util.SECTOR_SIZE);

        vmg.Seek(0x3E);
        ushort numberOfTitles = vmg.Read<ushort>();
        Util.Assert(numberOfTitles != 0, "Invalid number of title sets.");
        ProviderID = vmg.ReadString(32);

        // Parental management masks
        vmg.Seek(0xCC);
        uint ptlSector = vmg.Read<uint>();
        Util.Assert(ptlSector <= LastIFOSector, "Invalid parental management sector.");
        if (ptlSector != 0) ParentalManagementMasks = new ParentalManagementInfoTable(vmg, ptlSector * Util.SECTOR_SIZE);

        // Vts Stream attributes
        vmg.Seek(0xD0);
        uint vtsAttrSector = vmg.Read<uint>();
        Util.Assert(vtsAttrSector <= LastIFOSector, "Invalid VTS attributes sector.");
        if (vtsAttrSector != 0) VtsStreamAttributes = new VtsAttributeTable(vmg, vtsAttrSector * Util.SECTOR_SIZE);

        // Text data manager info
        vmg.Seek(0xD4);
        uint textDataSector = vmg.Read<uint>();
        Util.Assert(textDataSector <= LastIFOSector, "Invalid text data manager sector.");
        if (textDataSector != 0) TextData = new TextDataManagerInfo(vmg, textDataSector * Util.SECTOR_SIZE);

        // Read zeros/padding
        vmg.Seek(0xE0);
        vmg.ReadZeros(8);

        vmg.Seek(0x200);
        vmg.ReadZeros(512);

        // Now read remaining titles
        for (uint i = 1; i <= numberOfTitles; i++)
        {
            using DvdStream file = new DvdStream(file_reader.OpenFile($"VTS_{i:00}_0.IFO")) { OnWarning = onWarning };
            titles.Add(ParseIfo($"VTS_{i:00}_0.IFO", () => new IfoFile(file_reader, file, i)));
        }

        this.Titles = titles.AsReadOnly();
    }

    // Puts the file name in front of parse errors, e.g. "VTS_03_0.IFO: Invalid identifier."
    static IfoFile ParseIfo(string fileName, Func<IfoFile> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex)
        {
            throw new DvdException($"{fileName}: {ex.Message}", ex);
        }
    }

    static IDiscFileSystem? GetReader(string path) => DvdFiles.Open(path);

    /// <summary>
    /// Reads a DVD from a folder containing VIDEO_TS, or from an ISO image.
    /// </summary>
    /// <param name="onWarning">Receives non-fatal problems found while reading (the DVD is still usable).</param>
    /// <exception cref="DvdException">The path doesn't exist, or isn't a readable DVD. The message says why.</exception>
    public static Dvd Parse(string path, Action<string>? onWarning = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new DvdException($"\"{path}\" doesn't exist.");

        try
        {
            // Only needed while parsing; Dvd doesn't keep the files open
            using var reader = GetReader(path)!;
            return new Dvd(path, reader, onWarning);
        }
        catch (Exception ex)
        {
            throw new DvdException($"\"{path}\" couldn't be read as a DVD: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Opens one cell straight from the disc as an MPEG-2 program stream, without extracting anything.
    /// The stream keeps the disc open until it is disposed.
    /// </summary>
    /// <param name="titleNumber">The title set (0 for the main menu's VIDEO_TS).</param>
    /// <param name="isMenu">Whether the cell is in the title set's menu VOB rather than its title VOBs.</param>
    /// <exception cref="DvdException">The cell isn't in the title set's cell address table, or the disc can't be opened.</exception>
    public CellStream OpenCell(uint titleNumber, bool isMenu, int vobId, int cellId) =>
        OpenCells(titleNumber, isMenu, [(vobId, cellId)]);

    /// <summary>
    /// Several cells of one title set read one after another as a single stream, e.g. cells that play
    /// seamlessly in a row, which a player reads as one continuous program stream.
    /// </summary>
    /// <inheritdoc cref="OpenCell"/>
    public CellStream OpenCells(uint titleNumber, bool isMenu, IReadOnlyList<(int VobId, int CellId)> cells)
    {
        if (titleNumber >= Titles.Count)
            throw new DvdException($"The DVD has no title set {titleNumber}.");

        var ifo = Titles[(int)titleNumber];
        var table = isMenu || titleNumber == 0 ? ifo.MenuCellAddressTable : ifo.TitleCellAddressTable;
        var pieces = new List<CellAddress>();
        foreach (var (vobId, cellId) in cells)
        {
            // An interleaved (multi-angle) cell is listed as several runs; they play in sector order
            var cellPieces = table?.Cells.Where(x => x.VobID == vobId && x.CellID == cellId).OrderBy(x => x.StartSector).ToList() ?? [];
            if (cellPieces.Count == 0)
                throw new DvdException($"VTS_{titleNumber:00} has no {(isMenu ? "menu" : "title")} cell {vobId}-{cellId}.");
            pieces.AddRange(cellPieces);
        }

        var reader = GetReader(Path) ?? throw new DvdException($"\"{Path}\" no longer exists.");
        try
        {
            return new CellStream(reader, ifo, isMenu, pieces);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The subtitle colour palette of the first program chain that plays a cell, as 16 entries of
    /// 0x00YYCrCb, or null if no program chain plays it.
    /// </summary>
    public IReadOnlyList<uint>? GetCellPalette(uint titleNumber, bool isMenu, int vobId, int cellId)
    {
        if (titleNumber >= Titles.Count)
            return null;

        var ifo = Titles[(int)titleNumber];
        IEnumerable<PGC> candidates = isMenu || titleNumber == 0
            ? (ifo.MenuProgramChainTable?.SelectMany(lu => lu.PgcTable.Select(x => x.Pgc)) ?? [])
            : (ifo.TitleProgramChainTable?.Select(x => x.Pgc) ?? []);
        return candidates.FirstOrDefault(pgc => pgc.CellPosition.Any(pos => pos.VobID == vobId && pos.CellID == cellId))?.Palette;
    }

    /// <summary>
    /// Extracts every menu and title cell to its own VOB file in <paramref name="demux_folder"/>.
    /// </summary>
    /// <param name="progress">Receives the fraction done, from 0 to 1.</param>
    /// <param name="onWarning">Receives non-fatal problems, such as damaged sectors that were skipped.</param>
    /// <exception cref="DvdException">Extraction failed. Cells already completed stay on disk; the failed one is removed.</exception>
    public List<ExtractedFileInfo> ExtractAllFiles(string demux_folder, Action<double>? progress, Action<string>? onWarning = null)
    {
        List<ExtractedFileInfo> extractedFiles = new();

        using var reader = GetReader(this.Path) ?? throw new DvdException($"\"{this.Path}\" no longer exists.");

        long totalBytes = this.Titles.SelectMany(x => x.VobFiles).Select(x => x.Size).Sum();
        long currentBytes = 0;
        Action<long> reportProgress = bytes =>
        {
            if (totalBytes > 0)
                progress?.Invoke((double)(currentBytes + bytes) / totalBytes);
        };
        foreach(var ifo in this.Titles)
        {
            extractedFiles.AddRange(Extract(ifo, true));
            currentBytes += ifo.VobFiles[0].Size;

            if (ifo.TitleNumber > 0)
            {
                extractedFiles.AddRange(Extract(ifo, false));
                currentBytes += ifo.VobFiles.Skip(1).Select(x => x.Size).Sum();
            }
        }

        return extractedFiles;

        List<ExtractedFileInfo> Extract(IfoFile ifo, bool isMenu)
        {
            try
            {
                return new CellExtract(onWarning).ExtractFiles(reader, ifo, isMenu, demux_folder, reportProgress);
            }
            catch (Exception ex)
            {
                throw new DvdException($"Couldn't extract the VTS_{ifo.TitleNumber:00} {(isMenu ? "menus" : "titles")}: {ex.Message}", ex);
            }
        }
    }
}
