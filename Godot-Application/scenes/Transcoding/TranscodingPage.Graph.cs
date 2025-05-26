using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

// Editing the graph: adding nodes (from the add menu or by dropping a palette item), connecting and disconnecting
// them, removing them, and copy and paste.
public partial class TranscodingPage
{
    /// <summary>
    /// Which kinds of port connect to which, and dropping palette items: a scene can't hold these (the graph's signals
    /// are connected in it).
    /// </summary>
    private void SetUpGraph()
    {
        // Output files take any kind of track
        foreach (var kind in new[] { PortKind.Video, PortKind.Audio, PortKind.Subtitle })
            _graph.AddValidConnectionType((int)kind, (int)PortKind.Stream);
        _graph.SetDragForwarding(
            new Callable(),
            Callable.From<Vector2, Variant, bool>((_, data) => data.VariantType == Variant.Type.Dictionary
                && data.AsGodotDictionary().ContainsKey("node_type")),
            Callable.From<Vector2, Variant>((at, data) => AddItem(data.AsGodotDictionary()["node_type"].AsString(), ToGraph(at))));
    }

    private void On_Graph_ConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) =>
        OnConnectionRequest(fromNode, fromPort, toNode, toPort);

    private void On_Graph_DisconnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) =>
        OnDisconnectionRequest(fromNode, fromPort, toNode, toPort);

    private void On_Graph_DeleteNodesRequest(Godot.Collections.Array<StringName> names) => RemoveNodes(names.Select(n => n.ToString()));

    private void On_Graph_PopupRequest(Vector2 at) => ShowAddMenu(at, null);

    private void On_Graph_ConnectionToEmpty(StringName from, long port, Vector2 at)
    {
        var view = FindView(from);
        if (view is not null && port < view.OutputKeys.Count)
            ShowAddMenu(at, (view.Model.Id, view.OutputKeys[(int)port], view.OutputKinds[(int)port]));
    }

    private void On_Graph_EndNodeMove()
    {
        foreach (var view in _views.Values)
        {
            view.Model.X = Math.Round(view.PositionOffset.X);
            view.Model.Y = Math.Round(view.PositionOffset.Y);
        }

        // Comments and frames move too
        SyncLayout();
        ScheduleSave();
    }

    private void On_Graph_NodeSelected(Node node)
    {
        if (node is TranscodeNodeView view)
            SelectJobFor(view.Model);
    }

    private void On_Graph_CopyNodesRequest() => CopySelection();

    private void On_Graph_PasteNodesRequest() => Paste();

    private void On_Graph_DuplicateNodesRequest()
    {
        CopySelection();
        Paste();
    }

    /// <summary>A point in the GraphEdit (e.g. the mouse) on the canvas, which is scrolled and zoomed.</summary>
    private Vector2 ToGraph(Vector2 local) => (local + _graph.ScrollOffset) / _graph.Zoom;

    private TranscodeNodeView? FindView(StringName name) => _graph.GetNodeOrNull<TranscodeNodeView>(name.ToString());

    private TranscodeNodeView AddView(CanvasNode node)
    {
        var view = TranscodeNodeView.Create(node.Type);
        view.Setup(node, ResolveType(node.Type) ?? UnknownType(node));
        view.TemplateRowsEditRequested += EditTemplateRows;
        view.ActionRequested += OnNodeAction;
        view.TemplateRowTestRequested += TestTemplateRow;
        view.SettingChanged += _ =>
        {
            ScheduleSave();
            Refresh();
        };
        view.TrackOptionsRequested += EditTrackOptions;
        view.ChaptersEditRequested += EditChapters;
        view.TestRequested += TestOrCancel;
        view.ContextMenuRequested += ShowNodeMenu;
        _graph.AddChild(view);
        _views[node.Id] = view;
        if (_sources is not null && _canvas is not null)
        {
            _sources.Canvas = _canvas;
            view.Rebuild(_sources, force: true);
        }

        return view;
    }

    // A node from a newer version: shown so it isn't lost, with no ports (the view says where it's from)
    private static NodeType UnknownType(CanvasNode node) => new()
    {
        Id = node.Type,
        Title = $"Unknown ({node.Type})",
        Category = NodeCategory.Combine,
    };

    private void ShowAddMenu(Vector2 at, (int Node, string Port, PortKind Kind)? connection)
    {
        if (_canvas is null)
            return;

        _addPosition = ToGraph(at);
        _addMenu.Clear(freeSubmenus: true);
        var available = AvailableTypes();
        _menuTypes = available;
        foreach (var category in Enum.GetValues<NodeCategory>())
        {
            var types = available
                .Where(t => t.Category == category && (connection is null || FirstInputFor(t, connection.Value.Kind) is not null))
                .ToList();
            if (types.Count == 0)
                continue;

            var submenu = new PopupMenu();
            foreach (var type in types)
                submenu.AddItem(type.Title, available.IndexOf(type));
            submenu.IdPressed += id =>
            {
                var pending = _pendingConnection;
                var type = _menuTypes.ElementAtOrDefault((int)id);
                if (type is not null)
                    AddNode(type, _addPosition, pending);
            };
            _addMenu.AddSubmenuNodeItem(NodeTypes.CategoryName(category), submenu);
        }

        if (connection is null)
        {
            _addMenu.AddSeparator();
            _addMenu.AddItem("Frame", AddMenuFrameId);
            _addMenu.AddItem("Comment", AddMenuCommentId);
            _addMenu.AddItem("Search...  (Space)", AddMenuSearchId);
        }

        _pendingConnection = connection;
        _addMenu.Position = (Vector2I)(_graph.GetScreenPosition() + at);
        _addMenu.ResetSize();
        _addMenu.Popup();
    }

    /// <summary>The first input of a new node of <paramref name="type"/> that takes <paramref name="kind"/>, if any.</summary>
    private string? FirstInputFor(NodeType type, PortKind kind)
    {
        if (_sources is null || _canvas is null)
            return null;
        var probe = new CanvasNode { Id = -1, Type = type.Id };
        return type.GetPorts(probe, _sources).Inputs
            .FirstOrDefault(input => input.Kind == kind || (input.Kind == PortKind.Stream && kind is PortKind.Video or PortKind.Audio or PortKind.Subtitle))?.Key;
    }

    private void AddNode(NodeType type, Vector2 at, (int Node, string Port, PortKind Kind)? connectFrom = null)
    {
        var canvas = _canvas;
        if (canvas is null || _sources is null)
            return;

        var node = NodeTypes.Create(canvas, type, Math.Round(at.X), Math.Round(at.Y));
        FillDefaults(node);
        canvas.Nodes.Add(node);
        if (connectFrom is not null)
        {
            var input = FirstInputFor(type, connectFrom.Value.Kind);
            if (input is not null)
                canvas.Links.Add(new CanvasLink(connectFrom.Value.Node, connectFrom.Value.Port, node.Id, input));
        }

        var view = AddView(node);
        ScheduleSave();
        Refresh();
        _graph.SetSelected(view);
        SelectJobFor(node);
    }

    /// <summary>Starting values that depend on the project, e.g. a new source picks the first disc that's been read.</summary>
    private void FillDefaults(CanvasNode node)
    {
        var folder = _folder;
        if (folder is null || _sources is null)
            return;

        if (NodeTypes.IsSource(node.Type))
        {
            var disc = folder.Project.Discs.FirstOrDefault(d => _sources.GetContents(d) is not null) ?? folder.Project.Discs.FirstOrDefault();
            if (disc is null)
                return;
            var source = new SourceNode(node) { Disc = disc.Fingerprint };
            // Its first playlist, or else its first video file as listed (a named one if it has any)
            source.Item = NodeTypes.SourceItemChoices(node, _sources).FirstOrDefault()?.Value ?? "";
        }
        else if (node.Type == NodeTypes.Export && folder.Project.Movie is not null)
        {
            _ = new ExportNode(node) { Target = NodeTypes.MovieTarget };
        }
        else if (node.Type == NodeTypes.Template)
        {
            var template = folder.Project.Canvases.FirstOrDefault(c => c.Kind == CanvasKind.Template);
            if (template is not null)
                _ = new TemplateNode(node) { Template = template.Id };
        }
    }

    private void OnConnectionRequest(StringName fromName, long fromPort, StringName toName, long toPort)
    {
        var canvas = _canvas;
        var from = FindView(fromName);
        var to = FindView(toName);
        if (canvas is null || from is null || to is null || from == to)
            return;
        if (fromPort < 0 || toPort < 0 || fromPort >= from.OutputKeys.Count || toPort >= to.InputKeys.Count)
            return;

        var toKey = to.InputKeys[(int)toPort];
        // An input takes one link: a new one replaces the old
        canvas.Links.RemoveAll(l => l.ToNode == to.Model.Id && l.ToPort == toKey);
        canvas.Links.Add(new CanvasLink(from.Model.Id, from.OutputKeys[(int)fromPort], to.Model.Id, toKey));
        ScheduleSave();
        Refresh();
    }

    private void OnDisconnectionRequest(StringName fromName, long fromPort, StringName toName, long toPort)
    {
        var canvas = _canvas;
        var from = FindView(fromName);
        var to = FindView(toName);
        if (canvas is null || from is null || to is null)
            return;
        if (fromPort < 0 || toPort < 0 || fromPort >= from.OutputKeys.Count || toPort >= to.InputKeys.Count)
            return;

        canvas.Links.Remove(new CanvasLink(from.Model.Id, from.OutputKeys[(int)fromPort], to.Model.Id, to.InputKeys[(int)toPort]));
        ScheduleSave();
        Refresh();
    }

    private void RemoveNodes(IEnumerable<string> names)
    {
        var canvas = _canvas;
        if (canvas is null)
            return;

        foreach (var name in RemoveFramesAndComments(names))
        {
            var view = FindView(name);
            if (view is null)
                continue;
            RemoveFromFrame(view, record: false);
            canvas.RemoveNode(view.Model.Id);
            canvas.Exposed.RemoveAll(e => e.Node == view.Model.Id);
            _views.Remove(view.Model.Id);
            _graph.RemoveChild(view);
            view.QueueFree();
        }

        ScheduleSave();
        Refresh();
    }

    private void CopySelection()
    {
        var canvas = _canvas;
        if (canvas is null)
            return;

        var selected = _views.Values.Where(v => v.Selected).Select(v => v.Model).ToList();
        if (selected.Count == 0)
            return;
        var ids = selected.Select(n => n.Id).ToHashSet();
        _clipboard = selected.Select(n => new CanvasNode
        {
            Id = n.Id,
            Type = n.Type,
            X = n.X,
            Y = n.Y,
            Settings = new Dictionary<string, string>(n.Settings),
        }).ToList();
        _clipboardLinks = canvas.Links.Where(l => ids.Contains(l.FromNode) && ids.Contains(l.ToNode)).ToList();
    }

    private void Paste()
    {
        var canvas = _canvas;
        if (canvas is null || _clipboard.Count == 0)
            return;

        var newIds = new Dictionary<int, int>();
        var added = new List<TranscodeNodeView>();
        foreach (var copy in _clipboard)
        {
            var node = new CanvasNode
            {
                Id = canvas.NextNodeId(),
                Type = copy.Type,
                X = copy.X + PasteOffset.X,
                Y = copy.Y + PasteOffset.Y,
                Settings = new Dictionary<string, string>(copy.Settings),
            };
            newIds[copy.Id] = node.Id;
            canvas.Nodes.Add(node);
            added.Add(AddView(node));
        }

        foreach (var link in _clipboardLinks)
            canvas.Links.Add(new CanvasLink(newIds[link.FromNode], link.FromPort, newIds[link.ToNode], link.ToPort));

        // Pasting again steps further along
        foreach (var copy in _clipboard)
        {
            copy.X += PasteOffset.X;
            copy.Y += PasteOffset.Y;
        }

        foreach (var view in _views.Values)
            view.Selected = added.Contains(view);
        ScheduleSave();
        Refresh();
    }
}
