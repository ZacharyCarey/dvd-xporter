using System;
using System.IO;
using System.Linq;
using DvdXporter.Core;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The user's own library of templates and custom nodes, kept across projects: saving one to it, and adding a copy of
/// one to the project.
/// </summary>
public partial class CanvasLibraryDialog : AcceptDialog
{
    /// <summary>A copy of a library item was added to the project: it's <see cref="AddedCanvas"/>.</summary>
    [Signal]
    public delegate void CanvasAddedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>Something went wrong that the user needs to read.</summary>
    [Signal]
    public delegate void ErrorOccurredEventHandler(string message);

    /// <summary>The canvas last added to the project.</summary>
    public TranscodeCanvas? AddedCanvas { get; private set; }

    private ItemList _list = null!;
    private TranscodeLibrary _library = null!;
    private Project? _project;

    public override void _Ready()
    {
        _list = GetNode<ItemList>("%LibraryList");
        _library = TranscodeLibrary.Load(AppPaths.TranscodeLibrary);
    }

    /// <summary>Saves a template or custom node to the library (with the custom nodes it uses).</summary>
    public void Save(TranscodeCanvas canvas, Project project)
    {
        try
        {
            _library.Add(canvas, project);
            _library.Save(AppPaths.TranscodeLibrary);
            EmitSignal(SignalName.StatusChanged, $"Saved \"{canvas.Name}\" to your library.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmitSignal(SignalName.ErrorOccurred, $"Couldn't save to your library: {ex.Message}");
        }
    }

    /// <summary>Lists the library, to add from it to <paramref name="project"/>.</summary>
    public void Open(Project project)
    {
        _project = project;
        Reload();
        PopupCentered();
    }

    private void Reload()
    {
        _library = TranscodeLibrary.Load(AppPaths.TranscodeLibrary);
        _list.Clear();
        foreach (var item in _library.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name))
        {
            var index = _list.AddItem($"{item.Name}  ({TranscodeCanvas.KindName(item.Kind)})");
            _list.SetItemMetadata(index, item.Id);
        }

        if (_list.ItemCount == 0)
            _list.SetItemDisabled(_list.AddItem("Nothing yet: use Save to Library on a template or custom node."), true);
    }

    private TranscodeCanvas? SelectedItem()
    {
        if (_list.GetSelectedItems() is not { Length: > 0 } selected)
            return null;
        var id = _list.GetItemMetadata(selected[0]).AsString();
        return id is not null ? _library.Items.Find(i => i.Id == id) : null;
    }

    private void On_LibraryList_ItemActivated(long index) => AddToProject();

    private void On_AddToProjectButton_Pressed() => AddToProject();

    private void AddToProject()
    {
        var item = SelectedItem();
        if (item is null || _project is null)
            return;
        AddedCanvas = _library.AddToProject(item, _project);
        Hide();
        EmitSignal(SignalName.CanvasAdded);
    }

    private void On_RemoveFromLibraryButton_Pressed()
    {
        var item = SelectedItem();
        if (item is null)
            return;
        _library.Remove(item.Id);
        try
        {
            _library.Save(AppPaths.TranscodeLibrary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmitSignal(SignalName.ErrorOccurred, $"Couldn't save your library: {ex.Message}");
        }

        Reload();
    }
}
