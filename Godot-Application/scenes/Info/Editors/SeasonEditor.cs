using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DvdXporter.Core.Metadata;
using Godot;

namespace DvdXporter.UI;

/// <summary>A season's details and artwork, and its episodes as a table whose titles and air dates edit in place.</summary>
public partial class SeasonEditor : InfoSection
{
    private enum EpisodeColumn
    {
        Number,
        Title,
        AirDate,
    }

    /// <summary>The season's number changed, which can change the seasons' order.</summary>
    [Signal]
    public delegate void RenumberedEventHandler();

    [Signal]
    public delegate void AddEpisodesRequestedEventHandler(int count);

    /// <summary>The user wants to edit all of an episode's details.</summary>
    [Signal]
    public delegate void EpisodeOpenRequestedEventHandler(int number);

    private Tree _table = null!;
    private ShowInfo _show = new();
    private SeasonInfo _season = new();
    private readonly Dictionary<ulong, EpisodeInfo> _episodesByRow = [];

    public override void _Ready()
    {
        _table = GetNode<Tree>("%EpisodeTable");
        // Column titles and widths can't be set in a scene (the column count is in it)
        _table.SetColumnTitle((int)EpisodeColumn.Number, "#");
        _table.SetColumnTitle((int)EpisodeColumn.Title, "Title");
        _table.SetColumnTitle((int)EpisodeColumn.AirDate, "Air date");
        _table.SetColumnExpand((int)EpisodeColumn.Number, false);
        _table.SetColumnCustomMinimumWidth((int)EpisodeColumn.Number, 50);
        _table.SetColumnExpand((int)EpisodeColumn.AirDate, false);
        _table.SetColumnCustomMinimumWidth((int)EpisodeColumn.AirDate, 130);
    }

    public void Setup(ShowInfo show, SeasonInfo season)
    {
        _show = show;
        _season = season;
        Fill("%Heading", season.DisplayName);
        GetNode<SpinBox>("%Number").SetValueNoSignal(season.Number);
        Fill("%TitleEdit", season.Title);
        Fill("%PremieredEdit", FormatDate(season.Premiered));
        Fill("%OverviewEdit", season.Overview);
        // A new season usually wants a batch; after that, one at a time
        GetNode<SpinBox>("%AddCount").Value = season.Episodes.Count == 0 ? 10 : 1;
        FillEpisodes();
    }

    private void FillEpisodes()
    {
        _table.Clear();
        _episodesByRow.Clear();
        _table.CustomMinimumSize = new Vector2(0, Mathf.Clamp(36 + 29 * _season.Episodes.Count, 90, 380));
        _table.Visible = _season.Episodes.Count > 0;

        var root = _table.CreateItem();
        foreach (var episode in _season.Episodes)
        {
            var item = _table.CreateItem(root);
            _episodesByRow[item.GetInstanceId()] = episode;
            item.SetText((int)EpisodeColumn.Number, episode.Number.ToString(CultureInfo.InvariantCulture));
            item.SetText((int)EpisodeColumn.Title, episode.Title);
            item.SetText((int)EpisodeColumn.AirDate, FormatDate(episode.AirDate));
            item.SetEditable((int)EpisodeColumn.Title, true);
            item.SetEditable((int)EpisodeColumn.AirDate, true);
            item.SetTooltipText((int)EpisodeColumn.Number, "Double-click to open this episode");
        }
    }

    private void On_Number_ValueChanged(double value)
    {
        var number = (int)value;
        if (_show.Seasons.Any(s => s != _season && s.Number == number))
        {
            SetStatus($"There is already a {(number == 0 ? "Specials season" : $"Season {number}")}.");
            GetNode<SpinBox>("%Number").SetValueNoSignal(_season.Number);
            return;
        }

        _season.Number = number;
        _show.SortSeasons();
        Fill("%Heading", _season.DisplayName);
        EmitSignal(SignalName.Renumbered);
        MarkEdited();
    }

    private void On_TitleEdit_TextChanged(string text)
    {
        _season.Title = Clean(text);
        MarkEdited();
    }

    private void On_PremieredEdit_TextChanged(string text)
    {
        if (!TryDate("%PremieredEdit", text, out var date))
            return;
        _season.Premiered = date;
        MarkEdited();
    }

    private void On_OverviewEdit_TextChanged()
    {
        _season.Overview = Clean(GetNode<TextEdit>("%OverviewEdit").Text);
        MarkEdited();
    }

    private void On_EpisodeTable_ItemEdited()
    {
        var item = _table.GetEdited();
        if (item is null || !_episodesByRow.TryGetValue(item.GetInstanceId(), out var episode))
            return;

        switch ((EpisodeColumn)_table.GetEditedColumn())
        {
            case EpisodeColumn.Title:
                episode.Title = item.GetText((int)EpisodeColumn.Title).Trim();
                break;
            case EpisodeColumn.AirDate:
                var text = item.GetText((int)EpisodeColumn.AirDate).Trim();
                if (text.Length == 0)
                    episode.AirDate = null;
                else if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    episode.AirDate = date;
                else
                    SetStatus("Air dates look like 2002-09-20.");
                item.SetText((int)EpisodeColumn.AirDate, FormatDate(episode.AirDate));
                break;
        }

        MarkEdited();
    }

    private void On_EpisodeTable_ItemActivated()
    {
        var item = _table.GetSelected();
        // Deferred: opening it replaces this editor's contents, so wait until the table's event has finished
        if (item is not null && _table.GetSelectedColumn() == (int)EpisodeColumn.Number
            && _episodesByRow.TryGetValue(item.GetInstanceId(), out var episode))
        {
            Callable.From(() => EmitSignal(SignalName.EpisodeOpenRequested, episode.Number)).CallDeferred();
        }
    }

    private void On_AddEpisodesButton_Pressed()
    {
        // Deferred: adding refills this editor, which would change the button's row mid-click
        var count = (int)GetNode<SpinBox>("%AddCount").Value;
        Callable.From(() => EmitSignal(SignalName.AddEpisodesRequested, count)).CallDeferred();
    }
}
