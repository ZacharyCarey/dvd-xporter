using System.Linq;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Checks the programs the app needs are installed (see <see cref="Dependencies"/>). If any aren't, says which and how
/// to get them, and Quit closes the app: nothing could be previewed or exported without them.
/// </summary>
public partial class DependenciesDialog : AcceptDialog
{
    /// <summary>Everything needed is installed.</summary>
    [Signal]
    public delegate void SatisfiedEventHandler();

    /// <summary>Something's missing and the user closed the dialog.</summary>
    [Signal]
    public delegate void QuitRequestedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>Looks for the programs, and carries on (<see cref="Satisfied"/>) or says what's missing.</summary>
    public void Check()
    {
        var missing = Dependencies.FindMissing();
        if (missing.Count == 0)
        {
            Hide();
            EmitSignal(SignalName.Satisfied);
            return;
        }

        GetNode<Button>("%GetFFmpegButton").Visible = missing.Any(m => m.DownloadUrl == Dependencies.FFmpegUrl);
        GetNode<Button>("%GetVlcButton").Visible = missing.Any(m => m.DownloadUrl == Dependencies.VlcUrl);
        GetNode<Label>("%MessageLabel").Text = "DVD Xporter needs programs that weren't found:\n\n"
            + string.Join('\n', missing.Select(m => $"-  {m.Name}, {m.Use}: {m.Problem}."))
            + $"\n\n{Dependencies.InstallHint(missing)}\n\n"
            + $"Once {((missing.Count == 1) ? "it's" : "they're")} installed, use 'Check Again'. The app can't be used without {((missing.Count == 1) ? "it" : "them")}. ";
        EmitSignal(SignalName.StatusChanged, $"Missing {string.Join(" and ", missing.Select(m => m.Name))}.");
        ResetSize();
        PopupCentered();
    }

    private void On_GetFFmpegButton_Pressed() => OS.ShellOpen(Dependencies.FFmpegUrl);

    private void On_GetVlcButton_Pressed() => OS.ShellOpen(Dependencies.VlcUrl);

    private void On_CheckAgainButton_Pressed() => Check();

    private void On_Closed() => EmitSignal(SignalName.QuitRequested);
}
