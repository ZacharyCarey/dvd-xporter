using DvdXporter.Core;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>How the Clip Renamer shows video files, shared by its lists.</summary>
public static class ClipText
{
    public static readonly Color DimTextColor = new(1, 1, 1, 0.55f);

    /// <summary>A file's name and its file name, or just its file name if it hasn't been named.</summary>
    public static string Describe(ProjectDisc? disc, string clip)
    {
        var name = disc?.GetClipName(clip);
        return name is not null ? $"{name} ({clip})" : clip;
    }

    /// <summary>Shows a file's name in a tree cell, or its file name dimmed if it hasn't been named.</summary>
    public static void ShowName(TreeItem item, int column, ProjectDisc? disc, string clip)
    {
        var name = disc?.GetClipName(clip);
        item.SetText(column, name ?? clip);
        if (name is null)
            item.SetCustomColor(column, DimTextColor);
        else
            item.ClearCustomColor(column);
    }

    public static string FormatTime(PreciseTime time)
    {
        var span = time.AsTimeSpan();
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
