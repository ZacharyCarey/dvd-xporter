using System;
using System.Collections.Generic;
using DvdXporter.Core.Online;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Settings;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// User settings dialog. The Disc Library, the short clip length, the Emby libraries, the Online tab and the render
/// server are persisted; the other fields are placeholders.
/// </summary>
public partial class SettingsDialog : ConfirmationDialog
{
    private enum OnlineService
    {
        Tmdb,
        Tvdb,
        Omdb,
    }

    private static readonly Color ErrorColor = new(0.89f, 0.44f, 0.42f);
    private static readonly Color SuccessColor = new(0.5f, 0.81f, 0.52f);

    [Signal]
    public delegate void ClearCacheRequestedEventHandler();

    /// <summary>The online metadata sites, to check keys with.</summary>
    public OnlineServices? Online { get; set; }

    private FileDialog _browseDialog = null!;
    private LineEdit _discLibraryEdit = null!;
    private LineEdit _moviesLibraryEdit = null!;
    private LineEdit _showsLibraryEdit = null!;
    private LineEdit _tmdbKeyEdit = null!;
    private LineEdit _tvdbKeyEdit = null!;
    private LineEdit _tvdbPinEdit = null!;
    private LineEdit _omdbKeyEdit = null!;
    private LineEdit _languageEdit = null!;
    private SpinBox _shortClipSpin = null!;
    private Label _onlineResultLabel = null!;
    private LineEdit _serverAddressEdit = null!;
    private LineEdit _serverKeyEdit = null!;
    private Label _serverResultLabel = null!;
    private PopupMenu _foundServersMenu = null!;
    private LineEdit? _browseTarget;
    // The servers that answered Find on Network, by their menu item's ID
    private List<FoundRenderServer> _foundServers = [];

    /// <summary>Shows <paramref name="settings"/> to change.</summary>
    public void Open(AppSettings settings)
    {
        _discLibraryEdit.Text = settings.DiscLibraryPath ?? "";
        _moviesLibraryEdit.Text = settings.MoviesLibraryPath ?? "";
        _showsLibraryEdit.Text = settings.ShowsLibraryPath ?? "";
        _shortClipSpin.Value = settings.ShortClipSeconds;
        _tmdbKeyEdit.Text = settings.TmdbApiKey ?? "";
        _tvdbKeyEdit.Text = settings.TvdbApiKey ?? "";
        _tvdbPinEdit.Text = settings.TvdbPin ?? "";
        _omdbKeyEdit.Text = settings.OmdbApiKey ?? "";
        _languageEdit.Text = settings.MetadataLanguage ?? "";
        _serverAddressEdit.Text = settings.RenderServerAddress ?? "";
        _serverKeyEdit.Text = settings.RenderServerKey ?? "";
        PopupCentered();
    }

    /// <summary>Puts what was entered into <paramref name="settings"/>. Returns whether the Disc Library folder changed.</summary>
    public bool Apply(AppSettings settings)
    {
        var discLibrary = NullIfEmpty(_discLibraryEdit.Text);
        var discLibraryChanged = discLibrary != NullIfEmpty(settings.DiscLibraryPath ?? "");
        settings.DiscLibraryPath = discLibrary;
        settings.MoviesLibraryPath = NullIfEmpty(_moviesLibraryEdit.Text);
        settings.ShowsLibraryPath = NullIfEmpty(_showsLibraryEdit.Text);
        settings.TmdbApiKey = NullIfEmpty(_tmdbKeyEdit.Text);
        settings.TvdbApiKey = NullIfEmpty(_tvdbKeyEdit.Text);
        settings.TvdbPin = NullIfEmpty(_tvdbPinEdit.Text);
        settings.OmdbApiKey = NullIfEmpty(_omdbKeyEdit.Text);
        settings.MetadataLanguage = NullIfEmpty(_languageEdit.Text);
        settings.ShortClipSeconds = (int)_shortClipSpin.Value;
        settings.RenderServerAddress = NullIfEmpty(_serverAddressEdit.Text);
        settings.RenderServerKey = NullIfEmpty(_serverKeyEdit.Text);
        return discLibraryChanged;
    }

    private static string? NullIfEmpty(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    public override void _Ready()
    {
        _browseDialog = GetNode<FileDialog>("%BrowseDialog");
        _discLibraryEdit = GetNode<LineEdit>("%DiscLibraryEdit");
        _moviesLibraryEdit = GetNode<LineEdit>("%MoviesLibraryEdit");
        _showsLibraryEdit = GetNode<LineEdit>("%ShowsLibraryEdit");
        _tmdbKeyEdit = GetNode<LineEdit>("%TmdbKeyEdit");
        _tvdbKeyEdit = GetNode<LineEdit>("%TvdbKeyEdit");
        _tvdbPinEdit = GetNode<LineEdit>("%TvdbPinEdit");
        _omdbKeyEdit = GetNode<LineEdit>("%OmdbKeyEdit");
        _languageEdit = GetNode<LineEdit>("%MetadataLanguageEdit");
        _shortClipSpin = GetNode<SpinBox>("%ShortClipSpin");
        _onlineResultLabel = GetNode<Label>("%OnlineTestResultLabel");
        _serverAddressEdit = GetNode<LineEdit>("%ServerAddressEdit");
        _serverKeyEdit = GetNode<LineEdit>("%ServerKeyEdit");
        _serverResultLabel = GetNode<Label>("%ServerResultLabel");
        _foundServersMenu = GetNode<PopupMenu>("%FoundServersMenu");
    }

    private void On_AboutToPopup()
    {
        _onlineResultLabel.Text = "";
        _serverResultLabel.Text = "";
    }

    private void On_BrowseDiscLibraryButton_Pressed() => Browse(_discLibraryEdit, FileDialog.FileModeEnum.OpenDir);

    private void On_BrowseMoviesLibraryButton_Pressed() => Browse(_moviesLibraryEdit, FileDialog.FileModeEnum.OpenDir);

    private void On_BrowseShowsLibraryButton_Pressed() => Browse(_showsLibraryEdit, FileDialog.FileModeEnum.OpenDir);

    private void On_BrowseFfmpegPathButton_Pressed() => Browse(GetNode<LineEdit>("%FfmpegPathEdit"), FileDialog.FileModeEnum.OpenFile);

    private void On_BrowseFfprobePathButton_Pressed() => Browse(GetNode<LineEdit>("%FfprobePathEdit"), FileDialog.FileModeEnum.OpenFile);

    private void On_BrowseMakeMkvPathButton_Pressed() => Browse(GetNode<LineEdit>("%MakeMkvPathEdit"), FileDialog.FileModeEnum.OpenFile);

    private void On_ClearCacheButton_Pressed() => EmitSignal(SignalName.ClearCacheRequested);

    private void On_TestTmdbKeyButton_Pressed() => TestKey(OnlineService.Tmdb, _tmdbKeyEdit.Text.Trim());

    private void On_TestTvdbKeyButton_Pressed() => TestKey(OnlineService.Tvdb, _tvdbKeyEdit.Text.Trim());

    private void On_TestOmdbKeyButton_Pressed() => TestKey(OnlineService.Omdb, _omdbKeyEdit.Text.Trim());

    /// <summary>Asks the site whether a key works (before it's saved), and says.</summary>
    private async void TestKey(OnlineService service, string key)
    {
        _onlineResultLabel.Modulate = Colors.White;
        if (key.Length == 0)
        {
            _onlineResultLabel.Text = $"Enter a {ServiceName(service)} key first.";
            return;
        }

        _onlineResultLabel.Text = $"Testing {ServiceName(service)} key...";
        IMetadataProvider? provider = service switch
        {
            OnlineService.Tmdb => Online?.CreateTmdb(key, _languageEdit.Text.Trim()),
            OnlineService.Tvdb => Online?.CreateTvdb(key, _tvdbPinEdit.Text, _languageEdit.Text.Trim()),
            _ => Online?.CreateOmdb(key),
        };
        var problem = provider is null ? "Enter a key first." : await provider.TestKeyAsync();
        var name = ServiceName(service);
        _onlineResultLabel.Text = problem is null ? $"✓ {name}: the key works." : $"✗ {problem}";
        _onlineResultLabel.Modulate = problem is null ? SuccessColor : ErrorColor;
    }

    // ---- Render server ----

    /// <summary>Asks the network for render servers, and offers those that answer.</summary>
    private async void On_FindServersButton_Pressed()
    {
        var button = GetNode<Button>("%FindServersButton");
        ShowServerResult("Looking for render servers on this network...", null);
        button.Disabled = true;
        try
        {
            _foundServers = await RenderServerDiscovery.FindAsync(TimeSpan.FromSeconds(2));
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            _foundServers = [];
            ShowServerResult($"✗ Couldn't look on the network: {ex.Message}", false);
            return;
        }
        finally
        {
            button.Disabled = false;
        }

        if (_foundServers.Count == 0)
        {
            ShowServerResult("No render servers answered. Check the server is running (and its firewall lets UDP port "
                + $"{RenderServerProtocol.DiscoveryPort} in), or enter its address.", false);
            return;
        }

        ShowServerResult(_foundServers.Count == 1 ? "Found 1 render server." : $"Found {_foundServers.Count} render servers.", null);
        _foundServersMenu.Clear();
        for (int i = 0; i < _foundServers.Count; i++)
            _foundServersMenu.AddItem($"{_foundServers[i].Info.Name}  ({_foundServers[i].Address})", i);
        _foundServersMenu.Position = (Vector2I)(button.GetScreenPosition() + new Vector2(0, button.Size.Y));
        _foundServersMenu.Popup();
    }

    private void On_FoundServersMenu_IdPressed(long id)
    {
        var server = _foundServers[(int)id];
        _serverAddressEdit.Text = server.Address;
        ShowServerResult(_serverKeyEdit.Text.Trim().Length == 0
            ? $"Enter \"{server.Info.Name}\"'s access key, then Test it. The server shows it when it's set up."
            : $"Using \"{server.Info.Name}\". Test it to check the access key.", null);
    }

    /// <summary>Checks the server entered can be reached and takes the key (before they're saved), and says.</summary>
    private async void On_TestServerButton_Pressed()
    {
        var address = _serverAddressEdit.Text.Trim();
        if (address.Length == 0)
        {
            ShowServerResult("Enter the server's address first, or find it on the network.", null);
            return;
        }

        ShowServerResult($"Connecting to {address}...", null);
        try
        {
            using var client = new RenderServerClient(address, _serverKeyEdit.Text);
            var info = await client.ConnectAsync();
            var into = info.ExportsTo == RenderServerDestination.Emby ? "its Emby libraries" : "a folder";
            if (info.SameVersion)
                ShowServerResult($"✓ Connected to \"{info.Name}\" (version {info.Version}), which exports into {into}.", true);
            else
                ShowServerResult($"⚠ Connected, but {info.VersionWarning}", null);
        }
        catch (RenderServerException ex)
        {
            ShowServerResult($"✗ {ex.Message}", false);
        }
    }

    // Worked (green), didn't (red) or neither
    private void ShowServerResult(string text, bool? worked)
    {
        _serverResultLabel.Text = text;
        _serverResultLabel.Modulate = worked is null ? Colors.White : worked.Value ? SuccessColor : ErrorColor;
    }

    private static string ServiceName(OnlineService service) => service switch
    {
        OnlineService.Tmdb => "TMDB",
        OnlineService.Tvdb => "TVDB",
        _ => "OMDb",
    };

    private void Browse(LineEdit target, FileDialog.FileModeEnum mode)
    {
        _browseTarget = target;
        _browseDialog.FileMode = mode;
        _browseDialog.Title = mode == FileDialog.FileModeEnum.OpenDir ? "Choose Folder" : "Choose Executable";
        if (!string.IsNullOrWhiteSpace(target.Text))
            _browseDialog.CurrentPath = target.Text;
        _browseDialog.PopupCentered();
    }

    /// <summary>A folder or program picked with <see cref="Browse"/>.</summary>
    private void On_BrowseDialog_PathChosen(string path)
    {
        if (_browseTarget is not null)
            _browseTarget.Text = path;
    }
}
