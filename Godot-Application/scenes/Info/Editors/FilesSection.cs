using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The movie's or show's files that aren't videos (a comic as a PDF, wallpapers, a PC program), copied as they are into
/// a Files folder beside it: off the project's discs, or from this computer (copied into the project).
/// </summary>
public partial class FilesSection : InfoSection
{
    private enum FileColumn
    {
        Name,
        From,
        Remove,
    }

    [Export] public Texture2D RemoveIcon { get; set; } = null!;

    private Tree _table = null!;
    private TitleInfo? _info;
    private ProjectFolder? _folder;
    private AppServices? _services;
    private readonly Dictionary<ulong, TitleFile> _filesByRow = [];

    public override void _Ready()
    {
        _table = GetNode<Tree>("%FilesTable");
        // Column titles and widths can't be set in a scene (the column count is in it)
        _table.SetColumnTitle((int)FileColumn.Name, "Name");
        _table.SetColumnTitle((int)FileColumn.From, "From");
        _table.SetColumnExpand((int)FileColumn.From, false);
        _table.SetColumnCustomMinimumWidth((int)FileColumn.From, 200);
        _table.SetColumnExpand((int)FileColumn.Remove, false);
        _table.SetColumnCustomMinimumWidth((int)FileColumn.Remove, 40);
    }

    public void Setup(TitleInfo info, ProjectFolder folder, AppServices services)
    {
        _info = info;
        _folder = folder;
        _services = services;
        FillRows();
    }

    private void FillRows()
    {
        _table.Clear();
        _filesByRow.Clear();
        var info = _info;
        if (info is null)
            return;

        _table.CustomMinimumSize = new Vector2(0, Mathf.Clamp(40 + 30 * info.Files.Count, 90, 380));
        _table.Visible = info.Files.Count > 0;
        var root = _table.CreateItem();
        foreach (var file in info.Files)
        {
            var item = _table.CreateItem(root);
            _filesByRow[item.GetInstanceId()] = file;
            var disc = file.IsAdded ? null : _folder?.Project.FindDisc(file.Disc!);
            item.SetText((int)FileColumn.Name, file.IsFolder ? $"{file.Name}/" : file.Name);
            item.SetTooltipText((int)FileColumn.Name, file.Path);
            item.SetText((int)FileColumn.From, file.IsAdded ? "Project files" : disc is not null ? disc.Label : "A removed disc");
            item.SetTooltipText((int)FileColumn.From, file.IsAdded ? "Added manually, and kept in the project"
                : disc is not null ? $"\"{file.Path}\" on \"{disc.Label}\"" : $"\"{file.Path}\", on a disc that's been removed from the project");
            item.AddButton((int)FileColumn.Remove, RemoveIcon, 0, false, "Remove this file");
        }
    }

    private void On_FilesTable_ButtonClicked(TreeItem item, long column, long id, long mouseButtonIndex)
    {
        // Deferred: removing refills the table, which would free the row mid-click
        if (_filesByRow.TryGetValue(item.GetInstanceId(), out var file))
            Callable.From(() => Remove(file)).CallDeferred();
    }

    private void Remove(TitleFile file)
    {
        var info = _info;
        var folder = _folder;
        if (info is null || folder is null)
            return;
        info.Files.Remove(file);
        FillRows();
        MarkEdited();
        SetStatus($"Removed \"{file.Name}\".");

        // One added from this computer goes from the project too, once nothing uses it
        if (!file.IsAdded || info.Files.Any(f => f.IsAdded && f.Path == file.Path))
            return;
        try
        {
            folder.Files.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Removed \"{file.Name}\", but couldn't delete it from the project's files: {ex.Message}");
        }
    }

    // ---- Adding files ----

    private void On_AddFromDiscButton_Pressed()
    {
        var folder = _folder;
        var services = _services;
        if (folder is not null && services is not null)
            GetNode<DiscFilesDialog>("%DiscFilesDialog").Open(folder, services);
    }

    private void On_AddFromComputerButton_Pressed() => GetNode<FileDialog>("%AddFilesDialog").PopupCentered();

    private void On_DiscFilesDialog_FilesPicked(string fingerprint, string[] paths)
    {
        var added = paths.Count(path => Add(new TitleFile { Disc = fingerprint, Path = path }));
        FillRows();
        MarkEdited();
        SetStatus(added == 1 ? "Added 1 file or folder." : $"Added {added} files or folders.");
    }

    /// <summary>Copies the files picked into the project (a big one takes a while), and adds them.</summary>
    private async void On_AddFilesDialog_FilesSelected(string[] paths)
    {
        var folder = _folder;
        if (folder is null)
            return;

        SetStatus(paths.Length == 1 ? $"Copying \"{Path.GetFileName(paths[0])}\" into the project..." : $"Copying {paths.Length} files into the project...");
        var names = new List<string>();
        string? problem = null;
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    names.Add(folder.Files.Import(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problem = $"Couldn't copy \"{Path.GetFileName(path)}\" into the project: {ex.Message}";
                }
            }
        });

        if (folder != _folder || !IsInstanceValid(this))
            return;
        var added = names.Count(name => Add(new TitleFile { Path = name }));
        FillRows();
        MarkEdited();
        SetStatus(problem ?? (added == 1 ? "Added 1 file." : $"Added {added} files."));
    }

    /// <summary>Adds a file, unless it's there already.</summary>
    private bool Add(TitleFile file)
    {
        var info = _info;
        if (info is null || info.Files.Any(f => f.Disc == file.Disc && f.Path == file.Path))
            return false;
        info.Files.Add(file);
        return true;
    }
}
