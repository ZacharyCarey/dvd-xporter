using System.Globalization;
using DvdXporter.Core.Emby;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

// The choices a setting offers, where they come from the project (discs, clips, library items).
public static partial class NodeTypes
{
    /// <summary>
    /// The choices for a setting whose options come from the project (discs, clips, library items),
    /// or the setting's own fixed choices.
    /// </summary>
    public static IReadOnlyList<SettingChoice> GetChoices(CanvasNode node, NodeSetting setting, ITranscodeContext context)
    {
        switch (node.Type, setting.Key)
        {
            case (Source or TemplateInput, "disc"):
                return context.Project.Releases
                    .SelectMany(r => r.Discs.Select(d => new SettingChoice(d.Fingerprint, $"{r.Name} › {d.Label}")))
                    .ToList();
            case (Source or TemplateInput, SourceNode.ItemKey):
                return SourceItemChoices(node, context);
            case (Source or TemplateInput, SourceNode.StartChapterKey or SourceNode.EndChapterKey):
            {
                if (ChapterLists.AllOfSource(node, context) is not var (chapters, _, length))
                    return [];
                // A start shows where its chapter begins, an end where its chapter ends
                var ending = setting.Key == SourceNode.EndChapterKey;
                var list = chapters.Select((c, i) =>
                {
                    var time = ending ? i + 1 < chapters.Count ? chapters[i + 1].Start : length : c.Start;
                    var number = (i + 1).ToString(CultureInfo.InvariantCulture);
                    // "Chapter 12 (45:07)", or with its own name "12. The Chase (45:07)"
                    var name = c.Name == $"Chapter {number}" ? c.Name : $"{number}. {c.Name}";
                    return new SettingChoice(number, $"{name} ({FormatDuration(time)})");
                }).ToList();
                // Unset = the last, whichever that is
                if (ending)
                    list.Insert(0, new SettingChoice("", "The last chapter"));
                return list;
            }
            case (Template, TemplateNode.TemplateKey):
                return context.Project.Canvases.Where(c => c.Kind == CanvasKind.Template).Select(c => new SettingChoice(c.Id, c.Name)).ToList();
            case (Export, ExportNode.TargetKey):
                return ExportTargetChoices(context.Project);
            case (VideoCodec, VideoCodecNode.TuneKey) when new VideoCodecNode(node).Codec == "libx265":
                return setting.Choices!.Where(c => c.Value is not ("film" or "stillimage")).ToList();
            default:
                return setting.Choices ?? [];
        }
    }

    public static List<SettingChoice> SourceItemChoices(CanvasNode node, ITranscodeContext context)
    {
        var disc = new SourceNode(node).FindDisc(context.Project);
        if (disc is null)
            return [];

        var choices = disc.Playlists
            .Select(p => new SettingChoice(PlaylistItemPrefix + p.Id, $"Playlist: {p.Name}"))
            .ToList();
        var contents = context.GetContents(disc);
        if (contents is not null)
        {
            // The named video files after the playlists, then the rest
            choices.AddRange(contents.Clips
                .Select(c => (c.File, Name: disc.GetClipName(c.File)))
                .OrderBy(c => c.Name is null)
                .Select(c => new SettingChoice(ClipItemPrefix + c.File, c.Name is not null ? $"{c.Name} [{c.File}]" : c.File)));
        }

        return choices;
    }

    /// <summary>
    /// The movie or the episodes, each extra with what it belongs to: the show's first, a season's at the top of it, an
    /// episode's after it. Those whose season or episode is gone come last.
    /// </summary>
    public static List<SettingChoice> ExportTargetChoices(Project project)
    {
        var choices = new List<SettingChoice>();
        var extras = ExtraInfo.InEmbyOrder(project.Info.Extras);
        var placed = new HashSet<ExtraInfo>();
        void AddExtra(ExtraInfo extra)
        {
            var name = string.IsNullOrWhiteSpace(extra.Name) ? "Unnamed" : extra.Name.Trim();
            choices.Add(new SettingChoice(ExtraTargetPrefix + extra.Id, $"{LibraryNaming.Belonging(extra)}{extra.TypeLabel}: {name}"));
            placed.Add(extra);
        }

        void AddExtras(int? season, int? episode)
        {
            foreach (var extra in extras.Where(e => e.Season == season && e.Episode == episode))
                AddExtra(extra);
        }

        var movie = project.Movie;
        if (movie is not null)
            choices.Add(new SettingChoice(MovieTarget, $"Movie: {movie.Title}"));
        AddExtras(null, null);
        foreach (var season in project.Show?.Seasons ?? [])
        {
            AddExtras(season.Number, null);
            foreach (var episode in season.Episodes)
            {
                var title = string.IsNullOrWhiteSpace(episode.Title) ? "" : $" – {episode.Title}";
                choices.Add(new SettingChoice($"{EpisodeTargetPrefix}{season.Number}:{episode.Number}",
                    $"S{season.Number:00}E{episode.Number:00}{title}"));
                AddExtras(season.Number, episode.Number);
            }
        }

        foreach (var extra in extras.Where(e => !placed.Contains(e)).ToList())
            AddExtra(extra);
        return choices;
    }
}
