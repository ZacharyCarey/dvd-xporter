using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Turns downloaded image bytes into textures. The format is detected from the data itself,
/// because download URLs don't always end in a file extension.
/// </summary>
public static class ImageData
{
    /// <summary>".jpg", ".png" or ".webp", or null if the data isn't one of those.</summary>
    public static string? DetectExtension(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => ".webp",
        _ => null,
    };

    public static ImageTexture? ToTexture(byte[] data)
    {
        var image = new Image();
        var error = DetectExtension(data) switch
        {
            ".jpg" => image.LoadJpgFromBuffer(data),
            ".png" => image.LoadPngFromBuffer(data),
            ".webp" => image.LoadWebpFromBuffer(data),
            _ => Error.FileUnrecognized,
        };
        return error == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
    }
}
