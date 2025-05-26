using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DvdXporter.Core;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;
using Godot;
using CancellationTokenSource = System.Threading.CancellationTokenSource;

namespace DvdXporter.UI;

/// <summary>
/// The canvas's outputs: the selected one's ffmpeg command and details, and test encoding part of it, with what the
/// test says about the full encode (its size, time and, if asked for, quality).
/// </summary>
public partial class OutputPanel : VBoxContainer
{
    private const string TestEncodesFolderName = "Test Encodes";

    /// <summary>A test started, made progress or finished, or its range changed: the nodes' test buttons follow.</summary>
    [Signal]
    public delegate void TestStateChangedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>Something went wrong that the user needs to read.</summary>
    [Signal]
    public delegate void ErrorOccurredEventHandler(string message);

    /// <summary>The output whose command is shown.</summary>
    public int? SelectedOutput { get; private set; }

    public TranscodeJob? SelectedJob => _result?.Jobs.Find(j => j.Output.Id == SelectedOutput);

    public bool IsTesting => _testCancellation is not null;

    /// <summary>The output being test encoded, and how far it's got.</summary>
    public int? TestingOutput { get; private set; }

    public double TestFraction { get; private set; }

    /// <summary>The stretch a test encodes, as the user set it, e.g. "from 5:00 for 30 s".</summary>
    public string TestRangeText => $"from {(_testStartEdit.Text.Trim() is { Length: > 0 } start ? start : "0:00")} for {_testLengthSpin.Value:0} s";

    private OptionButton _jobOption = null!;
    private Label _jobInfoLabel = null!;
    private TextEdit _commandText = null!;
    private Button _copyCommandButton = null!;
    private LineEdit _testStartEdit = null!;
    private SpinBox _testLengthSpin = null!;
    private Button _testButton = null!;
    private Button _cancelTestButton = null!;
    private ProgressBar _testProgress = null!;
    private Button _openTestButton = null!;
    private CheckBox _qualityCheck = null!;
    private Label _testResultLabel = null!;

    private ProjectFolder? _folder;
    private ProjectTranscodeSources? _sources;
    private TranscodeCanvas? _canvas;
    private CompileResult? _result;

    private CancellationTokenSource? _testCancellation;
    private string? _testOutputPath;
    // The last test's findings, for the output they were for
    private (int Output, string Text)? _testResult;

    public override void _Ready()
    {
        _jobOption = GetNode<OptionButton>("%JobOption");
        _jobInfoLabel = GetNode<Label>("%JobInfoLabel");
        _commandText = GetNode<TextEdit>("%CommandText");
        _copyCommandButton = GetNode<Button>("%CopyCommandButton");
        _testStartEdit = GetNode<LineEdit>("%TestStartEdit");
        _testLengthSpin = GetNode<SpinBox>("%TestLengthSpin");
        _testButton = GetNode<Button>("%TestButton");
        _cancelTestButton = GetNode<Button>("%CancelTestButton");
        _testProgress = GetNode<ProgressBar>("%TestProgress");
        _openTestButton = GetNode<Button>("%OpenTestButton");
        _qualityCheck = GetNode<CheckBox>("%QualityCheck");
        _testResultLabel = GetNode<Label>("%TestResultLabel");

        // Which score this ffmpeg can give
        var vmaf = TestReports.HasVmaf();
        _qualityCheck.Text = vmaf ? "Quality score (VMAF)" : "Quality score (SSIM)";
        _qualityCheck.TooltipText = "After the test, compare it with the video as it was before its codec (after any filters), "
            + "to see how much the encoding lost. Takes about as long again as the test, more for 4K."
            + (vmaf ? "" : "\n\nThis ffmpeg doesn't include VMAF (libvmaf), so the simpler SSIM score is used. "
                + "An ffmpeg build with libvmaf gives VMAF, which better matches what people see.");
        ShowTestButtons();
    }

    /// <summary>Where a project's test encodes go.</summary>
    public static string TestFolderOf(ProjectFolder folder) =>
        Path.Combine(folder.FolderPath, ProjectFolder.VideoFilesFolderName, TestEncodesFolderName);

    /// <summary>For another project: stops any test, and forgets its result and which output was shown.</summary>
    public void Forget()
    {
        _testCancellation?.Cancel();
        _testOutputPath = null;
        SelectedOutput = null;
    }

    public void SetProject(ProjectFolder folder, ProjectTranscodeSources sources)
    {
        _folder = folder;
        _sources = sources;
    }

    /// <summary>For another canvas: shows its first output, once it's compiled.</summary>
    public void ClearSelection() => SelectedOutput = null;

    /// <summary>Shows <paramref name="canvas"/>'s outputs, as just compiled, keeping the one shown if it's still there.</summary>
    public void Show(CompileResult? result, TranscodeCanvas? canvas)
    {
        _result = result;
        _canvas = canvas;
        var jobs = result?.Jobs ?? [];
        if (SelectedOutput is null || jobs.All(j => j.Output.Id != SelectedOutput))
            SelectedOutput = jobs.FirstOrDefault()?.Output.Id;

        _jobOption.Clear();
        foreach (var job in jobs)
            _jobOption.AddItem(job.LibraryPath is null ? job.Name : $"{job.Name}  →  {job.LibraryPath}");
        _jobOption.Disabled = jobs.Count == 0;
        if (jobs.FindIndex(j => j.Output.Id == SelectedOutput) is >= 0 and var index)
            _jobOption.Select(index);
        ShowSelectedJob();
    }

    /// <summary>Shows <paramref name="outputId"/>'s command.</summary>
    public void Select(int? outputId)
    {
        SelectedOutput = outputId;
        if (_result?.Jobs.FindIndex(j => j.Output.Id == outputId) is >= 0 and var index)
            _jobOption.Select(index);
        ShowSelectedJob();
    }

    /// <summary>Test encodes the output shown, unless it can't be.</summary>
    public void StartTest()
    {
        var job = SelectedJob;
        if (job is null || ReadTestRange() is null)
            return;

        if (!job.IsReady)
            return;

        RunTest(job);
    }

    /// <summary>Test encodes <paramref name="output"/>, or cancels the test already running for it.</summary>
    public void TestOrCancel(int output)
    {
        if (_testCancellation is not null && TestingOutput == output)
        {
            _testCancellation.Cancel();
            return;
        }

        Select(output);
        StartTest();
    }

    /// <summary>Why an output can't be test encoded now, or null if it can.</summary>
    public string? TestBlocker(TranscodeJob? job) =>
        job is null ? "Connect this to an Output File first."
        : _result?.HasErrors(job.Output.Id) == true ? "Fix the problems with this output first."
        : job.MissingDiscs.Count > 0 ? "A disc this output reads isn't on this computer."
        : null;

    private void On_JobOption_ItemSelected(long index) => Select(_result?.Jobs.ElementAtOrDefault((int)index)?.Output.Id);

    private void On_CopyCommandButton_Pressed() => DisplayServer.ClipboardSet(_commandText.Text);

    private void On_TestButton_Pressed() => StartTest();

    private void On_TestStartEdit_TextChanged(string text) => EmitSignal(SignalName.TestStateChanged);

    private void On_TestLengthSpin_ValueChanged(double value) => EmitSignal(SignalName.TestStateChanged);

    private void On_CancelTestButton_Pressed() => _testCancellation?.Cancel();

    private void On_OpenTestButton_Pressed()
    {
        var path = _testOutputPath;
        if (path is not null && (File.Exists(path) || Directory.Exists(path)))
            OS.ShellOpen(path);
    }

    private void ShowSelectedJob()
    {
        var job = SelectedJob;
        var folder = _folder;
        if (job is null || folder is null)
        {
            _commandText.Text = "";
            _jobInfoLabel.Text = "Add an Output File node to see its ffmpeg command.";
            UpdateTestControls();
            return;
        }

        var target = job.LibraryPath is not null ? $"<Emby library>/{job.LibraryPath}" : $"{ProjectFolder.ToSafeFileName(job.Name)}.{job.Extension}";
        _commandText.Text = _result?.HasErrors(job.Output.Id) == true
            ? "Fix the problems with this output to see its ffmpeg command."
            : TranscodeJob.ToCommandLine(job.BuildArguments(target, Path.Combine(TestFolderOf(folder), "work")));

        var info = new List<string>();
        var duration = job.Duration;
        if (duration is not null)
            info.Add($"Length {NodeTypes.FormatDuration(duration.Value)}");
        if (job.Export is null)
            info.Add("not exported (connect an Export node to put it in the library)");
        if (job.Measurements.Count > 0)
            info.Add("loudness is measured first, then applied (the command shows it without the measurement)");
        if (job.MissingDiscs.Count > 0)
            info.Add($"{string.Join(", ", job.MissingDiscs.Select(d => $"\"{d.Label}\""))} isn't on this computer");

        _jobInfoLabel.Text = info.Count > 0 ? char.ToUpperInvariant(info[0][0]) + string.Join(" · ", info)[1..] : "";
        ShowTestResult();
        UpdateTestControls();
    }

    private void ShowTestResult()
    {
        var shown = _testResult is not null && _testResult.Value.Output == SelectedOutput;
        _testResultLabel.Visible = shown;
        _testResultLabel.Text = shown ? _testResult!.Value.Text : "";
        _testResultLabel.TooltipText = "Worked out from the test's stretch of video. Busier or calmer scenes elsewhere "
            + "will make the full encode bigger or smaller, and the time depends on what else the computer is doing.";
    }

    private void UpdateTestControls()
    {
        ShowTestButtons();
        EmitSignal(SignalName.TestStateChanged);
    }

    private void ShowTestButtons()
    {
        var running = _testCancellation is not null;
        var job = SelectedJob;
        _testButton.Disabled = running || TestBlocker(job) is not null;
        _testButton.TooltipText = job is null ? "" : TestBlocker(job) ?? "Encode part of this output to check its quality and sync.";
        _cancelTestButton.Visible = running;
        _openTestButton.Disabled = _testOutputPath is null || !(File.Exists(_testOutputPath) || Directory.Exists(_testOutputPath)) || running;
        _copyCommandButton.Disabled = job is null || _result?.HasErrors(job.Output.Id) != false;
    }

    private (PreciseTime Start, PreciseTime Length)? ReadTestRange()
    {
        var text = _testStartEdit.Text.Trim();
        var start = PreciseTime.Zero;
        if (text.Length > 0 && !PreciseTime.TryParse(text, out start))
        {
            EmitSignal(SignalName.ErrorOccurred, "The test start time isn't a time. Use e.g. 5:00 or 1:02:30.");
            return null;
        }

        return (start, new PreciseTime(0, 0, (long)_testLengthSpin.Value, 0));
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    private async void RunTest(TranscodeJob job)
    {
        var canvas = _canvas;
        var folder = _folder;
        if (canvas is null || folder is null || ReadTestRange() is not var (start, length))
            return;

        var testFolder = TestFolderOf(folder);
        var name = $"{ProjectFolder.ToSafeFileName(canvas.Name)} - {ProjectFolder.ToSafeFileName(job.Name)}";
        // A gallery's test is a folder of its images, made afresh
        var output = job.IsImageSequence ? Path.Combine(testFolder, name) : Path.Combine(testFolder, $"{name}.{job.Extension}");
        var work = Path.Combine(testFolder, "work");
        List<string> args;
        try
        {
            Directory.CreateDirectory(testFolder);
            if (job.IsImageSequence && Directory.Exists(output))
                Directory.Delete(output, recursive: true);
            args = job.BuildArguments(output, work, new TestRange(start, length), writeFiles: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmitSignal(SignalName.ErrorOccurred, $"Couldn't set up the test: {ex.Message}");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        TestingOutput = job.Output.Id;
        TestFraction = 0;
        _testOutputPath = output;
        _testProgress.Value = 0;
        UpdateTestControls();
        SetStatus($"Test encoding \"{job.Name}\"...");

        var testLength = job.Duration is not null && job.Duration.Value - start < length ? job.Duration.Value - start : length;
        string? error;
        var stats = new FFmpegRunStats();
        _testResult = null;
        ShowTestResult();
        try
        {
            var sources = _sources;
            // Loudness nodes that measure first measure the test's own stretch
            if (job.Measurements.Count > 0 && sources is not null)
            {
                SetStatus("Measuring the loudness...");
                sources.Canvas = canvas;
                var measured = await LoudnessMeasurer.MeasureAsync(canvas, sources, sources, job, new TestRange(start, length),
                    Path.Combine(work, "loudness"), cancellation.Token);
                args = job.BuildArguments(output, work, new TestRange(start, length), writeFiles: true, measured);
                SetStatus($"Test encoding \"{job.Name}\"...");
            }

            error = await FFmpegRunner.RunAsync(args, testLength, Path.Combine(testFolder, $"{name}.log"),
                fraction => Callable.From(() =>
                {
                    _testProgress.Value = fraction * 100;
                    TestFraction = fraction;
                    EmitSignal(SignalName.TestStateChanged);
                }).CallDeferred(), cancellation.Token, stats);
            if (error is null && job.IsImageSequence)
            {
                var count = GalleryImages.Tidy(job, output, Path.Combine(testFolder, $"{name}.log"));
                _testResult = (job.Output.Id, count == 0
                    ? "No pictures in this stretch of the video."
                    : $"{count} {(count == 1 ? "picture" : "pictures")} in the test's {length.AsTimeSpan().TotalSeconds:0} s. Open Result shows the folder.");
                ShowTestResult();
            }
            else if (error is null)
            {
                try
                {
                    await ReportTest(job, canvas, output, new TestRange(start, length), stats, cancellation);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException or FormatException
                    or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _testResult = (job.Output.Id, $"Couldn't measure the test: {ex.Message}");
                    ShowTestResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            error = null;
            SetStatus("Test encode cancelled.");
            _testOutputPath = null;
        }

        if (_testCancellation == cancellation)
            _testCancellation = null;
        cancellation.Dispose();
        UpdateTestControls();

        if (error is not null)
        {
            _testOutputPath = null;
            SetStatus($"The test encode of \"{job.Name}\" failed.");
            EmitSignal(SignalName.ErrorOccurred,
                $"ffmpeg couldn't encode \"{job.Name}\":\n\n{error}\n\nThe full log is in {Path.Combine(testFolder, name + ".log")}.");
        }
        else if (_testOutputPath is not null)
        {
            SetStatus($"Test encode finished: {output}");
        }

        UpdateTestControls();
    }

    /// <summary>
    /// What a finished test says about the full encode: its size by kind of track, how long it'll take, and (if
    /// asked for) the picture quality.
    /// </summary>
    private async Task ReportTest(TranscodeJob job, TranscodeCanvas canvas, string output, TestRange range, FFmpegRunStats stats,
        CancellationTokenSource cancellation)
    {
        var sources = _sources;
        var folder = _folder;
        if (sources is null || folder is null)
            return;

        SetStatus("Working out the full size and time...");
        var report = await TestReports.MeasureAsync(job, output, stats, cancellation.Token);
        var lines = new List<string>();
        var full = report.FullLength is not null;
        var kinds = report.Tracks.GroupBy(t => t.Label.Split(' ')[0]).Select(g => $"{g.Key.ToLowerInvariant()} {FormatSize(g.Sum(t => t.Bytes))}");
        lines.Add((full ? $"Full size: about {FormatSize(report.EstimatedBytes)}" : $"Test size: {FormatSize(report.SampleBytes)}")
            + $" ({report.Bitrate / 1e6:0.0} Mb/s) · {string.Join(", ", kinds)}");
        var time = report.EstimatedTime;
        if (time is not null)
            lines.Add($"Full encode: about {FormatTime(time.Value)} ({report.Speed:0.0}× real time on this computer)");

        if (_qualityCheck.ButtonPressed)
        {
            SetStatus(TestReports.HasVmaf() ? "Measuring the quality (VMAF)..." : "Measuring the quality (SSIM)...");
            _testProgress.Indeterminate = true;
            try
            {
                sources.Canvas = canvas;
                var quality = await TestReports.MeasureQualityAsync(canvas, sources, sources, job, output, range,
                    Path.Combine(TestFolderOf(folder), "work", "quality"), cancellation.Token);
                lines.Add(quality is null ? "Quality: the video is copied, so nothing is lost." : $"Quality: {quality}");
            }
            finally
            {
                _testProgress.Indeterminate = false;
            }
        }

        _testResult = (job.Output.Id, string.Join("\n", lines));
        ShowTestResult();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        >= 1_000_000 => $"{bytes / 1e6:0} MB",
        _ => $"{bytes / 1e3:0} KB",
    };

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{(int)Math.Round(time.TotalMinutes)} min" : $"{Math.Max(1, (int)time.TotalSeconds)} s";
}
