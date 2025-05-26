using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

// Keyboard shortcuts, undo and redo, searching for nodes at the mouse, and fitting the view.
public partial class TranscodingPage
{
    private const float NudgeStep = 10;

    private readonly CanvasHistory _history = new();
    private QuickAddPopup _quickAdd = null!;
    private Vector2 _quickAddPosition;
    private AcceptDialog _shortcutsDialog = null!;

    private void SetUpKeys()
    {
        _quickAdd = GetNode<QuickAddPopup>("%QuickAddPopup");
        _shortcutsDialog = GetNode<AcceptDialog>("%ShortcutsDialog");
    }

    private void On_QuickAddPopup_Picked(string id) => AddItem(id, _quickAddPosition);

    private void On_Graph_CutNodesRequest()
    {
        CopySelection();
        RemoveNodes(_views.Values.Where(v => v.Selected).Select(v => v.Name.ToString()).ToList());
    }

    // ---- What can be added ----

    /// <summary>Everything that can go on the canvas shown, in palette order, frames last.</summary>
    private List<AddableItem> AddableItems()
    {
        var items = AvailableTypes()
            .OrderBy(t => t.Category)
            .Select(t => new AddableItem(t.Id, t.Title, NodeTypes.CategoryName(t.Category), t.Description,
                TranscodeNodeView.CategoryColor(t).Lightened(0.3f)))
            .ToList();
        if (_canvas is not null)
        {
            items.Add(new AddableItem(FrameItemId, "Frame", "Layout", "A titled, coloured box to group nodes in. Drop nodes on it to add them."));
            items.Add(new AddableItem(CommentItemId, "Comment", "Layout", "A note on the canvas, typed straight into it."));
        }

        return items;
    }

    /// <summary>Adds a node type, or a frame, at a place on the canvas.</summary>
    private void AddItem(string id, Vector2 at)
    {
        if (id == FrameItemId)
            AddFrame(at);
        else if (id == CommentItemId)
            AddComment(at);
        else
        {
            var type = ResolveType(id);
            if (type is not null)
                AddNode(type, at);
        }
    }

    private void OpenQuickAdd()
    {
        if (_canvas is null)
            return;
        var mouse = _graph.GetLocalMousePosition();
        // Over the canvas, at the mouse; otherwise in the middle
        if (!new Rect2(Vector2.Zero, _graph.Size).HasPoint(mouse))
            mouse = _graph.Size / 2;
        _quickAddPosition = ToGraph(mouse);
        _quickAdd.Open(AddableItems(), (Vector2I)(_graph.GetScreenPosition() + mouse));
    }

    // ---- Undo ----

    /// <summary>Records the canvas shown, if it changed since it was last recorded.</summary>
    private void RecordHistory()
    {
        var canvas = _canvas;
        if (canvas is not null)
            _history.Record(canvas);
    }

    private void Undo() => StepHistory(-1);

    private void Redo() => StepHistory(1);

    private void StepHistory(int by)
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        if (!_history.Step(canvas, by))
        {
            SetStatus(by < 0 ? "Nothing to undo." : "Nothing to redo.");
            return;
        }

        ShowCanvas(canvas);
        ScheduleSave(record: false);
        SetStatus(by < 0 ? "Undone." : "Redone.");
    }

    // ---- The view ----

    /// <summary>Scrolls and zooms so the selected nodes (or all, if none are or <paramref name="all"/>) fill the view.</summary>
    private void FitView(bool all = false)
    {
        var elements = all ? [] : SelectedMembers().ToList();
        if (elements.Count == 0)
            elements = _views.Values.Cast<GraphElement>().Concat(_frameViews.Values).Concat(_commentViews.Values).ToList();
        if (elements.Count == 0)
            return;

        var rect = new Rect2(elements[0].PositionOffset, elements[0].Size);
        foreach (var element in elements.Skip(1))
            rect = rect.Merge(new Rect2(element.PositionOffset, element.Size));
        rect = rect.Grow(40);

        var fit = Math.Min(_graph.Size.X / rect.Size.X, _graph.Size.Y / rect.Size.Y);
        _graph.Zoom = Math.Clamp(fit, Math.Max(_graph.ZoomMin, 0.2f), 1.25f);
        _graph.ScrollOffset = rect.GetCenter() * _graph.Zoom - _graph.Size / 2;
    }

    private void ZoomBy(float factor)
    {
        // About the middle of the view
        var middle = ToGraph(_graph.Size / 2);
        _graph.Zoom = Math.Clamp(_graph.Zoom * factor, _graph.ZoomMin, _graph.ZoomMax);
        _graph.ScrollOffset = middle * _graph.Zoom - _graph.Size / 2;
    }

    private void Nudge(Vector2 by)
    {
        var selected = SelectedMembers().ToList();
        if (selected.Count == 0)
            return;
        foreach (var view in selected)
        {
            view.PositionOffset += by;
            if (view is TranscodeNodeView node)
            {
                node.Model.X = Math.Round(node.PositionOffset.X);
                node.Model.Y = Math.Round(node.PositionOffset.Y);
            }
        }

        SyncLayout();
        ScheduleSave();
    }

    // ---- Keys ----

    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true } key || !IsVisibleInTree() || _canvas is null)
            return;

        // While typing, only saving is a shortcut (text fields have their own undo, copy and so on)
        var typing = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        bool ctrl = key.IsCommandOrControlPressed(), shift = key.ShiftPressed, alt = key.AltPressed;
        if (alt)
            return;

        Action? action = (key.Keycode, ctrl, shift) switch
        {
            (Key.S, true, false) => FlushSave,
            _ when typing => null,
            (Key.Z, true, false) => Undo,
            (Key.Z, true, true) or (Key.Y, true, false) => Redo,
            (Key.C, true, false) => CopySelection,
            (Key.X, true, false) => () =>
            {
                CopySelection();
                RemoveNodes(_views.Values.Where(v => v.Selected).Select(v => v.Name.ToString()).ToList());
            },
            (Key.V, true, false) => Paste,
            (Key.D, true, false) => () =>
            {
                CopySelection();
                Paste();
            },
            (Key.Delete, false, false) => () => RemoveNodes(SelectedElementNames()),
            (Key.A, true, false) => () => SelectAll(true),
            (Key.Escape, false, false) => () => SelectAll(false),
            (Key.G, true, false) => GroupSelection,
            (Key.G, true, true) => UngroupSelection,
            (Key.F, true, false) => _palette.FocusSearch,
            (Key.F, false, false) => () => FitView(),
            (Key.Home, false, false) => () => FitView(all: true),
            (Key.Space, false, false) or (Key.A, false, true) => OpenQuickAdd,
            (Key.Equal or Key.Plus or Key.KpAdd, true, _) => () => ZoomBy(1.2f),
            (Key.Minus or Key.KpSubtract, true, _) => () => ZoomBy(1 / 1.2f),
            (Key.Key0 or Key.Kp0, true, false) => () => _graph.Zoom = 1,
            (Key.Enter or Key.KpEnter, true, false) => TestSelected,
            (Key.F1, false, false) => () => _shortcutsDialog.PopupCentered(),
            (Key.F2, false, false) => EditSelectedFrameOrRename,
            (Key.Left, false, _) => () => Nudge(new Vector2(-NudgeStep * (shift ? 5 : 1), 0)),
            (Key.Right, false, _) => () => Nudge(new Vector2(NudgeStep * (shift ? 5 : 1), 0)),
            (Key.Up, false, _) => () => Nudge(new Vector2(0, -NudgeStep * (shift ? 5 : 1))),
            (Key.Down, false, _) => () => Nudge(new Vector2(0, NudgeStep * (shift ? 5 : 1))),
            _ => null,
        };
        if (action is null)
            return;
        action();
        GetViewport().SetInputAsHandled();
    }

    private List<string> SelectedElementNames() =>
        SelectedMembers().Concat(_frameViews.Values.Where(f => f.Selected)).Select(e => e.Name.ToString()).ToList();

    private void SelectAll(bool selected)
    {
        foreach (var view in _views.Values.Cast<GraphElement>().Concat(_commentViews.Values))
            view.Selected = selected;
        foreach (var frame in _frameViews.Values)
            frame.Selected = false;
    }

    private void TestSelected()
    {
        var view = _views.Values.FirstOrDefault(v => v.Selected);
        // The selected node's output, or else the one whose command is shown
        if (view is not null && OutputFor(view.Model) is not null)
            TestOrCancel(view);
        else
            _outputPanel.StartTest();
    }

    private void EditSelectedFrameOrRename()
    {
        var frameView = _frameViews.Values.FirstOrDefault(f => f.Selected);
        var selectedFrame = frameView is not null ? FrameModel(frameView) : null;
        // A selected node's frame counts too
        if (selectedFrame is null)
        {
            var node = _views.Values.FirstOrDefault(v => v.Selected);
            selectedFrame = node is not null ? _canvas?.FrameOf(node.Model.Id) : null;
        }
        if (selectedFrame is not null)
        {
            EditFrame(selectedFrame);
            return;
        }

        _canvasPanel.FocusName();
    }
}
