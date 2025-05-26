using System.Collections.Generic;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Online;

namespace DvdXporter.UI;

/// <summary>A movie's details, IDs, artwork and files (its extras are in the navigator).</summary>
public partial class MovieEditor : InfoSection
{
    [Godot.Signal]
    public delegate void ImportRequestedEventHandler();

    private MovieInfo _movie = new();

    public void Setup(MovieInfo movie, IReadOnlyList<IMetadataProvider> providers)
    {
        _movie = movie;
        GetNode<ImportRow>("%ImportRow").Setup(providers);
        Fill("%TitleEdit", movie.Title);
        Fill("%YearEdit", FormatInteger(movie.Year));
        Fill("%OriginalTitleEdit", movie.OriginalTitle);
        Fill("%SortTitleEdit", movie.SortTitle);
        Fill("%PremieredEdit", FormatDate(movie.Premiered));
        Fill("%ContentRatingEdit", movie.ContentRating);
        Fill("%TaglineEdit", movie.Tagline);
        Fill("%CollectionEdit", movie.Collection);
        Fill("%GenresEdit", FormatList(movie.Genres));
        Fill("%StudiosEdit", FormatList(movie.Studios));
        Fill("%DirectorsEdit", FormatList(movie.Directors));
        Fill("%WritersEdit", FormatList(movie.Writers));
        Fill("%TagsEdit", FormatList(movie.Tags));
        Fill("%OverviewEdit", movie.Overview);
        GetNode<IdsSection>("%Ids").Setup(movie.Ids);
    }

    private void On_ImportRow_ImportRequested() => EmitSignal(SignalName.ImportRequested);

    private void On_TitleEdit_TextChanged(string text)
    {
        _movie.Title = Clean(text) ?? "";
        MarkEdited();
    }

    private void On_YearEdit_TextChanged(string text)
    {
        if (!TryInteger("%YearEdit", text, 1880, 2200, out var year))
            return;
        _movie.Year = year;
        MarkEdited();
    }

    private void On_OriginalTitleEdit_TextChanged(string text)
    {
        _movie.OriginalTitle = Clean(text);
        MarkEdited();
    }

    private void On_SortTitleEdit_TextChanged(string text)
    {
        _movie.SortTitle = Clean(text);
        MarkEdited();
    }

    private void On_PremieredEdit_TextChanged(string text)
    {
        if (!TryDate("%PremieredEdit", text, out var date))
            return;
        _movie.Premiered = date;
        MarkEdited();
    }

    private void On_ContentRatingEdit_TextChanged(string text)
    {
        _movie.ContentRating = Clean(text);
        MarkEdited();
    }

    private void On_TaglineEdit_TextChanged(string text)
    {
        _movie.Tagline = Clean(text);
        MarkEdited();
    }

    private void On_CollectionEdit_TextChanged(string text)
    {
        _movie.Collection = Clean(text);
        MarkEdited();
    }

    private void On_GenresEdit_TextChanged(string text)
    {
        _movie.Genres = SplitList(text);
        MarkEdited();
    }

    private void On_StudiosEdit_TextChanged(string text)
    {
        _movie.Studios = SplitList(text);
        MarkEdited();
    }

    private void On_DirectorsEdit_TextChanged(string text)
    {
        _movie.Directors = SplitList(text);
        MarkEdited();
    }

    private void On_WritersEdit_TextChanged(string text)
    {
        _movie.Writers = SplitList(text);
        MarkEdited();
    }

    private void On_TagsEdit_TextChanged(string text)
    {
        _movie.Tags = SplitList(text);
        MarkEdited();
    }

    private void On_OverviewEdit_TextChanged()
    {
        _movie.Overview = Clean(GetNode<Godot.TextEdit>("%OverviewEdit").Text);
        MarkEdited();
    }
}
