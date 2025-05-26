using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// One of the Info page's editors, or a section of one: fields laid out in its scene that write straight to the model
/// as they're edited, then say so (<see cref="EditedEventHandler"/>). Invalid input (e.g. a bad date) is shown in red
/// and not written.
/// </summary>
public partial class InfoSection : VBoxContainer
{
    private static readonly Color InvalidColor = new(0.89f, 0.44f, 0.42f);

    /// <summary>A field was edited, and what it holds written to the model.</summary>
    [Signal]
    public delegate void EditedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>The editor's artwork section, for one that has one.</summary>
    public ArtworkSection Artwork => GetNode<ArtworkSection>("%Artwork");

    /// <summary>The editor's files section, for one that has one.</summary>
    public FilesSection Files => GetNode<FilesSection>("%Files");

    protected void MarkEdited() => EmitSignal(SignalName.Edited);

    protected void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    // A section inside passes its edits and messages on
    protected void On_Section_Edited() => MarkEdited();

    protected void On_Section_StatusChanged(string status) => SetStatus(status);

    /// <summary>Shows <paramref name="text"/> in the field with that unique name, as valid.</summary>
    protected void Fill(string field, string? text)
    {
        switch (GetNode(field))
        {
            case LineEdit edit:
                edit.Text = text ?? "";
                SetValid(edit, true);
                break;
            case TextEdit edit:
                edit.Text = text ?? "";
                break;
            case Label label:
                label.Text = text ?? "";
                break;
        }
    }

    protected static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    protected static string FormatList(List<string> values) => string.Join(", ", values);

    /// <summary>A comma-separated list, e.g. genres.</summary>
    protected static List<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    protected static string FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    protected static string? FormatInteger(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>A date like 2002-09-20, or blank for none. Marks the <paramref name="field"/> invalid if it's neither.</summary>
    protected bool TryDate(string field, string text, out DateOnly? date)
    {
        text = text.Trim();
        date = null;
        var valid = text.Length == 0;
        if (!valid && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            valid = true;
        }

        SetValid(GetNode<LineEdit>(field), valid);
        return valid;
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>, or blank for none.</summary>
    protected bool TryInteger(string field, string text, int min, int max, out int? value)
    {
        text = text.Trim();
        value = null;
        var valid = text.Length == 0;
        if (!valid && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max)
        {
            value = number;
            valid = true;
        }

        SetValid(GetNode<LineEdit>(field), valid);
        return valid;
    }

    private static void SetValid(LineEdit edit, bool valid)
    {
        if (valid)
            edit.RemoveThemeColorOverride("font_color");
        else
            edit.AddThemeColorOverride("font_color", InvalidColor);
    }
}
