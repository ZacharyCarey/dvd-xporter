using DiscDecoders.Common;
using DiscDecoders.DvdDecoder;

namespace DiscDecoders;

public static class DvdExtract
{
    /// <summary>A stream to copy from a cell: its MPEG stream ID in the VOB, and its IFO attributes.</summary>
    record DesiredStream(byte StreamID, IStreamAttributes Attributes);

    /// <summary>
    /// Reads the DVD's playlists and cells from its IFO files without extracting anything, for previewing cells
    /// straight from the disc (see <see cref="Dvd.OpenCell"/>). Each cell's streams are in output order (video,
    /// audio, subtitles), with each stream's MPEG stream ID in its metadata ("MPEG Stream ID"). Whether the video is interlaced isn't known without probing it, so its
    /// "Scan" metadata says "Unknown".
    /// </summary>
    /// <exception cref="DvdException">The DVD couldn't be read.</exception>
    public static DiscInfo ReadInfo(Dvd dvd, Action<DiscMessageLevel, string> log)
    {
        DiscInfo info = NewDiscInfo(dvd);
        foreach (var ifo in dvd.Titles)
        {
            AddCells(ifo, true);
            if (ifo.TitleNumber > 0)
                AddCells(ifo, false);
        }
        AddPlaylists(info, dvd);
        return info;

        void AddCells(IfoFile ifo, bool isMenu)
        {
            var table = isMenu ? ifo.MenuCellAddressTable : ifo.TitleCellAddressTable;
            if (table == null)
                return;

            foreach (var (vobId, cellId) in table.Cells.Select(x => (x.VobID, x.CellID)).Distinct())
            {
                string name = $"VTS_{ifo.TitleNumber:00}_{(isMenu ? "Menu" : "Title")}Cell_{new CellID(vobId, cellId)}";
                var cell = new ExtractedFileInfo
                {
                    FileName = name + ".VOB",
                    Title = ifo.TitleNumber,
                    IsMenu = isMenu,
                    VobID = vobId,
                    CellID = cellId,
                };
                var duration = ReadCellDuration(dvd, ifo, cell);

                var clip = new ClipInfo
                {
                    Filename = name + ".mkv",
                    Duration = duration,
                    // A DVD's playlists time cells from their own start
                    Start = DiscTime.Zero,
                    SubtitlePalette = ToRgb(dvd.GetCellPalette(ifo.TitleNumber, isMenu, vobId, cellId)),
                };
                foreach (var stream in GetDesiredStreams(ifo, cell, log))
                    clip.Streams.Add(GetStreamInfo(stream.Attributes, duration, stream.StreamID, false));
                info.Clips.Add(clip);
            }
        }
    }

    /// <summary>
    /// A cell's length from its first and last navigation packs, as extraction measures it; failing that,
    /// its playback time in a PGC that plays it, or zero.
    /// </summary>
    static DiscTime ReadCellDuration(Dvd dvd, IfoFile ifo, ExtractedFileInfo cell)
    {
        try
        {
            using var stream = dvd.OpenCell(ifo.TitleNumber, cell.IsMenu, cell.VobID, cell.CellID);
            var times = stream.ReadPresentationTimes();
            if (times is not null && times.Value.End.Ticks > times.Value.Start.Ticks)
                return times.Value.End - times.Value.Start;
        }
        catch (Exception ex) when (ex is DvdException or IOException)
        {
        }

        foreach (var pgc in FindPgcsForCell(ifo, cell))
        {
            for (int i = 0; i < pgc.CellPosition.Count && i < pgc.CellPlayback.Count; i++)
            {
                if (pgc.CellPosition[i].VobID == cell.VobID && pgc.CellPosition[i].CellID == cell.CellID)
                    return pgc.CellPlayback[i].PlaybackTime;
            }
        }

        return DiscTime.Zero;
    }

    static DiscInfo NewDiscInfo(Dvd dvd) => new()
    {
        Name = dvd.ProviderID,
        DiscNumber = dvd.VolumeNumber,
        NumberOfDiscs = dvd.NumberOfVolumes,
        DiscSide = (dvd.DiscSide == DiscSides.Back) ? DiscSide.Back : DiscSide.Front,
    };

    /// <summary>Adds a playlist for every menu and title PGC (one per angle).</summary>
    static void AddPlaylists(DiscInfo info, Dvd dvd)
    {
        foreach(var ifo in dvd.Titles)
        {
            if (ifo.MenuProgramChainTable != null)
            {
                // Parse menu PGCs
                int pgcn = 0;
                foreach (var lu in ifo.MenuProgramChainTable)
                {
                    foreach (var pgc in lu.PgcTable.Select(x => x.Pgc))
                    {
                        pgcn++; // PGC number starts at 1
                        var playlists = GetPlaylistFromPGC(pgc, pgcn, ifo, true, lu.Language);
                        info.Playlists.AddRange(playlists);
                    }
                }
            }

            if (ifo.TitleProgramChainTable != null)
            {
                // Parse title PGCs
                int pgcn = 0;
                foreach(var pgc in ifo.TitleProgramChainTable.Select(x => x.Pgc))
                {
                    pgcn++; // PPGC number starts at 1
                    var playlists = GetPlaylistFromPGC(pgc, pgcn, ifo, false, null);
                    info.Playlists.AddRange(playlists);
                }
            }
        }
    }

    static IEnumerable<VideoAttributes> GetVideo(IfoFile ifo, bool isMenu)
    {
        if (isMenu)
        {
            if (ifo.MenuVobVideoAttributes != null)
                return [ifo.MenuVobVideoAttributes];
        } else
        {
            if (ifo.TitlesVobVideoAttributes != null)
                return [ifo.TitlesVobVideoAttributes];
        }

        return Enumerable.Empty<VideoAttributes>();
    }

    static IEnumerable<AudioAttributes> GetAudio(IfoFile ifo, bool isMenu)
    {
        if (isMenu)
        {
            if (ifo.MenuVobAudioAttributes != null)
                return [ifo.MenuVobAudioAttributes];
        } else
        {
            if (ifo.TitlesVobAudioAttributes != null)
                return ifo.TitlesVobAudioAttributes;
        }

        return Enumerable.Empty<AudioAttributes>();
    }

    static IEnumerable<SubpictureAttributes> GetSubs(IfoFile ifo, bool isMenu)
    {
        if (isMenu)
        {
            if (ifo.MenuSubpictureAttributes != null)
                return [ifo.MenuSubpictureAttributes];
        } else
        {
            if (ifo.TitlesVobSubpictureAttributes != null)
                return ifo.TitlesVobSubpictureAttributes;
        }

        return Enumerable.Empty<SubpictureAttributes>();
    }

    /// <summary>A program chain's palette (16 entries of 0x00YYCrCb) as RGB.</summary>
    static List<Rgb>? ToRgb(IReadOnlyList<uint>? palette)
    {
        if (palette is null)
            return null;

        static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
        return palette.Select(entry =>
        {
            double y = entry >> 16 & 0xFF, cr = (entry >> 8 & 0xFF) - 128.0, cb = (entry & 0xFF) - 128.0;
            return new Rgb(Clamp(y + 1.402 * cr), Clamp(y - 0.344136 * cb - 0.714136 * cr), Clamp(y + 1.772 * cb));
        }).ToList();
    }

    static IEnumerable<PlaylistInfo> GetPlaylistFromPGC(PGC pgc, int pgcn, IfoFile ifo, bool isMenu, Iso639.Language? languageUnit)
    {
        if (pgc.NumberOfCells <= 0)
            yield break;

        uint nAngles = pgc.NumberOfAngles;
        if (nAngles == 0) nAngles = 1;
        int[] cellAngles = GetCellAngles(pgc);
        for (uint angle = 1; angle <= nAngles; angle++)
        {
            var playlist = new PlaylistInfo();
            playlist.Angles = (int)pgc.NumberOfAngles;
            playlist.Duration = pgc.PlaybackTime;
            playlist.SubtitlePalette = ToRgb(pgc.Palette);
            playlist.Name = $"VTS_{ifo.TitleNumber:00}_{(isMenu ? "Menu" : "Title")}PGC_{pgcn:0000}{((nAngles > 1) ? $"_Angle{angle}" : "")}";

            // Add all video streams
            var video = GetVideo(ifo, isMenu).ToList();
            playlist.VideoStreams.AddRange(video.Select(x => GetStreamInfo(x, pgc.PlaybackTime, x.StreamID, false)).Cast<VideoStreamInfo>());

            // Add the audio and subtitle streams the PGC makes available, by the stream IDs it selects
            foreach (var (control, attribs) in pgc.AudioStreams.Zip(GetAudio(ifo, isMenu)).Where(x => x.First.Available))
                playlist.AudioStreams.Add((AudioStreamInfo)GetStreamInfo(attribs, pgc.PlaybackTime, control.GetStreamId(attribs.Format), false));

            //if (pgc.SubpictureStreams.Count() != subpictureAttribs.Count()) throw new Exception($"Number of subpictures did not match in PGC {pgcn}.");
            var aspect = video.FirstOrDefault()?.DisplayAspectRatio ?? DvdDecoder.AspectRatio.Fullscreen;
            foreach (var (control, attribs) in pgc.SubpictureStreams.Zip(GetSubs(ifo, isMenu)).Where(x => x.First.Available))
                playlist.SubtitleStreams.Add((SubtitleStreamInfo)GetStreamInfo(attribs, pgc.PlaybackTime, control.GetStreamId(aspect), false));

            playlist.Still = GetStill(pgc.StillTime);
            playlist.ProgramChain = new DvdProgramChain
            {
                Programs = pgc.NumberOfPrograms,
                // 0 is sequential; otherwise bit 7 is random (0) or shuffle (1), and bits 0-6 the programs played, less 1
                Playback = pgc.PGPlaybackMode == 0 ? ProgramPlayback.Sequential
                    : (pgc.PGPlaybackMode & 0x80) == 0 ? ProgramPlayback.Random
                    : ProgramPlayback.Shuffle,
                ProgramsPlayed = pgc.PGPlaybackMode == 0 ? null : (pgc.PGPlaybackMode & 0x7F) + 1,
                GoUpPgc = pgc.GoUpPgcNumber,
                MenuLanguage = languageUnit,
            };

            // Save clip info
            string menuName = isMenu ? "Menu" : "Title";
            for (int cellIndex = 0; cellIndex < pgc.CellPlayback.Count && cellIndex < pgc.CellPosition.Count; cellIndex++)
            {
                if (!IsCellInAngle(cellAngles[cellIndex], angle))
                    continue;

                var cellPlayback = pgc.CellPlayback[cellIndex];
                var cellPosition = pgc.CellPosition[cellIndex];

                CellID id = new CellID(cellPosition.VobID, cellPosition.CellID);

                PlaylistClip clipInfo = new();
                clipInfo.Filename = $"VTS_{ifo.TitleNumber:00}_{menuName}Cell_{id}.mkv";
                clipInfo.Angle = (int)angle;
                clipInfo.StartTime = DiscTime.Zero; 
                clipInfo.InTime = DiscTime.Zero;
                clipInfo.OutTime = cellPlayback.PlaybackTime;
                playlist.Clips.Add(clipInfo);
            }

            // Load chapter information
            DiscTime chapterTotal = DiscTime.Zero;
            int chapterNum = 1;
            // Every program is a chapter, including the last, which runs to the end of the PGC. Some discs end
            // with a very short program (e.g. 0.5 s on Fox releases); those are kept here and left for the
            // user to remove when setting up the transcode.
            // Some discs (e.g. Madagascar) list programs starting at cells that don't exist, or out of order: only
            // programs that start at a real cell after the last one count.
            var programStarts = new List<int>();
            foreach (var entry in pgc.ProgramMap)
            {
                int cell = entry - 1;
                if (cell >= 0 && cell < pgc.CellPlayback.Count && (programStarts.Count == 0 || cell > programStarts[^1]))
                    programStarts.Add(cell);
            }

            for (int programIndex = 0; programIndex < programStarts.Count; programIndex++)
            {
                int thisCellIndex = programStarts[programIndex];
                int nextCellIndex = programIndex + 1 < programStarts.Count
                    ? programStarts[programIndex + 1]
                    : pgc.CellPlayback.Count;

                DiscDecoders.Common.ChapterInfo chapter = new();
                chapter.Title = $"Chapter {chapterNum++}";
                chapter.Index = (UInt64)playlist.Chapters.Count;
                chapter.StartTime = chapterTotal;
                chapter.ClipReference = ""; //TODO (isMenu ? GetPgcFileName(title, pgcn, 0) : GetPgcFileName(title, pgcn, 1));

                // Count up the duration of this chapter, then save the stop time. In an angle block each angle
                // covers the same stretch of time, so only this playlist's angle counts.
                for (int cellIndex = thisCellIndex; cellIndex < nextCellIndex; cellIndex++)
                {
                    if (IsCellInAngle(cellAngles[cellIndex], angle))
                        chapterTotal += pgc.CellPlayback[cellIndex].PlaybackTime;
                }
                chapter.StopTime = chapterTotal;

                // Finally, save this chapter
                playlist.Chapters.Add(chapter);
            }


            yield return playlist;
        }
    }

    /// <summary>
    /// The angle each cell in a PGC belongs to: 0 for normal cells (played for every angle), or 1, 2, 3...
    /// for cells inside an angle block, counted from the block's first cell.
    /// </summary>
    static int[] GetCellAngles(PGC pgc)
    {
        int[] angles = new int[pgc.CellPlayback.Count];
        int angle = 0;
        for (int i = 0; i < angles.Length; i++)
        {
            var cell = pgc.CellPlayback[i];
            if (cell.BlockType != BlockType.Angle)
            {
                angle = 0;
            }
            else if (cell.CellType == AngleBlockType.First || angle == 0)
            {
                angle = 1;
            }
            else
            {
                angle++;
            }

            angles[i] = angle;
        }

        return angles;
    }

    /// <summary>Whether a cell (see <see cref="GetCellAngles"/>) plays when viewing the given angle.</summary>
    static bool IsCellInAngle(int cellAngle, uint angle) => cellAngle == 0 || cellAngle == angle;

    /// <summary>
    /// The streams a cell should be exported with, in output order (video, audio, subtitles), using the
    /// stream IDs that the PGCs playing this cell actually select. Subtitles use the widescreen or
    /// standard variant to match the video, rather than the letterbox/pan-and-scan copies.
    /// </summary>
    static List<DesiredStream> GetDesiredStreams(IfoFile ifo, ExtractedFileInfo cell, Action<DiscMessageLevel, string> log)
    {
        List<DesiredStream> result = new();
        var video = GetVideo(ifo, cell.IsMenu).FirstOrDefault();
        if (video != null)
            result.Add(new DesiredStream(video.StreamID, video));

        // Stream control entry i describes attribute i. A stream is wanted if any PGC playing this cell
        // makes it available; with no PGC found, fall back to the attribute's own ID.
        var pgcs = FindPgcsForCell(ifo, cell);

        var audio = GetAudio(ifo, cell.IsMenu).ToList();
        for (int i = 0; i < audio.Count; i++)
        {
            var format = audio[i].Format;
            var ids = pgcs.Count == 0
                ? [audio[i].StreamID]
                : pgcs.Select(x => x.AudioStreams[i]).Where(x => x.Available).Select(x => x.GetStreamId(format)).Distinct().ToList();
            AddStreams(result, ids, audio[i], cell, $"audio stream {i + 1}", log);
        }

        var aspect = video?.DisplayAspectRatio ?? DvdDecoder.AspectRatio.Fullscreen;
        var subs = GetSubs(ifo, cell.IsMenu).ToList();
        for (int i = 0; i < subs.Count; i++)
        {
            var ids = pgcs.Count == 0
                ? [subs[i].StreamID]
                : pgcs.Select(x => x.SubpictureStreams[i]).Where(x => x.Available).Select(x => x.GetStreamId(aspect)).Distinct().ToList();
            AddStreams(result, ids, subs[i], cell, $"subtitle stream {i + 1}", log);
        }

        return result;
    }

    /// <summary>
    /// Adds one attribute's stream(s). PGCs sharing a cell normally agree on the stream ID for an attribute;
    /// if they don't, every ID used is kept so no PGC loses its track.
    /// </summary>
    static void AddStreams(List<DesiredStream> result, List<byte> ids, IStreamAttributes attributes, ExtractedFileInfo cell, string description,
        Action<DiscMessageLevel, string> log)
    {
        if (ids.Count > 1)
            log(DiscMessageLevel.Warning, $"PGCs playing {cell.FileName} use different IDs for {description}: {string.Join(", ", ids.Select(x => $"0x{x:X2}"))}. Keeping all of them.");

        foreach (var id in ids)
        {
            // Several attributes can point at the same substream, e.g. a short logo or warning with one audio
            // track that plays whichever language is selected. Copy it once, labelled as the first.
            if (!result.Any(x => x.StreamID == id))
                result.Add(new DesiredStream(id, attributes));
        }
    }

    /// <summary>
    /// ffmpeg's ID for a stream in an MPEG program stream: the full stream ID (0x1E0, 0x1C0) for MPEG video
    /// and audio, or just the substream ID (0x80, 0x20, ...) for streams inside private stream 1.
    /// </summary>
    static uint FfmpegStreamID(byte streamID) => streamID >= 0xC0 ? 0x100u | streamID : streamID;

    /// <summary>Every PGC (menu or title, matching the cell) that plays the given cell.</summary>
    static List<PGC> FindPgcsForCell(IfoFile ifo, ExtractedFileInfo cell)
    {
        IEnumerable<PGC> candidates = cell.IsMenu
            ? (ifo.MenuProgramChainTable?.SelectMany(lu => lu.PgcTable.Select(x => x.Pgc)) ?? [])
            : (ifo.TitleProgramChainTable?.Select(x => x.Pgc) ?? []);

        return candidates
            .Distinct()
            .Where(pgc => pgc.CellPosition.Any(pos => pos.VobID == cell.VobID && pos.CellID == cell.CellID))
            .ToList();
    }

    /// <summary>A still time from the IFO: 0 for none, 0xFF until the viewer moves on, otherwise seconds.</summary>
    static Still? GetStill(byte seconds) => seconds switch
    {
        0 => null,
        0xFF => new Still(null),
        _ => new Still(DiscTime.FromSeconds(seconds)),
    };

    /// <param name="streamId">Its MPEG stream ID, or substream ID in private stream 1 (see <see cref="StreamInfo.StreamId"/>).</param>
    static StreamInfo GetStreamInfo(IStreamAttributes attributes, DiscTime duration, int streamId, bool interlaced)
    {
        switch(attributes)
        {
            case VideoAttributes attr:
                return new VideoStreamInfo()
                {
                    AspectRatio = ConvertAspectRatio(attr.DisplayAspectRatio),
                    Codec = ConvertVideoCodec(attr.Format),
                    Duration = duration,
                    Framerate = ConvertFramerate(attr.Region),
                    IsSecondary = false,
                    Language = null,
                    StreamId = streamId,
                    // The IFO doesn't say whether the video is interlaced: reading the cell does (see Disc.ReadInfo)
                    Resolution = ConvertResolution(attr.Resolution, interlaced),
                    // DVD video is always standard definition and SDR; both NTSC and PAL use BT.601 colour
                    DynamicRange = VideoDynamicRange.SDR,
                    ColorSpace = VideoColorSpace.BT601,
                };
            case AudioAttributes attr:
                return new AudioStreamInfo()
                {
                    ChannelCount = attr.Channels,
                    Codec = ConvertAudioCodec(attr.Format),
                    Duration = duration,
                    IsSecondary = false,
                    Language = attr.Language,
                    StreamId = streamId,
                    Rate = attr.SampleFrequency,
                };
            case SubpictureAttributes attr:
                return new SubtitleStreamInfo()
                {
                    Codec = SubtitleCodec.dvd_subtitle,
                    Duration = duration,
                    IsSecondary = false,
                    Language = attr.Language,
                    StreamId = streamId,
                };
            default:
                throw new Exception("Invalid stream type.");
        }
    }

    static DiscDecoders.Common.AspectRatio ConvertAspectRatio(DvdDecoder.AspectRatio value)
    {
        switch(value)
        {
            case DvdDecoder.AspectRatio.Fullscreen:
                return DiscDecoders.Common.AspectRatio.Aspect_4_3;
            case DvdDecoder.AspectRatio.WideScreen:
                return DiscDecoders.Common.AspectRatio.Aspect_16_9;
            default:
                throw new Exception("Invalid aspect ratio.");
        }
    }

    static VideoCodec ConvertVideoCodec(MpegVersion value)
    {
        switch(value)
        {
            case MpegVersion.Mpeg1:
                return VideoCodec.MPEG1;
            case MpegVersion.Mpeg2:
                return VideoCodec.MPEG2;
            default:
                throw new Exception("Invalid video codec.");
        }
    }

    static double ConvertFramerate(RegionStandard value)
    {
        switch(value)
        {
            case RegionStandard.NTSC:
                return 29.97;
            case RegionStandard.PAL:
                return 25.0;
            default:
                throw new Exception("Invalid value.");
        }
    }

    static DiscDecoders.Common.VideoResolution ConvertResolution(DvdDecoder.VideoResolution value, bool interlaced)
    {
        switch(value)
        {
            case DvdDecoder.VideoResolution.NTSC_352x240:
                return interlaced ? DiscDecoders.Common.VideoResolution.Resolution_240i : DiscDecoders.Common.VideoResolution.Resolution_240p;
            case DvdDecoder.VideoResolution.PAL_352x288:
                return interlaced ? DiscDecoders.Common.VideoResolution.Resolution_288i : DiscDecoders.Common.VideoResolution.Resolution_288p;
            case DvdDecoder.VideoResolution.NTSC_352x480:
            case DvdDecoder.VideoResolution.NTSC_704x480:
            case DvdDecoder.VideoResolution.NTSC_720x480:
                return interlaced ? DiscDecoders.Common.VideoResolution.Resolution_480i : DiscDecoders.Common.VideoResolution.Resolution_480p;
            case DvdDecoder.VideoResolution.PAL_352x576:
            case DvdDecoder.VideoResolution.PAL_704x576:
            case DvdDecoder.VideoResolution.PAL_720x576:
                return interlaced ? DiscDecoders.Common.VideoResolution.Resolution_576i : DiscDecoders.Common.VideoResolution.Resolution_576p;
            default:
                throw new Exception("Invalid resolution.");
        }
    }

    static AudioCodec ConvertAudioCodec(DvdDecoder.AudioEncoding value)
    {
        switch(value)
        {
            case AudioEncoding.AC3: return AudioCodec.AC3;
            case AudioEncoding.Mpeg1: return AudioCodec.MPEG1;
            case AudioEncoding.Mpeg2: return AudioCodec.MPEG2;
            case AudioEncoding.LPCM: return AudioCodec.LPCM;
            case AudioEncoding.SDDS: return AudioCodec.SDDS;
            case AudioEncoding.DTS: return AudioCodec.DTS;
            case AudioEncoding.Invalid:
            case AudioEncoding.Unknown:
            default:
                throw new Exception("Invalid audio codec.");
        }
    }

    static bool DetermineInterlaceFromFieldOrder(string? FieldOrder, string fileName, Action<DiscMessageLevel, string> log)
    {
        switch(FieldOrder)
        {
            case "progressive":
            case "unknown":
                return false;
            case "bb":
            case "tt":
            case "bt":
            case "tb":
            case "auto":
                return true;
            default:
                log(DiscMessageLevel.Warning, $"{fileName}: unknown field order \"{FieldOrder}\" from ffprobe; treating the video as interlaced.");
                return true;
        }
    }

    record struct CellID
    {
        public ushort VID;
        public byte CID;

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
}