using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DvdXporter.UI;

/// <summary>One thing that can be added to a canvas: a node type, or a frame.</summary>
/// <param name="Category">Its palette heading, e.g. "Video Filters".</param>
/// <param name="Tint">The colour its title is dragged in, if not white.</param>
public sealed record AddableItem(string Id, string Title, string Category, string Description, Color? Tint = null);

/// <summary>
/// A search box with the matching nodes under it, opened at the mouse to add a node by typing its name.
/// Up and down pick, Enter adds, Escape closes.
/// </summary>
public partial class QuickAddPopup : PopupPanel
{
    /// <summary>Raised with the chosen item's id.</summary>
    [Signal]
    public delegate void PickedEventHandler(string id);

    private LineEdit _search = null!;
    private ItemList _list = null!;
    private List<AddableItem> _items = [];
    private List<AddableItem> _shown = [];

    public override void _Ready()
    {
        _search = GetNode<LineEdit>("%Search");
        _list = GetNode<ItemList>("%List");
    }

    private void On_Search_TextChanged(string text) => Filter();

    private void On_Search_TextSubmitted(string text) => Pick(_list.GetSelectedItems().FirstOrDefault(0));

    private void On_List_ItemActivated(long index) => Pick((int)index);

    /// <summary>The arrows move through the list while typing.</summary>
    private void On_Search_GuiInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true } key || _shown.Count == 0)
            return;
        var by = key.Keycode switch
        {
            Key.Down => 1,
            Key.Up => -1,
            Key.Pagedown => 8,
            Key.Pageup => -8,
            _ => 0,
        };
        if (by == 0)
            return;
        var current = _list.GetSelectedItems().FirstOrDefault(0);
        var next = Math.Clamp(current + by, 0, _shown.Count - 1);
        _list.Select(next);
        _list.EnsureCurrentIsVisible();
        _search.AcceptEvent();
    }

    /// <summary>Opens at a point on the screen, listing <paramref name="items"/>.</summary>
    public void Open(IEnumerable<AddableItem> items, Vector2I at)
    {
        _items = items.ToList();
        _search.Text = "";
        Filter();
        Position = at;
        Popup();
        _search.GrabFocus();
    }

    private void Filter()
    {
        _shown = Search(_items, _search.Text).ToList();
        _list.Clear();
        foreach (var item in _shown)
        {
            var index = _list.AddItem($"{item.Title}    ·  {item.Category}");
            _list.SetItemTooltip(index, item.Description);
        }

        if (_shown.Count > 0)
            _list.Select(0);
    }

    private void Pick(int index)
    {
        var item = _shown.ElementAtOrDefault(index);
        if (item is null)
            return;
        Hide();
        EmitSignal(SignalName.Picked, item.Id);
    }

    /// <summary>
    /// The items matching a search, best first: titles starting with it, then titles containing it, then the
    /// rest that mention it. Every word must match somewhere. An empty search keeps everything, in order.
    /// </summary>
    public static IEnumerable<AddableItem> Search(IEnumerable<AddableItem> items, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return items;

        bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        return items
            .Where(i => words.All(w => Has(i.Title, w) || Has(i.Category, w) || Has(i.Description, w)))
            .OrderBy(i => i.Title.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 0
                : words.All(w => Has(i.Title, w)) ? 1
                : 2);
    }
}
