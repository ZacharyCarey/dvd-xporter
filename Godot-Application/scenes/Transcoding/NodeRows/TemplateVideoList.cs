using System.Collections.Generic;
using Godot;

namespace DvdXporter.UI;

/// <summary>A Template node's videos, one per line with a button to test encode it, and a button to edit them.</summary>
public partial class TemplateVideoList : VBoxContainer
{
    private readonly List<Button> _testButtons = [];

    /// <summary>Raised when one of the videos is to be test encoded (or its test cancelled), with its index.</summary>
    [Signal]
    public delegate void TestRequestedEventHandler(int index);

    /// <summary>Raised when the edit button is pressed.</summary>
    [Signal]
    public delegate void EditRequestedEventHandler();

    /// <summary>One video's line (see <see cref="TemplateVideoLine"/>), made for each.</summary>
    [Export] public PackedScene LineScene { get; set; } = null!;

    private void On_EditButton_Pressed() => EmitSignal(SignalName.EditRequested);

    /// <summary>Shows the videos, as described for the user, in place of any shown before.</summary>
    public void Setup(IReadOnlyList<string> videos)
    {
        var lines = GetNode<VBoxContainer>("%Lines");
        foreach (var old in lines.GetChildren())
        {
            lines.RemoveChild(old);
            old.QueueFree();
        }

        _testButtons.Clear();
        // The lines are made per video, so connected here
        for (int i = 0; i < videos.Count; i++)
        {
            int index = i;
            var line = LineScene.Instantiate<TemplateVideoLine>();
            line.Setup(videos[i]);
            line.TestButton.Pressed += () => EmitSignal(SignalName.TestRequested, index);
            _testButtons.Add(line.TestButton);
            lines.AddChild(line);
        }

        // An empty list still takes up a gap
        lines.Visible = videos.Count > 0;
        GetNode<Label>("%EmptyLabel").Visible = videos.Count == 0;
        GetNode<Button>("%EditButton").Text = videos.Count == 0 ? "Add Videos..." : "Edit Videos...";
    }

    /// <summary>Updates a video's test button, like <see cref="TranscodeNodeView.SetTestState"/>.</summary>
    public void SetTestState(int video, bool enabled, string tooltip, double? progress)
    {
        if (video >= _testButtons.Count)
            return;
        var button = _testButtons[video];
        button.Text = progress is not null ? $"{progress.Value:P0}" : "";
        button.Disabled = progress is null && !enabled;
        button.TooltipText = progress is not null ? "Stop the test encode" : tooltip;
    }
}
