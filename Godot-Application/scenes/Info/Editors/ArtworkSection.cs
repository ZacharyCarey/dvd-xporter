using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Online;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// An item's artwork: a slot for each kind of image it can have, filled from a file (chosen or dropped) or, for
/// kinds TMDB or TVDB have, picked from theirs. Images are copied into the project, and replaced ones deleted.
/// </summary>
public partial class ArtworkSection : InfoSection
{
    /// <summary>One piece of artwork (see <see cref="ImageSlot"/>), made for each kind the item has.</summary>
    [Export] public PackedScene ImageSlotScene { get; set; } = null!;

    private Container _slots = null!;
    private AcceptDialog _previewDialog = null!;
    private TextureRect _previewTexture = null!;
    private ImageBrowserDialog _imageBrowser = null!;

    private ProjectFolder? _folder;
    private OnlineServices? _online;
    private Action<OnlineImage>? _onBrowserImageChosen;

    public override void _Ready()
    {
        _slots = GetNode<Container>("%Slots");
        _previewDialog = GetNode<AcceptDialog>("%PreviewDialog");
        _previewTexture = GetNode<TextureRect>("%PreviewTexture");
        _imageBrowser = GetNode<ImageBrowserDialog>("%ImageBrowserDialog");
    }

    /// <summary>
    /// Shows <paramref name="images"/>, a slot for each of <paramref name="kinds"/>. A season's or episode's number
    /// says which of the online sources' images are its own.
    /// </summary>
    public void Fill(ProjectFolder folder, OnlineServices? online, Dictionary<ImageKind, string> images, IReadOnlyList<ImageKind> kinds,
        string? thumbnailName = null, int? seasonNumber = null, int? episodeNumber = null)
    {
        _folder = folder;
        _online = online;
        foreach (var child in _slots.GetChildren())
        {
            _slots.RemoveChild(child);
            child.QueueFree();
        }

        // The slots are made per item, so they're connected here
        foreach (var kind in kinds)
        {
            var slot = ImageSlotScene.Instantiate<ImageSlot>();
            _slots.AddChild(slot);
            var title = ImageKindDisplay.Name(kind, thumbnailName);
            slot.Setup(title, ImageKindDisplay.Hint(kind), ImageKindDisplay.AspectRatio(kind));
            slot.SetImage(images.TryGetValue(kind, out var existing) ? folder.Images.GetFullPath(existing) : null);

            slot.ImageChosen += sourcePath =>
            {
                try
                {
                    UseImage(images, kind, slot, title, folder.Images.Import(sourcePath));
                }
                catch (Exception ex)
                {
                    SetStatus($"Couldn't use that image: {ex.Message}");
                }
            };
            slot.ImageRemoved += () =>
            {
                if (images.Remove(kind, out var previous))
                    folder.Images.Delete(previous);
                slot.SetImage(null);
                MarkEdited();
                SetStatus($"Removed the {title.ToLowerInvariant()}.");
            };
            slot.PreviewRequested += ShowPreview;

            var sources = online?.ImageProviders.Where(p => p.HasImages(kind, seasonNumber, episodeNumber)).ToList() ?? [];
            slot.SetOnlineSources(sources.Select(p => p.Name).ToList());
            if (sources.Count > 0)
                slot.OnlineRequested += () => BrowseOnline(sources, images, kind, slot, title, seasonNumber, episodeNumber);
        }
    }

    private void BrowseOnline(List<IImageProvider> sources, Dictionary<ImageKind, string> images, ImageKind kind, ImageSlot slot,
        string title, int? seasonNumber, int? episodeNumber)
    {
        var folder = _folder;
        var online = _online;
        if (folder is null || online is null)
            return;

        var project = folder.Project;
        var browsable = sources
            .Select(p => new ImageBrowserDialog.ImageSource(p.Name, cancellationToken =>
                p.GetImagesAsync(project.Kind, project.Info.Ids, seasonNumber, episodeNumber, kind, cancellationToken)))
            .ToList();
        _imageBrowser.PopupFor(title, kind, browsable, online);

        _onBrowserImageChosen = async image =>
        {
            _imageBrowser.SetBusy(true, "Downloading the full-size image...");
            string imported;
            try
            {
                var data = await online.DownloadAsync(image.Url);
                var extension = ImageData.DetectExtension(data)
                    ?? throw new OnlineMetadataException("The downloaded file isn't a JPEG, PNG or WebP image.");
                imported = folder.Images.Import(data, extension);
            }
            catch (Exception ex)
            {
                _imageBrowser.SetBusy(false, $"Couldn't download that image: {ex.Message}");
                return;
            }

            _imageBrowser.Hide();
            if (folder != _folder)
                return;
            // The item it was picked for, even if another is shown now
            UseImage(images, kind, IsInstanceValid(slot) ? slot : null, title, imported);
        };
    }

    private void On_ImageBrowserDialog_ImageChosen()
    {
        var image = _imageBrowser.ChosenImage;
        if (image is not null)
            _onBrowserImageChosen?.Invoke(image);
    }

    /// <summary>Sets a newly imported image (project-relative path), replacing and deleting any previous one.</summary>
    private void UseImage(Dictionary<ImageKind, string> images, ImageKind kind, ImageSlot? slot, string title, string imported)
    {
        var folder = _folder;
        if (folder is null)
            return;
        if (images.TryGetValue(kind, out var previous))
            folder.Images.Delete(previous);
        images[kind] = imported;
        slot?.SetImage(folder.Images.GetFullPath(imported));
        MarkEdited();
        SetStatus($"Set the {title.ToLowerInvariant()}.");
    }

    private void ShowPreview(Texture2D texture, string title)
    {
        _previewTexture.Texture = texture;
        _previewDialog.Title = title;
        // Dialogs are drawn inside the app window, so size the preview to the window rather than the screen.
        var area = GetViewport().GetVisibleRect().Size;
        _previewDialog.PopupCentered(new Vector2I((int)(area.X * 0.85f), (int)(area.Y * 0.85f)));
    }
}
