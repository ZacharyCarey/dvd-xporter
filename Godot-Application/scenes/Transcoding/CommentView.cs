using System;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A comment on the canvas: a note typed straight into it. Dragged (and right-clicked) by its top strip, resized
/// from its corner, and it can go in a frame like a node.
/// </summary>
public partial class CommentView : GraphElement
{
    private static readonly Color DefaultColor = new(0.85f, 0.75f, 0.35f);

    /// <summary>Raised when its text was edited (once typing's done), or it was resized.</summary>
    [Signal]
    public delegate void ChangedEventHandler(CommentView view);

    /// <summary>Raised on a right-click on its strip, with the screen position.</summary>
    [Signal]
    public delegate void ContextMenuRequestedEventHandler(CommentView view, Vector2 at);

    public CanvasComment Model { get; private set; } = null!;

    private PanelContainer _panel = null!;
    // The note's own look (local to each comment in the scene), coloured by UpdateLook
    private StyleBoxFlat _style = null!;
    private Label _strip = null!;
    private TextEdit _text = null!;

    public static string ViewName(int id) => $"c{id}";

    /// <summary>Shows <paramref name="model"/>. Call once it's been added (its parts are found when it's ready).</summary>
    public void Setup(CanvasComment model)
    {
        Model = model;
        Name = ViewName(model.Id);
        PositionOffset = new Vector2((float)model.X, (float)model.Y);
        Size = new Vector2((float)model.Width, (float)model.Height);
        _text.Text = model.Text;
        UpdateLook();
    }

    public override void _Ready()
    {
        _panel = GetNode<PanelContainer>("%Panel");
        _style = (StyleBoxFlat)_panel.GetThemeStylebox("panel");
        _strip = GetNode<Label>("%Strip");
        _text = GetNode<TextEdit>("%Text");
        _panel.Size = Size;
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } click)
        {
            EmitSignal(SignalName.ContextMenuRequested, this, GetScreenPosition() + click.Position * GetGlobalTransformWithCanvas().Scale);
            AcceptEvent();
        }
    }

    private void On_Text_FocusExited()
    {
        if (_text.Text == Model.Text)
            return;
        Model.Text = _text.Text;
        EmitSignal(SignalName.Changed, this);
    }

    private void On_ResizeRequest(Vector2 size) => Size = size;

    private void On_ResizeEnd(Vector2 size)
    {
        Model.Width = Math.Round(Size.X);
        Model.Height = Math.Round(Size.Y);
        EmitSignal(SignalName.Changed, this);
    }

    // Also raised as it's added, before it's ready
    private void On_Resized()
    {
        if (_panel is not null)
            _panel.Size = Size;
    }

    /// <summary>Starts typing in it.</summary>
    public void FocusText() => _text.GrabFocus();

    public void UpdateLook()
    {
        if (Model is null)
            return;
        var color = Model.Color.Length > 0 ? Color.FromHtml(Model.Color) : DefaultColor;
        _style.BgColor = color.Darkened(0.55f) with { A = 0.92f };
        _style.BorderColor = Selected ? Colors.White : color;
        _style.SetBorderWidthAll(Selected ? 2 : 1);
        _strip.AddThemeColorOverride("font_color", color.Lightened(0.35f));
    }
}
