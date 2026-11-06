using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// One <see cref="CanvasNode"/> on the canvas: its settings as controls and its ports. Changes go straight into the
/// node's settings. Each node type has a scene of its own in NodeViews/ (named for the type, e.g. VideoCodecNodeView),
/// which inherits this one (TranscodeNodeView.tscn, the node's look) and lays the node out; most use
/// <see cref="SettingsNodeView"/>. Each kind of row is a scene in NodeRows/. A node without a scene of its own (a
/// custom node, or a type from a newer version) is laid out here, from its ports and settings.
/// </summary>
public partial class TranscodeNodeView : GraphNode
{
    private static readonly Color DimTextColor = new(1, 1, 1, 0.6f);
    private static readonly Color ErrorTextColor = new(1, 0.45f, 0.4f);
    private static readonly Color WarningTextColor = new(1, 0.8f, 0.35f);
    private static readonly Color HintTextColor = new(0.6f, 0.78f, 1);
    private const string ScenePath = "res://scenes/Transcoding/TranscodeNodeView.tscn";
    private const string RowFolder = "res://scenes/Transcoding/NodeRows/";
    private const string ViewFolder = "res://scenes/Transcoding/NodeViews/";


    /// <summary>The title bar's look: its colour and outline are set from the node's category and state.</summary>
    [Export]
    public StyleBoxFlat TitleStyle { get; set; } = new();

    // Outlines: selected nodes as selected comments are, hovered ones faintly, so it's clear what a click picks up
    [Export]
    public Color SelectedOutlineColor { get; set; } = Colors.White;

    [Export]
    public Color HoverOutlineColor { get; set; } = new(1, 1, 1, 0.55f);

    [Export]
    public int SelectedOutlineWidth { get; set; } = 2;

    [Export]
    public int HoverOutlineWidth { get; set; } = 1;

    /// <summary>Raised after a setting changes, once the node's settings hold the new value.</summary>
    [Signal]
    public delegate void SettingChangedEventHandler(TranscodeNodeView view);

    /// <summary>Raised when the user asks to edit an output track's title, language and flags.</summary>
    [Signal]
    public delegate void TrackOptionsRequestedEventHandler(TranscodeNodeView view, string track);

    /// <summary>Raised when the user asks to edit a Chapters node's list.</summary>
    [Signal]
    public delegate void ChaptersEditRequestedEventHandler(TranscodeNodeView view);

    /// <summary>Raised when one of the node's action buttons is pressed (e.g. a Crop's "detect"), with its key.</summary>
    [Signal]
    public delegate void ActionRequestedEventHandler(TranscodeNodeView view, string key);

    /// <summary>Raised when the user asks to edit a Template node's videos.</summary>
    [Signal]
    public delegate void TemplateRowsEditRequestedEventHandler(TranscodeNodeView view);

    /// <summary>Raised when one of a Template node's rows is to be test encoded (or its test cancelled).</summary>
    [Signal]
    public delegate void TemplateRowTestRequestedEventHandler(TranscodeNodeView view, int row);

    /// <summary>Raised when the node's Test Encode button is pressed (Output File and Export nodes).</summary>
    [Signal]
    public delegate void TestRequestedEventHandler(TranscodeNodeView view);

    /// <summary>Raised when the node is right-clicked, with the screen position for a menu.</summary>
    [Signal]
    public delegate void ContextMenuRequestedEventHandler(TranscodeNodeView view, Vector2 at);

    private string _signature = "";
    // The rows laid out here (for a node without a scene of its own), in slot order; the test button and issues come after them
    private readonly List<Control> _rows = [];
    // Each action's button, and its text when it isn't running
    private readonly Dictionary<string, (Button Button, string Text)> _actionButtons = [];
    // An action that's running: its button's text while it does, e.g. "Detecting... 50%"
    private readonly Dictionary<string, string> _busyActions = [];
    // One label per issue, each in its level's colour
    private VBoxContainer _issueBox = null!;
    private Button _testButton = null!;
    // The test button's state, kept across rebuilds: whether it can test, why not, and progress while testing
    private bool _testEnabled;
    private string _testTooltip = "";
    private double? _testProgress;
    private readonly Dictionary<string, Label> _trackLabels = [];
    private Color _color;
    private bool _hovered;

    public CanvasNode Model { get; private set; } = null!;

    public NodeType Type { get; private set; } = null!;

    /// <summary>The node's input and output port keys, in GraphEdit's port order.</summary>
    public List<string> InputKeys { get; } = [];

    public List<string> OutputKeys { get; } = [];

    public List<PortKind> InputKinds { get; } = [];

    public List<PortKind> OutputKinds { get; } = [];

    public static string ViewName(int id) => $"n{id}";

    /// <summary>A view for a node of the type: its own scene, or for a custom node (or unknown type) one laid out here.</summary>
    public static TranscodeNodeView Create(string type) =>
        GD.Load<PackedScene>(SceneOf(type) ?? ScenePath).Instantiate<TranscodeNodeView>();

    /// <summary>A type's own scene, e.g. "video-codec" → NodeViews/VideoCodecNodeView.tscn; null if it has none.</summary>
    private static string? SceneOf(string type)
    {
        if (NodeTypes.Get(type) is null)
            return null;
        // A template's stand-in for a Disc Video shows as one
        var name = type == NodeTypes.TemplateInput ? NodeTypes.Source : type;
        var path = ViewFolder + string.Concat(name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + "NodeView.tscn";
        return ResourceLoader.Exists(path) ? path : null;
    }

    private bool HasOwnScene => SceneOf(Type.Id) is not null;

    protected static T Row<T>(string scene) where T : Node => GD.Load<PackedScene>(RowFolder + scene + ".tscn").Instantiate<T>();

    public override void _Ready()
    {
        _testButton = GetNode<Button>("%TestButton");
        _issueBox = GetNode<VBoxContainer>("%IssueBox");
        // Under the rows of a scene that inherits this one
        MoveChild(_testButton, -1);
        MoveChild(_issueBox, -1);
        SetProcess(false);
    }

    private void On_TestButton_Pressed() => EmitSignal(SignalName.TestRequested, this);

    /// <summary>
    /// Leaving is checked each frame instead (see <see cref="_Process"/>): the mouse can leave over a port, which
    /// GraphEdit handles, without the node hearing of it.
    /// </summary>
    private void On_MouseEntered() => SetHovered(true);

    // Only while outlined
    public override void _Process(double delta)
    {
        if (!GetGlobalRect().HasPoint(GetGlobalMousePosition()))
            SetHovered(false);
    }

    public void Setup(CanvasNode model, NodeType type)
    {
        Model = model;
        Name = ViewName(model.Id);
        PositionOffset = new Vector2((float)model.X, (float)model.Y);
        SetType(type);
    }

    /// <summary>Changes the node's type, e.g. when the custom node it uses is renamed or gets other ports.</summary>
    public void SetType(NodeType type)
    {
        Type = type;
        Title = type.Title;
        TooltipText = type.Description;
        // A type's own scene sets its width
        if (!HasOwnScene)
            CustomMinimumSize = new Vector2(300, 0);

        _color = CategoryColor(type);
        UpdateOutline();
    }

    private void SetHovered(bool hovered)
    {
        if (hovered == _hovered)
            return;
        _hovered = hovered;
        SetProcess(hovered);
        UpdateOutline();
    }

    /// <summary>Styles the title bar and body, outlined all round when selected (or faintly, when hovered).</summary>
    private void UpdateOutline()
    {
        int hoverWidth = _hovered ? HoverOutlineWidth : 0;
        AddThemeStyleboxOverride("titlebar", TitleStyleFor(_color, selected: false, HoverOutlineColor, hoverWidth));
        AddThemeStyleboxOverride("titlebar_selected", TitleStyleFor(_color, selected: true, SelectedOutlineColor, SelectedOutlineWidth));
        AddThemeStyleboxOverride("panel", PanelStyle(HoverOutlineColor, hoverWidth));
        AddThemeStyleboxOverride("panel_selected", PanelStyle(SelectedOutlineColor, SelectedOutlineWidth));
    }

    /// <summary>The theme's node body, with an outline on its sides and bottom (the title bar has the top).</summary>
    private static StyleBox PanelStyle(Color outline, int width)
    {
        var theme = ThemeDB.GetDefaultTheme().GetStylebox("panel", "GraphNode");
        if (theme is not StyleBoxFlat flat)
            return theme;
        var style = (StyleBoxFlat)flat.Duplicate();
        // Margins stay put, so an outline doesn't shift what's inside
        foreach (var side in new[] { Side.Left, Side.Top, Side.Right, Side.Bottom })
            style.SetContentMargin(side, flat.GetContentMargin(side));
        style.BorderColor = outline;
        style.BorderWidthLeft = width;
        style.BorderWidthRight = width;
        style.BorderWidthBottom = width;
        style.BorderWidthTop = 0;
        return style;
    }

    /// <summary>Inside a custom node's canvas: this node's settings that show on the custom node, marked ◆.</summary>
    public HashSet<string> ExposedKeys { get; } = [];

    public static Color CategoryColor(NodeType type) => type.Id == NodeTypes.Export
        ? new Color("8e3ed0")
        : type.Category switch
        {
            NodeCategory.Inputs => new Color("b86a1c"),
            NodeCategory.VideoFilters => new Color("2f6fcf"),
            NodeCategory.AudioFilters => new Color("2a8fa8"),
            NodeCategory.Combine => new Color("5b55c9"),
            NodeCategory.Presets => new Color("1f8a78"),
            NodeCategory.Codecs => new Color("23924a"),
            NodeCategory.Custom => new Color("a8417a"),
            _ => new Color("b86a1c"),
        };

    private StyleBoxFlat TitleStyleFor(Color color, bool selected, Color outline, int width)
    {
        var style = (StyleBoxFlat)TitleStyle.Duplicate();
        style.BgColor = selected ? color.Lightened(0.15f) : color;
        style.BorderColor = outline;
        style.BorderWidthLeft = width;
        style.BorderWidthTop = width;
        style.BorderWidthRight = width;
        return style;
    }

    public static Color PortColor(PortKind kind) => kind switch
    {
        PortKind.Video => new Color("e2475f"),
        PortKind.Audio => new Color("3f8fff"),
        PortKind.Subtitle => new Color("c45ae0"),
        PortKind.File => new Color("f0f0f0"),
        PortKind.Chapters => new Color("e8b339"),
        _ => new Color("a0a0a8"),
    };

    /// <summary>
    /// Lays the node out again if what it shows has changed (other ports, settings shown or hidden, new choices).
    /// Returns whether it did, in which case its links need connecting again. Here: a row for each input and output
    /// (side by side, in order), then the settings shown.
    /// </summary>
    public virtual bool Rebuild(ICanvasContext context, bool force = false)
    {
        var ports = Type.GetPorts(Model, context);
        var settings = Type.Settings.Where(s => s.IsShown(Model, context.Canvas.Kind)).ToList();
        var signature = new StringBuilder(PortSignature(ports));
        foreach (var setting in settings)
            signature.Append('\n').Append(setting.Key).Append(':').Append(ChoiceSignature(setting, context));
        if (!StartRebuild(signature.ToString(), force))
            return false;

        foreach (var child in _rows)
        {
            RemoveChild(child);
            child.QueueFree();
        }

        _rows.Clear();
        _boundControls.Clear();
        var rowPorts = new Dictionary<Control, (NodePort? Input, NodePort? Output)>();
        void Add(Control row, NodePort? input = null, NodePort? output = null)
        {
            AddChild(row);
            MoveChild(row, _rows.Count);
            _rows.Add(row);
            if (input is not null || output is not null)
                rowPorts[row] = (input, output);
        }

        for (int i = 0; i < Math.Max(ports.Inputs.Count, ports.Outputs.Count); i++)
        {
            var input = ports.Inputs.ElementAtOrDefault(i);
            var output = ports.Outputs.ElementAtOrDefault(i);
            if (input is not null && output is not null)
            {
                var pair = Row<PortPairRow>("PortPairRow");
                pair.Setup(input.Label, output.Label);
                Add(pair, input, output);
            }
            else
            {
                var port = Row<Label>(output is not null ? "OutputPortRow" : "InputPortRow");
                port.Text = (input ?? output!).Label;
                Add(port, input, output);
            }
        }

        foreach (var setting in settings)
        {
            var row = Row<SettingRow>("SettingRow");
            row.Setup(setting.Label, setting.Tooltip, ExposedKeys.Contains(setting.Key));
            var control = Row<Control>(ControlScene(setting));
            row.AddControl(control);
            Bind((ISettingControl)control, setting, context);
            Add(row);
        }

        if (_rows.Count == 0)
        {
            var hint = Row<Label>("HintRow");
            ShowText(hint, Type.Id.StartsWith(NodeTypes.CustomPrefix, StringComparison.Ordinal)
                ? "Add Custom Input and Output nodes to its canvas."
                : "Made by a newer version of DVD Xporter.");
            Add(hint);
        }

        FinishRebuild(rowPorts);
        return true;
    }

    /// <summary>The scene of the control for a setting of its kind, e.g. a choice's drop-down.</summary>
    protected static string ControlScene(NodeSetting setting) => setting.Kind switch
    {
        SettingKind.Choice => "ChoiceControl",
        SettingKind.Toggle => "ToggleControl",
        SettingKind.Color => "ColorControl",
        SettingKind.Integer when setting.Stepper => "StepperControl",
        _ => "TextControl",
    };

    /// <summary>What decides how ports show: each one's key, name and kind (its colour and what it connects to).</summary>
    protected static string PortSignature(NodePorts ports) =>
        string.Join(",", ports.Inputs.Select(p => $"{p.Key}/{p.Label}/{p.Kind}")) + "|"
        + string.Join(",", ports.Outputs.Select(p => $"{p.Key}/{p.Label}/{p.Kind}"));

    /// <summary>A choice setting's choices as text, for telling when they've changed; empty for any other setting.</summary>
    protected string ChoiceSignature(NodeSetting setting, ICanvasContext context) => setting.Kind == SettingKind.Choice
        ? string.Join(",", NodeTypes.GetChoices(Model, setting, context).Select(c => $"{c.Value}={c.Label}"))
        : "";

    /// <summary>
    /// Whether the node needs laying out again: forced, or what it shows (<paramref name="signature"/>, and which
    /// settings are shown on the custom node) has changed since it was last laid out.
    /// </summary>
    protected bool StartRebuild(string signature, bool force)
    {
        signature += "\n" + string.Join(",", ExposedKeys);
        if (!force && signature == _signature)
            return false;
        _signature = signature;
        return true;
    }

    /// <summary>
    /// Ends laying the node out: gives the rows their ports, shows the test button if the node has one, and clears
    /// its issues until the canvas has been checked again.
    /// </summary>
    /// <param name="ports">The rows with a port on either side.</param>
    protected void FinishRebuild(IReadOnlyDictionary<Control, (NodePort? Input, NodePort? Output)> ports)
    {
        ClearAllSlots();
        InputKeys.Clear();
        OutputKeys.Clear();
        InputKinds.Clear();
        OutputKinds.Clear();

        // A slot is a shown row's place among the shown rows: hidden ones have none
        int slot = 0;
        foreach (var child in GetChildren().OfType<Control>())
        {
            if (!child.Visible)
                continue;
            if (ports.TryGetValue(child, out var port))
            {
                var input = port.Input;
                if (input is not null)
                {
                    InputKeys.Add(input.Key);
                    InputKinds.Add(input.Kind);
                }

                var output = port.Output;
                if (output is not null)
                {
                    OutputKeys.Add(output.Key);
                    OutputKinds.Add(output.Kind);
                }

                SetSlot(slot,
                    port.Input is not null, (int)(port.Input?.Kind ?? 0), PortColor(port.Input?.Kind ?? PortKind.Video),
                    port.Output is not null, (int)(port.Output?.Kind ?? 0), PortColor(port.Output?.Kind ?? PortKind.Video));
            }

            slot++;
        }

        _testButton.Visible = Type.Id is NodeTypes.OutputFile or NodeTypes.ImageGallery or NodeTypes.Export;
        UpdateTestButton();
        // Issues are shown again once the canvas has been checked
        SetIssues([]);

        // Shrink to fit after rows were removed, and again once wrapped text has its width: until then it's measured
        // at its narrowest, so the node would keep room for lines it doesn't use
        Size = Vector2.Zero;
        ShrinkAfterLayout();
    }

    private async void ShrinkAfterLayout()
    {
        if (!IsInsideTree())
            return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (IsInstanceValid(this))
            Size = Vector2.Zero;
    }

    /// <summary>Adds a row a type's own scene lays out itself: after the other rows, or before <paramref name="before"/>.</summary>
    protected void InsertRow(Control row, Control? before = null)
    {
        AddChild(row);
        MoveChild(row, (before ?? _testButton).GetIndex());
    }

    /// <summary>Removes rows added with <see cref="InsertRow"/>, and empties the list.</summary>
    protected void RemoveRows(List<Control> rows)
    {
        foreach (var row in rows)
        {
            RemoveChild(row);
            row.QueueFree();
        }

        rows.Clear();
    }

    /// <summary>Raises <see cref="ActionRequested"/>, for a type's own scene.</summary>
    protected void RequestAction(string key) => EmitSignal(SignalName.ActionRequested, this, key);

    /// <summary>Raises <see cref="ChaptersEditRequested"/>, for a type's own scene.</summary>
    protected void RequestChaptersEdit() => EmitSignal(SignalName.ChaptersEditRequested, this);

    /// <summary>Raises <see cref="TemplateRowsEditRequested"/>, for a type's own scene.</summary>
    protected void RequestTemplateRowsEdit() => EmitSignal(SignalName.TemplateRowsEditRequested, this);

    /// <summary>Raises <see cref="TemplateRowTestRequested"/>, for a type's own scene.</summary>
    protected void RequestTemplateRowTest(int row) => EmitSignal(SignalName.TemplateRowTestRequested, this, row);

    /// <summary>Raises <see cref="SettingChanged"/>, for a type's own scene.</summary>
    protected void OnSettingChanged() => EmitSignal(SignalName.SettingChanged, this);

    /// <summary>
    /// Binds a control in a type's own scene to one of the node's settings: it shows the setting and writes changes
    /// into the node, raising <see cref="SettingChanged"/>. Bound again on each layout, it raises it once.
    /// </summary>
    protected void Bind(ISettingControl control, string key, ICanvasContext context) =>
        Bind(control, Type.FindSetting(key) ?? throw new ArgumentException($"{Type.Id} has no setting {key}.", nameof(key)), context);

    private void Bind(ISettingControl control, NodeSetting setting, ICanvasContext context)
    {
        control.Bind(Model, setting, setting.Kind == SettingKind.Choice ? NodeTypes.GetChoices(Model, setting, context) : null);
        if (_boundControls.Add(control))
            control.SettingChanged += OnSettingChanged;
    }

    private readonly HashSet<ISettingControl> _boundControls = [];

    /// <summary>Makes an action button in a type's own scene raise <see cref="ActionRequested"/>, and show when it's running.</summary>
    protected void BindAction(Button button, string key)
    {
        if (!_actionButtons.ContainsKey(key))
            button.Pressed += () => RequestAction(key);
        _actionButtons[key] = (button, button.Text);
        UpdateActionButton(key);
    }

    /// <summary>Shows a line of text, or hides it (null). Text with a tooltip shows a help cursor over it.</summary>
    protected static void ShowText(Label label, string? text, string? tooltip = null)
    {
        label.Visible = text is not null;
        label.Text = text ?? "";
        label.TooltipText = tooltip ?? "";
        label.MouseFilter = tooltip is null ? MouseFilterEnum.Ignore : MouseFilterEnum.Pass;
        label.MouseDefaultCursorShape = tooltip is null ? CursorShape.Arrow : CursorShape.Help;
    }

    /// <summary>
    /// Updates the Test Encode button: whether a test can run now (and why not), and its progress (0–1) while one
    /// is running for this node, when the button cancels it instead.
    /// </summary>
    public void SetTestState(bool enabled, string tooltip, double? progress)
    {
        _testEnabled = enabled;
        _testTooltip = tooltip;
        _testProgress = progress;
        UpdateTestButton();
    }

    private void UpdateTestButton()
    {
        if (_testButton is null)
            return;
        var running = _testProgress is not null;
        _testButton.Text = running ? $"Cancel Test · {_testProgress:P0}" : "Test Encode";
        _testButton.Disabled = !running && !_testEnabled;
        _testButton.TooltipText = running ? "Stop the test encode" : _testTooltip;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } click)
        {
            Selected = true;
            EmitSignal(SignalName.ContextMenuRequested, this, GetScreenPosition() + click.Position * GetGlobalTransformWithCanvas().Scale);
            AcceptEvent();
        }
    }

    /// <summary>Shows the node's problems under it, worst first.</summary>
    public void SetIssues(IEnumerable<GraphIssue> issues)
    {
        var list = issues.OrderByDescending(i => i.Level).ToList();
        foreach (var child in _issueBox.GetChildren())
        {
            _issueBox.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var issue in list)
        {
            var label = Row<Label>("IssueLabel");
            label.Text = IssueIcon(issue.Level) + " " + issue.Message;
            label.AddThemeColorOverride("font_color", IssueColor(issue.Level));
            _issueBox.AddChild(label);
        }

        _issueBox.Visible = list.Count > 0;
        Size = Vector2.Zero;
    }

    public static Color IssueColor(IssueLevel level) => level switch
    {
        IssueLevel.Error => ErrorTextColor,
        IssueLevel.Warning => WarningTextColor,
        _ => HintTextColor,
    };

    public static string IssueIcon(IssueLevel level) => level switch
    {
        IssueLevel.Error => "⛔",
        IssueLevel.Warning => "⚠",
        _ => "ⓘ",
    };

    /// <summary>Shows what each of an output file's tracks is, e.g. "English · AC3 · 2 ch → AAC".</summary>
    public void SetTrackDescriptions(IReadOnlyDictionary<string, string> descriptions)
    {
        foreach (var (port, label) in _trackLabels)
        {
            var number = (NodeTypes.TryParseTrackIndex(port) ?? 0) + 1;
            label.Text = descriptions.TryGetValue(port, out var description) ? $"{number}. {description}" : $"{number}. (add a track)";
            label.TooltipText = label.Text;
            label.Modulate = descriptions.ContainsKey(port) ? Colors.White : DimTextColor;
        }
    }

    /// <summary>A Template node's video list, whose test buttons <see cref="SetRowTestState"/> updates.</summary>
    protected TemplateVideoList? TemplateVideos { get; set; }

    /// <summary>Shows an action as running (disabled, with <paramref name="busyText"/>), or as ready again (null).</summary>
    public void SetActionBusy(string key, string? busyText)
    {
        if (busyText is null)
            _busyActions.Remove(key);
        else
            _busyActions[key] = busyText;
        UpdateActionButton(key);
    }

    private void UpdateActionButton(string key)
    {
        if (!_actionButtons.TryGetValue(key, out var action) || !IsInstanceValid(action.Button))
            return;
        var busy = _busyActions.TryGetValue(key, out var text);
        action.Button.Disabled = busy;
        action.Button.Text = busy ? text! : action.Text;
    }

    /// <summary>Updates a Template node row's test button, like <see cref="SetTestState"/>.</summary>
    public void SetRowTestState(int row, bool enabled, string tooltip, double? progress) =>
        TemplateVideos?.SetTestState(row, enabled, tooltip, progress);

    /// <summary>
    /// A row for one of an output file's tracks, for <see cref="SetTrackDescriptions"/> to describe, with a button
    /// to edit its title, language and flags. <see cref="ClearTrackRows"/> forgets them.
    /// </summary>
    protected TrackRow BuildTrackRow(string key)
    {
        var row = Row<TrackRow>("TrackRow");
        // The track rows are made per track, so connected here
        row.EditRequested += () => EmitSignal(SignalName.TrackOptionsRequested, this, key);
        _trackLabels[key] = row.Label;
        SetTrackDescriptions(new Dictionary<string, string>());
        return row;
    }

    protected void ClearTrackRows() => _trackLabels.Clear();
}
