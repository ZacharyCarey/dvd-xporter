using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscDecoders.Common;
using DvdXporter.Core;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Subtitles;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A Subtitle Text node's track: reads its text off the subtitles' pictures (see <see cref="SubtitleOcr"/>) and shows
/// each line beside its picture to correct. Text that's read is saved straight away; corrections when they're saved.
/// </summary>
public partial class SubtitleTextDialog : ConfirmationDialog
{
    private enum Column
    {
        Time,
        Forced,
        Text,
    }

    /// <summary>Raised after the track's text is read or saved.</summary>
    [Signal]
    public delegate void AppliedEventHandler();

    private OptionButton _languageOption = null!;
    private Button _readButton = null!;
    private Label _statusLabel = null!;
    private Tree _lines = null!;
    private TextureRect _picture = null!;
    private Label _pictureMessage = null!;
    private Label _lineInfo = null!;
    private TextEdit _lineText = null!;
    private Button _previousButton = null!;
    private Button _nextButton = null!;
    private ConfirmationDialog _readAgainDialog = null!;

    private ProjectFolder? _folder;
    private ProjectTranscodeSources? _sources;
    private SubtitleTextSource? _source;
    // Each of the source's video files with its text (null until it's read), in order
    private readonly List<(string Clip, SubtitleText? Text)> _texts = [];
    // The lines listed, by their row
    private readonly List<(string Clip, SubtitleLine Line)> _rows = [];
    // The video files whose text has been corrected since it was saved
    private readonly HashSet<string> _edited = [];
    private CancellationTokenSource? _reading;
    private int _shown = -1;
    private bool _settingText;

    public override void _Ready()
    {
        _languageOption = GetNode<OptionButton>("%LanguageOption");
        _readButton = GetNode<Button>("%ReadButton");
        _statusLabel = GetNode<Label>("%StatusLabel");
        _lines = GetNode<Tree>("%Lines");
        _picture = GetNode<TextureRect>("%Picture");
        _pictureMessage = GetNode<Label>("%PictureMessage");
        _lineInfo = GetNode<Label>("%LineInfo");
        _lineText = GetNode<TextEdit>("%LineText");
        _previousButton = GetNode<Button>("%PreviousButton");
        _nextButton = GetNode<Button>("%NextButton");
        _readAgainDialog = GetNode<ConfirmationDialog>("%ReadAgainDialog");

        // Column titles and widths can't be set in a scene (the column count is in it)
        _lines.SetColumnTitle((int)Column.Time, "Time");
        _lines.SetColumnTitle((int)Column.Forced, "");
        _lines.SetColumnTitle((int)Column.Text, "Text");
        _lines.SetColumnExpand((int)Column.Time, false);
        _lines.SetColumnCustomMinimumWidth((int)Column.Time, 110);
        _lines.SetColumnExpand((int)Column.Forced, false);
        _lines.SetColumnCustomMinimumWidth((int)Column.Forced, 64);
    }

    /// <summary>Shows the text of <paramref name="source"/>'s track, as far as it's been read.</summary>
    public async void Edit(ProjectFolder folder, ProjectTranscodeSources sources, SubtitleTextSource source)
    {
        _folder = folder;
        _sources = sources;
        _source = source;
        _texts.Clear();
        _edited.Clear();
        foreach (var clip in source.Entries.Select(e => e.Clip).Distinct())
            _texts.Add((clip, SubtitleTexts.Load(folder, source.Disc, clip, source.Track.StreamId)));

        Title = $"Subtitle Text: {source.Track.Label}";
        Fill();
        ShowStatus(Summary());
        // Dialogs are laid out in the app's scaled UI space, so size to the visible area
        var area = GetTree().Root.GetVisibleRect().Size;
        PopupCentered(new Vector2I((int)(area.X * 0.85f), (int)(area.Y * 0.85f)));
        await FillLanguagesAsync(source);
    }

    /// <summary>The languages Tesseract can read, the track's own (or the one it was read in) chosen.</summary>
    private async Task FillLanguagesAsync(SubtitleTextSource source)
    {
        _languageOption.Clear();
        _readButton.Disabled = true;
        List<string> languages;
        try
        {
            languages = await Tesseract.ListLanguagesAsync();
        }
        catch (TesseractException ex)
        {
            ShowStatus(ex.Message);
            return;
        }

        if (source != _source)
            return;
        if (languages.Count == 0)
        {
            ShowStatus($"Tesseract has no languages to read. {Tesseract.InstallHelp}");
            return;
        }

        var readIn = _texts.Select(t => t.Text?.Language).FirstOrDefault(l => l is not null && languages.Contains(l));
        var chosen = readIn ?? SubtitleOcr.ChooseLanguage(source.Track.Language, languages);
        foreach (var language in languages)
            _languageOption.AddItem(language);
        _languageOption.Selected = Math.Max(0, chosen is null ? 0 : languages.IndexOf(chosen));
        _readButton.Disabled = false;
    }

    private void On_VisibilityChanged()
    {
        if (!Visible)
            _reading?.Cancel();
    }

    private void On_Confirmed()
    {
        var folder = _folder;
        var source = _source;
        if (folder is null || source is null)
            return;
        foreach (var (clip, text) in _texts)
        {
            if (text is not null && _edited.Contains(clip))
                SubtitleTexts.Save(folder, source.Disc, clip, source.Track.StreamId, text);
        }

        _edited.Clear();
        EmitSignal(SignalName.Applied);
    }

    // ---- Reading ----

    private void On_ReadButton_Pressed()
    {
        if (_reading is not null)
            _reading.Cancel();
        else if (_texts.Any(t => t.Text is not null))
            _readAgainDialog.PopupCentered();
        else
            Read();
    }

    private void On_ReadAgainDialog_Confirmed() => Read();

    /// <summary>Reads the text of every video file in the source, saving each as it's done.</summary>
    private async void Read()
    {
        var folder = _folder;
        var sources = _sources;
        var source = _source;
        if (folder is null || sources is null || source is null || _languageOption.Selected < 0)
            return;
        var disc = sources.GetSource(source.Disc);
        if (disc is null)
        {
            ShowStatus($"\"{source.Disc.Label}\" isn't on this computer, so its subtitles can't be read.");
            return;
        }

        var language = _languageOption.GetItemText(_languageOption.Selected);
        var cache = sources.Discs.Cache(source.Disc);
        var reading = _reading = new CancellationTokenSource();
        _readButton.Text = "Stop";
        _languageOption.Disabled = true;
        string message;
        try
        {
            for (int i = 0; i < _texts.Count; i++)
            {
                var clip = _texts[i].Clip;
                var file = _texts.Count > 1 ? $"Video file {i + 1} of {_texts.Count}: " : "";
                var progress = new Progress<SubtitleOcrProgress>(p =>
                {
                    if (reading == _reading)
                        ShowStatus(file + Describe(p));
                });
                var text = await SubtitleOcr.ReadAsync(disc, cache, clip, source.Track, language, progress, reading.Token);
                SubtitleTexts.Save(folder, source.Disc, clip, source.Track.StreamId, text);
                _texts[i] = (clip, text);
                _edited.Remove(clip);
            }

            message = Summary() + " Check each against its picture, then Save your corrections.";
        }
        catch (OperationCanceledException)
        {
            message = "Stopped. The video files read before then are kept.";
        }
        catch (Exception ex)
        {
            message = $"Couldn't read the text: {ex.Message}";
        }
        finally
        {
            _reading = null;
            reading.Dispose();
        }

        if (source != _source)
            return;
        _readButton.Text = "Read Text";
        _languageOption.Disabled = false;
        Fill();
        ShowStatus(message);
        EmitSignal(SignalName.Applied);
    }

    private static string Describe(SubtitleOcrProgress progress) => progress.Captions is null
        ? $"Reading the subtitles off the disc... {progress.DiscRead:P0}"
        : $"Reading the text... {progress.CaptionsRead} of {progress.Captions}";

    /// <summary>How much has been read, e.g. "812 captions (14 forced)."</summary>
    private string Summary()
    {
        var read = _texts.Where(t => t.Text is not null).ToList();
        if (read.Count == 0)
            return "Not read yet: choose Read Text.";
        var lines = read.SelectMany(t => t.Text!.Lines).ToList();
        var forced = lines.Count(l => l.Forced);
        var summary = $"{lines.Count} {(lines.Count == 1 ? "caption" : "captions")}{(forced > 0 ? $" ({forced} forced)" : "")}.";
        return read.Count < _texts.Count ? $"{read.Count} of {_texts.Count} video files read: {summary}" : summary;
    }

    private void ShowStatus(string text)
    {
        _statusLabel.Text = text;
        _statusLabel.TooltipText = text;
    }

    // ---- The lines ----

    private void Fill()
    {
        _lines.Clear();
        _rows.Clear();
        var root = _lines.CreateItem();
        var contents = _source is not null ? _sources?.GetContents(_source.Disc) : null;
        var several = _texts.Count > 1;
        for (int c = 0; c < _texts.Count; c++)
        {
            var (clip, text) = _texts[c];
            if (text is null)
                continue;
            var clipStart = contents?.FindClip(clip)?.Start ?? DiscTime.Zero;
            foreach (var line in text.Lines)
            {
                var item = _lines.CreateItem(root);
                // Times from the start of their video file, numbered when there are several
                item.SetText((int)Column.Time, (several ? $"{c + 1} · " : "") + Format(line.Start - clipStart));
                item.SetText((int)Column.Forced, line.Forced ? "Forced" : "");
                SetRowText(item, line.Text);
                item.SetMetadata(0, _rows.Count);
                _rows.Add((clip, line));
            }
        }

        _readButton.Text = _reading is not null ? "Stop" : "Read Text";
        if (_rows.Count > 0)
            root.GetChild(0).Select(0);
        else
            ShowLine(-1);
    }

    private static void SetRowText(TreeItem item, string text)
    {
        item.SetText((int)Column.Text, string.Join(" / ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        item.SetTooltipText((int)Column.Text, text);
    }

    private void On_Lines_ItemSelected()
    {
        var item = _lines.GetSelected();
        if (item is not null)
            ShowLine((int)item.GetMetadata(0));
    }

    private void ShowLine(int index)
    {
        _shown = index;
        var has = index >= 0 && index < _rows.Count;
        _lineText.Editable = has;
        _previousButton.Disabled = !has || index == 0;
        _nextButton.Disabled = !has || index == _rows.Count - 1;
        _settingText = true;
        _lineText.Text = has ? _rows[index].Line.Text : "";
        _settingText = false;
        _picture.Texture = null;
        if (!has || _sources is null || _source is null)
        {
            _lineInfo.Text = "";
            _pictureMessage.Text = _texts.Any(t => t.Text is not null) ? "" : "Read the text to see the captions here.";
            return;
        }

        var (clip, line) = _rows[index];
        var path = SubtitleTexts.PicturePath(_sources.Discs.Cache(_source.Disc), clip, _source.Track.StreamId, line.Picture);
        var image = new Image();
        if (File.Exists(path) && image.Load(path) == Error.Ok)
            _picture.Texture = ImageTexture.CreateFromImage(image);
        _pictureMessage.Text = _picture.Texture is null ? "This caption's picture isn't on this computer: read the text again to see it." : "";
        var length = Format(line.End - line.Start);
        _lineInfo.Text = $"Caption {index + 1} of {_rows.Count} · shown for {length}{(line.Forced ? " · forced" : "")}";
    }

    private void On_LineText_TextChanged()
    {
        if (_settingText || _shown < 0 || _shown >= _rows.Count)
            return;
        var (clip, line) = _rows[_shown];
        line.Text = _lineText.Text;
        _edited.Add(clip);
        SetRowText(_lines.GetRoot().GetChild(_shown), line.Text);
    }

    private void On_LineText_GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, AltPressed: true } key && key.Keycode is Key.Up or Key.Down)
        {
            Move(key.Keycode == Key.Up ? -1 : 1);
            _lineText.AcceptEvent();
        }
    }

    private void On_PreviousButton_Pressed() => Move(-1);

    private void On_NextButton_Pressed() => Move(1);

    /// <summary>Shows the line before or after, ready to type in.</summary>
    private void Move(int by)
    {
        var index = _shown + by;
        if (index < 0 || index >= _rows.Count)
            return;
        var item = _lines.GetRoot().GetChild(index);
        item.Select(0);
        _lines.ScrollToItem(item);
        _lineText.GrabFocus();
    }

    // e.g. "1:02:03.4"
    private static string Format(DiscTime time)
    {
        var span = time.ToPreciseTime().AsTimeSpan();
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 100}";
    }
}
