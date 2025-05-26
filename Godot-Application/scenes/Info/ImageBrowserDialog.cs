using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Online;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Shows the images online sources have for one kind of artwork, one source at a time. Clicking one raises
/// <see cref="ImageChosenEventHandler"/>; the owner downloads it (<see cref="ChosenImage"/>) and closes the dialog.
/// </summary>
public partial class ImageBrowserDialog : AcceptDialog
{
    /// <summary>One site's images of the artwork being picked.</summary>
    public sealed record ImageSource(string Name, Func<CancellationToken, Task<IReadOnlyList<OnlineImage>>> LoadImages);

    private const int ConcurrentThumbnailDownloads = 6;

    // The site picked last, shown first next time
    private static string? _lastSourceName;

    [Signal]
    public delegate void ImageChosenEventHandler();

    /// <summary>One image's tile: a button holding its thumbnail and its size, language and rating.</summary>
    [Export] public PackedScene TileScene { get; set; } = null!;

    public OnlineImage? ChosenImage { get; private set; }

    private Control _sourceRow = null!;
    private OptionButton _sourceOption = null!;
    private Label _messageLabel = null!;
    private HFlowContainer _grid = null!;
    private ScrollContainer _scroll = null!;
    private CancellationTokenSource? _cancellation;
    private bool _busy;

    private string _title = "";
    private ImageKind _kind;
    private IReadOnlyList<ImageSource> _sources = [];
    private OnlineServices? _online;

    public override void _Ready()
    {
        _sourceRow = GetNode<Control>("%SourceRow");
        _sourceOption = GetNode<OptionButton>("%SourceOption");
        _messageLabel = GetNode<Label>("%MessageLabel");
        _grid = GetNode<HFlowContainer>("%Grid");
        _scroll = GetNode<ScrollContainer>("%Scroll");
    }

    private void On_VisibilityChanged()
    {
        if (!Visible)
            _cancellation?.Cancel();
    }

    private void On_SourceOption_ItemSelected(long index)
    {
        _lastSourceName = _sources[(int)index].Name;
        ShowImages();
    }

    /// <summary>Shows the images of <paramref name="sources"/>, starting with the one picked last; the picker is hidden if there's one.</summary>
    public void PopupFor(string title, ImageKind kind, IReadOnlyList<ImageSource> sources, OnlineServices online)
    {
        _title = title;
        _kind = kind;
        _sources = sources;
        _online = online;
        _sourceOption.Clear();
        foreach (var source in sources)
            _sourceOption.AddItem(source.Name);
        _sourceOption.Selected = Math.Max(0, sources.ToList().FindIndex(s => s.Name == _lastSourceName));
        _sourceRow.Visible = sources.Count > 1;
        Title = sources.Count == 1 ? $"{title} from {sources[0].Name}" : title;

        // Dialogs are laid out in the app's scaled UI space, so size to the visible area: the window's pixel
        // size is larger than that whenever the UI is stretched to fit a big window.
        var area = GetTree().Root.GetVisibleRect().Size;
        PopupCentered(new Vector2I((int)(area.X * 0.85f), (int)(area.Y * 0.85f)));
        ShowImages();
    }

    private async void ShowImages()
    {
        var online = _online;
        if (online is null || _sources.Count == 0)
            return;

        var source = _sources[_sourceOption.Selected];
        var title = _title;
        _cancellation?.Cancel();
        var cancellation = _cancellation = new CancellationTokenSource();
        SetBusy(false);
        ClearGrid();
        _messageLabel.Text = $"Loading images from {source.Name}...";

        IReadOnlyList<OnlineImage> images;
        try
        {
            images = await source.LoadImages(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                _messageLabel.Text = ex.Message;
            return;
        }

        if (cancellation.IsCancellationRequested)
            return;

        _messageLabel.Text = images.Count == 0
            ? $"{source.Name} has no {title.ToLowerInvariant()} images for this item."
            : $"{images.Count} {(images.Count == 1 ? "image" : "images")}, best first. Click one to use it.";

        var thumbnailSize = ThumbnailSize(_kind);
        var tiles = new List<(OnlineImage Image, TextureRect Preview)>();
        foreach (var image in images)
            tiles.Add((image, AddTile(image, thumbnailSize)));

        await LoadThumbnailsAsync(online, tiles, cancellation.Token);
    }

    /// <summary>Shows a message and blocks further picks while the chosen image downloads.</summary>
    public void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _grid.Modulate = new Color(1, 1, 1, busy ? 0.4f : 1);
        if (message is not null)
            _messageLabel.Text = message;
    }

    private TextureRect AddTile(OnlineImage image, Vector2 thumbnailSize)
    {
        var tile = TileScene.Instantiate<Button>();
        var preview = tile.GetNode<TextureRect>("%Preview");
        preview.CustomMinimumSize = thumbnailSize;
        var language = image.Language is null ? "no text" : image.Language.ToUpperInvariant();
        var size = image.Width > 0 ? $"{image.Width}×{image.Height} · " : "";
        tile.GetNode<Label>("%Caption").Text = $"{size}{language}{(image.Rating > 0 ? $" · ★{image.Rating:0.0}" : "")}";
        tile.CustomMinimumSize = thumbnailSize + new Vector2(12, 34);
        tile.Pressed += () =>
        {
            if (_busy)
                return;
            ChosenImage = image;
            EmitSignal(SignalName.ImageChosen);
        };
        _grid.AddChild(tile);
        return preview;
    }

    private static async Task LoadThumbnailsAsync(OnlineServices online, List<(OnlineImage Image, TextureRect Preview)> tiles,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(ConcurrentThumbnailDownloads);
        var tasks = new List<Task>();
        foreach (var (image, preview) in tiles)
        {
            tasks.Add(LoadOneAsync(image, preview));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }

        async Task LoadOneAsync(OnlineImage image, TextureRect preview)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var data = await online.DownloadAsync(image.ThumbnailUrl, cancellationToken);
                if (!cancellationToken.IsCancellationRequested && GodotObject.IsInstanceValid(preview))
                    preview.Texture = ImageData.ToTexture(data);
            }
            catch (OnlineMetadataException)
            {
                // Leave the tile blank; the full image may still download fine.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private void ClearGrid()
    {
        foreach (var child in _grid.GetChildren())
        {
            _grid.RemoveChild(child);
            child.QueueFree();
        }

        _scroll.ScrollVertical = 0;
    }

    private static Vector2 ThumbnailSize(ImageKind kind) => kind switch
    {
        ImageKind.Poster => new Vector2(150, 225),
        ImageKind.Logo => new Vector2(240, 93),
        _ => new Vector2(256, 144),
    };
}
