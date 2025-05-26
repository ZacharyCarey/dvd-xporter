using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.UI;

/// <summary>How the Export step describes files, shared by its list and its details.</summary>
public static class ExportText
{
    public static string DiscNames(IEnumerable<ProjectDisc> discs) => string.Join(", ", discs.Select(d => $"\"{d.Label}\""));

    /// <summary>Every version of a file's library item, best first, and which is used.</summary>
    public static string DescribeAlternates(ExportFile file)
    {
        var lines = file.Alternates.Select((a, i) =>
        {
            var state = a == file ? (a.IsPicked ? "used, as you picked it" : "used") : a.State switch
            {
                ExportState.Ready => "also available",
                ExportState.MissingDisc => "disc not on this computer",
                _ => "has problems to fix",
            };
            return $"{i + 1}. {a.SourceName} ({a.MadeWhere}): {state}";
        });
        return $"{file.Alternates.Count} versions, best first (by the releases' order on the project overview):\n"
            + string.Join("\n", lines);
    }

    public static string FormatLength(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
}
