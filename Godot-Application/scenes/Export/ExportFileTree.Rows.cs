using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

public partial class ExportFileTree
{
    private static readonly Color GroupRowColor = new(1, 1, 1, 0.05f);
    // A version the user picked over the preferred one
    private static readonly Color PickedSourceColor = new(0.6f, 0.78f, 1);

    // Files that can't be picked list at most this many problems in their tooltip
    private const int TooltipProblems = 10;

    /// <summary>A row of the tree, showing a library folder or a file.</summary>
    private abstract class Row(ExportFileTree tree, TreeItem item)
    {
        protected ExportFileTree Tree { get; } = tree;

        public TreeItem Item { get; } = item;

        /// <summary>What it shows, to find it again after a rebuild.</summary>
        public abstract string Key { get; }
    }

    /// <summary>A library folder, e.g. "Season 01", ticked when all its files that can be exported are.</summary>
    private sealed class GroupRow : Row
    {
        public GroupRow(ExportFileTree tree, TreeItem parent, string group, int count)
            : base(tree, tree.CreateItem(parent))
        {
            Group = group;
            Item.SetCellMode((int)Column.Pick, TreeItem.TreeCellMode.Check);
            Item.SetText((int)Column.File, $"{group}  ({count} {(count == 1 ? "file" : "files")})");
            foreach (var column in Enum.GetValues<Column>())
                Item.SetCustomBgColor((int)column, GroupRowColor);
            Item.Collapsed = tree._collapsed.Contains(group);
        }

        public string Group { get; }

        public List<FileRow> Files { get; } = [];

        public List<ExportFile> Exportable => Files.Select(r => r.File).Where(f => f.CanExport).ToList();

        public override string Key => "group:" + Group;

        public void UpdateChecks()
        {
            foreach (var row in Files)
                row.Item.SetChecked((int)Column.Pick, Tree._picks.IsPicked(row.File));

            var valid = Exportable.Count;
            var picked = Files.Count(r => Tree._picks.IsPicked(r.File));
            Item.SetEditable((int)Column.Pick, valid > 0 && !Tree._exporting);
            Item.SetChecked((int)Column.Pick, valid > 0 && picked == valid);
            Item.SetIndeterminate((int)Column.Pick, picked > 0 && picked < valid);
            Item.SetTooltipText((int)Column.Pick, valid == 0 ? "None of these can be exported yet" : $"{picked} of {valid} picked");
        }
    }

    private sealed class FileRow : Row
    {
        // The versions in the Source dropdown, in its order, if there's more than one to choose from
        private readonly List<ExportFile> _sourceOptions = [];

        public FileRow(ExportFileTree tree, GroupRow groupRow, ExportFile file)
            : base(tree, tree.CreateItem(groupRow.Item))
        {
            File = file;
            var job = file.Job;
            Item.SetCellMode((int)Column.Pick, TreeItem.TreeCellMode.Check);
            Item.SetEditable((int)Column.Pick, file.CanExport && !tree._exporting);
            Item.SetTooltipText((int)Column.Pick, file.CanExport ? "Export this file" : CantExportReason());

            // Hints are in the details and the tooltip, but only warnings and errors get a symbol, so they stand out
            if (file.WorstLevel is >= IssueLevel.Warning and { } level)
            {
                Item.SetText((int)Column.Problems, TranscodeNodeView.IssueIcon(level));
                Item.SetCustomColor((int)Column.Problems, TranscodeNodeView.IssueColor(level));
                Item.SetTextAlignment((int)Column.Problems, HorizontalAlignment.Center);
            }

            if (file.Issues.Count > 0)
                Item.SetTooltipText((int)Column.Problems, ProblemTooltip());

            if (file.MissingDiscs.Count > 0)
            {
                Item.SetIcon((int)Column.Disc, tree.MissingIcon);
                Item.SetTooltipText((int)Column.Disc, $"Missing {(file.MissingDiscs.Count == 1 ? "disc" : "discs")}: "
                    + $"{ExportText.DiscNames(file.MissingDiscs)}. Use Locate on the project overview to find it.");
            }
            else if (file.Discs.Count > 0)
            {
                Item.SetTooltipText((int)Column.Disc, $"From {ExportText.DiscNames(file.Discs)}.");
            }

            Item.SetText((int)Column.File, file.LibraryPath is not null ? Path.GetFileName(file.LibraryPath) : file.Name);
            Item.SetTooltipText((int)Column.File, file.Destination ?? file.LibraryPath ?? file.Name);
            ShowSource();
            Item.SetText((int)Column.Canvas, file.Canvas?.Name ?? "Information");
            Item.SetTooltipText((int)Column.Canvas, file.Canvas is not null ? $"Made on the canvas \"{file.Canvas.Name}\"" : "Picked in the Information step's Files");
            Item.SetText((int)Column.Length, job?.Duration is not null ? ExportText.FormatLength(job.Duration.Value.AsTimeSpan()) : "");
            if (!file.CanExport)
            {
                foreach (var column in new[] { Column.File, Column.Source, Column.Canvas, Column.Length })
                    Item.SetCustomColor((int)column, ExportColors.Dim);
            }

            UpdateRun();
        }

        public ExportFile File { get; }

        public override string Key => File.Key;

        /// <summary>The version picked in the Source dropdown, if it has one.</summary>
        public ExportFile? PickedSource() => _sourceOptions.Count == 0
            ? null
            : _sourceOptions[Math.Clamp((int)Item.GetRange((int)Column.Source), 0, _sourceOptions.Count - 1)];

        /// <summary>Shows how its export is going.</summary>
        public void UpdateRun()
        {
            const int column = (int)Column.Run;
            Item.SetCustomColor(column, ExportColors.Dim);
            Item.SetTooltipText(column, "");
            var run = Tree._runs.GetValueOrDefault(File.Key);
            if (run is null)
            {
                Item.SetText(column, "");
                return;
            }

            Item.SetText(column, run.Text);
            var color = run.Color;
            if (color is not null)
                Item.SetCustomColor(column, color.Value);
            else if (run.Active)
                Item.ClearCustomColor(column);
            Item.SetTooltipText(column, run.Outcome switch
            {
                ExportOutcome.Done => run.Destination ?? "",
                ExportOutcome.Failed or ExportOutcome.Skipped => run.Error ?? "",
                _ => "",
            });
        }

        /// <summary>
        /// The release it's made from. With more than one version it can be made from now, it's a dropdown of them;
        /// the tooltip lists every version, and why any can't be used.
        /// </summary>
        private void ShowSource()
        {
            const int column = (int)Column.Source;
            var usable = File.Alternates.Where(a => a.CanExport).ToList();
            if (usable.Count > 1 && File.CanExport)
            {
                _sourceOptions.AddRange(usable);
                Item.SetCellMode(column, TreeItem.TreeCellMode.Range);
                Item.SetText(column, string.Join(",", usable.Select(a => a.SourceName.Replace(",", " "))));
                Item.SetRange(column, usable.IndexOf(File));
                Item.SetEditable(column, !Tree._exporting);
            }
            else
            {
                Item.SetText(column, File.Alternates.Count > 1 || File.Release is not null ? File.SourceName : "");
            }

            if (File.IsPicked)
                Item.SetCustomColor(column, PickedSourceColor);
            if (File.Alternates.Count > 1)
                Item.SetTooltipText(column, ExportText.DescribeAlternates(File) + (usable.Count > 1 ? "\n\nClick to pick which one to export." : ""));
            else if (File.Release is not null)
                Item.SetTooltipText(column, $"Made from \"{File.Release.Name}\".");
        }

        private string CantExportReason() => File.State switch
        {
            ExportState.MissingDisc => $"Its disc isn't on this computer: {ExportText.DiscNames(File.MissingDiscs)}",
            _ => "Fix its errors to export it",
        };

        private string ProblemTooltip()
        {
            var lines = File.Issues.Take(TooltipProblems).Select(i => $"{TranscodeNodeView.IssueIcon(i.Level)} {i.Message}").ToList();
            if (File.Issues.Count > TooltipProblems)
                lines.Add($"...and {File.Issues.Count - TooltipProblems} more");
            return string.Join("\n", lines);
        }
    }
}
