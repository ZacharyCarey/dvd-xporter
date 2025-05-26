using System.Text.Json;
using System.Text.Json.Serialization;

namespace DvdXporter.Core.Storage;

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"\"{path}\" is empty.");
    }

    /// <summary>
    /// Reads <paramref name="path"/>, or returns null if it is missing or unreadable.
    /// For caches and preferences that can safely start over.
    /// </summary>
    public static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? Read<T>(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A deep copy, made by writing and reading back.</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;

    /// <summary>
    /// Writes via a temporary file so a crash mid-write never leaves a truncated file behind.
    /// </summary>
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
            JsonSerializer.Serialize(stream, value, Options);
        File.Move(tempPath, path, overwrite: true);
    }
}
