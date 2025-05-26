using System.Text.RegularExpressions;
using DiscDecoders.Common;
using DiscDecoders.DvdDecoder;

namespace DiscDecoders;

/// <summary>
/// A DVD. Its clips are cells, named by <see cref="ReadInfo"/> as e.g. "VTS_01_TitleCell_07-02.mkv" (title set 1,
/// VOB 7, cell 2), and read straight out of the VOBs as program streams.
/// </summary>
public sealed class DvdDisc : Disc
{
    // Cells play on seamlessly when one ends within this many 90 kHz ticks of where the next starts
    private const uint SeamlessTolerance = 2;

    // A cell's clip name, e.g. "VTS_01_TitleCell_07-02.mkv"
    private static readonly Regex CellNameRegex = new(@"^VTS_(\d+)_(Menu|Title)Cell_(\d+)-(\d+)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Lock _parseLock = new();
    private Dvd? _dvd;

    public override DiscFormat Format => DiscFormat.Dvd;

    internal DvdDisc(string rootPath, bool isImage) : base(rootPath, isImage)
    {
    }

    // Read once; it keeps no files open
    private Dvd Dvd
    {
        get
        {
            lock (_parseLock)
            {
                if (_dvd is null)
                {
                    try
                    {
                        _dvd = Dvd.Parse(RootPath, message => Log(DiscMessageLevel.Warning, message));
                    }
                    catch (DvdException ex)
                    {
                        throw new DiscException(ex.Message, ex);
                    }
                }

                return _dvd;
            }
        }
    }

    protected override DiscInfo ReadNavigation()
    {
        Log(DiscMessageLevel.Info, "Reading DVD info...");
        try
        {
            return DvdExtract.ReadInfo(Dvd, Log);
        }
        catch (Exception ex) when (ex is not DiscException)
        {
            throw new DiscException(ex is DvdException ? ex.Message : $"Couldn't read the DVD: {ex.Message}", ex);
        }
    }

    public override Stream OpenClip(string clip)
    {
        var cell = ParseCellName(clip);
        return Read(() => Dvd.OpenCell(cell.Title, cell.IsMenu, cell.VobId, cell.CellId));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// With <paramref name="trackOrder"/>, the cell is preceded by one extra sector holding an empty packet for the
    /// video and each of those streams (see <see cref="TrackPrimer"/>).
    /// </remarks>
    public override ClipPlayback OpenForPlayback(string clip, IReadOnlyList<int>? trackOrder = null)
    {
        var cell = ParseCellName(clip);
        var dvd = Dvd;
        return Read(() =>
        {
            var stream = dvd.OpenCell(cell.Title, cell.IsMenu, cell.VobId, cell.CellId);
            var primed = trackOrder is not null ? TrackPrimer.Wrap(stream, trackOrder) : stream;
            long prefix = primed.Length - stream.Length;

            // Seeks look up the VOBU in a separate copy of the cell, as the player reads the other on its own thread
            long SeekOffset(TimeSpan time)
            {
                using var lookup = dvd.OpenCell(cell.Title, cell.IsMenu, cell.VobId, cell.CellId);
                return prefix + lookup.OffsetOfTime(time);
            }

            return new ClipPlayback(primed, SeekOffset, trackOrder is not null);
        });
    }

    public override Func<TimeSpan, long>? GetSeekIndex(string clip)
    {
        var cell = ParseCellName(clip);
        var dvd = Dvd;
        return time =>
        {
            using var lookup = Read(() => dvd.OpenCell(cell.Title, cell.IsMenu, cell.VobId, cell.CellId));
            return lookup.OffsetOfTime(time);
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A cell's streams, presentation span, pictures, lead-in, subtitles, and whether its video is interlaced (which
    /// its IFO doesn't say); see <see cref="CellStream.Scan"/>.
    /// </remarks>
    protected override ClipScan ScanClip(string clip, CancellationToken cancellation)
    {
        var cell = ParseCellName(clip);
        var scan = Read(() =>
        {
            using var stream = Dvd.OpenCell(cell.Title, cell.IsMenu, cell.VobId, cell.CellId);
            return stream.Scan();
        });

        var captions = scan.ForcedCaptions.ToLookup(c => c.StreamId);
        var subtitles = scan.Subtitles
            .Select(s => new SubtitleScan(s.StreamId, s.Total, s.Forced,
                captions[s.StreamId].Select(c => new ForcedCaption(new DiscTime((long)c.Pts), c.Data)).ToList()))
            .ToList();
        return new ClipScan(scan.StreamIds, ToTime(scan.StartPtm), ToTime(scan.EndPtm), scan.Pictures,
            scan.LeadIn.Select(p => new ByteRange(p.Offset, p.Length)).ToList(), subtitles, scan.Interlaced);

        static DiscTime? ToTime(uint? ptm) => ptm is not null ? new DiscTime((long)ptm) : null;
    }

    /// <summary>
    /// Cells grouped into runs (see <see cref="Disc.GroupSeamless"/>): cells of one title set where each starts where the
    /// one before it ends.
    /// </summary>
    internal static List<List<int>> GroupCells(IReadOnlyList<string> clips, IReadOnlyList<ClipScan?> scans)
    {
        var runs = new List<List<int>>();
        for (int i = 0; i < clips.Count; i++)
        {
            var cell = TryParseCellName(clips[i]);
            var previous = i > 0 ? TryParseCellName(clips[i - 1]) : null;
            var end = i > 0 ? scans[i - 1]?.End : null;
            var start = scans[i]?.Start;
            bool joins = i > 0 && cell is not null && previous is not null && cell.Value.Title == previous.Value.Title && cell.Value.IsMenu == previous.Value.IsMenu
                && end is not null && start is not null
                && Math.Abs(start.Value.Ticks - end.Value.Ticks) <= SeamlessTolerance;
            if (joins)
                runs[^1].Add(i);
            else
                runs.Add([i]);
        }

        return runs;
    }

    /// <inheritdoc/>
    /// <remarks>Cells of one title set, read as a player reads cells that run on seamlessly.</remarks>
    public override Stream OpenClips(IReadOnlyList<string> clips, IReadOnlyList<ByteRange>? blank = null)
    {
        var cells = clips.Select(ParseCellName).ToList();
        if (cells.Any(c => c.Title != cells[0].Title || c.IsMenu != cells[0].IsMenu))
            throw new DiscException("Only cells of one title set can be read as one stream.");

        return Read(() =>
        {
            Stream stream = Dvd.OpenCells(cells[0].Title, cells[0].IsMenu, cells.Select(c => (c.VobId, c.CellId)).ToList());
            return blank is { Count: > 0 }
                ? new PacketBlankingStream(stream, blank.Select(r => new CellScan.Packet(r.Offset, r.Length)).ToList())
                : stream;
        });
    }

    private static (uint Title, bool IsMenu, int VobId, int CellId)? TryParseCellName(string clip)
    {
        var match = CellNameRegex.Match(clip);
        if (!match.Success)
            return null;
        return (uint.Parse(match.Groups[1].Value), match.Groups[2].Value.Equals("Menu", StringComparison.OrdinalIgnoreCase),
            int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value));
    }

    private static (uint Title, bool IsMenu, int VobId, int CellId) ParseCellName(string clip) =>
        TryParseCellName(clip) ?? throw new DiscException($"{clip} isn't a DVD cell.");

    private static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (DvdException ex)
        {
            throw new DiscException(ex.Message, ex);
        }
    }
}
