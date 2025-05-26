using System.Text.Json.Serialization;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;

namespace DvdXporter.Core.Projects;

/// <summary>
/// A user-arranged sequence of a disc's video files, e.g. an episode split across several clips.
/// Can be built by hand or imported from one of the disc's own playlists.
/// </summary>
public sealed class ClipPlaylist(string name, string? id = null)
{
    /// <summary>Stays the same across renames and reordering, so transcoding graphs can refer to the playlist.</summary>
    public string Id { get; } = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N")[..12] : id;

    public string Name { get; set; } = name;

    /// <summary>The disc playlist this was imported from (e.g. "00800.mpls"), or null if built by hand.</summary>
    public string? ImportedFrom { get; set; }

    public List<PlaylistEntry> Entries { get; } = [];

    /// <summary>How long it plays for, with clip lengths from <paramref name="contents"/>; null if any entry's isn't known.</summary>
    public PreciseTime? DurationIn(DiscContents? contents)
    {
        var total = PreciseTime.Zero;
        foreach (var entry in Entries)
        {
            var duration = entry.DurationIn(contents);
            if (duration is null)
                return null;
            total += duration.Value;
        }

        return total;
    }

    /// <summary>Puts <paramref name="clip"/> in at <paramref name="index"/> (kept within the list). Returns where it went.</summary>
    public int Insert(string clip, int index)
    {
        index = Math.Clamp(index, 0, Entries.Count);
        Entries.Insert(index, new PlaylistEntry(clip));
        return index;
    }

    /// <summary>
    /// Moves the entry at <paramref name="from"/> to before the one at <paramref name="before"/> (the count for the end).
    /// Returns where it went, or null if there's no such entry.
    /// </summary>
    public int? Move(int from, int before)
    {
        if (from < 0 || from >= Entries.Count)
            return null;

        var entry = Entries[from];
        Entries.RemoveAt(from);
        var to = Math.Clamp(before > from ? before - 1 : before, 0, Entries.Count);
        Entries.Insert(to, entry);
        return to;
    }
}

/// <summary>
/// One video file in a <see cref="ClipPlaylist"/>.
/// </summary>
/// <param name="Clip">The video file's name on the disc, e.g. "00012.m2ts".</param>
/// <param name="InTime">Where playback starts, on the clip's own timeline as the disc gives it. Null = the start.</param>
/// <param name="OutTime">Where playback stops, on the same timeline. Null = the end.</param>
public sealed record PlaylistEntry(string Clip, DiscTime? InTime = null, DiscTime? OutTime = null)
{
    /// <summary>Whether this plays only part of the clip.</summary>
    [JsonIgnore]
    public bool IsTrimmed => InTime is not null || OutTime is not null;

    /// <summary>How long this entry plays for, if it is trimmed at both ends.</summary>
    [JsonIgnore]
    public DiscTime? TrimmedDuration => InTime is not null && OutTime is not null ? OutTime.Value - InTime.Value : null;

    /// <summary>
    /// How long it plays for, with its clip's length from <paramref name="contents"/> if it plays all of it; null if
    /// that isn't known, or it's trimmed at one end only.
    /// </summary>
    public PreciseTime? DurationIn(DiscContents? contents) =>
        (TrimmedDuration ?? (IsTrimmed ? null : contents?.FindClip(Clip)?.Duration)).ToPreciseTime();
}
