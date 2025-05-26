using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Online;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Searches TMDB, TVDB or OMDb for the movie or show, and lets the user pick a match and what to import.
/// </summary>
public partial class ImportDialog : ConfirmationDialog
{
    /// <summary>Raised when a match has been picked: it's <see cref="ChosenResult"/>, from <see cref="ChosenProvider"/>.</summary>
    [Signal]
    public delegate void ImportRequestedEventHandler();

    public IMetadataProvider? ChosenProvider { get; private set; }

    public SearchResult? ChosenResult { get; private set; }

    public ImportOptions? ChosenOptions { get; private set; }

    private OptionButton _sourceOption = null!;
    private LineEdit _queryEdit = null!;
    private LineEdit _yearEdit = null!;
    private Button _searchButton = null!;
    private ItemList _resultsList = null!;
    private Label _messageLabel = null!;
    private CheckBox _detailsCheck = null!;
    private CheckBox _episodesCheck = null!;
    private CheckBox _specialsCheck = null!;
    private CheckBox _overwriteCheck = null!;

    private IReadOnlyList<IMetadataProvider> _providers = [];
    private OnlineServices? _online;
    private ProjectKind _kind;
    private TitleInfo? _info;
    private List<SearchResult> _results = [];
    private CancellationTokenSource? _searchCancellation;

    private IMetadataProvider? Provider => _providers.Count > 0 ? _providers[_sourceOption.Selected] : null;

    public override void _Ready()
    {
        _sourceOption = GetNode<OptionButton>("%SourceOption");
        _queryEdit = GetNode<LineEdit>("%QueryEdit");
        _yearEdit = GetNode<LineEdit>("%YearEdit");
        _searchButton = GetNode<Button>("%SearchButton");
        _resultsList = GetNode<ItemList>("%ResultsList");
        _messageLabel = GetNode<Label>("%MessageLabel");
        _detailsCheck = GetNode<CheckBox>("%DetailsCheck");
        _episodesCheck = GetNode<CheckBox>("%EpisodesCheck");
        _specialsCheck = GetNode<CheckBox>("%SpecialsCheck");
        _overwriteCheck = GetNode<CheckBox>("%OverwriteCheck");
    }

    private void On_SearchButton_Pressed() => Search();

    /// <summary>Enter in the title or year.</summary>
    private void On_Edit_TextSubmitted(string text) => Search();

    private void On_SourceOption_ItemSelected(long index)
    {
        UpdateOptions();
        ApplyDefaultQuery();
        Search();
    }

    private void On_ResultsList_ItemSelected(long index) => UpdateOkButton();

    private void On_ResultsList_ItemActivated(long index)
    {
        Hide();
        Import();
    }

    private void On_DetailsCheck_Toggled(bool on) => UpdateOkButton();

    private void On_EpisodesCheck_Toggled(bool on)
    {
        UpdateOptions();
        UpdateOkButton();
    }

    private void On_Confirmed() => Import();

    private void On_VisibilityChanged()
    {
        if (!Visible)
            _searchCancellation?.Cancel();
    }

    public void PopupFor(ProjectKind kind, TitleInfo info, IReadOnlyList<IMetadataProvider> providers, OnlineServices online)
    {
        _kind = kind;
        _info = info;
        _providers = providers;
        _online = online;

        _sourceOption.Clear();
        foreach (var provider in providers)
            _sourceOption.AddItem(provider.Name);

        Title = $"Import {(kind == ProjectKind.Movie ? "Movie" : "Show")} Details";
        _episodesCheck.Visible = kind == ProjectKind.TvShow;
        _specialsCheck.Visible = kind == ProjectKind.TvShow;
        UpdateOptions();
        ApplyDefaultQuery();

        PopupCentered();
        _queryEdit.GrabFocus();
        Search();
    }

    // Look up the title by its saved ID when there is one, otherwise by name and year.
    private void ApplyDefaultQuery()
    {
        if (_info is null)
            return;

        var imdbId = _info.Ids.Imdb;
        _queryEdit.Text = imdbId is not null ? imdbId : _info.Title;
        _yearEdit.Text = imdbId is null ? _info.Year?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
    }

    private void UpdateOptions()
    {
        // OMDb has no specials.
        _specialsCheck.Disabled = !_episodesCheck.ButtonPressed || Provider is OmdbProvider;
    }

    private async void Search()
    {
        var provider = Provider;
        if (provider is null || _online is null)
            return;

        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var query = _queryEdit.Text.Trim();
        int? year = int.TryParse(_yearEdit.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

        _results = [];
        _resultsList.Clear();
        UpdateOkButton();
        if (query.Length == 0)
        {
            _messageLabel.Text = "Enter a title to search for.";
            return;
        }

        _messageLabel.Text = $"Searching {provider.Name}...";
        _searchButton.Disabled = true;
        try
        {
            _results = [.. await provider.SearchAsync(_kind, query, year, cancellation.Token)];
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cancellation == _searchCancellation)
                _messageLabel.Text = ex.Message;
            return;
        }
        finally
        {
            if (cancellation == _searchCancellation)
                _searchButton.Disabled = false;
        }

        if (cancellation.IsCancellationRequested)
            return;

        foreach (var result in _results)
        {
            var text = result.Year is not null ? $"{result.Title} ({result.Year.Value})" : result.Title;
            var index = _resultsList.AddItem(text);
            var overview = result.Overview;
            if (overview is not null)
                _resultsList.SetItemTooltip(index, overview.Length > 300 ? overview[..300] + "..." : overview);
        }

        _messageLabel.Text = _results.Count switch
        {
            0 => "No matches. Try a different title, remove the year, or paste an IMDb ID (tt...).",
            1 => "1 match. Select it and choose Import.",
            _ => $"{_results.Count} matches. Select the right one and choose Import.",
        };
        if (_results.Count == 1)
        {
            _resultsList.Select(0);
            UpdateOkButton();
        }

        await LoadThumbnailsAsync(_online, _results, cancellation.Token);
    }

    private async Task LoadThumbnailsAsync(OnlineServices online, List<SearchResult> results, CancellationToken cancellationToken)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var url = results[i].PosterThumbnailUrl;
            if (url is null)
                continue;

            try
            {
                var data = await online.DownloadAsync(url, cancellationToken);
                if (cancellationToken.IsCancellationRequested || results != _results)
                    return;
                var texture = ImageData.ToTexture(data);
                if (texture is not null)
                    _resultsList.SetItemIcon(i, texture);
            }
            catch (Exception ex) when (ex is OnlineMetadataException or OperationCanceledException)
            {
                // A missing thumbnail isn't worth interrupting the search for.
            }
        }
    }

    private void UpdateOkButton()
    {
        var hasSelection = _resultsList.GetSelectedItems().Length > 0;
        var hasSomethingToImport = _detailsCheck.ButtonPressed || (_episodesCheck.Visible && _episodesCheck.ButtonPressed);
        GetOkButton().Disabled = !hasSelection || !hasSomethingToImport;
    }

    private void Import()
    {
        var selected = _resultsList.GetSelectedItems();
        var provider = Provider;
        if (provider is null || selected.Length == 0)
            return;

        var options = new ImportOptions(
            Details: _detailsCheck.ButtonPressed,
            Episodes: _kind == ProjectKind.TvShow && _episodesCheck.ButtonPressed,
            IncludeSpecials: !_specialsCheck.Disabled && _specialsCheck.ButtonPressed,
            Overwrite: _overwriteCheck.ButtonPressed);
        ChosenProvider = provider;
        ChosenResult = _results[selected[0]];
        ChosenOptions = options;
        EmitSignal(SignalName.ImportRequested);
    }
}
