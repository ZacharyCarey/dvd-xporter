using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;

namespace DvdXporter.UI;

/// <summary>
/// Reads the open project's discs that haven't been read yet, one after another in the background: everything about
/// their clips and playlists, reading every clip through once, then each clip's pictures checked for interlacing (see
/// <see cref="DiscCache.ReadContentsAsync"/>), saved in the project so it's never read again, by anyone it's shared
/// with. That takes as long as reading the whole disc, so its progress is reported as it goes; while it's reading, the
/// app stays where it is (the other steps need what's read). A disc on the render server is read there, at the same
/// time as those here, and its progress is told apart (see <see cref="ServerStatusChanged"/>).
/// </summary>
public sealed class DiscReader
{
    // Discs are read one at a time in each place: on this computer, and on the render server
    private readonly Lane _here = new();
    private readonly Lane _server = new();
    // Discs that couldn't be read, this session: not read again until asked
    private readonly HashSet<string> _failed = [];
    // Which run of reading is current: a run that's been cancelled stops at once, though its disc read winds down
    private int _run;
    // How the server's reading ended, while discs here were still being read: told once they're read too
    private readonly List<string> _serverEnded = [];

    /// <summary>A disc being read (here, if one is), if one is being read.</summary>
    public ProjectDisc? Current => _here.Current ?? _server.Current;

    public bool IsReading => Current is not null;

    /// <summary>Raised with what it's doing here (and how reading ended, here or on the server), for the status bar.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised with how far through the disc being read here it is (0–1), or null when it's stopped reading here.</summary>
    public event Action<double?>? ProgressChanged;

    /// <summary>Raised with what it's doing on the render server, while it's reading discs there.</summary>
    public event Action<string>? ServerStatusChanged;

    /// <summary>
    /// Raised with how far through the disc being read on the render server it is (0–1), or null when it's stopped
    /// reading there (how that ended is told by <see cref="StatusChanged"/>).
    /// </summary>
    public event Action<double?>? ServerProgressChanged;

    /// <summary>How many other discs are being read, or waiting to be, besides <see cref="Current"/>.</summary>
    public int Waiting => _here.Queue.Count + _server.Queue.Count + (_here.Current is not null && _server.Current is not null ? 1 : 0);

    private Lane LaneOf(DiscSource source) => source.IsHere ? _here : _server;

    /// <summary>Whether a disc is being read or waiting to be.</summary>
    private bool IsQueued(ProjectDisc disc) => new[] { _here, _server }.Any(l => l.Current == disc || l.Queue.Any(q => q.Disc == disc));

    /// <summary>
    /// Reads those of the discs that have been found (here or on the render server) and need reading (see
    /// <see cref="DiscCache.NeedsReading"/>), after any already waiting there. A disc that couldn't be read this session
    /// is only tried again with <paramref name="retry"/>.
    /// </summary>
    public void ReadUnread(ProjectFolder folder, IEnumerable<(ProjectDisc Disc, DiscSource? Source)> discs, bool retry = false)
    {
        foreach (var (disc, source) in discs)
        {
            if (source is null || IsQueued(disc) || (!retry && _failed.Contains(disc.Fingerprint)))
                continue;
            if (!folder.GetDiscCache(disc).NeedsReading)
                continue;
            _failed.Remove(disc.Fingerprint);
            var lane = LaneOf(source);
            lane.Queue.Enqueue((folder, disc, source));
            lane.Discs++;
        }

        foreach (var lane in new[] { _here, _server })
        {
            if (lane.Current is null && lane.Queue.Count > 0)
                ReadQueued(lane);
        }
    }

    /// <summary>
    /// Stops reading (nothing of the discs being read is saved), and forgets the discs waiting. Reading can start again
    /// straight away.
    /// </summary>
    public void Cancel()
    {
        _run++;
        var stopped = new List<string>();
        foreach (var lane in new[] { _here, _server })
        {
            lane.Queue.Clear();
            lane.Cancellation?.Cancel();
            lane.Discs = lane.Started = 0;
            if (lane.Current is not null)
                stopped.Add($"\"{lane.Current.Label}\"");
            lane.Current = null;
        }

        _serverEnded.Clear();
        ProgressChanged?.Invoke(null);
        ServerProgressChanged?.Invoke(null);
        if (stopped.Count > 0)
            StatusChanged?.Invoke($"Stopped reading {string.Join(" and ", stopped)}: {(stopped.Count == 1 ? "it's" : "they're")} read again next time.");
    }

    /// <summary>
    /// Stops reading the disc (nothing of it is saved) or takes it off the waiting list, e.g. once it's removed from
    /// the project. The other discs carry on.
    /// </summary>
    public void Stop(ProjectDisc disc)
    {
        foreach (var lane in new[] { _here, _server })
        {
            var waiting = lane.Queue.Count;
            var others = lane.Queue.Where(q => q.Disc.Fingerprint != disc.Fingerprint).ToList();
            lane.Queue.Clear();
            foreach (var other in others)
                lane.Queue.Enqueue(other);
            lane.Discs -= waiting - lane.Queue.Count;

            if (lane.Current?.Fingerprint == disc.Fingerprint)
                lane.Cancellation?.Cancel();
        }
    }

    private async void ReadQueued(Lane lane)
    {
        int run = _run;
        string? said = null;
        while (run == _run && lane.Queue.TryDequeue(out var next))
        {
            var (folder, disc, source) = next;
            lane.Current = disc;
            lane.Started++;
            var cancellation = lane.Cancellation = new CancellationTokenSource();
            var cache = folder.GetDiscCache(disc);
            var warnings = new List<string>();
            // "Reading \"Disc 2\"", or with others to read, "Reading disc 1 of 3, \"Disc 2\"", and where if it's on the server
            var on = source.ServerName is not null ? $" on \"{source.ServerName}\"" : "";
            string reading() => (lane.Discs > 1 ? $"Reading disc {lane.Started} of {lane.Discs}, \"{disc.Label}\"" : $"Reading \"{disc.Label}\"") + on;
            Report(lane, $"{reading()}...", 0);
            // Reported on this thread: Progress posts back to the one it was made on
            var progress = new Progress<DiscContentsProgress>(p =>
            {
                if (lane.Current != disc || cancellation.IsCancellationRequested || p.Total == 0)
                    return;
                // One bar for the whole disc; the step it's on is told apart in the text
                var file = $"video file {Math.Min(p.Done + 1, p.Total)} of {p.Total}";
                var step = !p.CheckingPictures ? $"reading {file}"
                    : disc.CheckEveryFrame ? $"checking every frame of {file} for interlacing"
                    : $"checking {file}'s picture";
                Report(lane, $"{reading()}: {step} ({p.Fraction:P0}). The other steps open once it's read.", p.Fraction);
            });

            string? error = null;
            try
            {
                void warn(string warning)
                {
                    lock (warnings)
                        warnings.Add(warning);
                }

                await Task.Run(() => cache.ReadContentsAsync(source, progress, warn, cancellation.Token));
            }
            catch (OperationCanceledException)
            {
                // Cancel has said so, and moved on
                if (run != _run)
                    return;
                // Stopped on its own: the rest carry on
                lane.Current = null;
                said = $"Stopped reading \"{disc.Label}\".";
                Report(lane, said, null);
                continue;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _failed.Add(disc.Fingerprint);
            }

            if (run != _run)
                return;
            said = error is not null ? $"Couldn't read \"{disc.Label}\": {error}"
                : warnings.Count > 0 ? $"Read \"{disc.Label}\" with {warnings.Count} {(warnings.Count == 1 ? "warning" : "warnings")}: {warnings[0]}"
                : $"Read \"{disc.Label}\".";
            lane.Current = null;
            Report(lane, said, null);
        }

        if (run != _run)
            return;
        lane.Discs = lane.Started = 0;
        if (lane == _server)
        {
            // Its line goes, and how it ended is told on the main one: after the discs here, if they're still being read
            ServerProgressChanged?.Invoke(null);
            if (said is null)
                return;
            if (_here.IsBusy)
                _serverEnded.Add(said);
            else
                StatusChanged?.Invoke(said);
            return;
        }

        ProgressChanged?.Invoke(null);
        if (_serverEnded.Count > 0)
            StatusChanged?.Invoke(string.Join(" ", _serverEnded.Prepend(said ?? "").Where(s => s.Length > 0)));
        _serverEnded.Clear();
    }

    /// <summary>Tells what a lane's doing, on its own line.</summary>
    /// <param name="fraction">How far through its disc it is, or null for a message once a disc's done.</param>
    private void Report(Lane lane, string status, double? fraction)
    {
        if (lane == _server)
        {
            ServerStatusChanged?.Invoke(status);
            if (fraction is not null)
                ServerProgressChanged?.Invoke(fraction);
            return;
        }

        StatusChanged?.Invoke(status);
        if (fraction is not null)
            ProgressChanged?.Invoke(fraction);
    }

    /// <summary>The discs being read in one place, one at a time.</summary>
    private sealed class Lane
    {
        public Queue<(ProjectFolder Folder, ProjectDisc Disc, DiscSource Source)> Queue { get; } = new();

        /// <summary>The disc being read, if one is.</summary>
        public ProjectDisc? Current { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }

        // The discs of this run (those read, being read and waiting), and how many of them have been started
        public int Discs { get; set; }

        public int Started { get; set; }

        public bool IsBusy => Current is not null || Queue.Count > 0;
    }
}
