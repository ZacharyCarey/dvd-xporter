using System;
using System.IO;
using System.Threading.Tasks;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Settings;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Project packages (see <see cref="ProjectPackage"/>): a project packed into one file to share or keep, leaving out
/// this computer's working files, and unpacked into the projects folder.
/// </summary>
public partial class ProjectPackages : Node
{
    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    [Signal]
    public delegate void ErrorOccurredEventHandler(string title, string message);

    private FileDialog _exportDialog = null!;
    private ProjectFolder? _exporting;

    public override void _Ready() => _exportDialog = GetNode<FileDialog>("%ExportPackageDialog");

    /// <summary>Asks where to export <paramref name="folder"/>'s project to, beside its folder to start with.</summary>
    public void ShowExportDialog(ProjectFolder folder)
    {
        _exporting = folder;
        _exportDialog.CurrentDir = Path.GetDirectoryName(folder.FolderPath) ?? "";
        _exportDialog.CurrentFile = ProjectFolder.ToSafeFileName(folder.Project.Name) + ProjectPackage.Extension;
        _exportDialog.PopupCentered();
    }

    private async void On_ExportPackageDialog_FileSelected(string path)
    {
        var folder = _exporting;
        if (folder is null)
            return;
        if (!path.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase))
            path += ProjectPackage.Extension;

        SetStatus($"Exporting \"{folder.Project.Name}\"...");
        try
        {
            folder.Save();
            var count = await Task.Run(() => ProjectPackage.Export(folder, path));
            SetStatus($"Exported \"{folder.Project.Name}\" ({count} {(count == 1 ? "file" : "files")}) to {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("");
            EmitSignal(SignalName.ErrorOccurred, "Couldn't Export Project", ex.Message);
        }
    }

    /// <summary>Unpacks the package at <paramref name="path"/> into the projects folder. Returns its project file, or null if it couldn't be.</summary>
    public async Task<string?> ImportAsync(string path)
    {
        var location = AppSettings.Current.LastProjectLocation ?? AppPaths.DefaultProjectLocation;
        SetStatus($"Importing {Path.GetFileName(path)}...");
        try
        {
            return await Task.Run(() => ProjectPackage.Import(path, location));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("");
            EmitSignal(SignalName.ErrorOccurred, "Couldn't Import Project", ex.Message);
            return null;
        }
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);
}
