using System.Security.Cryptography;
using System.Text.Json;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;

namespace DvdXporter.Server;

/// <summary>The server's settings, made by <c>dvdx-server setup</c> and kept in <see cref="FileName"/> in its data folder.</summary>
public sealed class ServerConfig
{
    public const string FileName = "server.json";

    private static readonly JsonSerializerOptions FileOptions = new(RenderServerProtocol.Json) { WriteIndented = true };

    // No 0/O or 1/I, so a key read off the screen can be typed in
    private const string KeyLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>What the app shows it as.</summary>
    public string Name { get; set; } = Environment.MachineName;

    /// <summary>Tells this server apart from others, whatever it's called.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Port { get; set; } = RenderServerProtocol.DefaultPort;

    /// <summary>What the app has to send with every request.</summary>
    public string AccessKey { get; set; } = NewAccessKey();

    /// <summary>The folder of disc rips to find projects' discs in, by fingerprint; null for none.</summary>
    public string? DiscLibraryPath { get; set; }

    public RenderServerDestination ExportsTo { get; set; }

    public string? MoviesLibraryPath { get; set; }

    public string? ShowsLibraryPath { get; set; }

    /// <summary>Where exports go when <see cref="ExportsTo"/> is <see cref="RenderServerDestination.Folder"/>.</summary>
    public string? ExportFolderPath { get; set; }

    /// <summary>Where a project of <paramref name="kind"/> exports to, or null if that isn't set.</summary>
    public string? ExportRoot(ProjectKind kind) => ExportsTo == RenderServerDestination.Folder ? ExportFolderPath
        : kind == ProjectKind.Movie ? MoviesLibraryPath
        : ShowsLibraryPath;

    /// <summary>A new key: 20 letters and digits, in fives.</summary>
    public static string NewAccessKey()
    {
        var letters = RandomNumberGenerator.GetItems<char>(KeyLetters, 20);
        return string.Join('-', letters.Chunk(5).Select(c => new string(c)));
    }

    /// <summary>
    /// Sets what the environment variables give (see <see cref="EnvironmentVariables"/>), e.g. from a Docker compose
    /// file, over what was saved: so a container needn't be set up. One that's empty isn't given.
    /// </summary>
    /// <returns>The variables that gave settings.</returns>
    /// <exception cref="InvalidDataException">One can't be used, e.g. a port that isn't a number.</exception>
    public List<string> ApplyEnvironment()
    {
        var given = new List<string>();
        string? get(string name)
        {
            var value = Environment.GetEnvironmentVariable(name)?.Trim();
            if (string.IsNullOrEmpty(value))
                return null;
            given.Add(name);
            return value;
        }

        Name = get("DVDX_SERVER_NAME") ?? Name;
        var port = get("DVDX_PORT");
        if (port is not null)
        {
            Port = int.TryParse(port, out var number) && number is >= 1 and <= 65535
                ? number
                : throw new InvalidDataException($"DVDX_PORT is \"{port}\": it has to be a number from 1 to 65535.");
        }

        var library = get("DVDX_DISC_LIBRARY");
        if (library is not null)
            DiscLibraryPath = Path.GetFullPath(library);

        // A folder to export into, or the Emby libraries
        var exportFolder = get("DVDX_EXPORT_FOLDER");
        var movies = get("DVDX_MOVIES_LIBRARY");
        var shows = get("DVDX_SHOWS_LIBRARY");
        if (exportFolder is not null)
        {
            ExportsTo = RenderServerDestination.Folder;
            ExportFolderPath = Path.GetFullPath(exportFolder);
        }
        else if (movies is not null || shows is not null)
        {
            ExportsTo = RenderServerDestination.Emby;
            MoviesLibraryPath = movies is not null ? Path.GetFullPath(movies) : MoviesLibraryPath;
            ShowsLibraryPath = shows is not null ? Path.GetFullPath(shows) : ShowsLibraryPath;
        }

        AccessKey = get("DVDX_ACCESS_KEY") ?? AccessKey;
        return given;
    }

    /// <summary>The environment variables that can give settings (see <see cref="ApplyEnvironment"/>).</summary>
    public static readonly string[] EnvironmentVariables =
    [
        "DVDX_SERVER_NAME", "DVDX_PORT", "DVDX_DISC_LIBRARY", "DVDX_MOVIES_LIBRARY", "DVDX_SHOWS_LIBRARY",
        "DVDX_EXPORT_FOLDER", "DVDX_ACCESS_KEY",
    ];

    /// <summary>The settings in <paramref name="dataFolder"/>, or null if the server hasn't been set up there.</summary>
    /// <exception cref="InvalidDataException">The file is there, but can't be read.</exception>
    public static ServerConfig? Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), FileOptions)
                ?? throw new InvalidDataException($"\"{path}\" is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"\"{path}\" can't be read: {ex.Message}", ex);
        }
    }

    /// <summary>Saves the settings, readable only by this user (they hold the key).</summary>
    public void Save(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        var path = Path.Combine(dataFolder, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, FileOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
