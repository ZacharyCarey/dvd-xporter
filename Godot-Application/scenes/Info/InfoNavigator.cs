using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Emby;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The movie or show, a show's seasons and their episodes, and the extras, to pick which to edit, with seasons,
/// episodes and extras added and removed here. A show's extras are listed with what they belong to: the show, a season,
/// or an episode. The season shown (or the selected episode's or extra's) stays unfolded; the rest fold to keep long
/// shows tidy.
/// </summary>
public partial class InfoNavigator : VBoxContainer
{
    /// <summary>Something else was picked to edit: it's <see cref="Selected"/>.</summary>
    [Signal]
    public delegate void SelectionChangedEventHandler();

    /// <summary>Seasons, episodes or extras were added or removed, which needs saving.</summary>
    [Signal]
    public delegate void EditedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>What's being edited: the movie or show's info, a season, an episode or an extra.</summary>
    public object? Selected { get; private set; }

    private Tree _tree = null!;
    private Button _addSeasonButton = null!;
    private Button _addEpisodeButton = null!;
    private Button _removeButton = null!;
    private ConfirmationDialog _removeDialog = null!;

    // By their tree item's instance ID. Rebuilt with the tree.
    private readonly Dictionary<ulong, object> _targets = [];
    private ProjectFolder? _folder;
    private bool _rebuilding;

    public override void _Ready()
    {
        _tree = GetNode<Tree>("%NavTree");
        _addSeasonButton = GetNode<Button>("%AddSeasonButton");
        _addEpisodeButton = GetNode<Button>("%AddEpisodeButton");
        _removeButton = GetNode<Button>("%RemoveButton");
        _removeDialog = GetNode<ConfirmationDialog>("%RemoveDialog");
    }

    /// <summary>Lists <paramref name="folder"/>'s movie or show, with its own info selected.</summary>
    public void ShowProject(ProjectFolder folder)
    {
        _folder = folder;
        Selected = folder.Project.Info;
        Rebuild();
    }

    /// <summary>Selects <paramref name="target"/> to edit.</summary>
    public void Select(object target)
    {
        Selected = target;
        Rebuild();
        EmitSignal(SignalName.SelectionChanged);
    }

    /// <summary>Lists the seasons, episodes and extras again, e.g. after one is renumbered.</summary>
    public void Rebuild()
    {
        _rebuilding = true;
        _tree.Clear();
        _targets.Clear();

        var info = _folder?.Project.Info;
        if (info is not null)
        {
            var root = _tree.CreateItem();
            var titleItem = Add(root, info);
            var extras = ExtraInfo.InEmbyOrder(info.Extras);
            var placed = new HashSet<ExtraInfo>();
            var selectedExtra = Selected as ExtraInfo;
            foreach (var season in (info as ShowInfo)?.Seasons ?? [])
            {
                var seasonItem = Add(titleItem, season);
                var unfold = Selected == season;
                foreach (var episode in season.Episodes)
                {
                    var episodeItem = Add(seasonItem, episode);
                    var episodeExtras = extras.Where(e => e.Season == season.Number && e.Episode == episode.Number).ToList();
                    foreach (var extra in episodeExtras)
                        Add(episodeItem, extra);
                    placed.UnionWith(episodeExtras);
                    unfold |= Selected == episode || (selectedExtra is not null && episodeExtras.Contains(selectedExtra));
                }

                var seasonExtras = extras.Where(e => e.Season == season.Number && e.Episode is null).ToList();
                AddExtras(seasonItem, seasonExtras);
                placed.UnionWith(seasonExtras);
                unfold |= selectedExtra is not null && seasonExtras.Contains(selectedExtra);
                seasonItem.Collapsed = !unfold;
            }

            // The rest under a heading of their own, after the seasons: the show's or movie's own, and ones whose
            // season or episode is gone (saying which)
            AddExtras(titleItem, extras.Where(e => !placed.Contains(e)).ToList());
        }

        var isShow = info is ShowInfo;
        _addSeasonButton.Visible = _addEpisodeButton.Visible = isShow;
        _rebuilding = false;
        UpdateButtons();
    }

    /// <summary>Updates the labels in place (e.g. while a title is being typed) without rebuilding.</summary>
    public void RefreshText()
    {
        foreach (var (instanceId, target) in _targets)
        {
            if (InstanceFromId(instanceId) is TreeItem item)
                item.SetText(0, Text(target));
        }
    }

    /// <summary>
    /// Adds <paramref name="count"/> episodes to the selected season (or the selected episode's). A single episode
    /// opens for editing; a batch stays on the season so its table can be filled in.
    /// </summary>
    public void AddEpisodes(int count)
    {
        var season = SelectedSeason();
        if (season is null)
            return;

        var first = season.AddEpisodes(count);
        EmitSignal(SignalName.Edited);
        EmitSignal(SignalName.StatusChanged, count == 1
            ? $"Added episode {first.Number} to {season.DisplayName}."
            : $"Added episodes {first.Number}–{first.Number + count - 1} to {season.DisplayName}.");
        Select(count == 1 ? first : season);
    }

    /// <summary>Selects the episode numbered <paramref name="number"/> of the selected season.</summary>
    public void OpenEpisode(int number)
    {
        var episode = SelectedSeason()?.Episodes.Find(e => e.Number == number);
        if (episode is not null)
            Select(episode);
    }

    // Under an "Extras" heading
    private void AddExtras(TreeItem parent, List<ExtraInfo> extras)
    {
        if (extras.Count == 0)
            return;
        var heading = _tree.CreateItem(parent);
        heading.SetText(0, "Extras");
        heading.SetSelectable(0, false);
        heading.SetCustomColor(0, new Color(1, 1, 1, 0.6f));
        foreach (var extra in extras)
            Add(heading, extra);
    }

    private TreeItem Add(TreeItem parent, object target)
    {
        var item = _tree.CreateItem(parent);
        item.SetText(0, Text(target));
        _targets[item.GetInstanceId()] = target;
        if (target == Selected)
        {
            item.Select(0);
            _tree.CallDeferred(Tree.MethodName.ScrollToItem, item);
        }

        return item;
    }

    private string Text(object target) => target switch
    {
        MovieInfo movie => movie.Title.Length > 0 ? movie.Title : "Untitled movie",
        ShowInfo show => show.Title.Length > 0 ? show.Title : "Untitled show",
        // One whose season or episode is gone is with the show's, so it says where it was
        ExtraInfo extra => (_folder is not null && LibraryNaming.BelongsToRemoved(extra, _folder.Project) ? LibraryNaming.Belonging(extra) : "")
            + (extra.Name.Length > 0 ? extra.Name : "Unnamed extra"),
        SeasonInfo season => string.IsNullOrEmpty(season.Title) || season.Title == season.DisplayName
            ? season.DisplayName
            : $"{season.DisplayName}: {season.Title}",
        EpisodeInfo episode => $"{episode.Number}. {(episode.Title.Length > 0 ? episode.Title : "Untitled")}",
        _ => "",
    };

    private void UpdateButtons()
    {
        _addEpisodeButton.Disabled = SelectedSeason() is null;
        _removeButton.Disabled = Selected is not (SeasonInfo or EpisodeInfo or ExtraInfo);
    }

    private SeasonInfo? SelectedSeason() => Selected switch
    {
        SeasonInfo season => season,
        EpisodeInfo episode => FindSeason(episode),
        _ => null,
    };

    private SeasonInfo? FindSeason(EpisodeInfo episode) =>
        _folder?.Project.Show?.Seasons.Find(s => s.Episodes.Contains(episode));

    private void On_NavTree_ItemSelected()
    {
        var item = _tree.GetSelected();
        if (_rebuilding || item is null)
            return;

        var target = _targets.GetValueOrDefault(item.GetInstanceId());
        if (target is null || target == Selected)
            return;

        Selected = target;
        UpdateButtons();
        EmitSignal(SignalName.SelectionChanged);
    }

    private void On_AddSeasonButton_Pressed()
    {
        var show = _folder?.Project.Show;
        if (show is null)
            return;

        var season = show.AddSeason(show.NextSeasonNumber());
        EmitSignal(SignalName.Edited);
        Select(season);
        EmitSignal(SignalName.StatusChanged, $"Added {season.DisplayName}.");
    }

    private void On_AddEpisodeButton_Pressed() => AddEpisodes(1);

    private void On_AddExtraButton_Pressed()
    {
        var info = _folder?.Project.Info;
        if (info is null)
            return;

        var extra = new ExtraInfo();
        info.Extras.Add(extra);
        EmitSignal(SignalName.Edited);
        Select(extra);
        EmitSignal(SignalName.StatusChanged, "Added an extra. Give it a title, then pick its type.");
    }

    private void On_RemoveButton_Pressed()
    {
        switch (Selected)
        {
            case SeasonInfo season:
                var episodeCount = season.Episodes.Count;
                _removeDialog.DialogText = episodeCount == 0
                    ? $"Remove {season.DisplayName}?"
                    : $"Remove {season.DisplayName} and its {episodeCount} {(episodeCount == 1 ? "episode" : "episodes")}?";
                break;
            case EpisodeInfo episode:
                _removeDialog.DialogText = $"Remove episode {episode.Number}{(episode.Title.Length > 0 ? $", \"{episode.Title}\"" : "")}?";
                break;
            case ExtraInfo extra:
                _removeDialog.DialogText = $"Remove the extra {(extra.Name.Length > 0 ? $"\"{extra.Name}\"" : "with no name")}?";
                break;
            default:
                return;
        }

        _removeDialog.PopupCentered();
    }

    private void On_RemoveDialog_Confirmed()
    {
        var folder = _folder;
        if (folder is null)
            return;
        var info = folder.Project.Info;

        switch (Selected)
        {
            case ExtraInfo extra:
            {
                folder.Images.DeleteAll(extra.Images);
                info.Extras.Remove(extra);
                var target = NodeTypes.ExtraTargetPrefix + extra.Id;
                var uses = folder.Project.Canvases.SelectMany(c => c.Nodes).Count(n => n.Type == NodeTypes.Export && new ExportNode(n).Target == target);
                var name = extra.Name.Length > 0 ? $"\"{extra.Name}\"" : "the extra";
                EmitSignal(SignalName.StatusChanged, uses == 0 ? $"Removed {name}."
                    : $"Removed {name}. {uses} Export {(uses == 1 ? "node points" : "nodes point")} at it; pick another library item for {(uses == 1 ? "it" : "them")} in Transcoding.");
                Select(info);
                break;
            }
            case SeasonInfo season when info is ShowInfo show:
                folder.Images.DeleteAll(season.Images);
                season.Episodes.ForEach(e => folder.Images.DeleteAll(e.Images));
                show.Seasons.Remove(season);
                EmitSignal(SignalName.StatusChanged, $"Removed {season.DisplayName}.");
                Select(show);
                break;
            case EpisodeInfo episode when info is ShowInfo:
            {
                var season = FindSeason(episode);
                if (season is null)
                    return;
                folder.Images.DeleteAll(episode.Images);
                season.Episodes.Remove(episode);
                EmitSignal(SignalName.StatusChanged, $"Removed episode {episode.Number}.");
                Select(season);
                break;
            }
            default:
                return;
        }

        EmitSignal(SignalName.Edited);
    }
}
