using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// One piece of artwork: a preview framed at the expected shape, with Choose/Remove buttons.
/// Image files dropped onto it from the OS are accepted too. Click the preview to see it full size.
/// </summary>
public partial class ImageSlot : VBoxContainer
{
    [Signal]
    public delegate void ImageChosenEventHandler(string path);

    [Signal]
    public delegate void ImageRemovedEventHandler();

    [Signal]
    public delegate void PreviewRequestedEventHandler(Texture2D texture, string title);

    /// <summary>The user wants to pick from online images (see <see cref="SetOnlineSources"/>).</summary>
    [Signal]
    public delegate void OnlineRequestedEventHandler();

    private Label _titleLabel = null!;
    private Label _hintLabel = null!;
    private AspectRatioContainer _frame = null!;
    private TextureRect _preview = null!;
    private Label _emptyLabel = null!;
    private Label _infoLabel = null!;
    private Button _removeButton = null!;
    private Button _onlineButton = null!;
    private FileDialog _chooseDialog = null!;

    // Shared by every slot, for as long as the app is open
    private static string? _lastFolder;

    public override void _Ready()
    {
        _titleLabel = GetNode<Label>("%TitleLabel");
        _hintLabel = GetNode<Label>("%HintLabel");
        _frame = GetNode<AspectRatioContainer>("%Frame");
        _preview = GetNode<TextureRect>("%Preview");
        _emptyLabel = GetNode<Label>("%EmptyLabel");
        _infoLabel = GetNode<Label>("%InfoLabel");
        _removeButton = GetNode<Button>("%RemoveButton");
        _onlineButton = GetNode<Button>("%OnlineButton");
        _chooseDialog = GetNode<FileDialog>("%ChooseDialog");
        // The window isn't in this scene, so its drops are connected here
        GetWindow().FilesDropped += OnFilesDropped;
    }

    private void On_OnlineButton_Pressed() => EmitSignal(SignalName.OnlineRequested);

    private void On_RemoveButton_Pressed() => EmitSignal(SignalName.ImageRemoved);

    // Starts where the last image came from, as artwork tends to be kept together (e.g. a screenshots folder)
    private void On_ChooseButton_Pressed()
    {
        var last = _lastFolder;
        if (last is not null && Directory.Exists(last))
            _chooseDialog.CurrentDir = last;
        _chooseDialog.PopupCentered();
    }

    private void On_ChooseDialog_FileSelected(string path)
    {
        _lastFolder = Path.GetDirectoryName(path);
        EmitSignal(SignalName.ImageChosen, path);
    }

    public override void _ExitTree() => GetWindow().FilesDropped -= OnFilesDropped;

    public void Setup(string title, string hint, float aspectRatio)
    {
        _titleLabel.Text = title;
        _hintLabel.Text = hint;
        _frame.Ratio = aspectRatio;
        // AspectRatioContainer doesn't size itself from its ratio, so give the frame a height that suits the shape.
        _frame.CustomMinimumSize = new Vector2(0, Mathf.Clamp(CustomMinimumSize.X / aspectRatio, 60, 280));
        _chooseDialog.Title = $"Choose {title}";
    }

    /// <summary>
    /// Shows a button for picking from online sources' images, named for the source if there's one (e.g. "TMDB...").
    /// None hides it.
    /// </summary>
    public void SetOnlineSources(IReadOnlyList<string> sourceNames)
    {
        _onlineButton.Visible = sourceNames.Count > 0;
        _onlineButton.Text = sourceNames.Count == 1 ? $"{sourceNames[0]}..." : "Online...";
        _onlineButton.TooltipText = $"Choose from {string.Join(" or ", sourceNames.Select(n => $"{n}'s"))} images";
    }

    /// <summary>Shows the image at <paramref name="fullPath"/>, or the empty state for null.</summary>
    public void SetImage(string? fullPath)
    {
        _preview.Texture = null;
        _infoLabel.Text = "";
        _removeButton.Disabled = fullPath is null;
        _emptyLabel.Text = "No image";

        if (fullPath is not null)
        {
            var image = new Image();
            if (image.Load(fullPath) == Error.Ok)
            {
                _preview.Texture = ImageTexture.CreateFromImage(image);
                var extension = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
                _infoLabel.Text = $"{image.GetWidth()} × {image.GetHeight()}  ·  {extension}";
            }
            else
            {
                _emptyLabel.Text = "Couldn't load image";
            }
        }

        _emptyLabel.Visible = _preview.Texture is null;
        TooltipText = _preview.Texture is null ? "Choose an image, or drop an image file here" : "Click to preview";
    }

    private void On_PreviewArea_GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && _preview.Texture is not null)
            EmitSignal(SignalName.PreviewRequested, _preview.Texture, $"{_titleLabel.Text} — {_infoLabel.Text}");
    }

    private void OnFilesDropped(string[] files)
    {
        if (files.Length > 0 && IsVisibleInTree() && GetGlobalRect().HasPoint(GetGlobalMousePosition()))
            EmitSignal(SignalName.ImageChosen, files[0]);
    }
}
