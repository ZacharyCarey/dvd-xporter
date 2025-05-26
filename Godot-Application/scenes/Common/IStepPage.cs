using DvdXporter.Core.Projects;

namespace DvdXporter.UI;

/// <summary>
/// One of a project's workflow steps (see <see cref="ProjectStep"/>), as a page Main shows and keeps up to date.
/// Each is told about what happens elsewhere in the app, and does what it needs to (often nothing, unless it's shown).
/// </summary>
public interface IStepPage
{
    ProjectStep Step { get; }

    /// <summary>Shows <paramref name="folder"/>'s project, about to be shown (or shown again).</summary>
    void ShowProject(ProjectFolder folder, AppServices services);

    /// <summary>Is about to be hidden: saves what's still to save, and stops anything playing.</summary>
    void Leave();

    /// <summary>The Disc Library was scanned or changed, so discs may be found in other places.</summary>
    void OnDiscLibraryChanged();

    /// <summary>The user's settings were changed (see <see cref="Core.Settings.AppSettings.Current"/>).</summary>
    void OnSettingsChanged();
}
