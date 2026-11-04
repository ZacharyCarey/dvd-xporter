using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Subtitles;

/// <summary>One caption's text: read off its picture (see <see cref="SubtitleOcr"/>), then corrected by the user.</summary>
public sealed class SubtitleLine
{
    /// <summary>When it shows, on its clip's own timeline (as <see cref="ClipSummary.Start"/>).</summary>
    public DiscTime Start { get; set; }

    public DiscTime End { get; set; }

    /// <summary>Whether it's meant to show even with subtitles off (see <see cref="Transcoding.SubtitleHelp.Forced"/>).</summary>
    public bool Forced { get; set; }

    /// <summary>Its text, a line of the caption per line; blank ones are left out of the subtitles.</summary>
    public string Text { get; set; } = "";

    /// <summary>The number of the picture it was read from (see <see cref="SubtitleTexts.PicturePath"/>).</summary>
    public int Picture { get; set; }
}

/// <summary>The text of a clip's picture-based subtitle track, as text subtitles are made from.</summary>
public sealed class SubtitleText
{
    /// <summary>The OCR language it was read in, e.g. "eng" (see <see cref="Tesseract"/>).</summary>
    public string Language { get; set; } = "";

    public List<SubtitleLine> Lines { get; set; } = [];
}

/// <summary>
/// Where subtitle text is kept. The text is part of the project (under <see cref="ProjectFolder.SubtitleTextFolderName"/>,
/// one file per disc track), so corrections are shared with it. The pictures it was read from are this user's, kept with
/// the disc's other generated files, for checking the text against.
/// </summary>
public static class SubtitleTexts
{
    public static string PathOf(ProjectFolder folder, ProjectDisc disc, string clip, int streamId) =>
        Path.Combine(folder.FolderPath, ProjectFolder.SubtitleTextFolderName, disc.Fingerprint, $"{TrackName(clip, streamId)}.json");

    /// <summary>The track's saved text, or null if it hasn't been read.</summary>
    public static SubtitleText? Load(ProjectFolder folder, ProjectDisc disc, string clip, int streamId) =>
        JsonFile.TryRead<SubtitleText>(PathOf(folder, disc, clip, streamId));

    public static void Save(ProjectFolder folder, ProjectDisc disc, string clip, int streamId, SubtitleText text) =>
        JsonFile.Write(PathOf(folder, disc, clip, streamId), text);

    /// <summary>The folder of the pictures a track's text was read from.</summary>
    public static string PictureFolder(DiscCache cache, string clip, int streamId) =>
        Path.Combine(cache.FolderPath, "Subtitle Pictures", TrackName(clip, streamId));

    /// <summary>A line's picture (see <see cref="SubtitleLine.Picture"/>); it may not be on this computer.</summary>
    public static string PicturePath(DiscCache cache, string clip, int streamId, int picture) =>
        Path.Combine(PictureFolder(cache, clip, streamId), $"{picture:0000}.png");

    // e.g. "00012 - 1200" for a Blu-ray clip's PID 0x1200, "VTS_01_TitleCell_07-02 - 20" for a DVD cell's first subtitles
    private static string TrackName(string clip, int streamId) => $"{Path.GetFileNameWithoutExtension(clip)} - {streamId:x}";
}
