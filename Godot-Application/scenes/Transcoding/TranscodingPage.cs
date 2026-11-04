using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Step 3: transcoding graphs. Each canvas wires disc video through filters and codecs into output files,
/// which go to the movie, episodes or extras from the Information step. Outputs can be test encoded here;
/// the full encodes run in the Export step.
/// </summary>
public partial class TranscodingPage : MarginContainer, IStepPage
{
    private static readonly Vector2 PasteOffset = new(40, 40);

    [Signal]
    public delegate void BackRequestedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>A comment on the canvas (see <see cref="CommentView"/>), made for each one.</summary>
    [Export] public PackedScene CommentScene { get; set; } = null!;

    /// <summary>A frame on the canvas, made for each one.</summary>
    [Export] public PackedScene FrameScene { get; set; } = null!;

    private CanvasPanel _canvasPanel = null!;
    private NodePalette _palette = null!;
    private GraphEdit _graph = null!;
    private Control _emptyHint = null!;
    private ItemList _issueList = null!;
    private OutputPanel _outputPanel = null!;
    private AcceptDialog _errorDialog = null!;
    private Timer _saveTimer = null!;
    private PopupMenu _addMenu = null!;
    private PopupMenu _nodeMenu = null!;
    private TranscodeNodeView? _menuView;
    private TrackOptionsDialog _trackOptionsDialog = null!;
    private TemplateRowsDialog _templateRowsDialog = null!;
    private CanvasLibraryDialog _libraryDialog = null!;
    // The node types the add-node menu offers, by item id
    private List<NodeType> _menuTypes = [];
    private ChapterEditorDialog _chapterEditor = null!;

    private SubtitleTextDialog _subtitleTextDialog = null!;

    private ProjectFolder? _folder;
    private DiscServices? _discServices;
    private RenderServerLink? _server;
    private readonly Dictionary<ProjectDisc, DiscSource?> _discSources = [];
    private ProjectTranscodeSources? _sources;
    private TranscodeCanvas? _canvas;
    private readonly Dictionary<int, TranscodeNodeView> _views = [];
    private CompileResult? _result;

    // Where the add-node menu puts the node (graph coordinates), and the output port to wire it to, if any
    private Vector2 _addPosition;
    private (int Node, string Port, PortKind Kind)? _pendingConnection;

    // Nodes and the links between them, from the last copy
    private List<CanvasNode> _clipboard = [];
    private List<CanvasLink> _clipboardLinks = [];

    public override void _Ready()
    {
        _canvasPanel = GetNode<CanvasPanel>("%CanvasPanel");
        _palette = GetNode<NodePalette>("%NodePalette");
        _graph = GetNode<GraphEdit>("%Graph");
        _emptyHint = GetNode<Control>("%EmptyHint");
        _issueList = GetNode<ItemList>("%IssueList");
        _outputPanel = GetNode<OutputPanel>("%OutputPanel");
        _errorDialog = GetNode<AcceptDialog>("%ErrorDialog");
        _saveTimer = GetNode<Timer>("%SaveTimer");
        _addMenu = GetNode<PopupMenu>("%AddMenu");
        _nodeMenu = GetNode<PopupMenu>("%NodeMenu");
        _chapterEditor = GetNode<ChapterEditorDialog>("%ChapterEditorDialog");
        _trackOptionsDialog = GetNode<TrackOptionsDialog>("%TrackOptionsDialog");
        _subtitleTextDialog = GetNode<SubtitleTextDialog>("%SubtitleTextDialog");
        _templateRowsDialog = GetNode<TemplateRowsDialog>("%TemplateRowsDialog");
        _libraryDialog = GetNode<CanvasLibraryDialog>("%LibraryDialog");

        SetUpGraph();
        SetUpFrames();
        SetUpKeys();
    }

    // ---- Signals connected in the scene ----

    private void On_BackButton_Pressed() => EmitSignal(SignalName.BackRequested);

    private void On_SaveTimer_Timeout() => SaveNow();

    private void On_CanvasPanel_CanvasChosen()
    {
        var canvas = _canvasPanel.Chosen;
        if (canvas is not null && canvas != _canvas)
            ShowCanvas(canvas);
    }

    /// <summary>A new canvas, template or custom node; a template's input starts with the project's first disc.</summary>
    private void On_CanvasPanel_CanvasAdded()
    {
        var canvas = _canvasPanel.Chosen;
        if (canvas is null)
            return;
        foreach (var input in canvas.Nodes.Where(n => n.Type == NodeTypes.TemplateInput))
            FillDefaults(input);
        ScheduleSave();
        ShowCanvas(canvas);
        _canvasPanel.FocusName();
    }

    private void On_CanvasPanel_CanvasRenamed()
    {
        ScheduleSave();
        _palette.Show(AddableItems());
        Refresh();
    }

    private void On_CanvasPanel_CanvasRemoved()
    {
        var folder = _folder;
        if (folder is null)
            return;
        ScheduleSave();
        ShowCanvas(folder.Project.Canvases.First(c => c.Kind == CanvasKind.Canvas));
    }

    private void On_CanvasPanel_SaveToLibraryRequested()
    {
        if (_canvas is { Kind: not CanvasKind.Canvas } canvas && _folder is not null)
            _libraryDialog.Save(canvas, _folder.Project);
    }

    private void On_CanvasPanel_LibraryRequested()
    {
        var folder = _folder;
        if (folder is not null)
            _libraryDialog.Open(folder.Project);
    }

    private void On_LibraryDialog_CanvasAdded()
    {
        var copy = _libraryDialog.AddedCanvas;
        if (copy is null)
            return;
        ScheduleSave();
        ShowCanvas(copy);
        SetStatus($"Added \"{copy.Name}\" to the project.");
    }

    /// <summary>Double-clicking an item (or Enter in the search) adds it in the middle of the view.</summary>
    private void On_NodePalette_ItemChosen(string id) => AddItem(id, ToGraph(_graph.Size / 2) - new Vector2(120, 60));

    private void On_NodePalette_Dismissed() => _graph.GrabFocus();

    private void On_OutputPanel_TestStateChanged() => UpdateNodeTestButtons();

    private void On_Panel_StatusChanged(string status) => SetStatus(status);

    private void On_Panel_ErrorOccurred(string message) => ShowError(message);

    /// <summary>A dialog changed a node's settings: chapters, track options or a template's videos.</summary>
    private void On_Dialog_Applied()
    {
        ScheduleSave();
        Refresh();
    }

    private void On_TemplateRowsDialog_DetachRequested(int nodeId, int row)
    {
        var node = _canvas?.FindNode(nodeId);
        if (node is not null)
            DetachTemplateRow(node, row);
    }

    // A block, not an expression: an expression lambda would hand back the tuple, which can't be a Variant
    private void On_AddMenu_PopupHide() => Callable.From(() => { _pendingConnection = null; }).CallDeferred();

    private void On_AddMenu_IdPressed(long id)
    {
        if (id == AddMenuFrameId)
            AddFrame(_addPosition);
        else if (id == AddMenuCommentId)
            AddComment(_addPosition);
        else if (id == AddMenuSearchId)
        {
            _quickAddPosition = _addPosition;
            _quickAdd.Open(AddableItems(), _addMenu.Position);
        }
    }

    private void On_NodeMenu_IdPressed(long id) => OnNodeMenu((NodeMenuItem)id);

    private void On_IssueList_ItemSelected(long index) => FocusIssue((int)index);

    public override void _ExitTree() => FlushSave();

    public void ShowProject(ProjectFolder folder, DiscServices discServices, RenderServerLink server)
    {
        FlushSave();
        if (folder != _folder)
        {
            _outputPanel.Forget();
            _canvas = null;
        }

        _folder = folder;
        _discServices = discServices;
        _server = server;
        _discSources.Clear();
        _sources = new ProjectTranscodeSources(folder, _discSources);
        _outputPanel.SetProject(folder, _sources);

        if (folder.Project.Canvases.All(c => c.Kind != CanvasKind.Canvas))
        {
            folder.Project.AddCanvas(CanvasKind.Canvas);
            ScheduleSave();
        }

        ShowCanvas(_canvas is not null && folder.Project.Canvases.Contains(_canvas) ? _canvas
            : folder.Project.Canvases.First(c => c.Kind == CanvasKind.Canvas));
        FindDiscs(folder);
    }

    public void FlushSave()
    {
        if (!_saveTimer.IsStopped())
        {
            _saveTimer.Stop();
            SaveNow();
        }
    }

    /// <summary>Saves soon. <paramref name="record"/>: the change is one to undo (not, e.g., a frame fitting its nodes).</summary>
    private void ScheduleSave(bool record = true)
    {
        if (record)
            RecordHistory();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try
        {
            _folder?.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Couldn't save the project: {ex.Message}");
        }
    }

    /// <summary>
    /// Works out where each disc is, in the background: on this computer, or the render server (streamed from it, so
    /// its videos can still be previewed and tested).
    /// </summary>
    private async void FindDiscs(ProjectFolder folder)
    {
        var services = _discServices;
        if (services is null)
            return;

        var found = await DiscFinder.For(folder, services, _server?.Client).GetAvailableDiscsAsync(folder.Project.Discs.ToList());
        if (folder != _folder)
            return;
        _discSources.Clear();
        foreach (var (disc, resolution) in found)
            _discSources[disc] = resolution.Source;
        _sources?.Refresh();
        Refresh();

        var streamed = found.Where(f => f.Value.Source is { IsHere: false }).ToList();
        if (streamed.Count > 0)
        {
            SetStatus($"{string.Join(", ", streamed.Select(f => $"\"{f.Key.Label}\""))} {(streamed.Count == 1 ? "isn't" : "aren't")} on this computer, "
                + $"so {(streamed.Count == 1 ? "it's" : "they're")} read from \"{streamed[0].Value.Source!.ServerName}\".");
        }
    }

    // ---- Canvases ----

    private void ShowCanvas(TranscodeCanvas? canvas)
    {
        _canvas = canvas;
        _canvasPanel.Show(_folder?.Project, canvas);
        _outputPanel.ClearSelection();
        _palette.Show(AddableItems());

        _graph.ClearConnections();
        foreach (var view in _views.Values)
        {
            _graph.RemoveChild(view);
            view.QueueFree();
        }

        _views.Clear();
        foreach (var node in canvas?.Nodes ?? [])
            AddView(node);
        ShowFramesAndComments();
        if (canvas is not null)
            _history.Start(canvas);
        Refresh();
    }

    // ---- Node types ----

    /// <summary>
    /// The node types that can go on the canvas shown: the built-in ones its kind allows, and the project's
    /// custom nodes (except the one being edited, which can't contain itself).
    /// </summary>
    private List<NodeType> AvailableTypes()
    {
        var folder = _folder;
        var canvas = _canvas;
        if (folder is null || canvas is null)
            return [];
        var types = NodeTypes.All.Where(t => t.IsAllowedIn(canvas.Kind)).ToList();
        foreach (var custom in folder.Project.Canvases.Where(c => c.Kind == CanvasKind.CustomNode && c != canvas))
        {
            var type = NodeTypes.Resolve(CustomNodes.TypeId(custom), folder.Project);
            if (type is not null)
                types.Add(type);
        }

        return types;
    }

    private NodeType? ResolveType(string id) => _folder is not null ? NodeTypes.Resolve(id, _folder.Project) : NodeTypes.Get(id);

    // ---- Refreshing ----

    /// <summary>
    /// Brings the canvas up to date with its model: re-lays out nodes whose rows changed, reconnects links,
    /// compiles the outputs, and shows problems and the selected output's command.
    /// </summary>
    private void Refresh()
    {
        var canvas = _canvas;
        if (canvas is null || _sources is null)
            return;

        _sources.Canvas = canvas;
        foreach (var view in _views.Values)
        {
            // A custom node's ports and settings follow its definition
            if (view.Model.Type.StartsWith(NodeTypes.CustomPrefix, StringComparison.Ordinal))
                view.SetType(ResolveType(view.Model.Type) ?? UnknownType(view.Model));
            view.ExposedKeys.Clear();
            foreach (var exposed in canvas.Exposed.Where(e => e.Node == view.Model.Id))
                view.ExposedKeys.Add(exposed.Key);
            view.Rebuild(_sources);
        }
        ReconnectLinks(canvas);

        _result = TranscodeCompiler.Compile(canvas, _sources, _sources);
        foreach (var view in _views.Values)
            view.SetIssues(_result.Issues.Where(i => i.NodeId == view.Model.Id));
        foreach (var job in _result.Jobs)
        {
            if (_views.TryGetValue(job.Output.Id, out var view))
            {
                view.SetTrackDescriptions(job.TrackPorts.Zip(job.Tracks)
                    .ToDictionary(p => p.First, p => DescribeTrack(p.Second)));
            }
        }

        _emptyHint.Visible = canvas.Nodes.Count == 0;
        RebuildIssueList(canvas);
        _outputPanel.Show(_result, canvas);
    }

    private static string DescribeTrack(OutputTrack track)
    {
        var tags = new List<string>();
        var title = track.Title;
        if (title is not null)
            tags.Add($"\"{title}\"");
        if (track.Default)
            tags.Add("default");
        if (track.Forced)
            tags.Add("forced");
        if (track.HearingImpaired)
            tags.Add("SDH");
        if (track.VisualImpaired)
            tags.Add("audio description");
        if (track.Descriptions)
            tags.Add("text descriptions");
        if (track.Original)
            tags.Add("original");
        if (track.Commentary)
            tags.Add("commentary");
        var language = track.Language;
        if (language is not null)
            tags.Add(language);
        return tags.Count > 0 ? $"{track.Description} ({string.Join(", ", tags)})" : track.Description;
    }

    private void ReconnectLinks(TranscodeCanvas canvas)
    {
        _graph.ClearConnections();
        foreach (var link in canvas.Links)
        {
            if (!_views.TryGetValue(link.FromNode, out var from) || !_views.TryGetValue(link.ToNode, out var to))
                continue;
            int fromPort = from.OutputKeys.IndexOf(link.FromPort), toPort = to.InputKeys.IndexOf(link.ToPort);
            if (fromPort >= 0 && toPort >= 0)
                _graph.ConnectNode(from.Name, fromPort, to.Name, toPort);
        }
    }

    private void RebuildIssueList(TranscodeCanvas canvas)
    {
        _issueList.Clear();
        foreach (var issue in _result?.Issues ?? [])
        {
            var node = issue.NodeId is not null ? canvas.FindNode(issue.NodeId.Value) : null;
            var where = node is not null ? $"{ResolveType(node.Type)?.Title ?? node.Type}: " : "";
            var index = _issueList.AddItem(TranscodeNodeView.IssueIcon(issue.Level) + " " + where + issue.Message);
            _issueList.SetItemMetadata(index, issue.NodeId ?? -1);
            _issueList.SetItemCustomFgColor(index, TranscodeNodeView.IssueColor(issue.Level));
        }

        if (_issueList.ItemCount == 0)
        {
            var index = _issueList.AddItem(canvas.Nodes.Count == 0 ? "Nothing on this canvas yet." : "No problems.");
            _issueList.SetItemMetadata(index, -1);
            _issueList.SetItemDisabled(index, true);
        }
    }

    private void FocusIssue(int index)
    {
        if (_issueList.GetItemMetadata(index).AsInt32() is var id && id >= 0)
            FocusNode(id);
    }

    private void FocusNode(int id)
    {
        if (!_views.TryGetValue(id, out var view))
            return;
        foreach (var other in _views.Values)
            other.Selected = other == view;
        _graph.ScrollOffset = view.PositionOffset * _graph.Zoom - _graph.Size / 2 + view.Size * _graph.Zoom / 2;
    }

    /// <summary>Shows one of the project's canvases (after <see cref="ShowProject"/>), with a node on it picked out.</summary>
    public void ShowNode(TranscodeCanvas canvas, int? nodeId)
    {
        if (_folder?.Project.Canvases.Contains(canvas) != true)
            return;
        if (canvas != _canvas)
            ShowCanvas(canvas);
        // Once the nodes have their sizes
        if (nodeId is not null)
            Callable.From(() => FocusNode(nodeId.Value)).CallDeferred();
    }

    // ---- Outputs and test encodes ----

    /// <summary>Picking an output file, or the export it goes to, shows its command.</summary>
    private void SelectJobFor(CanvasNode node)
    {
        if (NodeTypes.IsOutput(node.Type))
            _outputPanel.Select(node.Id);
        else if (node.Type == NodeTypes.Export)
        {
            var link = _canvas?.LinkInto(node.Id, "file");
            if (link is not null)
                _outputPanel.Select(link.FromNode);
        }
    }

    /// <summary>The output an Output File or Export node tests: itself, or the output wired into the export.</summary>
    private int? OutputFor(CanvasNode node) => node.Type switch
    {
        NodeTypes.OutputFile or NodeTypes.ImageGallery => node.Id,
        NodeTypes.Export => _canvas?.LinkInto(node.Id, "file")?.FromNode,
        _ => null,
    };

    /// <summary>Shows on each Output File and Export node whether it can be tested, and the running test's progress.</summary>
    private void UpdateNodeTestButtons()
    {
        var running = _outputPanel.IsTesting;
        var range = _outputPanel.TestRangeText;
        foreach (var view in _views.Values)
        {
            if (view.Model.Type == NodeTypes.Template)
            {
                var rows = new TemplateNode(view.Model).Rows;
                for (int row = 0; row < rows.Count; row++)
                {
                    var rowOutput = RowOutput(view.Model, row);
                    var rowJob = _result?.Jobs.Find(j => j.Output.Id == rowOutput);
                    var rowBlocker = running ? "Another test encode is running." : _outputPanel.TestBlocker(rowJob);
                    view.SetRowTestState(row, rowBlocker is null, rowBlocker ?? $"Test encode this one ({range})",
                        running && rowOutput is not null && _outputPanel.TestingOutput == rowOutput ? _outputPanel.TestFraction : null);
                }

                continue;
            }

            var output = OutputFor(view.Model);
            if (output is null)
            {
                view.SetTestState(false, "Connect an Output File to this export first.", null);
                continue;
            }

            var job = _result?.Jobs.Find(j => j.Output.Id == output.Value);
            var blocker = running ? "Another test encode is running." : _outputPanel.TestBlocker(job);
            view.SetTestState(blocker is null, blocker ?? $"Encode part of this output ({range}; set in the panel below) to check its quality and sync.",
                running && _outputPanel.TestingOutput == output.Value ? _outputPanel.TestFraction : null);
        }
    }

    /// <summary>A node's Test Encode button: tests its output, or cancels the test already running for it.</summary>
    private void TestOrCancel(TranscodeNodeView view)
    {
        var output = OutputFor(view.Model);
        if (output is not null)
            _outputPanel.TestOrCancel(output.Value);
    }

    private enum NodeMenuItem
    {
        TestEncode,
        ShowCommand,
        EditVideos,
        Duplicate,
        Delete,
        Group,
        Ungroup,
    }

    // Ids past any node type's in the add menu
    private const int AddMenuFrameId = 100_000;
    private const int AddMenuSearchId = 100_001;
    private const int AddMenuCommentId = 100_002;

    /// <summary>A Ctrl (Cmd on macOS) shortcut, to show beside a menu item.</summary>
    private static Shortcut KeyShortcut(Key key, bool shift = false) =>
        new() { Events = [new InputEventKey { Keycode = key, CommandOrControlAutoremap = true, ShiftPressed = shift }] };

    private void ShowNodeMenu(TranscodeNodeView view, Vector2 at)
    {
        _menuView = view;
        foreach (var other in _views.Values)
            other.Selected = other == view;

        _nodeMenu.Clear(freeSubmenus: true);
        var output = OutputFor(view.Model);
        if (output is not null)
        {
            var running = _outputPanel.IsTesting && _outputPanel.TestingOutput == output.Value;
            var blocker = _outputPanel.IsTesting && !running ? "Another test encode is running."
                : _outputPanel.TestBlocker(_result?.Jobs.Find(j => j.Output.Id == output.Value));
            _nodeMenu.AddItem(running ? "Cancel Test Encode" : "Test Encode", (int)NodeMenuItem.TestEncode);
            _nodeMenu.SetItemDisabled(_nodeMenu.ItemCount - 1, !running && blocker is not null);
            if (!running && blocker is not null)
                _nodeMenu.SetItemTooltip(_nodeMenu.ItemCount - 1, blocker);
            _nodeMenu.AddItem("Show Command", (int)NodeMenuItem.ShowCommand);
            _nodeMenu.AddSeparator();
        }

        if (view.Model.Type == NodeTypes.Template)
        {
            _nodeMenu.AddItem("Edit Videos...", (int)NodeMenuItem.EditVideos);
            _nodeMenu.AddSeparator();
        }

        // Inside a custom node: choose which of this node's settings show on the custom node
        if (_canvas?.Kind == CanvasKind.CustomNode && view.Type.Settings.Where(st => CustomNodes.CanExpose(st, view.Model.Type) && st.IsShown(view.Model, CanvasKind.CustomNode)).ToList() is { Count: > 0 } exposable)
        {
            var submenu = new PopupMenu();
            for (int i = 0; i < exposable.Count; i++)
            {
                submenu.AddCheckItem(exposable[i].Label, i);
                submenu.SetItemChecked(i, _canvas.Exposed.Contains(new ExposedSetting(view.Model.Id, exposable[i].Key)));
            }

            submenu.IdPressed += id => ToggleExposed(view.Model, exposable[(int)id].Key);
            _nodeMenu.AddSubmenuNodeItem("Show on Custom Node", submenu);
            _nodeMenu.AddSeparator();
        }

        if (_canvas?.FrameOf(view.Model.Id) is not null)
            _nodeMenu.AddItem("Remove from Frame", (int)NodeMenuItem.Ungroup);
        else
            _nodeMenu.AddItem("Group in Frame", (int)NodeMenuItem.Group);
        _nodeMenu.SetItemShortcut(_nodeMenu.ItemCount - 1, KeyShortcut(Key.G, shift: _canvas?.FrameOf(view.Model.Id) is not null));
        _nodeMenu.AddItem("Duplicate", (int)NodeMenuItem.Duplicate);
        _nodeMenu.SetItemShortcut(_nodeMenu.ItemCount - 1, KeyShortcut(Key.D));
        _nodeMenu.AddItem("Delete", (int)NodeMenuItem.Delete);
        _nodeMenu.Position = (Vector2I)at;
        _nodeMenu.ResetSize();
        _nodeMenu.Popup();
    }

    private void OnNodeMenu(NodeMenuItem item)
    {
        var view = _menuView;
        if (view is null || !IsInstanceValid(view))
            return;

        switch (item)
        {
            case NodeMenuItem.TestEncode:
                TestOrCancel(view);
                break;
            case NodeMenuItem.ShowCommand:
                var output = OutputFor(view.Model);
                if (output is not null)
                    _outputPanel.Select(output.Value);
                break;
            case NodeMenuItem.EditVideos:
                EditTemplateRows(view);
                break;
            case NodeMenuItem.Duplicate:
                CopySelection();
                Paste();
                break;
            case NodeMenuItem.Delete:
                RemoveNodes([view.Name.ToString()]);
                break;
            case NodeMenuItem.Group:
                GroupSelection();
                break;
            case NodeMenuItem.Ungroup:
                RemoveFromFrame(view);
                break;
        }
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    private void ShowError(string message)
    {
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    // ---- As a step page ----

    public ProjectStep Step => ProjectStep.Transcoding;

    void IStepPage.ShowProject(ProjectFolder folder, AppServices services) => ShowProject(folder, services.Discs, services.Server);

    void IStepPage.Leave() => FlushSave();

    void IStepPage.OnDiscLibraryChanged()
    {
    }

    /// <summary>Discs that aren't here may be streamed from another render server now, or none.</summary>
    void IStepPage.OnSettingsChanged()
    {
        if (_folder is not null)
            FindDiscs(_folder);
    }
}
