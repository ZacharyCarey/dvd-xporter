using System.IO;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Asks for the name and location of a new movie or TV show project.
/// </summary>
public partial class NewProjectDialog : ConfirmationDialog
{
    private static readonly Color ErrorColor = new(0.89f, 0.44f, 0.42f);

    private Label _promptLabel = null!;
    private LineEdit _nameEdit = null!;
    private LineEdit _locationEdit = null!;
    private Label _previewLabel = null!;
    private FileDialog _locationDialog = null!;

    public ProjectKind Kind { get; private set; }

    public string ProjectName => _nameEdit.Text.Trim();

    public string ProjectLocation => _locationEdit.Text.Trim();

    public override void _Ready()
    {
        _promptLabel = GetNode<Label>("%PromptLabel");
        _nameEdit = GetNode<LineEdit>("%NameEdit");
        _locationEdit = GetNode<LineEdit>("%LocationEdit");
        _previewLabel = GetNode<Label>("%PreviewLabel");
        _locationDialog = GetNode<FileDialog>("%LocationDialog");

        RegisterTextEnter(_nameEdit);
        RegisterTextEnter(_locationEdit);
    }

    /// <summary>The name or location changed.</summary>
    private void On_Edit_TextChanged(string text) => Validate();

    private void On_LocationDialog_DirSelected(string path)
    {
        _locationEdit.Text = path;
        Validate();
    }

    public void PopupFor(ProjectKind kind, string defaultLocation)
    {
        Kind = kind;
        Title = $"New {kind.DisplayName()} Project";
        _promptLabel.Text = kind == ProjectKind.Movie ? "Movie title:" : "Show title:";
        _nameEdit.PlaceholderText = kind == ProjectKind.Movie ? "e.g. The Matrix (1999)" : "e.g. Firefly";
        _nameEdit.Text = "";
        _locationEdit.Text = defaultLocation;
        Validate();

        PopupCentered();
        _nameEdit.GrabFocus();
    }

    private void On_BrowseLocationButton_Pressed()
    {
        if (Directory.Exists(ProjectLocation))
            _locationDialog.CurrentDir = ProjectLocation;
        _locationDialog.PopupCentered();
    }

    private void Validate()
    {
        string? error = null;
        var preview = "";
        if (ProjectName.Length == 0)
        {
            error = "Enter a title.";
        }
        else if (ProjectLocation.Length == 0)
        {
            error = "Choose where to create the project.";
        }
        else
        {
            var folder = ProjectFolder.GetFolderPath(ProjectLocation, ProjectName);
            var fileName = Path.GetFileName(folder) + ProjectFolder.ProjectFileExtension;
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).GetEnumerator().MoveNext())
                error = $"A folder named \"{Path.GetFileName(folder)}\" already exists there.";
            else
                preview = $"Creates {Path.Combine(folder, fileName)}";
        }

        _previewLabel.Text = error ?? preview;
        _previewLabel.Modulate = error is null ? new Color(1, 1, 1, 0.6f) : ErrorColor;
        GetOkButton().Disabled = error is not null;
    }
}
