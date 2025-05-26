using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

// Frames and comments, which lay out a canvas without changing what it makes. A frame is a titled, tinted box
// grouping nodes and comments: with some in it, it fits around them and moves them with it; empty, it keeps its
// own size, and anything dropped on it joins it. A comment is a note typed straight onto the canvas.
public partial class TranscodingPage
{
    /// <summary>The palette's (and quick add's) ids for a new frame and a new comment, which aren't node types.</summary>
    private const string FrameItemId = "frame";
    private const string CommentItemId = "comment";

    private static readonly Color DefaultFrameTint = new(0.45f, 0.5f, 0.6f);
    private static readonly Vector2 NewFrameSize = new(420, 260);

    private readonly Dictionary<int, GraphFrame> _frameViews = [];
    private readonly Dictionary<int, CommentView> _commentViews = [];
    private FrameDialog _frameDialog = null!;
    private PopupMenu _frameMenu = null!;
    private PopupMenu _commentMenu = null!;
    private CanvasFrame? _menuFrame;
    private CommentView? _menuComment;

    private enum FrameMenuItem
    {
        Edit,
        SelectContents,
        RemoveFrame,
        DeleteWithContents,
    }

    private enum CommentMenuItem
    {
        RemoveFromFrame = 1000,
        Delete,
    }

    private static string FrameViewName(int id) => $"f{id}";

    private void SetUpFrames()
    {
        _frameDialog = GetNode<FrameDialog>("%FrameDialog");
        _frameMenu = GetNode<PopupMenu>("%FrameMenu");
        _commentMenu = GetNode<PopupMenu>("%CommentMenu");
    }

    private void On_FrameMenu_IdPressed(long id) => OnFrameMenu((FrameMenuItem)id);

    private void On_CommentMenu_IdPressed(long id) => OnCommentMenu(id);

    /// <summary>Nodes and comments dropped onto a frame join it (Godot passes their names).</summary>
    private void On_Graph_GraphElementsLinkedToFrameRequest(Godot.Collections.Array elements, StringName frameName)
    {
        var frame = FindFrame(frameName);
        if (frame is not null)
            AddToFrame(frame, elements.Select(e => e.AsStringName().ToString()));
    }

    private void On_Graph_FrameRectChanged(GraphFrame frameView, Rect2 rect) => SyncFrame(frameView);

    private CanvasFrame? FindFrame(StringName viewName) =>
        _frameViews.FirstOrDefault(p => p.Value.Name == viewName) is { Value: not null } pair ? _canvas?.FindFrame(pair.Key) : null;

    private CanvasFrame? FrameModel(GraphFrame view) =>
        _frameViews.FirstOrDefault(p => p.Value == view) is { Value: not null } pair ? _canvas?.FindFrame(pair.Key) : null;

    /// <summary>The view of what can go in a frame, by its name: a node's or a comment's.</summary>
    private GraphElement? FrameMember(string name) => name.Length > 1 && int.TryParse(name[1..], out var id)
        ? name[0] switch
        {
            'n' => _views.GetValueOrDefault(id),
            'c' => _commentViews.GetValueOrDefault(id),
            _ => null,
        }
        : null;

    /// <summary>Shows the canvas's comments and frames (after its nodes, which frames hold).</summary>
    private void ShowFramesAndComments()
    {
        foreach (var view in _frameViews.Values.Cast<GraphElement>().Concat(_commentViews.Values))
        {
            _graph.RemoveChild(view);
            view.QueueFree();
        }

        _frameViews.Clear();
        _commentViews.Clear();
        foreach (var comment in _canvas?.Comments ?? [])
            AddCommentView(comment);
        foreach (var frame in _canvas?.Frames ?? [])
            AddFrameView(frame);
    }

    // ---- Frames ----

    private GraphFrame AddFrameView(CanvasFrame frame)
    {
        // Made per frame, so connected here
        var view = FrameScene.Instantiate<GraphFrame>();
        view.Name = FrameViewName(frame.Id);
        view.PositionOffset = new Vector2((float)frame.X, (float)frame.Y);
        view.Size = new Vector2((float)frame.Width, (float)frame.Height);
        view.ResizeRequest += size => view.Size = size;
        view.ResizeEnd += _ =>
        {
            SyncFrame(view);
            ScheduleSave();
        };
        view.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true } click)
                return;
            if (click.ButtonIndex == MouseButton.Left && click.DoubleClick)
            {
                EditFrame(frame);
                view.AcceptEvent();
            }
            else if (click.ButtonIndex == MouseButton.Right)
            {
                ShowFrameMenu(frame, view.GetScreenPosition() + click.Position * view.GetGlobalTransformWithCanvas().Scale);
                view.AcceptEvent();
            }
        };

        _graph.AddChild(view);
        _frameViews[frame.Id] = view;
        foreach (var name in frame.Nodes.Select(TranscodeNodeView.ViewName).Concat(frame.Comments.Select(CommentView.ViewName)))
        {
            var member = FrameMember(name);
            if (member is not null)
                _graph.AttachGraphElementToFrame(member.Name, view.Name);
        }

        UpdateFrameView(frame);
        return view;
    }

    private void UpdateFrameView(CanvasFrame frame)
    {
        if (!_frameViews.TryGetValue(frame.Id, out var view))
            return;
        view.Title = frame.Title.Length > 0 ? frame.Title : "Frame";
        var tint = frame.Color.Length > 0 ? Color.FromHtml(frame.Color) : DefaultFrameTint;
        view.TintColor = tint with { A = 0.35f };
        // A frame with something in it fits around it; an empty one keeps the size it's given
        view.AutoshrinkEnabled = !frame.IsEmpty;
        view.Resizable = frame.IsEmpty;
    }

    private void EditFrame(CanvasFrame frame) => _frameDialog.Edit(frame, saved =>
    {
        UpdateFrameView(saved);
        ScheduleSave();
    });

    /// <summary>Keeps a frame's place and size (worked out by Godot when it fits what's in it).</summary>
    private void SyncFrame(GraphFrame view)
    {
        var frame = FrameModel(view);
        if (frame is null)
            return;
        frame.X = Math.Round(view.PositionOffset.X);
        frame.Y = Math.Round(view.PositionOffset.Y);
        frame.Width = Math.Round(view.Size.X);
        frame.Height = Math.Round(view.Size.Y);
        ScheduleSave(record: false);
    }

    /// <summary>Keeps where comments and frames are after things moved.</summary>
    private void SyncLayout()
    {
        foreach (var comment in _commentViews.Values)
        {
            comment.Model.X = Math.Round(comment.PositionOffset.X);
            comment.Model.Y = Math.Round(comment.PositionOffset.Y);
        }

        foreach (var view in _frameViews.Values)
            SyncFrame(view);
    }

    /// <summary>A new, empty frame at a place on the canvas, made once it's named (not if that's cancelled).</summary>
    private void AddFrame(Vector2 at)
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var draft = new CanvasFrame { X = Math.Round(at.X), Y = Math.Round(at.Y), Width = NewFrameSize.X, Height = NewFrameSize.Y };
        _frameDialog.Edit(draft, frame =>
        {
            frame.Id = canvas.NextFrameId();
            canvas.Frames.Add(frame);
            AddFrameView(frame);
            ScheduleSave();
        }, "New Frame");
    }

    /// <summary>Puts the selected nodes and comments in a new frame, made once it's named (not if that's cancelled).</summary>
    private void GroupSelection()
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var names = SelectedMembers().Select(m => m.Name.ToString()).ToList();
        if (names.Count == 0)
        {
            SetStatus("Select the nodes to put in a frame first.");
            return;
        }

        _frameDialog.Edit(new CanvasFrame(), frame =>
        {
            frame.Id = canvas.NextFrameId();
            canvas.Frames.Add(frame);
            AddFrameView(frame);
            AddToFrame(frame, names);
        }, "Group in Frame");
    }

    private IEnumerable<GraphElement> SelectedMembers() =>
        _views.Values.Where(v => v.Selected).Cast<GraphElement>().Concat(_commentViews.Values.Where(c => c.Selected));

    /// <summary>Puts nodes and comments (by view name) in a frame, taking them out of any other.</summary>
    private void AddToFrame(CanvasFrame frame, IEnumerable<string> names)
    {
        if (!_frameViews.TryGetValue(frame.Id, out var frameView))
            return;
        foreach (var name in names.ToList())
        {
            switch (FrameMember(name))
            {
                case TranscodeNodeView node:
                    RemoveFromFrame(node, record: false);
                    frame.Nodes.Add(node.Model.Id);
                    break;
                case CommentView comment:
                    RemoveFromFrame(comment, record: false);
                    frame.Comments.Add(comment.Model.Id);
                    break;
                default:
                    continue;
            }

            _graph.AttachGraphElementToFrame(name, frameView.Name);
        }

        UpdateFrameView(frame);
        Callable.From(() => SyncFrame(frameView)).CallDeferred();
        ScheduleSave();
    }

    /// <summary>Takes a node or comment out of its frame, if it's in one. <paramref name="record"/>: as a change of its own to undo.</summary>
    private void RemoveFromFrame(GraphElement member, bool record = true)
    {
        var frame = member switch
        {
            TranscodeNodeView node => _canvas?.FrameOf(node.Model.Id),
            CommentView comment => _canvas?.FrameOfComment(comment.Model.Id),
            _ => null,
        };
        if (frame is null)
            return;

        if (member is TranscodeNodeView n)
            frame.Nodes.Remove(n.Model.Id);
        else if (member is CommentView c)
            frame.Comments.Remove(c.Model.Id);
        if (_graph.GetElementFrame(member.Name) is not null)
            _graph.DetachGraphElementFromFrame(member.Name);
        UpdateFrameView(frame);
        if (_frameViews.TryGetValue(frame.Id, out var frameView))
            Callable.From(() => SyncFrame(frameView)).CallDeferred();
        ScheduleSave(record);
    }

    private void UngroupSelection()
    {
        foreach (var member in SelectedMembers().ToList())
            RemoveFromFrame(member, record: false);
        ScheduleSave();
    }

    /// <summary>Removes a frame; what's in it stays where it is, unless <paramref name="withContents"/>.</summary>
    private void RemoveFrame(CanvasFrame frame, bool withContents = false, bool record = true)
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var members = frame.Nodes.Select(TranscodeNodeView.ViewName).Concat(frame.Comments.Select(CommentView.ViewName)).ToList();
        foreach (var name in members)
        {
            var member = FrameMember(name);
            if (member is not null && _graph.GetElementFrame(member.Name) is not null)
                _graph.DetachGraphElementFromFrame(member.Name);
        }

        canvas.Frames.Remove(frame);
        if (_frameViews.Remove(frame.Id, out var frameView))
        {
            _graph.RemoveChild(frameView);
            frameView.QueueFree();
        }

        if (withContents)
            RemoveNodes(members);
        else
            ScheduleSave(record);
    }

    /// <summary>Removes the frames and comments among deleted elements (by view name); returns the other names.</summary>
    private List<string> RemoveFramesAndComments(IEnumerable<string> names)
    {
        var rest = new List<string>();
        foreach (var name in names)
        {
            var frame = FindFrame(name);
            if (frame is not null)
                RemoveFrame(frame, record: false);
            else if (FrameMember(name) is CommentView comment)
                RemoveComment(comment, record: false);
            else
                rest.Add(name);
        }

        return rest;
    }

    private void ShowFrameMenu(CanvasFrame frame, Vector2 at)
    {
        _menuFrame = frame;
        _frameMenu.Clear();
        _frameMenu.AddItem("Edit...", (int)FrameMenuItem.Edit);
        if (!frame.IsEmpty)
        {
            _frameMenu.AddItem("Select What's in It", (int)FrameMenuItem.SelectContents);
            _frameMenu.AddSeparator();
            _frameMenu.AddItem("Remove Frame (Keep What's in It)", (int)FrameMenuItem.RemoveFrame);
            _frameMenu.AddItem("Delete Frame and What's in It", (int)FrameMenuItem.DeleteWithContents);
        }
        else
        {
            _frameMenu.AddSeparator();
            _frameMenu.AddItem("Delete", (int)FrameMenuItem.RemoveFrame);
        }

        _frameMenu.Position = (Vector2I)at;
        _frameMenu.ResetSize();
        _frameMenu.Popup();
    }

    private void OnFrameMenu(FrameMenuItem item)
    {
        var frame = _menuFrame;
        if (frame is null || _canvas?.Frames.Contains(frame) != true)
            return;
        switch (item)
        {
            case FrameMenuItem.Edit:
                EditFrame(frame);
                break;
            case FrameMenuItem.SelectContents:
                foreach (var (id, view) in _views)
                    view.Selected = frame.Nodes.Contains(id);
                foreach (var (id, view) in _commentViews)
                    view.Selected = frame.Comments.Contains(id);
                break;
            case FrameMenuItem.RemoveFrame:
                RemoveFrame(frame);
                break;
            case FrameMenuItem.DeleteWithContents:
                RemoveFrame(frame, withContents: true);
                break;
        }
    }

    // ---- Comments ----

    private CommentView AddCommentView(CanvasComment comment)
    {
        // Made per comment, so connected here
        var view = CommentScene.Instantiate<CommentView>();
        _graph.AddChild(view);
        view.Setup(comment);
        view.Changed += _ => ScheduleSave();
        view.ContextMenuRequested += ShowCommentMenu;
        _commentViews[comment.Id] = view;
        return view;
    }

    /// <summary>A new comment at a place on the canvas, ready to type in.</summary>
    private void AddComment(Vector2 at)
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var comment = new CanvasComment { Id = canvas.NextCommentId(), X = Math.Round(at.X), Y = Math.Round(at.Y) };
        canvas.Comments.Add(comment);
        var view = AddCommentView(comment);
        ScheduleSave();
        foreach (var other in SelectedMembers())
            other.Selected = false;
        Callable.From(view.FocusText).CallDeferred();
    }

    private void RemoveComment(CommentView view, bool record = true)
    {
        RemoveFromFrame(view, record: false);
        _canvas?.RemoveComment(view.Model.Id);
        _commentViews.Remove(view.Model.Id);
        _graph.RemoveChild(view);
        view.QueueFree();
        ScheduleSave(record);
    }

    private void ShowCommentMenu(CommentView view, Vector2 at)
    {
        _menuComment = view;
        _commentMenu.Clear(freeSubmenus: true);
        var colors = new PopupMenu();
        for (int i = 0; i < FrameDialog.Colors.Length; i++)
        {
            colors.AddRadioCheckItem(FrameDialog.Colors[i].Name, i);
            colors.SetItemChecked(i, FrameDialog.Colors[i].Hex == view.Model.Color);
        }

        colors.IdPressed += id =>
        {
            view.Model.Color = FrameDialog.Colors[(int)id].Hex;
            view.UpdateLook();
            ScheduleSave();
        };
        _commentMenu.AddSubmenuNodeItem("Colour", colors);
        if (_canvas?.FrameOfComment(view.Model.Id) is not null)
            _commentMenu.AddItem("Remove from Frame", (int)CommentMenuItem.RemoveFromFrame);
        _commentMenu.AddSeparator();
        _commentMenu.AddItem("Delete", (int)CommentMenuItem.Delete);
        _commentMenu.Position = (Vector2I)at;
        _commentMenu.ResetSize();
        _commentMenu.Popup();
    }

    private void OnCommentMenu(long id)
    {
        var view = _menuComment;
        if (view is null || !IsInstanceValid(view) || !_commentViews.ContainsValue(view))
            return;
        switch ((CommentMenuItem)id)
        {
            case CommentMenuItem.RemoveFromFrame:
                RemoveFromFrame(view);
                break;
            case CommentMenuItem.Delete:
                RemoveComment(view);
                break;
        }
    }
}
