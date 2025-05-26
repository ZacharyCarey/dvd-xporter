using System;
using System.Linq;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The project's canvases, templates and custom nodes, each kind under its own heading, with the one shown named and
/// removed here, and added to or from the user's library.
/// </summary>
public partial class CanvasPanel : VBoxContainer
{
    private static readonly Color HeadingColor = new(1, 1, 1, 0.6f);

    /// <summary>Another canvas was picked to show: it's <see cref="Chosen"/>.</summary>
    [Signal]
    public delegate void CanvasChosenEventHandler();

    /// <summary>A canvas was added to the project, to show: it's <see cref="Chosen"/>.</summary>
    [Signal]
    public delegate void CanvasAddedEventHandler();

    [Signal]
    public delegate void CanvasRenamedEventHandler();

    /// <summary>The canvas shown was taken out of the project.</summary>
    [Signal]
    public delegate void CanvasRemovedEventHandler();

    [Signal]
    public delegate void SaveToLibraryRequestedEventHandler();

    [Signal]
    public delegate void LibraryRequestedEventHandler();

    /// <summary>The canvas last picked or added.</summary>
    public TranscodeCanvas? Chosen { get; private set; }

    private Tree _list = null!;
    private Button _newButton = null!;
    private PopupMenu _newMenu = null!;
    private LineEdit _nameEdit = null!;
    private Button _removeButton = null!;
    private Button _saveToLibraryButton = null!;
    private ConfirmationDialog _removeDialog = null!;

    private Project? _project;
    private TranscodeCanvas? _canvas;

    public override void _Ready()
    {
        _list = GetNode<Tree>("%CanvasList");
        _newButton = GetNode<Button>("%NewCanvasButton");
        _newMenu = GetNode<PopupMenu>("%NewCanvasMenu");
        _nameEdit = GetNode<LineEdit>("%CanvasNameEdit");
        _removeButton = GetNode<Button>("%RemoveCanvasButton");
        _saveToLibraryButton = GetNode<Button>("%SaveToLibraryButton");
        _removeDialog = GetNode<ConfirmationDialog>("%RemoveCanvasDialog");
    }

    /// <summary>Lists <paramref name="project"/>'s canvases, with <paramref name="canvas"/> as the one shown.</summary>
    public void Show(Project? project, TranscodeCanvas? canvas)
    {
        _project = project;
        _canvas = canvas;
        _nameEdit.Text = canvas?.Name ?? "";
        _nameEdit.Editable = canvas is not null;
        RebuildList();
    }

    /// <summary>Starts renaming the canvas shown.</summary>
    public void FocusName()
    {
        _nameEdit.GrabFocus();
        _nameEdit.SelectAll();
    }

    private static string KindHeading(CanvasKind kind) => kind switch
    {
        CanvasKind.Template => "Templates",
        CanvasKind.CustomNode => "Custom Nodes",
        _ => "Canvases",
    };

    private void RebuildList()
    {
        _list.Clear();
        var root = _list.CreateItem();
        foreach (var kind in Enum.GetValues<CanvasKind>())
        {
            var canvases = _project?.Canvases.Where(c => c.Kind == kind).ToList() ?? [];
            if (canvases.Count == 0 && kind != CanvasKind.Canvas)
                continue;

            var heading = _list.CreateItem(root);
            heading.SetText(0, KindHeading(kind));
            heading.SetSelectable(0, false);
            heading.SetCustomColor(0, HeadingColor);
            foreach (var canvas in canvases)
            {
                var item = _list.CreateItem(heading);
                item.SetText(0, canvas.Name);
                item.SetMetadata(0, canvas.Id);
                if (canvas == _canvas)
                    item.Select(0);
            }
        }

        // There's always an ordinary canvas to come back to
        _removeButton.Disabled = _canvas is null || (_canvas.Kind == CanvasKind.Canvas && !HasOtherCanvas());
        _saveToLibraryButton.Disabled = _canvas is null || _canvas.Kind == CanvasKind.Canvas;
    }

    private bool HasOtherCanvas() => _project?.Canvases.Count(c => c.Kind == CanvasKind.Canvas) > 1;

    private void On_CanvasList_ItemSelected()
    {
        // The list is rebuilt when a canvas is shown, which can't happen while it's telling us about a selection
        var id = _list.GetSelected()?.GetMetadata(0).AsString();
        var canvas = _project?.Canvases.Find(c => c.Id == id);
        if (canvas is null || canvas == _canvas)
            return;

        Chosen = canvas;
        Callable.From(() => EmitSignal(SignalName.CanvasChosen)).CallDeferred();
    }

    private void On_NewCanvasButton_Pressed()
    {
        var below = _newButton.GetScreenPosition() + new Vector2(0, _newButton.Size.Y);
        _newMenu.Popup(new Rect2I((Vector2I)below, Vector2I.Zero));
    }

    private void On_NewCanvasMenu_IdPressed(long id)
    {
        if (_project is null)
            return;
        Chosen = _project.AddCanvas((CanvasKind)(int)id);
        EmitSignal(SignalName.CanvasAdded);
    }

    private void On_CanvasNameEdit_TextSubmitted(string text) => Rename();

    private void On_CanvasNameEdit_FocusExited() => Rename();

    private void Rename()
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var name = _nameEdit.Text.Trim();
        if (name.Length == 0 || name == canvas.Name)
        {
            _nameEdit.Text = canvas.Name;
            return;
        }

        canvas.Name = name;
        RebuildList();
        EmitSignal(SignalName.CanvasRenamed);
    }

    private void On_RemoveCanvasButton_Pressed()
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var outputs = canvas.Nodes.Count(n => NodeTypes.IsOutput(n.Type));
        var text = canvas.Kind == CanvasKind.Canvas && outputs > 0
            ? $"Remove \"{canvas.Name}\" and its {outputs} {(outputs == 1 ? "output" : "outputs")}?"
            : $"Remove \"{canvas.Name}\"?";

        // Say what would break: Template nodes using a template, or uses of a custom node
        var uses = _project?.Canvases.Where(c => c != canvas).Sum(c => c.Nodes.Count(n =>
            (canvas.Kind == CanvasKind.Template && n.Type == NodeTypes.Template && new TemplateNode(n).Template == canvas.Id)
            || (canvas.Kind == CanvasKind.CustomNode && n.Type == CustomNodes.TypeId(canvas)))) ?? 0;
        if (uses > 0)
            text += $"\n\nIt's used {uses} {(uses == 1 ? "time" : "times")} in this project; those nodes will stop working.";
        _removeDialog.DialogText = text;
        _removeDialog.PopupCentered();
    }

    private void On_RemoveCanvasDialog_Confirmed()
    {
        var project = _project;
        var canvas = _canvas;
        if (project is null || canvas is null)
            return;
        if (canvas.Kind == CanvasKind.Canvas && !HasOtherCanvas())
            return;
        project.Canvases.Remove(canvas);
        EmitSignal(SignalName.CanvasRemoved);
    }

    private void On_SaveToLibraryButton_Pressed() => EmitSignal(SignalName.SaveToLibraryRequested);

    private void On_LibraryButton_Pressed() => EmitSignal(SignalName.LibraryRequested);
}
