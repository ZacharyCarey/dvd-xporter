using DvdXporter.Core.Metadata;

namespace DvdXporter.UI;

/// <summary>
/// How each kind of artwork is labelled in the UI, with Emby's recommended shape and size.
/// </summary>
public static class ImageKindDisplay
{
    /// <param name="thumbnailName">What a thumbnail is called, for an item whose own picture it is (an episode's, an extra's).</param>
    public static string Name(ImageKind kind, string? thumbnailName = null) => kind switch
    {
        ImageKind.Poster => "Poster",
        ImageKind.Backdrop => "Backdrop",
        ImageKind.Banner => "Banner",
        ImageKind.Logo => "Logo",
        ImageKind.Thumb => thumbnailName ?? "Landscape Thumbnail",
        ImageKind.ClearArt => "Clear Art",
        ImageKind.DiscArt => "Disc Art",
        _ => kind.ToString(),
    };

    public static string Hint(ImageKind kind) => kind switch
    {
        ImageKind.Poster => "2:3, e.g. 1000×1500",
        ImageKind.Backdrop => "16:9 background, e.g. 1920×1080",
        ImageKind.Banner => "Wide strip, e.g. 1000×185",
        ImageKind.Logo => "Transparent PNG, e.g. 800×310",
        ImageKind.Thumb => "16:9, e.g. 1000×562",
        ImageKind.ClearArt => "Transparent PNG, e.g. 1000×562",
        ImageKind.DiscArt => "Transparent PNG, 1000×1000",
        _ => "",
    };

    /// <summary>Width ÷ height of the preview frame.</summary>
    public static float AspectRatio(ImageKind kind) => kind switch
    {
        ImageKind.Poster => 2f / 3f,
        ImageKind.Banner => 1000f / 185f,
        ImageKind.Logo => 800f / 310f,
        ImageKind.DiscArt => 1f,
        _ => 16f / 9f,
    };
}
