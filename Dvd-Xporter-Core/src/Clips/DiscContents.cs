using System.Globalization;
using System.Text.Json.Serialization;
using DiscDecoders.Common;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Clips;

/// <summary>
/// The video files and playlists on one disc, summarised from its full <see cref="DiscInfo"/>, with what reading each
/// clip through found. Read once, when the disc is added, and saved in the project (see
/// <see cref="ProjectFolder.DiscInfoFolderName"/>), so it travels with it and nobody reads the disc again.
/// </summary>
public sealed class DiscContents
{
    /// <summary>
    /// Whether every frame of its clips was checked for interlacing when it was read (see
    /// <see cref="ProjectDisc.CheckEveryFrame"/>), rather than only their colour.
    /// </summary>
    public bool EveryFrameChecked { get; set; }

    public List<ClipSummary> Clips { get; set; } = [];

    public List<DiscPlaylist> Playlists { get; set; } = [];

    public ClipSummary? FindClip(string file) =>
        Clips.Find(c => c.File.Equals(file, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of the disc's own playlists that play each clip, by clip file name.</summary>
    public Dictionary<string, List<string>> PlaylistsByClip()
    {
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in Playlists)
        {
            foreach (var clip in playlist.Entries.Select(e => e.Clip).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!usage.TryGetValue(clip, out var list))
                    usage[clip] = list = [];
                list.Add(playlist.Name);
            }
        }

        return usage;
    }

    internal static DiscContents From(DiscInfo info)
    {
        var durations = info.Clips.ToDictionary(c => c.Filename, c => c.Duration, StringComparer.OrdinalIgnoreCase);
        return new DiscContents
        {
            Clips = info.Clips
                .OrderBy(c => c.Filename, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ClipSummary(c.Filename, c.Duration, Describe(c.Streams), GetTracks(c.Streams, c.Scan),
                    c.Chapters.Count > 0 ? c.Chapters : null, c.Start,
                    c.SubtitlePalette?.Select(rgb => $"{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}").ToList(), c.Scan))
                .ToList(),
            Playlists = info.Playlists
                .Select(p => new DiscPlaylist(p.Name, p.Duration, p.Clips.Select(c => ToEntry(c, durations)).ToList()))
                .ToList(),
        };
    }

    // How far a playlist item's length can be from the clip's measured length and still count as the whole clip
    private static readonly DiscTime WholeClipTolerance = new(DiscTime.TicksPerSecond / 2); // 0.5s

    /// <summary>
    /// A playlist item, trimmed only if it plays part of the clip. In/out times are on the clip's own
    /// timeline (a Blu-ray clip's often starts at 10:00), so compare lengths rather than the times themselves.
    /// </summary>
    private static PlaylistEntry ToEntry(PlaylistClip clip, Dictionary<string, DiscTime> durations)
    {
        var length = clip.OutTime - clip.InTime;
        if (durations.TryGetValue(clip.Filename, out var duration) && duration > DiscTime.Zero
            && length - duration < WholeClipTolerance && duration - length < WholeClipTolerance)
        {
            return new PlaylistEntry(clip.Filename);
        }

        return new PlaylistEntry(clip.Filename, clip.InTime, clip.OutTime);
    }

    /// <summary>A one-line summary of a clip's streams, e.g. "1080p H264 · Audio: eng, fra · 3 subtitles".</summary>
    private static string Describe(List<StreamInfo> streams)
    {
        var parts = new List<string>();
        var video = streams.OfType<VideoStreamInfo>().FirstOrDefault(v => !v.IsSecondary && !v.IsEnhancementLayer);
        if (video is not null)
        {
            parts.Add($"{video.Resolution.ToString().Replace("Resolution_", "")} {video.Codec}");
        }

        var audio = streams.OfType<AudioStreamInfo>().Where(a => !a.IsSecondary).ToList();
        if (audio.Count > 0)
            parts.Add("Audio: " + string.Join(", ", audio.Select(a => a.Language?.Part2 ?? a.Language?.Part3 ?? "und")));
        else
            parts.Add("No audio");

        var subtitles = streams.OfType<SubtitleStreamInfo>().Count();
        if (subtitles > 0)
            parts.Add(subtitles == 1 ? "1 subtitle" : $"{subtitles} subtitles");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The clip's video, audio and subtitle tracks in the order they should be offered, with display names, and what its
    /// scan measured of them.
    /// </summary>
    private static List<ClipTrack> GetTracks(List<StreamInfo> streams, ClipScan? scan)
    {
        var tracks = new List<ClipTrack>();
        foreach (var stream in streams.OfType<VideoStreamInfo>())
        {
            var label = new List<string> { stream.Resolution.ToString().Replace("Resolution_", ""), stream.Codec.ToString() };
            if (stream.IsEnhancementLayer)
                label.Add("enhancement layer");
            else if (stream.IsSecondary)
                label.Add("secondary");
            tracks.Add(new ClipTrack(ClipTrackKind.Video, stream.StreamId, string.Join(" · ", label), Codec: stream.Codec.ToString(),
                FrameRate: stream.Framerate > 0 ? stream.Framerate : null));
        }

        foreach (var stream in streams.OfType<AudioStreamInfo>())
        {
            var label = new List<string> { LanguageName(stream.Language) };
            label.Add(stream.Codec.ToString());
            if (stream.ChannelCount > 0)
                label.Add($"{stream.ChannelCount} ch");
            if (stream.IsSecondary)
                label.Add("secondary");
            // A Blu-ray gives it in kHz, a DVD in Hz
            var rate = stream.Rate is > 0 and < 1000 ? stream.Rate * 1000 : stream.Rate;
            tracks.Add(new ClipTrack(ClipTrackKind.Audio, stream.StreamId, string.Join(" · ", label), LanguageCode(stream.Language), stream.Codec.ToString(),
                rate > 0 ? (int)Math.Round(rate) : null, Bitrate: scan?.Audio?.FirstOrDefault(a => a.StreamId == stream.StreamId)?.Kbps));
        }

        foreach (var stream in streams.OfType<SubtitleStreamInfo>())
            tracks.Add(new ClipTrack(ClipTrackKind.Subtitle, stream.StreamId, LanguageName(stream.Language), LanguageCode(stream.Language),
                stream.Codec == SubtitleCodec.Unknown ? null : stream.Codec.ToString()));

        return tracks;
    }

    private static string LanguageName(Iso639.Language? language) => language?.Name ?? "Unknown language";

    // Matroska's language element uses ISO 639-2/B codes ("fre", "ger"), which the Iso639 library marks
    // obsolete. Only 22 languages have one; the rest use the 639-2/T code.
#pragma warning disable CS0612
    private static string? LanguageCode(Iso639.Language? language) => language?.Part2B ?? language?.Part2 ?? language?.Part3;
#pragma warning restore CS0612
}

/// <param name="File">The video file's name, e.g. "00012.m2ts" or "VTS_01_TitleCell_01-01.mkv".</param>
/// <param name="Description">A one-line summary of its streams.</param>
/// <param name="Tracks">Its audio and subtitle tracks, in the order to offer them.</param>
/// <param name="Chapters">Where the disc's chapters start in it, from its start: Blu-ray playlist marks, or (for a
/// DVD cell) 0 if a program starts with it.</param>
/// <param name="Start">The time its presentation starts at on its own timeline, which playlist in/out times use
/// (a Blu-ray clip's is often 10:00; a DVD cell's is 0). Null if unknown.</param>
/// <param name="SubtitlePalette">The colours its image subtitles are drawn with, as "rrggbb", where the disc gives
/// them apart from the subtitles (a DVD cell's program chain palette); otherwise null.</param>
/// <param name="Scan">What reading it through found (see <see cref="ClipInfo.Scan"/>); null if it couldn't be read.</param>
/// <param name="Picture">Whether its video is progressive, interlaced or telecined, and its dynamic range, checked when the
/// disc was read; null if it has no video, or too little could be read to tell.</param>
public sealed record ClipSummary(string File, DiscTime Duration, string Description, List<ClipTrack>? Tracks = null,
    List<DiscTime>? Chapters = null, DiscTime? Start = null, List<string>? SubtitlePalette = null, ClipScan? Scan = null,
    Transcoding.PictureAnalysis? Picture = null);

public enum ClipTrackKind
{
    Video,
    Audio,
    Subtitle,
}

/// <param name="StreamId">For a DVD cell, the MPEG stream ID (0x80-0x87 AC-3, 0x20-0x3F subtitles, ...). For a
/// Blu-ray clip, the transport stream PID.</param>
/// <param name="Label">What to call it, e.g. "English · AC3 · 6 ch".</param>
/// <param name="Language">Its ISO 639-2 language code as Matroska writes it (e.g. "eng", "fre"), if known.</param>
/// <param name="Codec">Its codec as the disc names it (<see cref="VideoCodec"/>, <see cref="AudioCodec"/> or
/// <see cref="SubtitleCodec"/>), e.g. "H264", "VC1", "TruHD", "AC3", "dvd_subtitle"; null if unknown.</param>
/// <param name="SampleRate">Audio: its sample rate in Hz, e.g. 48000, as the disc gives it.</param>
/// <param name="FrameRate">Video: its frames per second, e.g. 29.97, as the disc gives it.</param>
/// <param name="Bitrate">Audio: its bitrate in kbps, measured from its first few seconds when the disc was read (see
/// <see cref="DiscDecoders.Common.BitrateMeter"/>); null if it wasn't.</param>
public sealed record ClipTrack(ClipTrackKind Kind, int StreamId, string Label, string? Language = null, string? Codec = null,
    int? SampleRate = null, double? FrameRate = null, int? Bitrate = null)
{
    /// <summary>All that's known about it, a line each, e.g. for a tooltip.</summary>
    [JsonIgnore]
    public string Details
    {
        get
        {
            var lines = new List<string> { Label };
            if (Kind == ClipTrackKind.Audio)
            {
                lines.Add(SampleRate is not null
                    ? $"Sample rate: {(SampleRate.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} kHz"
                    : "Sample rate: not known (the disc was read before it was noted; add it again to read it)");
                lines.Add(Bitrate is not null
                    ? $"Bitrate: {Bitrate.Value} kbps"
                    : "Bitrate: not known (the disc was read before it was measured; add it again to read it)");
            }

            if (FrameRate is not null)
                lines.Add($"Frame rate: {FrameRate.Value.ToString("0.###", CultureInfo.InvariantCulture)} fps");
            lines.Add($"Stream ID: 0x{StreamId:X}");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>How far reading a disc has got (see <see cref="DiscCache.ReadContentsAsync"/>).</summary>
/// <param name="CheckingPictures">False while its clips are read through, then true while their pictures are checked.</param>
/// <param name="Done">How many clips are done in this step, of <paramref name="Total"/>.</param>
/// <param name="Fraction">How far through the whole disc it is, 0–1: both steps, as one.</param>
public sealed record DiscContentsProgress(bool CheckingPictures, int Done, int Total, double Fraction);

/// <summary>One of the disc's own playlists (Blu-ray .mpls, DVD program chain), for importing.</summary>
public sealed record DiscPlaylist(string Name, DiscTime Duration, List<PlaylistEntry> Entries);
