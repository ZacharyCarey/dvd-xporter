using System.Text.Json.Serialization;
using DiscDecoders.Common;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Projects;

// The .dxproj file: its on-disk shape, kept separate from the model so the format can evolve independently.
public sealed partial class ProjectFolder
{
    private const int CurrentFormatVersion = 1;

    /// <exception cref="InvalidDataException">The file isn't a project this version can read.</exception>
    private static Project ReadProjectFile(string path)
    {
        ProjectFileData data;
        try
        {
            data = JsonFile.Read<ProjectFileData>(path);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException($"\"{Path.GetFileName(path)}\" isn't a valid project file.", ex);
        }

        if (data.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"\"{Path.GetFileName(path)}\" was saved by a newer version of DVD Xporter. Update the app to open it.");
        }

        var project = new Project(data.Id, data.Name, data.Kind);
        foreach (var releaseData in data.Releases)
        {
            var release = new Release(releaseData.Name) { Barcode = releaseData.Barcode };
            release.Discs.AddRange(releaseData.Discs.Select(ToDisc));
            project.Releases.Add(release);
        }

        if (project.Kind == ProjectKind.Movie && data.Movie is not null)
            project.Info = data.Movie;
        else if (project.Kind == ProjectKind.TvShow && data.Show is not null)
            project.Info = data.Show;
        project.Canvases.AddRange(data.Canvases ?? []);
        project.Show?.SortSeasons();
        project.Show?.Seasons.ForEach(s => s.SortEpisodes());
        return project;
    }

    private static ProjectFileData ToFileData(Project project) => new()
    {
        FormatVersion = CurrentFormatVersion,
        Id = project.Id,
        Name = project.Name,
        Kind = project.Kind,
        Movie = project.Movie,
        Show = project.Show,
        Releases = project.Releases.Select(r => new ReleaseData
        {
            Name = r.Name,
            Barcode = r.Barcode,
            Discs = r.Discs.Select(ToDiscData).ToList(),
        }).ToList(),
        Canvases = project.Canvases.Count > 0 ? project.Canvases : null,
    };

    private static ProjectDisc ToDisc(DiscData data)
    {
        var disc = new ProjectDisc(data.Label, data.Format, data.Fingerprint) { CheckEveryFrame = data.CheckEveryFrame };
        foreach (var (clip, name) in data.ClipNames ?? [])
            disc.SetClipName(clip, name);
        foreach (var playlistData in data.Playlists ?? [])
        {
            var playlist = new ClipPlaylist(playlistData.Name, playlistData.Id) { ImportedFrom = playlistData.ImportedFrom };
            playlist.Entries.AddRange(playlistData.Entries.Select(e => new PlaylistEntry(e.Clip, e.InTime, e.OutTime)));
            disc.Playlists.Add(playlist);
        }

        return disc;
    }

    private static DiscData ToDiscData(ProjectDisc disc) => new()
    {
        Label = disc.Label,
        Format = disc.Format,
        Fingerprint = disc.Fingerprint,
        CheckEveryFrame = disc.CheckEveryFrame,
        ClipNames = disc.ClipNames.Count > 0 ? new SortedDictionary<string, string>(disc.ClipNames, StringComparer.OrdinalIgnoreCase) : null,
        Playlists = disc.Playlists.Count > 0
            ? disc.Playlists.Select(p => new PlaylistData
            {
                Id = p.Id,
                Name = p.Name,
                ImportedFrom = p.ImportedFrom,
                Entries = p.Entries.Select(e => new PlaylistEntryData { Clip = e.Clip, InTime = e.InTime, OutTime = e.OutTime }).ToList(),
            }).ToList()
            : null,
    };

    private sealed class ProjectFileData
    {
        public int FormatVersion { get; set; }

        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public ProjectKind Kind { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MovieInfo? Movie { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ShowInfo? Show { get; set; }

        public List<ReleaseData> Releases { get; set; } = [];

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<Transcoding.TranscodeCanvas>? Canvases { get; set; }
    }

    private sealed class ReleaseData
    {
        public string Name { get; set; } = "";

        public string? Barcode { get; set; }

        public List<DiscData> Discs { get; set; } = [];
    }

    private sealed class DiscData
    {
        public string Label { get; set; } = "";

        public DiscFormat Format { get; set; }

        public string Fingerprint { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool CheckEveryFrame { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<string, string>? ClipNames { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PlaylistData>? Playlists { get; set; }
    }

    private sealed class PlaylistData
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImportedFrom { get; set; }

        public List<PlaylistEntryData> Entries { get; set; } = [];
    }

    private sealed class PlaylistEntryData
    {
        public string Clip { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiscTime? InTime { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiscTime? OutTime { get; set; }
    }
}
