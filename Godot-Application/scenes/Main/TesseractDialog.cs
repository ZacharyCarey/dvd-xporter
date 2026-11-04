using DvdXporter.Core.Settings;
using DvdXporter.Core.Subtitles;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Says once that Tesseract isn't installed, and how to get it. It's optional: only the Subtitle Text node needs it,
/// while a project is made (exporting uses the text it read), so unlike <see cref="DependenciesDialog"/> nothing waits for it.
/// </summary>
public partial class TesseractDialog : AcceptDialog
{
    /// <summary>It was suggested, so <see cref="AppSettings.TesseractSuggested"/> changed and wants saving.</summary>
    [Signal]
    public delegate void SuggestedEventHandler();

    /// <summary>Looks for Tesseract, and says it's missing if that hasn't been said before.</summary>
    public async void SuggestOnce()
    {
        if (AppSettings.Current.TesseractSuggested || await Tesseract.GetVersionAsync() is not null)
            return;

        AppSettings.Current.TesseractSuggested = true;
        EmitSignal(SignalName.Suggested);
        GetNode<Label>("%MessageLabel").Text = "Tesseract OCR wasn't found. It's optional: the Subtitle Text node uses it to read DVD "
            + "and Blu-ray subtitles as text while you make a project. Exporting doesn't need it.\n\n"
            + $"{Dependencies.TesseractInstallHint()}\n\n"
            + "This is only mentioned once. The Subtitle Text node says so again if you use it without Tesseract.";
        ResetSize();
        PopupCentered();
    }

    private void On_GetTesseractButton_Pressed() => OS.ShellOpen(Dependencies.TesseractUrl);
}
