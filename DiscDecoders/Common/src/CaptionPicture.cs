namespace DiscDecoders.Common;

/// <summary>
/// One caption of a picture-based subtitle stream (DVD or Blu-ray), drawn: see <c>Disc.ReadCaptions</c>.
/// </summary>
/// <param name="Start">When it shows, on the clip's own timeline (as <see cref="ClipScan.Start"/>).</param>
/// <param name="End">When it goes, on the same timeline; null if the stream doesn't say (it lasts until the next).</param>
/// <param name="Forced">Whether it's meant to show even with subtitles off.</param>
/// <param name="X">Where its picture sits on the video frame.</param>
/// <param name="Rgba">Its picture, 4 bytes a pixel, row by row; transparent around the text.</param>
public sealed record CaptionPicture(DiscTime Start, DiscTime? End, bool Forced, int X, int Y, int Width, int Height, byte[] Rgba);
