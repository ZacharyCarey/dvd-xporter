using Godot;

namespace DvdXporter.UI;

/// <summary>One of a Template node's videos, with a button to test encode it.</summary>
public partial class TemplateVideoLine : HBoxContainer
{
    public Button TestButton => GetNode<Button>("%TestButton");

    public void Setup(string text)
    {
        var label = GetNode<Label>("%Label");
        label.Text = text;
        label.TooltipText = text;
    }
}
