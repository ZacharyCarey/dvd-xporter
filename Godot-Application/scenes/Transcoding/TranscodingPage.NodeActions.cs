using System;
using System.IO;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

// What nodes ask for besides their own settings: track options, chapters, detecting a crop, subtitle text, a
// template's videos, and which of a custom node's settings show on it.
public partial class TranscodingPage
{
    // ---- Track options ----

    private void EditTrackOptions(TranscodeNodeView view, string port)
    {
        var job = _result?.Jobs.Find(j => j.Output.Id == view.Model.Id);
        var index = job?.TrackPorts.IndexOf(port) ?? -1;
        var track = index >= 0 ? job!.Tracks[index] : null;
        var number = (NodeTypes.TryParseTrackIndex(port) ?? 0) + 1;
        _trackOptionsDialog.Edit(view.Model, port, $"Track {number}", track);
    }

    // ---- Node actions ----

    private void OnNodeAction(TranscodeNodeView view, string key)
    {
        if (view.Model.Type == NodeTypes.Crop && key == CropNode.DetectAction)
            DetectCrop(view);
        else if (view.Model.Type == NodeTypes.SubtitleText && key == SubtitleTextNode.EditAction)
            EditSubtitleText(view);
    }

    /// <summary>Opens the text of the subtitle track wired into a Subtitle Text node, to read and correct.</summary>
    private void EditSubtitleText(TranscodeNodeView view)
    {
        var canvas = _canvas;
        var sources = _sources;
        var folder = _folder;
        if (canvas is null || sources is null || folder is null)
            return;

        sources.Canvas = canvas;
        var source = SubtitleTextNode.FindSource(canvas, sources, view.Model);
        if (source is null)
        {
            ShowError("Connect a subtitle track from a Disc Video (or a Forced Subtitles node) to the Subtitle Text node first.");
            return;
        }

        _subtitleTextDialog.Edit(folder, sources, source);
    }

    /// <summary>
    /// Measures the black bars of the video wired into a Crop node, from samples across it, and sets the crop to
    /// remove them.
    /// </summary>
    private async void DetectCrop(TranscodeNodeView view)
    {
        var canvas = _canvas;
        var sources = _sources;
        var folder = _folder;
        if (canvas is null || sources is null || folder is null)
            return;
        var node = view.Model;
        var link = canvas.LinkInto(node.Id, "in");
        if (link is null)
        {
            ShowError("Connect the video to crop to the Crop node first.");
            return;
        }

        view.SetActionBusy(CropNode.DetectAction, "Detecting...");
        var work = Path.Combine(OutputPanel.TestFolderOf(folder), "work");
        string? message;
        try
        {
            sources.Canvas = canvas;
            var probe = TranscodeCompiler.CompileProbe(canvas, sources, sources, node.Id, link.FromNode, link.FromPort, CropDetector.Filter, out var problem);

            if (probe is null)
            {
                message = $"Couldn't measure the video: {problem}";
            }
            else if (probe.MissingDiscs.Count > 0)
            {
                message = $"\"{probe.MissingDiscs.First().Label}\" isn't on this computer, so its video can't be measured.";
            }
            else
            {
                SetStatus("Looking for black bars...");
                var result = await CropDetector.DetectAsync(probe, work,
                    fraction => Callable.From(() => view.SetActionBusy(CropNode.DetectAction, $"Detecting... {fraction:P0}")).CallDeferred(), default);
                message = ApplyCrop(node, result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = $"Couldn't measure the video: {ex.Message}";
        }

        if (IsInstanceValid(view))
        {
            view.SetActionBusy(CropNode.DetectAction, null);
            // Its fields show the new values
            view.Rebuild(sources, force: true);
        }

        SetStatus(message);
        ScheduleSave();
        Refresh();
    }

    /// <summary>Sets a Crop node from what was detected, and says what that was.</summary>
    private static string ApplyCrop(CanvasNode node, CropResult? result)
    {
        if (result is null)
            return "Couldn't find any picture to measure: the samples were all black.";
        if (!result.HasBars)
        {
            _ = new CropNode(node) { Width = null, Height = null, Left = null, Top = null };
            return $"No black bars: the picture fills the whole {result.FrameWidth}×{result.FrameHeight} frame, so nothing is cropped.";
        }

        _ = new CropNode(node) { Width = result.Width, Height = result.Height, Left = result.X, Top = result.Y };
        return $"Black bars found: cropping {result.FrameWidth}×{result.FrameHeight} to {result.Width}×{result.Height} "
            + $"(from {result.Samples} samples across the video).";
    }

    // ---- Templates and custom nodes ----

    /// <summary>The Output File a Template node's row makes (its first, if the template has several).</summary>
    private int? RowOutput(CanvasNode node, int row) =>
        _result?.RowOutputs.TryGetValue((node.Id, row), out var outputs) == true && outputs.Count > 0 ? outputs[0] : null;

    private void TestTemplateRow(TranscodeNodeView view, int row)
    {
        var output = RowOutput(view.Model, row);
        if (output is not null)
            _outputPanel.TestOrCancel(output.Value);
    }

    private void EditTemplateRows(TranscodeNodeView view)
    {
        if (_sources is null || _canvas is null || _folder is null)
            return;

        // A first row starts from the template's sample video
        var template = _folder.Project.Canvases.Find(c => c.Kind == CanvasKind.Template && c.Id == new TemplateNode(view.Model).Template);
        var found = template?.Nodes.Find(n => n.Type == NodeTypes.TemplateInput);
        var input = found is not null ? new SourceNode(found) : null;
        var sample = input is null ? null : new TemplateRow(input.Disc, input.Item, "");
        _sources.Canvas = _canvas;
        _templateRowsDialog.Edit(view.Model, _sources, sample);
    }

    /// <summary>Turns a Template node's row into ordinary nodes below it, and takes it off the node.</summary>
    private void DetachTemplateRow(CanvasNode node, int row)
    {
        var canvas = _canvas;
        if (canvas is null || _folder is null)
            return;
        var templateNode = new TemplateNode(node);
        var rows = templateNode.Rows;
        var template = _folder.Project.Canvases.Find(c => c.Kind == CanvasKind.Template && c.Id == templateNode.Template);
        if (template is null
            || row >= rows.Count)
        {
            return;
        }

        var below = canvas.Nodes.Count > 0 ? canvas.Nodes.Max(n => n.Y) + 450 : 0;
        var (nodes, links) = CanvasExpander.DetachRow(template, rows[row], canvas.NextNodeId(), node.X, below);
        canvas.Nodes.AddRange(nodes);
        canvas.Links.AddRange(links);
        foreach (var added in nodes)
            AddView(added);

        var description = TemplateRows.DescribeTarget(rows[row], _folder.Project);
        rows.RemoveAt(row);
        templateNode.Rows = rows;
        ScheduleSave();
        Refresh();
        SetStatus($"Detached {description}: its nodes are below the others, and no longer follow the template.");
    }

    private void ToggleExposed(CanvasNode node, string key)
    {
        var canvas = _canvas;
        if (canvas is null)
            return;
        var setting = new ExposedSetting(node.Id, key);
        if (!canvas.Exposed.Remove(setting))
            canvas.Exposed.Add(setting);
        ScheduleSave();
        Refresh();
    }

    // ---- Chapters ----

    private void EditChapters(TranscodeNodeView view)
    {
        if (_sources is null || _canvas is null)
            return;

        _sources.Canvas = _canvas;
        var current = ChapterLists.Resolve(view.Model, "out", _sources)?.Chapters ?? [];
        // What the node would give without the edited list
        var unedited = view.Model.Clone(view.Model.Id);
        _ = new ChaptersNode(unedited) { List = "" };
        var fromInput = ChapterLists.Resolve(unedited, "out", _sources)?.Chapters ?? [];
        _chapterEditor.Edit(view.Model, current, fromInput);
    }
}
