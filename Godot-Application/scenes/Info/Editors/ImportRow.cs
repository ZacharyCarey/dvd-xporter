using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Online;
using Godot;

namespace DvdXporter.UI;

/// <summary>"Import Details Online...", and which services it can use (or how to set one up).</summary>
public partial class ImportRow : HBoxContainer
{
    [Signal]
    public delegate void ImportRequestedEventHandler();

    public void Setup(IReadOnlyList<IMetadataProvider> providers)
    {
        GetNode<Button>("%ImportButton").Disabled = providers.Count == 0;
        GetNode<Label>("%ImportHint").Text = providers.Count == 0
            ? "Add a TMDB, TVDB or OMDb API key in Settings → Online to import details."
            : $"Fill this in from {string.Join(" or ", providers.Select(p => p.Name))}. Blank fields are filled; what you've typed is kept.";
    }

    private void On_ImportButton_Pressed() => EmitSignal(SignalName.ImportRequested);
}
