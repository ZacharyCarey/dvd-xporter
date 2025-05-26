using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Everything that can go on the canvas, under headings, searchable. Items are dragged onto the canvas, or
/// double-clicked (or the best match picked with Enter) to add them in the middle of it.
/// </summary>
public partial class NodePalette : VBoxContainer
{
    private static readonly Color NoneColor = new(1, 1, 1, 0.5f);

    /// <summary>An item was double-clicked, or Enter pressed in the search: add it (by its id).</summary>
    [Signal]
    public delegate void ItemChosenEventHandler(string id);

    /// <summary>Escape was pressed in the search, which was cleared: back to the canvas.</summary>
    [Signal]
    public delegate void DismissedEventHandler();

    private LineEdit _search = null!;
    private Tree _tree = null!;
    private IReadOnlyList<AddableItem> _items = [];

    public override void _Ready()
    {
        _search = GetNode<LineEdit>("%PaletteSearch");
        _tree = GetNode<Tree>("%PaletteTree");

        // Dragging puts a node where it's dropped; a scene can't hold this
        _tree.SetDragForwarding(
            Callable.From<Vector2, Variant>(GetDragData),
            Callable.From<Vector2, Variant, bool>((_, _) => false),
            Callable.From<Vector2, Variant>((_, _) => { }));
    }

    /// <summary>Lists <paramref name="items"/>, or those matching the search.</summary>
    public void Show(IReadOnlyList<AddableItem> items)
    {
        _items = items;
        Rebuild();
    }

    public void FocusSearch()
    {
        _search.GrabFocus();
        _search.SelectAll();
    }

    private List<AddableItem> Matches() => QuickAddPopup.Search(_items, _search.Text).ToList();

    private void Rebuild()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        var matches = Matches();
        // By heading; a search lists the best matches first, so headings follow their best match
        foreach (var group in matches.GroupBy(i => i.Category))
        {
            var categoryItem = _tree.CreateItem(root);
            categoryItem.SetText(0, group.Key);
            categoryItem.SetSelectable(0, false);
            foreach (var addable in group)
            {
                var item = _tree.CreateItem(categoryItem);
                item.SetText(0, addable.Title);
                item.SetTooltipText(0, addable.Description.Length > 0 ? addable.Description : addable.Title);
                item.SetMetadata(0, addable.Id);
            }
        }

        if (_search.Text.Length > 0 && matches.Count == 0)
        {
            var none = _tree.CreateItem(root);
            none.SetText(0, "No matching nodes");
            none.SetSelectable(0, false);
            none.SetCustomColor(0, NoneColor);
        }
    }

    private void On_PaletteSearch_TextChanged(string text) => Rebuild();

    /// <summary>Enter adds the best match.</summary>
    private void On_PaletteSearch_TextSubmitted(string text)
    {
        var first = Matches().FirstOrDefault();
        if (first is not null)
            EmitSignal(SignalName.ItemChosen, first.Id);
    }

    /// <summary>Down goes into the list; Escape clears the search and goes back to the canvas.</summary>
    private void On_PaletteSearch_GuiInput(InputEvent input)
    {
        var first = _tree.GetRoot()?.GetNextVisible();
        if (input is InputEventKey { Pressed: true, Keycode: Key.Down } && first is not null)
        {
            var item = first.GetFirstChild() ?? first;
            item.Select(0);
            _tree.GrabFocus();
            _search.AcceptEvent();
        }
        else if (input is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            _search.Text = "";
            Rebuild();
            EmitSignal(SignalName.Dismissed);
            _search.AcceptEvent();
        }
    }

    private void On_PaletteTree_ItemActivated()
    {
        if (_tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id)
            EmitSignal(SignalName.ItemChosen, id);
    }

    private Variant GetDragData(Vector2 at)
    {
        if (_tree.GetItemAtPosition(at)?.GetMetadata(0).AsString() is not { Length: > 0 } id)
            return default;
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null)
            return default;

        var preview = new Label { Text = item.Title };
        preview.AddThemeColorOverride("font_color", item.Tint ?? Colors.White);
        _tree.SetDragPreview(preview);
        return new Godot.Collections.Dictionary { ["node_type"] = id };
    }
}
