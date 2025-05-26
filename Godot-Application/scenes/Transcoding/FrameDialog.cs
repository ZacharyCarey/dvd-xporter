using System;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Edits a canvas frame's title and colour. Nothing changes unless it's saved, so a new frame can be made only
/// then (and not at all if it's cancelled).
/// </summary>
public partial class FrameDialog : ConfirmationDialog
{
    /// <summary>The colours a frame can have: a name, and its "#rrggbb" ("" = the default).</summary>
    public static readonly (string Name, string Hex)[] Colors =
    [
        ("Default", ""),
        ("Red", "#c0504d"),
        ("Orange", "#d9822b"),
        ("Yellow", "#c9a227"),
        ("Green", "#5a9e4b"),
        ("Teal", "#3a9a9a"),
        ("Blue", "#4a78c0"),
        ("Purple", "#8a5ab8"),
        ("Grey", "#7a7a7a"),
    ];

    private LineEdit _titleEdit = null!;
    private OptionButton _colorOption = null!;
    private CanvasFrame? _frame;
    private Action<CanvasFrame>? _saved;

    public override void _Ready()
    {
        _titleEdit = GetNode<LineEdit>("%TitleEdit");
        _colorOption = GetNode<OptionButton>("%ColorOption");
        RegisterTextEnter(_titleEdit);

        // A swatch for each colour, made from the list the comments' colours use too
        foreach (var (name, hex) in Colors)
        {
            if (hex.Length == 0)
            {
                _colorOption.AddItem(name);
                continue;
            }

            var swatch = Image.CreateEmpty(14, 14, false, Image.Format.Rgba8);
            swatch.Fill(Color.FromHtml(hex));
            _colorOption.AddIconItem(ImageTexture.CreateFromImage(swatch), name);
        }
    }

    /// <param name="saved">Called with the frame once its new title and colour are set.</param>
    public void Edit(CanvasFrame frame, Action<CanvasFrame> saved, string title = "Frame")
    {
        _frame = frame;
        _saved = saved;
        Title = title;
        _titleEdit.Text = frame.Title;
        _colorOption.Select(Math.Max(0, Array.FindIndex(Colors, c => c.Hex == frame.Color)));
        PopupCentered();
        _titleEdit.GrabFocus();
        _titleEdit.SelectAll();
    }

    private void On_Confirmed()
    {
        var frame = _frame;
        if (frame is null)
            return;
        frame.Title = _titleEdit.Text.Trim();
        frame.Color = Colors.ElementAtOrDefault(_colorOption.Selected).Hex ?? "";
        _saved?.Invoke(frame);
    }
}
