using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Emby;
using DvdXporter.Core.Metadata;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// An extra's details and thumbnail, as Emby shows them. Export nodes pick it as a library item, and its file goes in
/// its type's folder, named after its title (e.g. "Featurettes/Gag Reel.mkv"), beside the movie or show, or the show's
/// season or episode it belongs to.
/// </summary>
public partial class ExtraEditor : InfoSection
{
    /// <summary>Its sort order, name or what it belongs to changed, which moves it in the list.</summary>
    [Signal]
    public delegate void RenumberedEventHandler();

    private ExtraInfo _extra = new();
    private ShowInfo? _show;
    // The season and episode each Belongs to choice stands for (nulls for the whole show)
    private readonly List<(int? Season, int? Episode)> _places = [];
    private bool _renamed;

    public override void _Ready()
    {
        var type = GetNode<OptionButton>("%TypeChoice");
        foreach (var (_, label) in ExtraInfo.Types)
            type.AddItem(label);
    }

    public void Setup(ExtraInfo extra, ShowInfo? show)
    {
        _extra = extra;
        _show = show;
        ShowHeading();
        Fill("%TitleEdit", extra.Name);
        GetNode<OptionButton>("%TypeChoice").Select(Math.Max(0, ExtraInfo.Types.ToList().FindIndex(t => t.Value == extra.Type)));
        FillPlaces();
        Fill("%SortOrder", FormatInteger(extra.SortOrder));
        ShowDetails();
        Fill("%ReleasedEdit", FormatDate(extra.Released));
        Fill("%OverviewEdit", extra.Overview);

        // A new one's title is ready to type
        if (extra.Name.Length == 0)
            GetNode<LineEdit>("%TitleEdit").CallDeferred(Control.MethodName.GrabFocus);
    }

    // A gallery is just a folder of pictures, with no .nfo or video for its details, thumbnail or place in Emby's list
    // of extras; a trailer has no thumbnail Emby shows. What it had before stays, for if it's changed back
    private void ShowDetails()
    {
        foreach (var name in new[] { "%SortOrderLabel", "%SortOrder", "%ReleasedLabel", "%ReleasedEdit", "%OverviewLabel", "%OverviewEdit" })
            GetNode<Control>(name).Visible = !_extra.IsGallery;
        Artwork.Visible = _extra.HasThumbnail;
    }

    private void ShowHeading() =>
        Fill("%Heading", $"{LibraryNaming.Belonging(_extra)}{_extra.TypeLabel}: {(_extra.Name.Length > 0 ? _extra.Name : "Unnamed")}");

    /// <summary>The show, each season, and each season's episodes, for a show's extra to belong to.</summary>
    private void FillPlaces()
    {
        var choice = GetNode<OptionButton>("%BelongsChoice");
        choice.Clear();
        _places.Clear();
        // A movie's extras go with the movie
        var show = _show;
        choice.Visible = GetNode<Control>("%BelongsLabel").Visible = show is not null;
        if (show is null)
            return;

        void add(string label, int? season, int? episode)
        {
            choice.AddItem(label);
            _places.Add((season, episode));
        }

        add("The whole show", null, null);
        foreach (var season in show.Seasons)
        {
            add(season.DisplayName, season.Number, null);
            foreach (var episode in season.Episodes)
                add($"    S{season.Number:00}E{episode.Number:00}{(episode.Title.Length > 0 ? $" – {episode.Title}" : "")}", season.Number, episode.Number);
        }

        var current = _places.IndexOf((_extra.Season, _extra.Episode));
        if (current < 0)
        {
            add($"{LibraryNaming.Belonging(_extra).TrimEnd(' ', '›')} (gone from the show)", _extra.Season, _extra.Episode);
            current = _places.Count - 1;
        }

        choice.Select(current);
    }

    private void On_BelongsChoice_ItemSelected(long index)
    {
        if (index < 0 || index >= _places.Count)
            return;
        (_extra.Season, _extra.Episode) = _places[(int)index];
        ShowHeading();
        EmitSignal(SignalName.Renumbered);
        MarkEdited();
    }

    private void On_TitleEdit_TextChanged(string text)
    {
        _extra.Name = text.Trim();
        _renamed = true;
        ShowHeading();
        MarkEdited();
    }

    // Once it's typed, as the list goes by name after the sort order. Deferred, as leaving it can be a click on the list.
    private void On_TitleEdit_FocusExited()
    {
        if (!_renamed)
            return;
        _renamed = false;
        CallDeferred(GodotObject.MethodName.EmitSignal, SignalName.Renumbered);
    }

    private void On_TypeChoice_ItemSelected(long index)
    {
        _extra.Type = ExtraInfo.Types[Math.Clamp((int)index, 0, ExtraInfo.Types.Count - 1)].Value;
        ShowDetails();
        ShowHeading();
        MarkEdited();
    }

    private void On_SortOrder_TextChanged(string text)
    {
        if (!TryInteger("%SortOrder", text, 0, 999, out var order))
            return;
        _extra.SortOrder = order;
        EmitSignal(SignalName.Renumbered);
        MarkEdited();
    }

    private void On_ReleasedEdit_TextChanged(string text)
    {
        if (!TryDate("%ReleasedEdit", text, out var date))
            return;
        _extra.Released = date;
        MarkEdited();
    }

    private void On_OverviewEdit_TextChanged()
    {
        _extra.Overview = Clean(GetNode<TextEdit>("%OverviewEdit").Text);
        MarkEdited();
    }
}
