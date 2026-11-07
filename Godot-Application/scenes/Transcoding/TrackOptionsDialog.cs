using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Edits one output track's title, language and flags, which are kept as the output file node's settings
/// under the track's key ("t0.title", "t0.language", ...).
/// </summary>
public partial class TrackOptionsDialog : ConfirmationDialog
{
    /// <summary>Raised after the node's settings are updated.</summary>
    [Signal]
    public delegate void AppliedEventHandler();

    private LineEdit _titleEdit = null!;
    private LineEdit _languageEdit = null!;
    private OptionButton _defaultOption = null!;
    private Label _sourceLabel = null!;
    // The flags' tickboxes, by the option each sets (each is named for it in the scene, e.g. "forced")
    private readonly Dictionary<string, CheckBox> _flagChecks = [];

    private CanvasNode? _node;
    private string _track = "";

    public override void _Ready()
    {
        _titleEdit = GetNode<LineEdit>("%TitleEdit");
        _languageEdit = GetNode<LineEdit>("%LanguageEdit");
        _defaultOption = GetNode<OptionButton>("%DefaultOption");
        _sourceLabel = GetNode<Label>("%SourceLabel");
        foreach (var check in GetNode("%Flags").GetChildren().OfType<CheckBox>())
            _flagChecks[check.Name] = check;
    }

    /// <param name="track">What the track is now (from the last compile), or null if nothing is connected.</param>
    public void Edit(CanvasNode node, string trackKey, string name, OutputTrack? track)
    {
        _node = node;
        _track = trackKey;
        Title = $"{name} Options";
        _sourceLabel.Text = track is null ? "Nothing is connected to this track yet." : track.Description;

        _titleEdit.Text = node.Get(Key(TranscodeCompiler.TitleOption));
        // Without one, it's the name given to the disc's track
        _titleEdit.PlaceholderText = track?.Title is not null && _titleEdit.Text.Length == 0 ? track.Title : "None";
        _languageEdit.Text = node.Get(Key(TranscodeCompiler.LanguageOption));
        _languageEdit.PlaceholderText = track?.Language is not null && _languageEdit.Text.Length == 0 ? track.Language : "From the source";
        _defaultOption.Select(node.Get(Key(TranscodeCompiler.DefaultOption), "auto") switch
        {
            "yes" => 1,
            "no" => 2,
            _ => 0,
        });
        foreach (var (option, check) in _flagChecks)
            check.ButtonPressed = node.GetBool(Key(option));

        PopupCentered();
        _titleEdit.GrabFocus();
    }

    private string Key(string option) => TranscodeCompiler.TrackOptionKey(_track, option);

    private void On_Confirmed()
    {
        var node = _node;
        if (node is null)
            return;

        node.Set(Key(TranscodeCompiler.TitleOption), _titleEdit.Text.Trim());
        node.Set(Key(TranscodeCompiler.LanguageOption), _languageEdit.Text.Trim().ToLowerInvariant());
        node.Set(Key(TranscodeCompiler.DefaultOption), _defaultOption.Selected switch
        {
            1 => "yes",
            2 => "no",
            _ => "",
        });
        foreach (var (option, check) in _flagChecks)
        {
            // Unset flags are left out rather than saved as "false"
            if (check.ButtonPressed)
                node.Set(Key(option), true);
            else
                node.Settings.Remove(Key(option));
        }

        EmitSignal(SignalName.Applied);
    }
}
