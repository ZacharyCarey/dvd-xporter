using System.Collections.Generic;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.UI;

/// <summary>
/// Which files the user picked to export, by file key, so it lasts while the list is checked again. Files the user hasn't
/// picked or left out are picked if they can be exported.
/// </summary>
public sealed class ExportPicks
{
    private readonly Dictionary<string, bool> _choices = [];

    public bool IsPicked(ExportFile file) => file.CanExport && _choices.GetValueOrDefault(file.Key, true);

    public void Set(ExportFile file, bool picked) => _choices[file.Key] = picked;

    public void Set(IEnumerable<ExportFile> files, bool picked)
    {
        foreach (var file in files)
            Set(file, picked);
    }

    public void Clear() => _choices.Clear();
}
