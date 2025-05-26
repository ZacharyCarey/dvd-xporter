using DvdXporter.Core.Projects;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Settings;

public sealed record RecentProject(string Name, ProjectKind Kind, string ProjectFilePath, DateTime LastOpened);

/// <summary>
/// The most recently opened projects, newest first.
/// </summary>
public sealed class RecentProjectList
{
    public const int MaxEntries = 10;

    private readonly string _filePath;
    private readonly List<RecentProject> _entries;

    private RecentProjectList(string filePath, List<RecentProject> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public IReadOnlyList<RecentProject> Entries => _entries;

    public static RecentProjectList Load(string filePath) =>
        new(filePath, JsonFile.TryRead<List<RecentProject>>(filePath) ?? []);

    /// <summary>Moves (or adds) a project to the top of the list and saves.</summary>
    public void Touch(ProjectFolder folder)
    {
        _entries.RemoveAll(e => e.ProjectFilePath == folder.ProjectFilePath);
        _entries.Insert(0, new RecentProject(folder.Project.Name, folder.Project.Kind, folder.ProjectFilePath, DateTime.Now));
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public void Remove(string projectFilePath)
    {
        _entries.RemoveAll(e => e.ProjectFilePath == projectFilePath);
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private void Save() => JsonFile.Write(_filePath, _entries);
}
