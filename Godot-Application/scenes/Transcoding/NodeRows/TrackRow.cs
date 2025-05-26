using Godot;

namespace DvdXporter.UI;

/// <summary>One of an output file's tracks: what it is, and a button to edit its title, language and flags.</summary>
public partial class TrackRow : HBoxContainer
{
    /// <summary>Raised when the edit button is pressed.</summary>
    [Signal]
    public delegate void EditRequestedEventHandler();

    public Label Label => GetNode<Label>("%Label");

    private void On_EditButton_Pressed() => EmitSignal(SignalName.EditRequested);
}
