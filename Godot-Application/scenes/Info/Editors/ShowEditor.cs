using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Online;
using Godot;

namespace DvdXporter.UI;

/// <summary>A show's details, IDs, artwork and files, and how many seasons and episodes it has.</summary>
public partial class ShowEditor : InfoSection
{
    [Signal]
    public delegate void ImportRequestedEventHandler();

    private ShowInfo _show = new();

    public void Setup(ShowInfo show, IReadOnlyList<IMetadataProvider> providers)
    {
        _show = show;
        GetNode<ImportRow>("%ImportRow").Setup(providers);
        Fill("%TitleEdit", show.Title);
        Fill("%YearEdit", FormatInteger(show.Year));
        Fill("%OriginalTitleEdit", show.OriginalTitle);
        Fill("%SortTitleEdit", show.SortTitle);
        Fill("%PremieredEdit", FormatDate(show.Premiered));
        var status = GetNode<OptionButton>("%Status");
        status.Select(status.GetItemIndex((int)show.Status));
        Fill("%ContentRatingEdit", show.ContentRating);
        Fill("%StudiosEdit", FormatList(show.Studios));
        Fill("%GenresEdit", FormatList(show.Genres));
        Fill("%TagsEdit", FormatList(show.Tags));
        Fill("%OverviewEdit", show.Overview);
        GetNode<IdsSection>("%Ids").Setup(show.Ids);

        var seasonCount = show.Seasons.Count;
        var episodeCount = show.Seasons.Sum(s => s.Episodes.Count);
        Fill("%SeasonsHint", seasonCount == 0
            ? "No seasons yet. Use \"+ Season\" in the list on the left to add one."
            : $"{seasonCount} {(seasonCount == 1 ? "season" : "seasons")}, {episodeCount} {(episodeCount == 1 ? "episode" : "episodes")}. Select one on the left to edit it.");
    }

    private void On_ImportRow_ImportRequested() => EmitSignal(SignalName.ImportRequested);

    private void On_TitleEdit_TextChanged(string text)
    {
        _show.Title = Clean(text) ?? "";
        MarkEdited();
    }

    private void On_YearEdit_TextChanged(string text)
    {
        if (!TryInteger("%YearEdit", text, 1880, 2200, out var year))
            return;
        _show.Year = year;
        MarkEdited();
    }

    private void On_OriginalTitleEdit_TextChanged(string text)
    {
        _show.OriginalTitle = Clean(text);
        MarkEdited();
    }

    private void On_SortTitleEdit_TextChanged(string text)
    {
        _show.SortTitle = Clean(text);
        MarkEdited();
    }

    private void On_PremieredEdit_TextChanged(string text)
    {
        if (!TryDate("%PremieredEdit", text, out var date))
            return;
        _show.Premiered = date;
        MarkEdited();
    }

    private void On_Status_ItemSelected(long index)
    {
        _show.Status = (ShowStatus)GetNode<OptionButton>("%Status").GetItemId((int)index);
        MarkEdited();
    }

    private void On_ContentRatingEdit_TextChanged(string text)
    {
        _show.ContentRating = Clean(text);
        MarkEdited();
    }

    private void On_StudiosEdit_TextChanged(string text)
    {
        _show.Studios = SplitList(text);
        MarkEdited();
    }

    private void On_GenresEdit_TextChanged(string text)
    {
        _show.Genres = SplitList(text);
        MarkEdited();
    }

    private void On_TagsEdit_TextChanged(string text)
    {
        _show.Tags = SplitList(text);
        MarkEdited();
    }

    private void On_OverviewEdit_TextChanged()
    {
        _show.Overview = Clean(GetNode<TextEdit>("%OverviewEdit").Text);
        MarkEdited();
    }
}
