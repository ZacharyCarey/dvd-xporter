using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>Asks where a disc is: its folder, or its disc image.</summary>
public partial class LocateDiscPicker : Node
{
    private enum MenuId
    {
        Folder,
        Image,
    }

    /// <summary>A folder or image was picked for <see cref="Disc"/>.</summary>
    [Signal]
    public delegate void PathChosenEventHandler(string path);

    /// <summary>The disc being located.</summary>
    public ProjectDisc? Disc { get; private set; }

    private FileDialog _folderDialog = null!;
    private FileDialog _imageDialog = null!;

    public override void _Ready()
    {
        _folderDialog = GetNode<FileDialog>("%FolderDialog");
        _imageDialog = GetNode<FileDialog>("%ImageDialog");
    }

    /// <summary>Shows the menu of where to look at <paramref name="at"/>.</summary>
    public void ShowMenu(ProjectDisc disc, Rect2I at)
    {
        Disc = disc;
        GetNode<PopupMenu>("%Menu").Popup(at);
    }

    private void On_Menu_IdPressed(long id)
    {
        if ((MenuId)id == MenuId.Folder)
            _folderDialog.PopupCentered();
        else
            _imageDialog.PopupCentered();
    }

    private void On_Dialog_PathSelected(string path) => EmitSignal(SignalName.PathChosen, path);
}
