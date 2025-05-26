using DvdXporter.Core.Transcoding;

namespace DvdXporter.Core.Emby;

/// <summary>Which devices an output file's playback is checked against.</summary>
public enum PlaybackDevices
{
    /// <summary>Not checked.</summary>
    None,

    /// <summary>TVs and streaming boxes (Android TV, Apple TV, Fire TV, Roku, smart TV apps).</summary>
    LivingRoom,

    /// <summary>Those, plus phones, tablets and web browsers.</summary>
    All,
}

/// <summary>An output track, as far as playing it goes.</summary>
/// <param name="Codec">What's in the file: an encoder ("libx264", "aac", ...), or for a copied track the disc's codec
/// ("H264", "VC1", "TruHD", "AC3", ..., as <see cref="Clips.ClipTrack.Codec"/>).</param>
/// <param name="Copied">Whether it's copied from the disc rather than encoded.</param>
/// <param name="BitDepth">A video's bits per sample, if known.</param>
/// <param name="Shown">Whether a subtitle track is shown without asking (forced, or the default).</param>
public sealed record PlaybackTrack(
    int Number,
    PortKind Kind,
    string Codec,
    bool Copied,
    PictureAnalysis? Picture = null,
    bool Deinterlaced = false,
    int? BitDepth = null,
    bool IsBitmapSubtitle = false,
    bool Shown = false);

/// <summary>
/// Spots choices that make Emby transcode a file when it's played, or that its container can't hold. Emby plays a
/// file directly when the device can; otherwise it converts what the device can't play, on the server, while it
/// streams. Converting only the audio is cheap; converting the video (including to burn in subtitles) is slow
/// and costs quality.
/// </summary>
public static class EmbyPlayback
{
    public static PlaybackDevices ParseDevices(string value) => value switch
    {
        "tv" => PlaybackDevices.LivingRoom,
        "off" => PlaybackDevices.None,
        _ => PlaybackDevices.All,
    };

    /// <summary>Problems that stop the file being made at all: tracks the container can't hold.</summary>
    public static IEnumerable<string> ContainerProblems(PlaybackTrack track, string extension)
    {
        if (extension != "mp4")
            yield break;
        if (track.Kind == PortKind.Subtitle && track.IsBitmapSubtitle)
            yield return $"Track {track.Number}: MP4 can't hold DVD or Blu-ray subtitles. Use Matroska.";
        if (track.Kind == PortKind.Audio && track.Codec == "TruHD")
            yield return $"Track {track.Number}: MP4 can't hold TrueHD audio. Use Matroska, or encode it.";
    }

    /// <summary>Why Emby would transcode the track on the given devices, and how bad that is.</summary>
    public static IEnumerable<(IssueLevel Level, string Message)> Check(PlaybackTrack track, PlaybackDevices devices)
    {
        if (devices == PlaybackDevices.None)
            yield break;
        var all = devices == PlaybackDevices.All;
        var n = $"Track {track.Number}";

        switch (track.Kind)
        {
            case PortKind.Video:
                var codec = VideoFamily(track.Codec);
                if (codec == "h264" && track.BitDepth == 10)
                {
                    yield return (IssueLevel.Warning, $"{n} is 10-bit H.264, which almost no TV, box, phone or browser can play: Emby "
                        + "transcodes it everywhere. Set the codec's Bit depth to 8-bit, or use H.265 for 10-bit.");
                }
                else if (codec == "ffv1")
                {
                    yield return (IssueLevel.Warning, $"{n} is FFV1, a lossless format for archiving that players don't support: "
                        + "Emby transcodes it everywhere.");
                }
                else if (codec == "vc1")
                {
                    yield return (IssueLevel.Hint, $"{n} is VC-1, which only a few devices play (e.g. Nvidia Shield): Emby transcodes "
                        + "it for most. Encode it to H.264 or H.265.");
                }
                else if (codec == "mpeg2")
                {
                    yield return (IssueLevel.Hint, $"{n} is MPEG-2, which some TVs play but most other devices don't, so Emby "
                        + "transcodes it for them. Encode it to H.264 to play everywhere.");
                }
                else if (codec == "av1")
                {
                    yield return (IssueLevel.Hint, $"{n} is AV1, which only recent devices play (roughly 2020 on): Emby "
                        + "transcodes it for older TVs, boxes and phones.");
                }
                else if (codec == "hevc" && all)
                {
                    yield return (IssueLevel.Hint, $"{n} is H.265, which most web browsers can't play, so Emby transcodes it "
                        + "for them. TVs, boxes and phone apps generally play it.");
                }

                // (H.264 doesn't keep HDR, which the codec warns about)
                if (track.Picture is { IsHdr: true } hdr && codec != "h264")
                {
                    yield return (IssueLevel.Hint, $"{n} is {(hdr.Range == DynamicRange.Hlg ? "HLG" : "HDR10")}: on SDR screens Emby tone "
                        + "maps it, which means transcoding. Add a Dynamic Range node set to SDR if it's mostly watched on SDR screens.");
                }

                if (track.Copied && !track.Deinterlaced && track.Picture?.Type is PictureType.Interlaced or PictureType.Telecined or PictureType.Mixed)
                {
                    yield return (IssueLevel.Hint, $"{n} is {track.Picture.Summary.ToLowerInvariant()} and copied as it is: Emby "
                        + "transcodes it to deinterlace for many devices, and others show it combed.");
                }

                break;

            case PortKind.Audio:
                if (track.Codec is "TruHD" or "DTS" or "DTSHD" or "DTSHD_Master" or "DTSHD_Secondary" or "LPCM" or "SDDS")
                {
                    var name = track.Codec switch
                    {
                        "TruHD" => "TrueHD",
                        "LPCM" => "uncompressed (LPCM)",
                        "SDDS" => "SDDS",
                        _ => "DTS",
                    };
                    yield return (IssueLevel.Hint, $"{n} is {name} audio, which most devices can only pass through to an AV receiver: "
                        + "Emby converts the audio for the rest (the video still plays directly). Adding a second, AC-3 or AAC, "
                        + "track avoids that.");
                }
                else if (all && track.Codec is "ac3" or "eac3" or "AC3" or "AC3Plus" or "AC3Plus_Secondary")
                {
                    yield return (IssueLevel.Hint, $"{n} is Dolby Digital, which most web browsers can't play, so Emby converts the "
                        + "audio for them (the video still plays directly).");
                }
                else if (all && track.Codec is "MPEG1" or "MPEG2")
                {
                    yield return (IssueLevel.Hint, $"{n} is MPEG audio, which many devices can't play, so Emby converts the audio "
                        + "for them (the video still plays directly).");
                }

                break;

            case PortKind.Subtitle when track.IsBitmapSubtitle:
                var when = track.Shown
                    ? "It's shown automatically, so that happens every time. To show it without transcoding, burn the forced captions into the video (Burn Forced Subtitles)."
                    : "That only happens while they're turned on.";
                yield return (IssueLevel.Hint, $"{n} is picture-based (DVD or Blu-ray) subtitles. Devices that can't draw them, "
                    + $"including web browsers and many TV apps, make Emby burn them into the video, which transcodes it. {when}");
                break;
        }
    }

    // An encoder's or a disc codec's family
    private static string VideoFamily(string codec) => codec switch
    {
        "libx264" or "H264" => "h264",
        "libx265" or "H265" => "hevc",
        "libsvtav1" => "av1",
        "ffv1" => "ffv1",
        "VC1" => "vc1",
        "MPEG1" or "MPEG2" => "mpeg2",
        _ => codec.ToLowerInvariant(),
    };
}
