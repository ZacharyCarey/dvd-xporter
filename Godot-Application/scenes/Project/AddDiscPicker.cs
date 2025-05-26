using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DiscDecoders.Common;
using DvdXporter.Core.Remote;
using Godot;

namespace DvdXporter.UI;

public enum AddDiscSource
{
    Library,
    Folder,
    Image,
}

/// <summary>Which discs are added to have every frame checked for interlacing: the add dialogs' choice, in their order.</summary>
public enum EveryFrameDiscs
{
    /// <summary>DVDs, which are often interlaced or telecined, but not Blu-rays, which seldom are and take far longer.</summary>
    Dvds,
    All,
    None,
}

/// <summary>
/// Picks discs to add to a release: from the Disc Library, a folder or a disc image. Discs picked from files are
/// identified before they're handed over.
/// </summary>
public partial class AddDiscPicker : Node
{
    /// <summary>A source was picked from the menu, for <see cref="Release"/> (or, when that's null, the page to choose).</summary>
    [Signal]
    public delegate void SourceChosenEventHandler(AddDiscSource source);

    /// <summary>
    /// Discs were picked for <see cref="Release"/>: they're <see cref="ChosenSources"/> (here, or on the render server),
    /// plus any <see cref="Problems"/>.
    /// </summary>
    [Signal]
    public delegate void DiscsChosenEventHandler();

    [Signal]
    public delegate void LibraryRescanRequestedEventHandler();

    /// <summary>The release the discs are for.</summary>
    public Release? Release { get; private set; }

    public IReadOnlyList<DiscSource> ChosenSources { get; private set; } = [];

    /// <summary>The picked files that couldn't be identified, and why.</summary>
    public List<string> Problems { get; private set; } = [];

    /// <summary>Which of the discs picked are to have every frame checked for interlacing, as chosen when they were picked.</summary>
    public EveryFrameDiscs EveryFrame { get; private set; }

    /// <summary>Whether a disc picked is to have every frame checked for interlacing (see <see cref="ProjectDisc.CheckEveryFrame"/>).</summary>
    public bool ChecksEveryFrame(DiscFormat format) =>
        EveryFrame == EveryFrameDiscs.All || (EveryFrame == EveryFrameDiscs.Dvds && format == DiscFormat.Dvd);

    private PopupMenu _menu = null!;
    private FileDialog _folderDialog = null!;
    private FileDialog _imageDialog = null!;
    private LibraryPickerDialog _libraryPicker = null!;
    private DiscServices? _services;

    public override void _Ready()
    {
        _menu = GetNode<PopupMenu>("%Menu");
        _folderDialog = GetNode<FileDialog>("%FolderDialog");
        _imageDialog = GetNode<FileDialog>("%ImageDialog");
        _libraryPicker = GetNode<LibraryPickerDialog>("%LibraryPickerDialog");
    }

    /// <summary>Shows the menu of sources at <paramref name="at"/>, for <paramref name="release"/> (null: the page chooses).</summary>
    public void ShowMenu(Release? release, Rect2I at)
    {
        Release = release;
        _menu.Popup(at);
    }

    /// <summary>
    /// Asks for discs from <paramref name="source"/>, leaving out those already in <paramref name="project"/>. The Disc
    /// Library's include <paramref name="server"/>'s, if there is one.
    /// </summary>
    public void Open(AddDiscSource source, Release release, Project project, DiscServices services, RenderServerClient? server)
    {
        Release = release;
        _services = services;
        switch (source)
        {
            case AddDiscSource.Library:
                _libraryPicker.Title = $"Add to \"{release.Name}\" from Disc Library";
                _libraryPicker.PopupFor(services.Library, project.Discs.Select(d => d.Fingerprint), server);
                break;
            case AddDiscSource.Folder:
                _folderDialog.PopupCentered();
                break;
            case AddDiscSource.Image:
                _imageDialog.PopupCentered();
                break;
        }
    }

    /// <summary>Drops discs still being identified, e.g. when the project closes.</summary>
    public void Cancel() => Release = null;

    /// <summary>Call after the Disc Library is rescanned or changed.</summary>
    public void OnDiscLibraryChanged()
    {
        if (_libraryPicker.Visible)
            _libraryPicker.OnLibraryChanged();
    }

    private void On_Menu_IdPressed(long id) => EmitSignal(SignalName.SourceChosen, id);

    private void On_FolderDialog_DirSelected(string path)
    {
        EveryFrame = ChosenEveryFrame(_folderDialog);
        Identify([path]);
    }

    private void On_ImageDialog_FilesSelected(string[] paths)
    {
        EveryFrame = ChosenEveryFrame(_imageDialog);
        Identify(paths);
    }

    private void On_LibraryPickerDialog_DiscsChosen()
    {
        EveryFrame = _libraryPicker.EveryFrame;
        Choose(_libraryPicker.ChosenSources, []);
    }

    // The dialog's one option, "Check every frame for interlacing" (a native dialog may not show it: then it's DVDs)
    private static EveryFrameDiscs ChosenEveryFrame(FileDialog dialog)
    {
        var options = dialog.GetSelectedOptions();
        var name = dialog.GetOptionName(0);
        return options.ContainsKey(name) ? (EveryFrameDiscs)options[name].AsInt32() : EveryFrameDiscs.Dvds;
    }

    private void On_LibraryPickerDialog_RescanRequested() => EmitSignal(SignalName.LibraryRescanRequested);

    private async void Identify(string[] paths)
    {
        var services = _services;
        var release = Release;
        if (services is null || release is null)
            return;

        var sources = new List<DiscSource>();
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                sources.Add(await Task.Run(() => services.Identifier.Identify(path)));
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}: {ex.Message}");
            }
        }

        if (release == Release)
            Choose(sources, problems);
    }

    private void Choose(IReadOnlyList<DiscSource> sources, List<string> problems)
    {
        ChosenSources = sources;
        Problems = problems;
        EmitSignal(SignalName.DiscsChosen);
    }
}
