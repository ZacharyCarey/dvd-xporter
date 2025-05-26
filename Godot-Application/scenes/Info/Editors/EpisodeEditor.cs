using System.Linq;
using DvdXporter.Core.Metadata;
using Godot;

namespace DvdXporter.UI;

/// <summary>An episode's details and artwork.</summary>
public partial class EpisodeEditor : InfoSection
{
    /// <summary>The episode's number changed, which can change the episodes' order.</summary>
    [Signal]
    public delegate void RenumberedEventHandler();

    private SeasonInfo _season = new();
    private EpisodeInfo _episode = new();

    public void Setup(SeasonInfo season, EpisodeInfo episode)
    {
        _season = season;
        _episode = episode;
        Fill("%Heading", $"{season.DisplayName}, Episode {episode.Number}");
        Fill("%Season", season.DisplayName);
        GetNode<SpinBox>("%Number").SetValueNoSignal(episode.Number);
        Fill("%TitleEdit", episode.Title);
        Fill("%AirDateEdit", FormatDate(episode.AirDate));
        Fill("%DirectorsEdit", FormatList(episode.Directors));
        Fill("%WritersEdit", FormatList(episode.Writers));
        Fill("%OverviewEdit", episode.Overview);
    }

    private void On_Number_ValueChanged(double value)
    {
        var number = (int)value;
        if (_season.Episodes.Any(e => e != _episode && e.Number == number))
        {
            SetStatus($"{_season.DisplayName} already has an episode {number}.");
            GetNode<SpinBox>("%Number").SetValueNoSignal(_episode.Number);
            return;
        }

        _episode.Number = number;
        _season.SortEpisodes();
        Fill("%Heading", $"{_season.DisplayName}, Episode {number}");
        EmitSignal(SignalName.Renumbered);
        MarkEdited();
    }

    private void On_TitleEdit_TextChanged(string text)
    {
        _episode.Title = Clean(text) ?? "";
        MarkEdited();
    }

    private void On_AirDateEdit_TextChanged(string text)
    {
        if (!TryDate("%AirDateEdit", text, out var date))
            return;
        _episode.AirDate = date;
        MarkEdited();
    }

    private void On_DirectorsEdit_TextChanged(string text)
    {
        _episode.Directors = SplitList(text);
        MarkEdited();
    }

    private void On_WritersEdit_TextChanged(string text)
    {
        _episode.Writers = SplitList(text);
        MarkEdited();
    }

    private void On_OverviewEdit_TextChanged()
    {
        _episode.Overview = Clean(GetNode<TextEdit>("%OverviewEdit").Text);
        MarkEdited();
    }
}
