namespace DvdXporter.Core.Transcoding;

/// <summary>
/// Each canvas's undo history: its contents after each change (see <see cref="TranscodeCanvas.Snapshot"/>), and where
/// in them it is now. Kept for as long as the app runs, not saved.
/// </summary>
public sealed class CanvasHistory(int limit = 200)
{
    private readonly Dictionary<string, (List<string> States, int Index)> _canvases = [];

    /// <summary>Starts a canvas's history, if it hasn't one yet.</summary>
    public void Start(TranscodeCanvas canvas)
    {
        if (!_canvases.ContainsKey(canvas.Id))
            _canvases[canvas.Id] = ([canvas.Snapshot()], 0);
    }

    /// <summary>Records <paramref name="canvas"/>, if it changed since it was last recorded.</summary>
    public void Record(TranscodeCanvas canvas)
    {
        Start(canvas);
        var (states, index) = _canvases[canvas.Id];
        var now = canvas.Snapshot();
        if (states[index] == now)
            return;

        // A change after undoing replaces what was undone
        states.RemoveRange(index + 1, states.Count - index - 1);
        states.Add(now);
        if (states.Count > limit)
            states.RemoveAt(0);
        _canvases[canvas.Id] = (states, states.Count - 1);
    }

    /// <summary>
    /// Puts <paramref name="canvas"/> back as it was <paramref name="by"/> changes ago (negative, to undo) or ahead (to
    /// redo). Returns false if there's nothing that far.
    /// </summary>
    public bool Step(TranscodeCanvas canvas, int by)
    {
        // (Every change was recorded as it was made)
        Start(canvas);
        var (states, index) = _canvases[canvas.Id];
        var to = index + by;
        if (to < 0 || to >= states.Count)
            return false;

        _canvases[canvas.Id] = (states, to);
        canvas.Restore(states[to]);
        return true;
    }
}
