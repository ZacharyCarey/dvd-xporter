using DiscDecoders.Common;
using DvdXporter.Core.Clips;

namespace DvdXporter.Core.Projects;

/// <summary>
/// One disc in a project. Only shareable data lives here; where the disc is on this computer
/// is tracked separately in the project's <see cref="ProjectCache"/>.
/// </summary>
public sealed class ProjectDisc(string label, DiscFormat format, string fingerprint)
{
    public string Label { get; set; } = label;

    public DiscFormat Format { get; } = format;

    public string Fingerprint { get; } = fingerprint;

    /// <summary>
    /// Whether every frame of its clips is checked for interlacing when it's read, which takes as long as decoding the
    /// whole disc. Without it, their pictures are <see cref="Transcoding.PictureType.Unknown"/>. Chosen when it's added.
    /// </summary>
    public bool CheckEveryFrame { get; set; }

    /// <summary>
    /// Names the user gave the disc's video files, by file name (e.g. "00012.m2ts" → "Episode 1").
    /// File names come from the disc's structure, so they are the same on every rip of it.
    /// </summary>
    public Dictionary<string, string> ClipNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names the user gave video files' tracks, by file name and then the track's stream ID (see
    /// <see cref="ClipTrack.StreamId"/>), e.g. "00012.m2ts" → 0x1101 → "Director's Commentary".
    /// </summary>
    public Dictionary<string, Dictionary<int, string>> TrackNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ClipPlaylist> Playlists { get; } = [];

    public string? GetClipName(string clip) => ClipNames.GetValueOrDefault(clip);

    public string? GetTrackName(string clip, int streamId) => TrackNames.GetValueOrDefault(clip)?.GetValueOrDefault(streamId);

    /// <summary>A playlist's name for a track: the first of its video files, in order, to name it.</summary>
    public string? GetTrackName(IEnumerable<PlaylistEntry> entries, int streamId) =>
        entries.Select(e => GetTrackName(e.Clip, streamId)).FirstOrDefault(n => n is not null);

    /// <summary>Names one of a video file's tracks; a blank name removes it.</summary>
    public void SetTrackName(string clip, int streamId, string? name)
    {
        var names = TrackNames.GetValueOrDefault(clip);
        if (string.IsNullOrWhiteSpace(name))
        {
            if (names is not null && names.Remove(streamId) && names.Count == 0)
                TrackNames.Remove(clip);
            return;
        }

        if (names is null)
            TrackNames[clip] = names = [];
        names[streamId] = name.Trim();
    }

    /// <summary>Names a video file; a blank name removes it.</summary>
    public void SetClipName(string clip, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            ClipNames.Remove(clip);
        else
            ClipNames[clip] = name.Trim();
    }

    /// <summary>Adds an empty playlist, named "Playlist" and the next free number.</summary>
    public ClipPlaylist NewPlaylist()
    {
        var number = Playlists.Count + 1;
        while (Playlists.Any(p => p.Name == $"Playlist {number}"))
            number++;
        var playlist = new ClipPlaylist($"Playlist {number}");
        Playlists.Add(playlist);
        return playlist;
    }

    /// <summary>Adds a copy of one of the disc's own playlists, named after its file.</summary>
    public ClipPlaylist ImportPlaylist(DiscPlaylist source)
    {
        var playlist = new ClipPlaylist(Path.GetFileNameWithoutExtension(source.Name)) { ImportedFrom = source.Name };
        playlist.Entries.AddRange(source.Entries);
        Playlists.Add(playlist);
        return playlist;
    }

    /// <summary>
    /// Moves the playlist at <paramref name="from"/> to before the one at <paramref name="before"/> (the count for the
    /// end). Returns where it went, or null if there's no such playlist.
    /// </summary>
    public int? MovePlaylist(int from, int before)
    {
        if (from < 0 || from >= Playlists.Count)
            return null;

        var playlist = Playlists[from];
        Playlists.RemoveAt(from);
        var to = Math.Clamp(before > from ? before - 1 : before, 0, Playlists.Count);
        Playlists.Insert(to, playlist);
        return to;
    }

    // TODO: Store a title layout summary (durations, chapters, streams) for probable-match detection.
}
