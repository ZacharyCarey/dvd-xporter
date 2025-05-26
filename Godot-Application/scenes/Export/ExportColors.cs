using Godot;

namespace DvdXporter.UI;

/// <summary>The colours the Export step shows files' states in.</summary>
public static class ExportColors
{
    public static readonly Color Ready = new(0.5f, 0.81f, 0.52f);
    public static readonly Color Missing = new(0.89f, 0.44f, 0.42f);
    public static readonly Color Checking = new(0.6f, 0.63f, 0.66f);
    public static readonly Color Dim = new(1, 1, 1, 0.5f);
}
