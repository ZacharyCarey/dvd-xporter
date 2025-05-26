using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>How a file's export is going, or went.</summary>
public sealed class ExportFileRun
{
    /// <summary>Null while it's waiting or being made.</summary>
    public ExportOutcome? Outcome { get; set; }

    public bool Active { get; set; }

    public string Stage { get; set; } = "";

    public double Fraction { get; set; }

    public string? Error { get; set; }

    public string? LogPath { get; set; }

    public string? Destination { get; set; }

    /// <summary>How it's going, in a few words.</summary>
    public string Text => Outcome switch
    {
        ExportOutcome.Done => "✓ Exported",
        ExportOutcome.Failed => "⛔ Failed",
        ExportOutcome.Skipped => "⛔ Skipped",
        ExportOutcome.Cancelled => "Cancelled",
        _ when !Active => Stage,
        _ when Stage == "Encoding" => $"Encoding {Fraction * 100:0}%",
        _ => Stage + "...",
    };

    /// <summary>The colour of how it went, or null while it's waiting or being made.</summary>
    public Color? Color => Outcome switch
    {
        ExportOutcome.Done => ExportColors.Ready,
        ExportOutcome.Failed or ExportOutcome.Skipped => TranscodeNodeView.IssueColor(IssueLevel.Error),
        ExportOutcome.Cancelled => ExportColors.Dim,
        _ => null,
    };
}
