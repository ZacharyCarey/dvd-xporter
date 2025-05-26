using System;
using System.IO;
using System.Linq;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

public partial class DiscTree
{
    private static readonly Color FoundColor = new(0.5f, 0.81f, 0.52f);
    private static readonly Color ProbableColor = new(0.91f, 0.72f, 0.29f);
    private static readonly Color MissingColor = new(0.89f, 0.44f, 0.42f);
    private static readonly Color CheckingColor = new(0.6f, 0.63f, 0.66f);
    private static readonly Color ReleaseRowColor = new(1, 1, 1, 0.05f);
    private static readonly Color DimTextColor = new(1, 1, 1, 0.55f);

    /// <summary>A row of the tree, showing a release or disc. <see cref="Update"/> shows its current details.</summary>
    private abstract class Row(DiscTree tree, TreeItem item)
    {
        protected DiscTree Tree { get; } = tree;

        public TreeItem Item { get; } = item;

        public abstract object Target { get; }

        public abstract void Update();

        protected void SetStatus(string text, Texture2D icon, Color color)
        {
            Item.SetText((int)Column.Status, text);
            Item.SetIcon((int)Column.Status, icon);
            Item.SetCustomColor((int)Column.Status, color);
        }
    }

    private sealed class ReleaseRow : Row
    {
        public ReleaseRow(DiscTree tree, TreeItem parent, Release release)
            : base(tree, tree.CreateItem(parent))
        {
            Release = release;
            Item.SetIcon((int)Column.Label, tree.ReleaseIcon);
            Item.SetIconMaxWidth((int)Column.Label, 20);
            Item.SetCustomColor((int)Column.Format, DimTextColor);
            Item.SetCustomColor((int)Column.Location, DimTextColor);
            Item.AddButton((int)Column.Location, tree.AddIcon, (int)RowButton.AddDisc, false, "Add discs to this release...");
            Item.AddButton((int)Column.Location, tree.EditIcon, (int)RowButton.EditRelease, false, "Edit this release...");
            foreach (var column in Enum.GetValues<Column>())
                Item.SetCustomBgColor((int)column, ReleaseRowColor);
            Update();
        }

        public Release Release { get; }

        public override object Target => Release;

        public override void Update()
        {
            ShowStatus();

            // Numbered when there's more than one, as their order decides which is used for what they share
            var releases = Tree._project!.Releases;
            var numbered = releases.Count > 1;
            Item.SetText((int)Column.Label, numbered ? $"{releases.IndexOf(Release) + 1}. {Release.Name}" : Release.Name);
            Item.SetTooltipText((int)Column.Label, numbered
                ? $"Double-click to edit this release.\n\n{Tree.OrderHelp}"
                : "Double-click to edit this release");

            var formats = Release.Discs.Select(d => d.Format).Distinct().Order().Select(DiscResolver.FormatName);
            Item.SetText((int)Column.Format, string.Join(" + ", formats));
            Item.SetText((int)Column.Location, Release.Barcode is null ? "" : $"Barcode {Release.Barcode}");
        }

        private void ShowStatus()
        {
            var resolutions = Release.Discs.Select(Tree.ResolutionOf).ToList();
            if (resolutions.Count == 0)
            {
                SetStatus("No discs", Tree.CheckingIcon, CheckingColor);
                return;
            }

            if (resolutions.Any(r => r is null))
            {
                SetStatus("Checking...", Tree.CheckingIcon, CheckingColor);
                return;
            }

            var found = resolutions.Count(r => r!.Status == DiscStatus.Found);
            if (found == resolutions.Count)
                SetStatus(found == 1 ? "Found" : $"All {found} found", Tree.FoundIcon, FoundColor);
            else if (resolutions.Any(r => r!.Status == DiscStatus.Missing))
                SetStatus($"{found} of {resolutions.Count} found", Tree.MissingIcon, MissingColor);
            else
                SetStatus($"{found} of {resolutions.Count} found", Tree.ProbableIcon, ProbableColor);
        }
    }

    private sealed class DiscRow : Row
    {
        public DiscRow(DiscTree tree, ReleaseRow releaseRow, ProjectDisc disc)
            : base(tree, tree.CreateItem(releaseRow.Item))
        {
            ReleaseRow = releaseRow;
            Disc = disc;
            Item.SetIcon((int)Column.Label, tree.DiscIcon);
            Item.SetIconMaxWidth((int)Column.Label, 20);
            Item.SetEditable((int)Column.Label, true);
            Item.SetTooltipText((int)Column.Label, "Double-click to rename");
            Item.SetText((int)Column.Format, DiscResolver.FormatName(disc.Format));
            Item.AddButton((int)Column.Location, tree.LocateIcon, (int)RowButton.Locate, false, "Locate this disc manually...");
            Item.AddButton((int)Column.Location, tree.RescanIcon, (int)RowButton.Rescan, false, "Rescan the Disc Library");
            Update();
        }

        public ReleaseRow ReleaseRow { get; }

        public ProjectDisc Disc { get; }

        public override object Target => Disc;

        public override void Update()
        {
            Item.SetText((int)Column.Label, Disc.Label);
            var resolution = Tree.ResolutionOf(Disc);
            var source = resolution?.Source;
            ShowOnServer(source is { IsHere: false });
            if (source is { IsHere: false })
            {
                var server = $"\"{source.ServerName}\"";
                SetStatus("On server", Tree.ServerIcon, FoundColor);
                Item.SetTooltipText((int)Column.Status, $"It isn't on this computer, but {server} has it: it's read and played from there.");
                Item.SetText((int)Column.Location, $"{server} › {source.Path}");
                Item.SetTooltipText((int)Column.Location, $"In {server}'s Disc Library. Use Locate if it's on this computer too.");
                return;
            }

            var (text, icon, color) = resolution?.Status switch
            {
                null => ("Checking...", Tree.CheckingIcon, CheckingColor),
                DiscStatus.Found => ("Found", Tree.FoundIcon, FoundColor),
                DiscStatus.ProbableMatch => ("Probable match", Tree.ProbableIcon, ProbableColor),
                _ => ("Missing", Tree.MissingIcon, MissingColor),
            };
            SetStatus(text, icon, color);
            Item.SetTooltipText((int)Column.Status, resolution?.Message ?? text);

            var location = resolution?.Source is not null ? DisplayPath(resolution.Source.Path) : resolution is null ? "" : "—";
            var tooltip = string.Join("\n\n", new[] { resolution?.Source?.Path, resolution?.Message }.Where(s => s is not null));
            Item.SetText((int)Column.Location, location);
            Item.SetTooltipText((int)Column.Location, tooltip.Length > 0 ? tooltip : location);
        }

        /// <summary>
        /// A second symbol beside the status, if the render server has the disc too. A disc that's only there has it as
        /// its status instead.
        /// </summary>
        private void ShowOnServer(bool onlyOnServer)
        {
            var column = (int)Column.Status;
            var button = Item.GetButtonById(column, (int)RowButton.OnServer);
            var onServer = !onlyOnServer && Tree._serverDiscs?.Contains(Disc.Fingerprint) == true;
            if (onServer && button < 0)
                Item.AddButton(column, Tree.ServerIcon, (int)RowButton.OnServer, false, $"\"{Tree._serverName}\" has this disc too");
            else if (!onServer && button >= 0)
                Item.EraseButton(column, button);
        }

        /// <summary>Takes the label as edited, if it isn't blank. Returns whether it changed.</summary>
        public bool Rename()
        {
            var label = Item.GetText((int)Column.Label).Trim();
            if (label.Length == 0 || label == Disc.Label)
            {
                Item.SetText((int)Column.Label, Disc.Label);
                return false;
            }

            Disc.Label = label;
            return true;
        }

        // Paths inside the Disc Library are shown relative to it; the tooltip has the full path.
        private string DisplayPath(string path)
        {
            var root = Tree._libraryRoot;
            if (root is null)
                return path;

            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..") || Path.IsPathRooted(relative) ? path : $"Disc Library › {relative}";
        }
    }
}
