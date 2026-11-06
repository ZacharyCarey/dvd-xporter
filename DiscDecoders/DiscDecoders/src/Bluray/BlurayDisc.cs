using DiscDecoders.BlurayDecoder;
using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>
/// A Blu-ray. Its clips are the .m2ts files in BDMV/STREAM, named as they are there (e.g. "00012.m2ts"), read from the
/// folder or straight out of the image.
/// </summary>
public sealed partial class BlurayDisc : Disc
{
    private const string StreamFolder = "BDMV/STREAM";

    // Clip info doesn't change, and reading it out of an image means opening the image
    private readonly Dictionary<string, ClipInformation?> _clipInfo = new(StringComparer.OrdinalIgnoreCase);

    public override DiscFormat Format => DiscFormat.Bluray;

    internal BlurayDisc(string rootPath, bool isImage) : base(rootPath, isImage)
    {
    }

    /// <summary>
    /// Reads the disc's playlists and clips without its video files: clip lengths come from the clip info
    /// files, and audio channel counts are as the playlists give them (e.g. "multi-channel" is left at 0).
    /// For previewing straight from the disc.
    /// </summary>
    /// <exception cref="DiscException">The disc couldn't be read.</exception>
    protected override DiscInfo ReadNavigation()
    {
        try
        {
            var bluray = Bluray.Parse(RootPath, message => Log(DiscMessageLevel.Warning, message));
            DiscInfo info = new();

            var metadata = bluray.GetMetadata();
            info.Name = metadata?.Name;
            info.DiscNumber = metadata?.SetNumber;
            info.NumberOfDiscs = metadata?.NumberOfSets;

            // Playlists are named by their number (00001.mpls), which players such as mpv and bdpslice also accept,
            // rather than by libbluray's "title" numbers, which count the playlists in the order it lists them
            Dictionary<string, ClipInfo> clipInfos = new();
            foreach (var bdPlaylist in bluray.Playlists)
            {
                // A multi-angle playlist becomes one playlist per angle, like the DVD side. Each angle plays its own
                // clip for multi-angle segments and the shared clip elsewhere.
                int angleCount = Math.Max(1, bdPlaylist.AngleCount);
                for (int angle = 0; angle < angleCount; angle++)
                {
                    var title = bluray.GetTitle(bdPlaylist, angle);

                    PlaylistInfo playlist = new();
                    playlist.Name = angleCount > 1 ? $"{bdPlaylist.Number:00000}_Angle{angle + 1}.mpls" : $"{bdPlaylist.Number:00000}.mpls";
                    playlist.Duration = title.Duration;
                    playlist.Angles = title.AngleCount;

                    // Clips can carry different streams (a silent bumper or still often has no audio), so list
                    // every stream any clip has, in disc order, the first time each one appears
                    var seenStreams = new HashSet<(string Kind, ushort Pid, byte? SubPathId)>();
                    IEnumerable<(TitleClip Clip, PlaylistStream Stream)> AllStreams(string kind, Func<StreamTable, IEnumerable<PlaylistStream>> streams) =>
                        title.Clips.SelectMany(clip => streams(clip.Streams).Select(stream => (clip, stream)))
                            .Where(x => seenStreams.Add((kind, x.stream.Pid, x.stream.SubPathId)));

                    foreach (var (clip, stream) in AllStreams("video", s => s.Video))
                    {
                        playlist.VideoStreams.Add(ExtractVideoStreamInfo(stream, clip.GetAspect(stream), false, playlist.Duration));
                    }

                    foreach (var (clip, stream) in AllStreams("secondary video", s => s.SecondaryVideo))
                    {
                        playlist.VideoStreams.Add(ExtractVideoStreamInfo(stream, clip.GetAspect(stream), true, playlist.Duration));
                    }

                    foreach (var (clip, stream) in AllStreams("dolby vision", s => s.DolbyVision))
                    {
                        var stream_info = ExtractVideoStreamInfo(stream, clip.GetAspect(stream), false, playlist.Duration);
                        stream_info.IsEnhancementLayer = true;
                        playlist.VideoStreams.Add(stream_info);
                    }

                    foreach (var (_, stream) in AllStreams("audio", s => s.Audio))
                    {
                        playlist.AudioStreams.Add(ExtractAudioStreamInfo(stream, false, playlist.Duration));
                    }

                    foreach (var (_, stream) in AllStreams("secondary audio", s => s.SecondaryAudio))
                    {
                        playlist.AudioStreams.Add(ExtractAudioStreamInfo(stream, true, playlist.Duration));
                    }

                    foreach (var (_, stream) in AllStreams("subtitle", SubtitleStreams))
                    {
                        playlist.SubtitleStreams.Add(ExtractPresentationStreamInfo(stream));
                    }

                    foreach (var clip in title.Clips)
                    {
                        var clip_info = new PlaylistClip();
                        clip_info.Still = clip.PlayItem.StillMode switch
                        {
                            StillMode.Time => new Still(clip.PlayItem.StillTime),
                            StillMode.Infinite => new Still(null),
                            _ => null,
                        };
                        clip_info.StartTime = clip.StartTime;
                        clip_info.InTime = clip.InTime;
                        clip_info.OutTime = clip.OutTime;
                        clip_info.Filename = $"{clip.ClipId}.m2ts";
                        clip_info.Angle = angle + 1;
                        playlist.Clips.Add(clip_info);

                        // A clip played by several playlists (e.g. a film's different cuts) can have a different
                        // stream selection in each, so its streams are all of theirs together
                        if (!clipInfos.TryGetValue(clip_info.Filename, out var clpi))
                        {
                            clpi = new ClipInfo();
                            clpi.Filename = clip_info.Filename;
                            clpi.Duration = clip.ClipDuration;
                            clpi.Start = clip.PresentationStart;
                            clipInfos.Add(clpi.Filename, clpi);
                        }

                        var streams = new List<StreamInfo>();
                        foreach (var stream in clip.Streams.Video)
                        {
                            streams.Add(ExtractVideoStreamInfo(stream, clip.GetAspect(stream), false, DiscTime.Zero));
                        }
                        foreach (var stream in clip.Streams.SecondaryVideo)
                        {
                            streams.Add(ExtractVideoStreamInfo(stream, clip.GetAspect(stream), true, DiscTime.Zero));
                        }
                        foreach (var stream in clip.Streams.DolbyVision)
                        {
                            var stream_info = ExtractVideoStreamInfo(stream, clip.GetAspect(stream), false, DiscTime.Zero);
                            stream_info.IsEnhancementLayer = true;
                            streams.Add(stream_info);
                        }
                        foreach (var stream in clip.Streams.Audio)
                        {
                            streams.Add(ExtractAudioStreamInfo(stream, false, DiscTime.Zero));
                        }
                        foreach (var stream in clip.Streams.SecondaryAudio)
                        {
                            streams.Add(ExtractAudioStreamInfo(stream, true, DiscTime.Zero));
                        }
                        foreach (var stream in SubtitleStreams(clip.Streams))
                        {
                            streams.Add(ExtractPresentationStreamInfo(stream));
                        }

                        foreach (var stream in streams)
                        {
                            if (!clpi.Streams.Any(x => x.StreamId == stream.StreamId && x.Type == stream.Type && x.IsSecondary == stream.IsSecondary))
                                clpi.Streams.Add(stream);
                        }

                        // The same order however the playlists listed them: by kind, main before secondary, then PID
                        clpi.Streams = clpi.Streams.OrderBy(x => x.Type).ThenBy(x => x.IsSecondary).ThenBy(x => x.StreamId).ToList();
                    }

                    // TODO do we actually need to re-calculate the chapter start time?
                    DiscTime chapter_start = DiscTime.Zero;
                    int chapterNum = 1;
                    foreach (var chapter in title.Chapters)
                    {
                        var chapter_info = new ChapterInfo();
                        chapter_info.Title = $"Chapter {chapterNum++}";
                        chapter_info.Index = (ulong)chapter.Index;
                        chapter_info.StartTime = chapter_start;
                        chapter_info.StopTime = chapter_start + chapter.Duration;
                        // The chapter's clip is a position in the playlist's clip list (0 = first clip), not a clip
                        // number, so look up which .m2ts file is at that position
                        chapter_info.ClipReference = chapter.ClipIndex < title.Clips.Count
                            ? $"{title.Clips[chapter.ClipIndex].ClipId}.m2ts"
                            : "";
                        playlist.Chapters.Add(chapter_info);

                        chapter_start += chapter.Duration;
                    }

                    info.Playlists.Add(playlist);
                }
            }

            info.Clips = clipInfos.Values.ToList();
            return info;
        }
        catch (BlurayException ex)
        {
            // The disc can't be used at all (e.g. it's still encrypted), so tell the user why
            throw new DiscException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new DiscException($"Couldn't read the Blu-ray's playlists: {ex.Message}", ex);
        }
    }

    // PGS subtitles, then those for picture-in-picture video, as players number them
    private static IEnumerable<PlaylistStream> SubtitleStreams(StreamTable streams) =>
        streams.PresentationGraphics.Concat(streams.PipPresentationGraphics);

    public override Stream OpenClip(string clip)
    {
        var file = GetClipFilePath(clip);
        if (file is not null)
            return File.OpenRead(file);
        if (!IsImage)
            throw new DiscException($"{clip} isn't in the disc's {StreamFolder} folder.");

        var fs = OpenFileSystem(RootPath);
        try
        {
            var name = DiscFileSystem.Find(fs.GetFileNames(StreamFolder), clip)
                ?? throw new DiscException($"{clip} isn't in the image's {StreamFolder} folder.");
            return new OwningStream(fs.OpenRead($"{StreamFolder}/{name}"), fs);
        }
        catch (Exception ex)
        {
            fs.Dispose();
            if (ex is DiscException)
                throw;
            throw new DiscException($"{clip} couldn't be read from the image: {ex.Message}", ex);
        }
    }

    public override string? GetClipFilePath(string clip)
    {
        if (IsImage)
            return null;
        var file = Path.Combine(RootPath, "BDMV", "STREAM", clip);
        return File.Exists(file) ? file : null;
    }

    public override Func<TimeSpan, long>? GetSeekIndex(string clip)
    {
        var info = GetClipInfo(clip);
        return (info is not null) ? info.GetByteOffset : null;
    }

    private ClipInformation? GetClipInfo(string clip)
    {
        lock (_clipInfo)
        {
            if (_clipInfo.TryGetValue(clip, out var known))
                return known;
        }

        ClipInformation? info;
        try
        {
            info = Bluray.ReadClipInfo(RootPath, Path.GetFileNameWithoutExtension(clip));
        }
        catch (BlurayException)
        {
            // Seeking falls back to the player's own, and times to the playlists'
            info = null;
        }

        lock (_clipInfo)
            _clipInfo[clip] = info;
        return info;
    }

    /// <summary>The PIDs of the clip's presentation graphics subtitles (PGS), as its clip info lists them.</summary>
    private IReadOnlyList<int> GetSubtitlePids(string clip) => GetPids(clip, t => t == StreamCodingType.PresentationGraphics);

    /// <summary>The PIDs of the clip's audio, as its clip info lists them.</summary>
    private IReadOnlyList<int> GetAudioPids(string clip) => GetPids(clip, t => t is StreamCodingType.Mpeg1Audio or StreamCodingType.Mpeg2Audio
        or (>= StreamCodingType.Lpcm and <= StreamCodingType.DtsHdMaster) or StreamCodingType.Ac3PlusSecondary or StreamCodingType.DtsHdSecondary);

    private IReadOnlyList<int> GetPids(string clip, Func<StreamCodingType, bool> kind) =>
        GetClipInfo(clip)?.Programs.SelectMany(p => p.Streams)
            .Where(s => kind(s.CodingType))
            .Select(s => (int)s.Pid).Distinct().ToList() ?? [];

    /// <inheritdoc/>
    /// <remarks>
    /// The clip's presentation graphics subtitles (PGS, as its clip info lists them): their captions, and the forced ones
    /// as display sets, which reads the whole clip (about half a minute for a film); and its audio's bitrates, from its
    /// first few seconds.
    /// </remarks>
    protected override ClipScan ScanClip(string clip, CancellationToken cancellation)
    {
        var pids = GetSubtitlePids(clip);
        var audioPids = GetAudioPids(clip);
        IReadOnlyList<AudioBitrate>? audio = null;
        if (audioPids.Count > 0)
        {
            using var stream = OpenClip(clip);
            audio = TsBitrates.Measure(stream, audioPids, cancellation);
        }

        // The clip info says the rest
        if (pids.Count == 0)
            return new ClipScan(null, null, null, null, [], [], Audio: audio);
        Dictionary<int, PgsStreamScan> scans;
        using (var stream = OpenClip(clip))
            scans = PgsScanner.Scan(stream, pids, cancellation);

        var subtitles = scans.Values.OrderBy(s => s.Pid)
            .Select(s => new SubtitleScan(s.Pid, s.Total, s.Forced,
                s.ForcedSets.Select(set => new ForcedCaption(new DiscTime((long)set[0].Pts), PgsScanner.ToSup(set))).ToList()))
            .ToList();
        return new ClipScan(null, null, null, null, [], subtitles, Audio: audio);
    }
}
