using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Edits a Template node's videos: for each, the disc and video file or playlist to play, and the library item
/// and version its outputs go to. Kept as the node's <see cref="TemplateRows.Setting"/>.
/// </summary>
public partial class TemplateRowsDialog : ConfirmationDialog
{
    /// <summary>Raised after the node's rows are updated.</summary>
    [Signal]
    public delegate void AppliedEventHandler();

    /// <summary>Raised (after saving the rows) when a row is to become an ordinary graph on the canvas: the template node's ID, and the row.</summary>
    [Signal]
    public delegate void DetachRequestedEventHandler(int nodeId, int row);

    /// <summary>One video's row: its disc, video, chapters, library item and version, and buttons to move, detach or remove it.</summary>
    [Export] public PackedScene RowScene { get; set; } = null!;

    private VBoxContainer _rowsBox = null!;
    private Label _emptyLabel = null!;
    private readonly List<TemplateRow> _rows = [];

    private CanvasNode? _node;
    private ICanvasContext? _context;
    // Where a first row starts: the template's sample video
    private TemplateRow? _sample;

    public override void _Ready()
    {
        _rowsBox = GetNode<VBoxContainer>("%Rows");
        _emptyLabel = GetNode<Label>("%EmptyLabel");
    }

    private void On_AddVideoButton_Pressed() => AddRow();

    private void On_Confirmed() => Apply();

    /// <param name="sample">The template's sample video, which a first row starts from.</param>
    public void Edit(CanvasNode node, ICanvasContext context, TemplateRow? sample)
    {
        _node = node;
        _context = context;
        _sample = sample;
        _rows.Clear();
        _rows.AddRange(new TemplateNode(node).Rows);
        if (_rows.Count == 0)
            AddRow();
        else
            Rebuild();
        PopupCentered(new Vector2I(900, 520));
    }

    private void Rebuild()
    {
        foreach (var child in _rowsBox.GetChildren())
        {
            _rowsBox.RemoveChild(child);
            child.QueueFree();
        }

        _emptyLabel.Visible = _rows.Count == 0;
        for (int i = 0; i < _rows.Count; i++)
            _rowsBox.AddChild(BuildRow(i));
    }

    /// <summary>A row for the video at <paramref name="index"/>; made per video, so it's connected here.</summary>
    private Control BuildRow(int index)
    {
        var row = _rows[index];
        var line = RowScene.Instantiate<Control>();
        line.GetNode<Label>("%Number").Text = $"{index + 1}.";

        Fill(line.GetNode<OptionButton>("%Disc"), Discs(), row.Disc, value =>
        {
            // Another disc's videos are different; start from its first
            var first = Items(value).FirstOrDefault()?.Value ?? "";
            _rows[index] = row with { Disc = value, Item = first };
            Rebuild();
        });
        Fill(line.GetNode<OptionButton>("%Item"), Items(row.Disc), row.Item, value => _rows[index] = _rows[index] with { Item = value });
        var chapters = line.GetNode<LineEdit>("%Chapters");
        chapters.Text = row.Chapters;
        chapters.TooltipText += ChapterCountNote(row);
        chapters.TextChanged += text => _rows[index] = _rows[index] with { Chapters = NormalizeChapters(text) };
        var version = line.GetNode<LineEdit>("%Version");
        version.Text = row.Version;
        version.TextChanged += text => _rows[index] = _rows[index] with { Version = text.Trim() };
        // Extras have no versions
        void ShowVersion(string target) => version.Editable = !NodeTypes.IsExtraTarget(target);
        ShowVersion(row.Target);
        Fill(line.GetNode<OptionButton>("%Target"), Targets(), row.Target, value =>
        {
            _rows[index] = _rows[index] with { Target = value };
            ShowVersion(value);
        });

        var up = line.GetNode<Button>("%UpButton");
        up.Disabled = index == 0;
        up.Pressed += () => Move(index, -1);
        var down = line.GetNode<Button>("%DownButton");
        down.Disabled = index == _rows.Count - 1;
        down.Pressed += () => Move(index, 1);
        line.GetNode<Button>("%DetachButton").Pressed += () => Detach(index);
        line.GetNode<Button>("%RemoveButton").Pressed += () =>
        {
            _rows.RemoveAt(index);
            Rebuild();
        };
        return line;
    }

    private static void Fill(OptionButton option, IReadOnlyList<SettingChoice> choices, string current, Action<string> changed)
    {
        var values = new List<string>();
        if (choices.All(c => c.Value != current))
        {
            option.AddItem(current.Length == 0 ? "Choose..." : "(missing)");
            values.Add(current);
        }

        foreach (var choice in choices)
        {
            option.AddItem(choice.Label);
            values.Add(choice.Value);
        }

        option.Select(values.IndexOf(current));
        option.TooltipText = choices.FirstOrDefault(c => c.Value == current)?.Label ?? "";
        option.ItemSelected += i => changed(values[(int)i]);
    }

    private List<SettingChoice> Discs() => _context is null ? []
        : _context.Project.Releases.SelectMany(r => r.Discs.Select(d => new SettingChoice(d.Fingerprint, $"{r.Name} › {d.Label}"))).ToList();

    private List<SettingChoice> Items(string disc) => _context is null ? []
        : NodeTypes.SourceItemChoices(SourceNode.Create(disc), _context);

    private List<SettingChoice> Targets() => _context is null ? [] : NodeTypes.ExportTargetChoices(_context.Project);

    /// <summary>
    /// Adds the next video: the one after the last row's on the same disc, going to the next library item, as the
    /// same version.
    /// </summary>
    private void AddRow()
    {
        var targets = Targets();
        TemplateRow next;
        var last = _rows.LastOrDefault();
        if (last is not null)
        {
            var target = targets.ElementAtOrDefault(targets.FindIndex(c => c.Value == last.Target) + 1)?.Value ?? "";
            if (last.ChapterRange is var (first, end) && end < ChapterCount(last))
            {
                // The next run of as many chapters, on the same video
                var size = end - first + 1;
                next = new TemplateRow(last.Disc, last.Item, target, $"{end + 1}-{Math.Min(end + size, ChapterCount(last))}", last.Version);
            }
            else
            {
                var items = Items(last.Disc);
                var item = items.ElementAtOrDefault(items.FindIndex(c => c.Value == last.Item) + 1)?.Value ?? last.Item;
                // A clip split by chapters likely continues on the next one from its first chapters
                var chapters = last.ChapterRange is var (f, l) && item != last.Item ? $"1-{l - f + 1}" : "";
                next = new TemplateRow(last.Disc, item, target, chapters, last.Version);
            }
        }
        else
        {
            var disc = _sample?.Disc ?? Discs().FirstOrDefault()?.Value ?? "";
            var item = _sample?.Item is { Length: > 0 } sampleItem ? sampleItem : Items(disc).FirstOrDefault()?.Value ?? "";
            next = new TemplateRow(disc, item, targets.FirstOrDefault()?.Value ?? "");
        }

        _rows.Add(next);
        Rebuild();
    }

    private int ChapterCount(TemplateRow row) => _context is null ? 0
        : ChapterLists.AllOfSource(SourceNode.Create(row.Disc, row.Item), _context)?.Chapters.Count ?? 0;

    private string ChapterCountNote(TemplateRow row) => ChapterCount(row) is > 0 and var count ? $"\nThis video has {count} chapters." : "";

    // "3" means "3-3"; anything else that isn't a range is dropped
    private static string NormalizeChapters(string text)
    {
        text = text.Replace(" ", "").Replace('–', '-');
        if (int.TryParse(text, out var single))
            text = $"{single}-{single}";
        return new TemplateRow("", "", "", text).ChapterRange is not null ? text : "";
    }

    private void Move(int index, int by)
    {
        (_rows[index], _rows[index + by]) = (_rows[index + by], _rows[index]);
        Rebuild();
    }

    private void Detach(int index)
    {
        var node = _node;
        if (node is null)
            return;
        Apply();
        Hide();
        EmitSignal(SignalName.DetachRequested, node.Id, index);
    }

    private void Apply()
    {
        var node = _node;
        if (node is null)
            return;
        _ = new TemplateNode(node) { Rows = _rows };
        EmitSignal(SignalName.Applied);
    }
}
