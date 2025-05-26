using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Edits a Chapters node's list: times and names, adding and removing chapters. Saved as the node's
/// <see cref="ChapterLists.ListSetting"/>; resetting goes back to following the chapters wired into it.
/// </summary>
public partial class ChapterEditorDialog : ConfirmationDialog
{
    private enum Column
    {
        Number,
        Start,
        Name,
    }

    /// <summary>Raised after the node's settings are updated.</summary>
    [Signal]
    public delegate void AppliedEventHandler();

    private Tree _tree = null!;
    private Button _resetButton = null!;

    private CanvasNode? _node;
    // What the node shows without an edited list: the chapters wired into it, after its own settings
    private List<Chapter> _unedited = [];

    public override void _Ready()
    {
        _tree = GetNode<Tree>("%Tree");
        _resetButton = GetNode<Button>("%ResetButton");

        // Column titles and widths can't be set in a scene (the column count is in it)
        _tree.SetColumnTitle((int)Column.Number, "#");
        _tree.SetColumnTitle((int)Column.Start, "Start");
        _tree.SetColumnTitle((int)Column.Name, "Name");
        _tree.SetColumnExpand((int)Column.Number, false);
        _tree.SetColumnCustomMinimumWidth((int)Column.Number, 40);
        _tree.SetColumnExpand((int)Column.Start, false);
        _tree.SetColumnCustomMinimumWidth((int)Column.Start, 130);
    }

    private void On_ResetButton_Pressed() => Fill(_unedited);

    /// <param name="current">The chapters the node gives now.</param>
    /// <param name="unedited">What it would give without an edited list.</param>
    public void Edit(CanvasNode node, IReadOnlyList<Chapter> current, IReadOnlyList<Chapter> unedited)
    {
        _node = node;
        _unedited = [.. unedited];
        _resetButton.Disabled = unedited.Count == 0;
        Fill(current);
        PopupCentered(new Vector2I(560, 480));
    }

    private void Fill(IEnumerable<Chapter> chapters)
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        foreach (var chapter in chapters.OrderBy(c => c.Start))
            AddRow(root, chapter);
        Renumber(numbersOnly: true);
    }

    private TreeItem AddRow(TreeItem root, Chapter chapter, int index = -1)
    {
        var item = _tree.CreateItem(root, index);
        item.SetText((int)Column.Start, ChapterLists.Format(chapter.Start));
        item.SetEditable((int)Column.Start, true);
        item.SetText((int)Column.Name, chapter.Name ?? "");
        item.SetEditable((int)Column.Name, true);
        item.SetMetadata((int)Column.Start, chapter.Start.Nanoseconds);
        return item;
    }

    private List<TreeItem> Rows() => _tree.GetRoot()?.GetChildren().ToList() ?? [];

    private void On_Tree_ItemEdited()
    {
        var item = _tree.GetEdited();
        if (item is null || _tree.GetEditedColumn() != (int)Column.Start)
            return;

        // A time that doesn't parse goes back to what it was
        if (PreciseTime.TryParse(item.GetText((int)Column.Start).Trim(), out var time) && time >= PreciseTime.Zero)
        {
            item.SetMetadata((int)Column.Start, time.Nanoseconds);
            item.SetText((int)Column.Start, ChapterLists.Format(time));
            Fill(Read());
        }
        else
        {
            item.SetText((int)Column.Start, ChapterLists.Format(new PreciseTime(item.GetMetadata((int)Column.Start).AsInt64())));
        }
    }

    private List<Chapter> Read() => Rows()
        .Select(item => new Chapter(new PreciseTime(item.GetMetadata((int)Column.Start).AsInt64()), item.GetText((int)Column.Name)))
        .ToList();

    private void On_AddButton_Pressed()
    {
        var rows = Rows();
        var selected = _tree.GetSelected();
        var index = selected is null ? rows.Count - 1 : rows.IndexOf(selected);
        var after = index >= 0 ? new PreciseTime(rows[index].GetMetadata((int)Column.Start).AsInt64()) : PreciseTime.Zero;
        var next = index + 1 < rows.Count ? new PreciseTime(rows[index + 1].GetMetadata((int)Column.Start).AsInt64()) : (PreciseTime?)null;

        // Halfway to the next chapter, or a minute on from the last
        var start = index < 0 ? PreciseTime.Zero
            : next is not null ? new PreciseTime((after.Nanoseconds + next.Value.Nanoseconds) / 2)
            : after + new PreciseTime(0, 1, 0, 0);
        var item = AddRow(_tree.GetRoot(), new Chapter(start, ""), index + 1);
        Renumber(numbersOnly: true);
        item.Select((int)Column.Name);
        _tree.ScrollToItem(item);
        _tree.EditSelected(true);
    }

    private void On_RemoveButton_Pressed()
    {
        var item = _tree.GetSelected();
        if (item is not null)
        {
            item.Free();
            Renumber(numbersOnly: true);
        }
    }

    private void On_NumberNamesButton_Pressed() => Renumber(numbersOnly: false);

    private void Renumber(bool numbersOnly)
    {
        var rows = Rows();
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].SetText((int)Column.Number, (i + 1).ToString());
            if (!numbersOnly)
                rows[i].SetText((int)Column.Name, ChapterLists.DefaultName(i));
        }
    }

    private void On_Confirmed()
    {
        var node = _node;
        if (node is null)
            return;

        // Unnamed chapters get numbered; a list the same as what the node gives anyway isn't kept as an edit,
        // so it goes on following its input
        var chapters = ChapterLists.Finish(Read().Select(c => c with { Name = string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim() }), null);
        var text = ChapterLists.Serialize(chapters);
        _ = new ChaptersNode(node) { List = text == ChapterLists.Serialize(_unedited) ? "" : text };
        EmitSignal(SignalName.Applied);
    }
}
