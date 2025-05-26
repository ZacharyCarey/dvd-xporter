using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscDecoders;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Picks files off one of the project's discs, besides its video (a comic as a PDF, wallpapers, a PC program), for the
/// Information step's Files: files, or whole folders. The disc is found on this computer or the render server.
/// </summary>
public partial class DiscFilesDialog : ConfirmationDialog
{
    /// <summary>Files and folders (ending "/") were picked off the disc.</summary>
    [Signal]
    public delegate void FilesPickedEventHandler(string fingerprint, string[] paths);

    private OptionButton _discChoice = null!;
    private Label _statusLabel = null!;
    private Tree _filesTree = null!;

    private ProjectFolder? _folder;
    private AppServices? _services;
    private List<ProjectDisc> _discs = [];
    // Which disc is being listed: one picked after it replaces it
    private int _listing;

    public override void _Ready()
    {
        _discChoice = GetNode<OptionButton>("%DiscChoice");
        _statusLabel = GetNode<Label>("%StatusLabel");
        _filesTree = GetNode<Tree>("%FilesTree");
        _filesTree.SetColumnExpand(1, false);
        _filesTree.SetColumnCustomMinimumWidth(1, 90);
    }

    public void Open(ProjectFolder folder, AppServices services)
    {
        _folder = folder;
        _services = services;
        _discs = [];
        _discChoice.Clear();
        foreach (var release in folder.Project.Releases)
        {
            foreach (var disc in release.Discs)
            {
                _discs.Add(disc);
                _discChoice.AddItem($"{release.Name} › {disc.Label}");
            }
        }

        PopupCentered(new Vector2I(560, 480));
        if (_discs.Count == 0)
        {
            ShowStatus("The project has no discs yet. Add them on the project overview.");
            return;
        }

        _discChoice.Select(0);
        ListFiles(_discs[0]);
    }

    private void On_DiscChoice_ItemSelected(long index)
    {
        if (index >= 0 && index < _discs.Count)
            ListFiles(_discs[(int)index]);
    }

    private async void ListFiles(ProjectDisc disc)
    {
        var folder = _folder;
        var services = _services;
        if (folder is null || services is null)
            return;

        var listing = ++_listing;
        ShowStatus($"Looking for \"{disc.Label}\"...");
        string? problem = null;
        IReadOnlyList<DiscFile> files = [];
        try
        {
            var source = (await DiscFinder.For(folder, services.Discs, services.Server.Client).GetAvailableDiscsAsync([disc]))[disc].Source;
            if (source is null)
                problem = $"\"{disc.Label}\" isn't on this computer or the render server. Use Locate on the project overview to find it.";
            else
                files = await Task.Run(source.ListOtherFiles);
        }
        catch (Exception ex) when (ex is DiscException or IOException or RenderServerException)
        {
            problem = $"Couldn't read \"{disc.Label}\": {ex.Message}";
        }

        if (listing != _listing || !IsInstanceValid(this))
            return;
        if (problem is not null)
            ShowStatus(problem);
        else if (files.Count == 0)
            ShowStatus($"\"{disc.Label}\" has no files besides its video.");
        else
            ShowFiles(files);
    }

    private void ShowStatus(string status)
    {
        _filesTree.Clear();
        _filesTree.Visible = false;
        _statusLabel.Text = status;
        GetOkButton().Disabled = true;
    }

    /// <summary>The disc's files in their folders, each file and folder with its own box to tick.</summary>
    private void ShowFiles(IReadOnlyList<DiscFile> files)
    {
        _filesTree.Clear();
        var root = _filesTree.CreateItem();
        var folders = new Dictionary<string, TreeItem> { [""] = root };
        TreeItem parentOf(string path)
        {
            var slash = path.TrimEnd('/').LastIndexOf('/');
            return slash < 0 ? root : folders[path[..(slash + 1)]];
        }

        foreach (var path in DiscFiles.Folders(files))
        {
            var item = AddRow(parentOf(path), path, path.TrimEnd('/').Split('/')[^1] + "/", DiscFiles.Under(files, path).Sum(f => f.Length));
            item.Collapsed = true;
            folders[path] = item;
        }

        foreach (var file in files)
            AddRow(parentOf(file.Path), file.Path, file.Path.Split('/')[^1], file.Length);

        _filesTree.Visible = true;
        _statusLabel.Text = $"{NodeTypes.DescribeFiles(files)}. Tick the files to add, each on its own, or folders to add whole.";
        GetOkButton().Disabled = true;
    }

    private TreeItem AddRow(TreeItem parent, string path, string name, long length)
    {
        var item = _filesTree.CreateItem(parent);
        item.SetCellMode(0, TreeItem.TreeCellMode.Check);
        item.SetEditable(0, true);
        item.SetText(0, name);
        item.SetTooltipText(0, path);
        item.SetMetadata(0, path);
        item.SetText(1, NodeTypes.FormatSize(length));
        item.SetTextAlignment(1, HorizontalAlignment.Right);
        return item;
    }

    // Each box stands for itself: a folder is copied whole, as a folder, and a file in it ticked too is copied again on its own
    private void On_FilesTree_ItemEdited() => GetOkButton().Disabled = Picked().Count == 0;

    /// <summary>Everything ticked, folders and files.</summary>
    private List<string> Picked()
    {
        var picked = new List<string>();
        void visit(TreeItem parent)
        {
            foreach (var item in parent.GetChildren())
            {
                if (item.IsChecked(0))
                    picked.Add((string)item.GetMetadata(0));
                visit(item);
            }
        }

        var root = _filesTree.GetRoot();
        if (root is not null)
            visit(root);
        return picked;
    }

    private void On_Confirmed()
    {
        var index = _discChoice.Selected;
        var picked = Picked();
        if (index >= 0 && index < _discs.Count && picked.Count > 0)
            EmitSignal(SignalName.FilesPicked, _discs[index].Fingerprint, picked.ToArray());
    }
}
