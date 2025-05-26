using System;
using System.Linq;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Online;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Step 1: the movie or show details Emby needs, plus artwork. A navigator on the left (see <see cref="InfoNavigator"/>)
/// lists the movie or show, a show's seasons and their episodes, and the extras; the editor on the right edits
/// whichever is selected.
/// Edits save automatically shortly after typing stops.
/// </summary>
public partial class InfoPage : MarginContainer, IStepPage
{
    [Signal]
    public delegate void BackRequestedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    private Label _stepTitleLabel = null!;
    private Label _projectNameLabel = null!;
    private InfoNavigator _navigator = null!;
    private ScrollContainer _editorScroll = null!;
    private MovieEditor _movieEditor = null!;
    private ShowEditor _showEditor = null!;
    private SeasonEditor _seasonEditor = null!;
    private EpisodeEditor _episodeEditor = null!;
    private ExtraEditor _extraEditor = null!;
    private Timer _saveTimer = null!;
    private ImportDialog _importDialog = null!;
    private AcceptDialog _errorDialog = null!;

    private ProjectFolder? _folder;
    private AppServices? _services;

    public override void _Ready()
    {
        _stepTitleLabel = GetNode<Label>("%StepTitle");
        _projectNameLabel = GetNode<Label>("%ProjectName");
        _navigator = GetNode<InfoNavigator>("%NavPanel");
        _editorScroll = GetNode<ScrollContainer>("%EditorScroll");
        _movieEditor = GetNode<MovieEditor>("%MovieEditor");
        _showEditor = GetNode<ShowEditor>("%ShowEditor");
        _seasonEditor = GetNode<SeasonEditor>("%SeasonEditor");
        _episodeEditor = GetNode<EpisodeEditor>("%EpisodeEditor");
        _extraEditor = GetNode<ExtraEditor>("%ExtraEditor");
        _saveTimer = GetNode<Timer>("%SaveTimer");
        _importDialog = GetNode<ImportDialog>("%ImportDialog");
        _errorDialog = GetNode<AcceptDialog>("%ErrorDialog");
    }

    private void On_BackButton_Pressed() => EmitSignal(SignalName.BackRequested);

    private void On_NavPanel_SelectionChanged() => ShowEditor();

    private void On_ImportDialog_ImportRequested()
    {
        if (_importDialog is { ChosenProvider: { } provider, ChosenResult: { } result, ChosenOptions: { } options })
            ImportOnline(provider, result, options);
    }

    public override void _ExitTree() => FlushSave();

    public void ShowProject(ProjectFolder folder, AppServices services)
    {
        FlushSave();
        _folder = folder;
        _services = services;
        _stepTitleLabel.Text = folder.Project.Kind == ProjectKind.Movie ? "1. Movie Information" : "1. Show Information";
        _projectNameLabel.Text = folder.Project.Name;

        _navigator.ShowProject(folder);
        ShowEditor();
    }

    /// <summary>Writes any pending edits now instead of waiting for the save delay.</summary>
    public void FlushSave()
    {
        if (!_saveTimer.IsStopped())
        {
            _saveTimer.Stop();
            SaveNow();
        }
    }

    // ---- Editors ----

    /// <summary>Shows the editor for what's selected, filled in from it.</summary>
    private void ShowEditor()
    {
        _editorScroll.ScrollVertical = 0;
        _movieEditor.Visible = _showEditor.Visible = _seasonEditor.Visible = _episodeEditor.Visible = _extraEditor.Visible = false;
        var folder = _folder;
        var services = _services;
        if (folder is null || services is null)
            return;

        var online = services.Online;
        switch (_navigator.Selected)
        {
            case MovieInfo movie:
                _movieEditor.Setup(movie, online.Providers);
                _movieEditor.Artwork.Fill(folder, online, movie.Images, ImageKinds.Movie);
                _movieEditor.Files.Setup(movie, folder, services);
                _movieEditor.Visible = true;
                break;
            case ShowInfo show:
                _showEditor.Setup(show, online.Providers);
                _showEditor.Artwork.Fill(folder, online, show.Images, ImageKinds.Show);
                _showEditor.Files.Setup(show, folder, services);
                _showEditor.Visible = true;
                break;
            case SeasonInfo season:
                _seasonEditor.Setup(folder.Project.Show!, season);
                _seasonEditor.Artwork.Fill(folder, online, season.Images, ImageKinds.Season, seasonNumber: season.Number);
                _seasonEditor.Visible = true;
                break;
            case EpisodeInfo episode:
            {
                var season = folder.Project.Show?.Seasons.Find(s => s.Episodes.Contains(episode));
                if (season is null)
                    break;
                _episodeEditor.Setup(season, episode);
                _episodeEditor.Artwork.Fill(folder, online, episode.Images, ImageKinds.Episode, "Episode Thumbnail", season.Number, episode.Number);
                _episodeEditor.Visible = true;
                break;
            }
            case ExtraInfo extra:
                _extraEditor.Setup(extra, folder.Project.Show);
                _extraEditor.Artwork.Fill(folder, null, extra.Images, ImageKinds.Extra, "Thumbnail");
                _extraEditor.Visible = true;
                break;
        }
    }

    /// <summary>A field was edited in the editor on show.</summary>
    private void On_Editor_Edited()
    {
        MarkChanged();
        _navigator.RefreshText();
    }

    /// <summary>A season or episode was renumbered, which can reorder the list.</summary>
    private void On_Editor_Renumbered() => _navigator.Rebuild();

    private void On_Editor_ImportRequested()
    {
        var folder = _folder;
        var online = _services?.Online;
        if (folder is not null && online is not null)
            _importDialog.PopupFor(folder.Project.Kind, folder.Project.Info, online.Providers, online);
    }

    private void On_SeasonEditor_AddEpisodesRequested(int count) => _navigator.AddEpisodes(count);

    private void On_SeasonEditor_EpisodeOpenRequested(int number) => _navigator.OpenEpisode(number);

    private async void ImportOnline(IMetadataProvider provider, SearchResult result, ImportOptions options)
    {
        var folder = _folder;
        if (folder is null)
            return;

        SetStatus($"Importing \"{result.Title}\" from {provider.Name}...");
        TitleInfo fetched;
        try
        {
            fetched = await provider.FetchAsync(folder.Project.Kind, result.Id, options);
        }
        catch (Exception ex)
        {
            SetStatus($"Import from {provider.Name} failed.");
            ShowError("Import Failed", ex.Message);
            return;
        }

        if (folder != _folder)
            return;

        MetadataMerger.Merge(folder.Project.Info, fetched, options);
        MarkChanged();
        FlushSave();
        _navigator.Rebuild();
        ShowEditor();

        var summary = fetched is ShowInfo { Seasons.Count: > 0 } show
            ? $" with {show.Seasons.Count} {(show.Seasons.Count == 1 ? "season" : "seasons")} and {show.Seasons.Sum(s => s.Episodes.Count)} episodes"
            : "";
        SetStatus($"Imported \"{result.Title}\" from {provider.Name}{summary}.");
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    private void ShowError(string title, string message)
    {
        _errorDialog.Title = title;
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    // ---- Saving ----

    private void MarkChanged() => _saveTimer.Start();

    private void On_SaveTimer_Timeout() => SaveNow();

    private void SaveNow()
    {
        try
        {
            _folder?.Save();
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't save the project: {ex.Message}");
        }
    }

    // ---- As a step page ----

    public ProjectStep Step => ProjectStep.Information;

    void IStepPage.ShowProject(ProjectFolder folder, AppServices services) => ShowProject(folder, services);

    void IStepPage.Leave() => FlushSave();

    void IStepPage.OnDiscLibraryChanged()
    {
    }

    /// <summary>The import and online image buttons depend on which keys are set.</summary>
    void IStepPage.OnSettingsChanged()
    {
        if (Visible && _folder is not null && _services is not null)
            ShowProject(_folder, _services);
    }
}
