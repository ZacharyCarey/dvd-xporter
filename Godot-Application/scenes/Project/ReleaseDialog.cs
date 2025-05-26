using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Creates a release, or edits an existing one's name and barcode.
/// </summary>
public partial class ReleaseDialog : ConfirmationDialog
{
    private static readonly Color ErrorColor = new(0.89f, 0.44f, 0.42f);

    private LineEdit _nameEdit = null!;
    private LineEdit _barcodeEdit = null!;
    private Label _messageLabel = null!;

    /// <summary>The release being edited, or null when creating a new one.</summary>
    public Release? EditingRelease { get; private set; }

    public string ReleaseName => _nameEdit.Text.Trim();

    public string? Barcode => Release.NormalizeBarcode(_barcodeEdit.Text, out _);

    /// <summary>
    /// Saves what was entered to the release being edited, or adds it to <paramref name="project"/> as a new one.
    /// </summary>
    public Release Apply(Project project)
    {
        var release = EditingRelease;
        if (release is not null)
        {
            release.Name = ReleaseName;
            release.Barcode = Barcode;
            return release;
        }

        release = new Release(ReleaseName) { Barcode = Barcode };
        project.Releases.Add(release);
        return release;
    }

    public override void _Ready()
    {
        _nameEdit = GetNode<LineEdit>("%NameEdit");
        _barcodeEdit = GetNode<LineEdit>("%BarcodeEdit");
        _messageLabel = GetNode<Label>("%MessageLabel");

        RegisterTextEnter(_nameEdit);
        RegisterTextEnter(_barcodeEdit);
    }

    /// <summary>Either field changed.</summary>
    private void On_Edit_TextChanged(string text) => Validate();

    public void PopupForNew(string suggestedName)
    {
        EditingRelease = null;
        Title = "New Release";
        GetOkButton().Text = "Create";
        Open(suggestedName, "");
    }

    public void PopupForEdit(Release release)
    {
        EditingRelease = release;
        Title = "Edit Release";
        GetOkButton().Text = "Save";
        Open(release.Name, release.Barcode ?? "");
    }

    private void Open(string name, string barcode)
    {
        _nameEdit.Text = name;
        _barcodeEdit.Text = barcode;
        Validate();
        PopupCentered();
        _nameEdit.GrabFocus();
        _nameEdit.SelectAll();
    }

    private void Validate()
    {
        Release.NormalizeBarcode(_barcodeEdit.Text, out var barcodeError);
        var error = ReleaseName.Length == 0 ? "Enter a name for this release." : barcodeError;

        _messageLabel.Text = error ?? "The barcode is optional. It helps others find the same release.";
        _messageLabel.Modulate = error is null ? new Color(1, 1, 1, 0.6f) : ErrorColor;
        GetOkButton().Disabled = error is not null;
    }
}
