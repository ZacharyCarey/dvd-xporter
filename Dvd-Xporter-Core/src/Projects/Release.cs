namespace DvdXporter.Core.Projects;

/// <summary>
/// One product in a project's series of releases, e.g. "Season 1 (DVD)", and the discs it contains.
/// </summary>
public sealed class Release(string name)
{
    private static readonly int[] BarcodeLengths = [8, 12, 13, 14];

    public string Name { get; set; } = name;

    /// <summary>The product's UPC/EAN barcode (digits only), if known.</summary>
    public string? Barcode { get; set; }

    public List<ProjectDisc> Discs { get; } = [];

    /// <summary>
    /// Moves one of its discs <paramref name="by"/> places up (negative) or down the list, e.g. to put them in the
    /// order they're numbered. Returns its new place, or null if it can't move that far.
    /// </summary>
    public int? MoveDisc(ProjectDisc disc, int by)
    {
        var index = Discs.IndexOf(disc);
        var to = index + by;
        if (index < 0 || to < 0 || to >= Discs.Count)
            return null;

        Discs.RemoveAt(index);
        Discs.Insert(to, disc);
        return to;
    }

    /// <summary>
    /// Cleans up a barcode as typed (spaces and dashes removed).
    /// Returns null for an empty barcode, and sets <paramref name="error"/> if it isn't a valid UPC/EAN.
    /// </summary>
    public static string? NormalizeBarcode(string? input, out string? error)
    {
        error = null;
        var barcode = new string((input ?? "").Where(c => c is not (' ' or '-')).ToArray());
        if (barcode.Length == 0)
            return null;

        if (!barcode.All(char.IsAsciiDigit))
            error = "A barcode can only contain digits.";
        else if (!BarcodeLengths.Contains(barcode.Length))
            error = "A barcode is 8, 12, 13 or 14 digits long (EAN-8, UPC-A, EAN-13 or GTIN-14).";

        return barcode;
    }
}
