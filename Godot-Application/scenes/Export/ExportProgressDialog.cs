using System;
using System.Collections.Generic;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The export's progress, in a window that holds the app until the export ends: nothing else can be clicked, so
/// nothing changes under it. The only way out early is Cancel, which asks first, in this window: a dialog of its own
/// could end up behind it.
/// </summary>
public partial class ExportProgressDialog : Window
{
    /// <summary>The user agreed to stop exporting.</summary>
    [Signal]
    public delegate void CancelRequestedEventHandler();

    private Label _destinationLabel = null!;
    private Label _fileLabel = null!;
    private Label _stageLabel = null!;
    private ProgressBar _fileProgress = null!;
    private ProgressBar _overallProgress = null!;
    private Label _timeLabel = null!;
    private ItemList _fileList = null!;
    private Button _cancelButton = null!;
    private HBoxContainer _stopQuestion = null!;
    private Label _stopQuestionLabel = null!;

    // What to do once the user agrees to stop
    private Action? _afterStopping;
    private bool _stopping;
    // What's asked when Cancel is pressed, and what stopping leaves
    private string _stopAsk = "";
    private string _stopNote = "";

    public override void _Ready()
    {
        _destinationLabel = GetNode<Label>("%DestinationLabel");
        _fileLabel = GetNode<Label>("%FileLabel");
        _stageLabel = GetNode<Label>("%StageLabel");
        _fileProgress = GetNode<ProgressBar>("%FileProgress");
        _overallProgress = GetNode<ProgressBar>("%OverallProgress");
        _timeLabel = GetNode<Label>("%TimeLabel");
        _fileList = GetNode<ItemList>("%FileList");
        _cancelButton = GetNode<Button>("%CancelButton");
        _stopQuestion = GetNode<HBoxContainer>("%StopQuestion");
        _stopQuestionLabel = GetNode<Label>("%StopQuestionLabel");
    }

    private void On_CancelButton_Pressed() => AskToStop(_stopAsk, null);

    /// <summary>The window's close button cancels too, after asking.</summary>
    private void On_CloseRequested() => AskToStop(_stopAsk, null);

    private void On_StopButton_Pressed()
    {
        ShowQuestion(false);
        Stopping();
        EmitSignal(SignalName.CancelRequested);
        var then = _afterStopping;
        _afterStopping = null;
        then?.Invoke();
    }

    private void On_KeepButton_Pressed()
    {
        ShowQuestion(false);
        _afterStopping = null;
    }

    /// <summary>Opens over the app, listing the files to make (by name), all waiting.</summary>
    /// <param name="heading">What's happening, e.g. "Exporting to /media/Movies".</param>
    /// <param name="stopQuestion">What Cancel asks, e.g. "Stop exporting?".</param>
    /// <param name="stopNote">What stopping leaves behind, for the question.</param>
    public void Open(string heading, IEnumerable<string> files, string stopQuestion, string stopNote)
    {
        _stopping = false;
        _stopAsk = stopQuestion;
        _stopNote = stopNote;
        ShowQuestion(false);
        _cancelButton.Disabled = false;
        _destinationLabel.Text = heading;
        _fileLabel.Text = "Starting...";
        _stageLabel.Text = "";
        _timeLabel.Text = "";
        _fileProgress.Value = 0;
        _overallProgress.Value = 0;
        _fileList.Clear();
        foreach (var file in files)
        {
            var index = _fileList.AddItem($"{file}  ·  Waiting");
            _fileList.SetItemMetadata(index, file);
            _fileList.SetItemCustomFgColor(index, new Color(1, 1, 1, 0.5f));
        }

        PopupCentered(new Vector2I(640, 460));
    }

    /// <summary>Shows how far the file being made has got, and the whole export.</summary>
    /// <param name="index">Which of the files, from 0.</param>
    public void ShowProgress(int index, int count, string name, string stage, double fraction, double overall, string time)
    {
        if (_stopping)
            return;
        _fileLabel.Text = $"{index + 1} of {count}: {name}";
        _stageLabel.Text = stage;
        _fileProgress.Value = fraction * 100;
        _overallProgress.Value = overall * 100;
        _timeLabel.Text = time;
    }

    /// <summary>Shows how one file is going, or went, in the list.</summary>
    public void SetFileState(int index, string state, Color? color)
    {
        if (index < 0 || index >= _fileList.ItemCount)
            return;
        _fileList.SetItemText(index, $"{_fileList.GetItemMetadata(index).AsString()}  ·  {state}");
        if (color is not null)
            _fileList.SetItemCustomFgColor(index, color.Value);
        else
            _fileList.SetItemCustomFgColor(index, Colors.White);
    }

    /// <summary>
    /// Asks whether to stop, in this window (which holds the app, so the question has to be asked here).
    /// If they agree, it's cancelled and <paramref name="then"/> is done.
    /// </summary>
    public void AskToStop(string question, Action? then)
    {
        if (_stopping)
        {
            then?.Invoke();
            return;
        }

        _afterStopping = then;
        _stopQuestionLabel.Text = $"{question} {_stopNote}";
        ShowQuestion(true);
    }

    // The question takes the Cancel button's place until it's answered
    private void ShowQuestion(bool asking)
    {
        _stopQuestion.Visible = asking;
        _cancelButton.Visible = !asking;
    }

    private void Stopping()
    {
        _stopping = true;
        _cancelButton.Disabled = true;
        _stageLabel.Text = "Stopping...";
    }
}
