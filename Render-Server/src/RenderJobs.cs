using System.Text.Json;
using System.Text.Json.Serialization;
using DiscDecoders;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.Server;

/// <summary>
/// The exports the app has sent: each waits for its project (and the discs it needs that the server hasn't got), then
/// its turn, and runs as Export would in the app, into the server's library. One runs at a time. They're kept in
/// <see cref="ListFileName"/> so the queue survives a restart, and each one's project in <see cref="JobsFolderName"/>
/// until it's removed. The discs sent for a job are its own, and go as soon as it's finished or cancelled.
/// </summary>
public sealed class RenderJobs(ServerConfig config, ServerDiscs discs, string dataFolder, Action<string> log)
{
    public const string JobsFolderName = "Jobs";
    private const string ListFileName = "jobs.json";
    private const string PackageFileName = "project.dvdxp";
    private const string ProjectFolderName = "Project";
    private const string DiscsFolderName = "Discs";
    // Where its discs are put together as they're sent
    private const string IncomingFolderName = "Incoming";

    private static readonly JsonSerializerOptions FileOptions = new(RenderServerProtocol.Json) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly List<Job> _jobs = [];
    private readonly SemaphoreSlim _queued = new(0);

    /// <summary>A job, as it's kept and shown.</summary>
    private sealed class Job
    {
        public required string Id { get; init; }
        public required RenderJobRequest Request { get; init; }
        public RenderJobState State { get; set; }
        public DateTime Submitted { get; init; }
        public DateTime? Finished { get; set; }
        public string? Message { get; set; }
        public double Overall { get; set; }
        public List<RenderFileStatus> Files { get; set; } = [];

        /// <summary>The project's file, once it's been sent and unpacked.</summary>
        public string? ProjectFile { get; set; }

        /// <summary>The discs sent for it: where each one is, by the project's fingerprint for it.</summary>
        public Dictionary<string, string> Discs { get; set; } = [];

        /// <summary>The files it's put in the library so far, which go if it has to start again.</summary>
        public List<string> Made { get; set; } = [];

        [JsonIgnore]
        public CancellationTokenSource? Cancellation { get; set; }

        public RenderJobStatus ToStatus() => new(Id, Request.ProjectName, State, Submitted, Finished, Overall, Message, [.. Files]);
    }

    private string JobsFolder => Path.Combine(dataFolder, JobsFolderName);

    private string FolderOf(string id) => Path.Combine(JobsFolder, id);

    private string DiscsFolderOf(string id) => Path.Combine(FolderOf(id), DiscsFolderName);

    // ---- What the app asks for ----

    public List<RenderJobStatus> List()
    {
        lock (_lock)
            return _jobs.OrderByDescending(j => j.Submitted).Select(j => j.ToStatus()).ToList();
    }

    /// <summary>Makes a job, which waits for its project (<see cref="ReceiveProjectAsync"/>).</summary>
    public RenderJobStatus Create(RenderJobRequest request)
    {
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Request = request,
            State = RenderJobState.Receiving,
            Submitted = DateTime.UtcNow,
            Files = request.Files.Select(f => new RenderFileStatus(f.Name, "Waiting", 0, null, null)).ToList(),
        };
        lock (_lock)
            _jobs.Add(job);
        Save();
        return job.ToStatus();
    }

    /// <summary>Takes a job's project, packaged, and puts the job in the queue.</summary>
    /// <exception cref="KeyNotFoundException">There's no such job waiting for its project.</exception>
    /// <exception cref="InvalidDataException">It isn't a project.</exception>
    public async Task<RenderJobStatus> ReceiveProjectAsync(string id, Stream body, CancellationToken cancellation)
    {
        var job = Find(id);
        if (job is not { State: RenderJobState.Receiving })
            throw new KeyNotFoundException("There's no job waiting for that project.");

        var folder = FolderOf(job.Id);
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, PackageFileName);
        await using (var file = File.Create(package))
            await body.CopyToAsync(file, cancellation);
        var projectFile = ProjectPackage.Import(package, Path.Combine(folder, ProjectFolderName));
        File.Delete(package);

        lock (_lock)
        {
            job.ProjectFile = projectFile;
            if (job.State == RenderJobState.Receiving)
                job.State = RenderJobState.Queued;
        }

        Save();
        log($"Queued \"{job.Request.ProjectName}\": {Plural(job.Files.Count, "file")}.");
        _queued.Release();
        return job.ToStatus();
    }

    /// <summary>Where a disc for a job that's still being sent is put together.</summary>
    /// <exception cref="KeyNotFoundException">There's no such job being sent.</exception>
    /// <exception cref="ArgumentException">The fingerprint isn't one.</exception>
    public string IncomingFolder(string id, string fingerprint)
    {
        ServerDiscs.CheckFingerprint(fingerprint);
        if (Find(id) is not { State: RenderJobState.Receiving } job)
            throw new KeyNotFoundException("There's no job waiting for that disc.");
        return Path.Combine(FolderOf(job.Id), IncomingFolderName, fingerprint);
    }

    /// <summary>Takes a disc sent whole for a job, for it alone.</summary>
    /// <exception cref="KeyNotFoundException">There's no such job being sent.</exception>
    /// <exception cref="ArgumentException">It isn't a disc, or wasn't sent.</exception>
    public void FinishDisc(string id, string fingerprint, string name)
    {
        var incoming = IncomingFolder(id, fingerprint);
        discs.Received(incoming, name);
        var folder = Path.Combine(DiscsFolderOf(id), fingerprint);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(DiscsFolderOf(id));
        Directory.Move(incoming, folder);

        var job = Find(id)!;
        lock (_lock)
            job.Discs[fingerprint] = Path.Combine(folder, name);
        Save();
    }

    /// <summary>Stops a running job, or takes a waiting one off the queue. Returns whether there was one to stop.</summary>
    public bool Cancel(string id)
    {
        var job = Find(id);
        if (job is null || job.ToStatus().IsFinished)
            return false;

        lock (_lock)
        {
            if (job.State == RenderJobState.Running)
            {
                job.Cancellation?.Cancel();
                return true;
            }

            job.State = RenderJobState.Cancelled;
            job.Finished = DateTime.UtcNow;
        }

        RemoveDiscs(job);
        Save();
        log($"Cancelled \"{job.Request.ProjectName}\" before it started.");
        return true;
    }

    /// <summary>Forgets a finished job, and deletes its copy of the project. Returns whether it could.</summary>
    public bool Remove(string id)
    {
        var job = Find(id);
        if (job is null || !job.ToStatus().IsFinished)
            return false;

        lock (_lock)
            _jobs.Remove(job);
        Save();
        TryDeleteFolder(FolderOf(job.Id));
        return true;
    }

    // ---- Running them ----

    /// <summary>Runs the jobs in the queue, one at a time, until <paramref name="stopping"/>.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        Load();
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await _queued.WaitAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Job? job;
            lock (_lock)
            {
                job = _jobs.Where(j => j.State == RenderJobState.Queued).MinBy(j => j.Submitted);
                if (job is null)
                    continue;
                job.State = RenderJobState.Running;
                job.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            }

            Save();
            var cancellation = job.Cancellation;
            await RunAsync(job, cancellation.Token, stopping);
            lock (_lock)
                job.Cancellation = null;
            cancellation.Dispose();
            Save();
        }
    }

    /// <param name="cancellation">Cancelled when the job is, or the server stops.</param>
    /// <param name="stopping">Cancelled when the server stops: the job's left to start again when it next runs.</param>
    private async Task RunAsync(Job job, CancellationToken cancellation, CancellationToken stopping)
    {
        var name = job.Request.ProjectName;
        log($"Exporting \"{name}\"...");
        try
        {
            var folder = ProjectFolder.Open(job.ProjectFile!);
            var root = config.ExportRoot(folder.Project.Kind);
            if (root is null)
            {
                var what = config.ExportsTo == RenderServerDestination.Folder ? "export folder"
                    : folder.Project.Kind == ProjectKind.Movie ? "Emby Movies library" : "Emby TV Shows library";
                Finish(job, RenderJobState.Failed, $"This server has no {what}: it isn't set, or its folder wasn't there when the "
                    + "server started. Set it (with Docker, mount it into the container), then start the server again.");
                return;
            }

            // Its own discs, then the library's (looked through again if it hasn't some, as they may have been added since)
            var sent = new Dictionary<string, string>(job.Discs);
            if (folder.Project.Discs.Any(d => !sent.ContainsKey(d.Fingerprint) && discs.Find(d.Fingerprint) is null))
                discs.Rescan();
            var found = folder.Project.Discs.ToDictionary(d => d, DiscSource? (d) => sent.TryGetValue(d.Fingerprint, out var path)
                ? discs.Identify(path)
                : discs.Find(d.Fingerprint));
            var sources = new ProjectTranscodeSources(folder, found);
            var picked = job.Request.Files.ToDictionary(f => f.Key, f => f.SourceId);
            var available = ExportList.Build(sources, root, picked);

            // The file the user picked, as it's made here (it's skipped if it can't be, saying why)
            var files = new List<ExportFile>();
            for (int i = 0; i < job.Request.Files.Count; i++)
            {
                var request = job.Request.Files[i];
                var file = available.Find(f => f.Key == request.Key);
                file = file?.Alternates.Find(a => a.SourceId == request.SourceId) ?? file;
                if (file is not null)
                    files.Add(file);
                else
                    SetFile(job, i, new RenderFileStatus(request.Name, "Not in the project", 0, ExportOutcome.Skipped, "It isn't in the project that was sent."));
            }

            var options = new ExportOptions { WriteNfo = job.Request.WriteNfo, CopyArtwork = job.Request.CopyArtwork };
            var run = await ExportRunner.RunAsync(folder, sources, files, root, options,
                progress =>
                {
                    lock (_lock)
                        job.Overall = progress.Overall;
                    SetFile(job, IndexOf(job, progress.File), new RenderFileStatus(progress.File.Name, progress.Stage, progress.Fraction, null, null));
                },
                result =>
                {
                    if (result.Outcome == ExportOutcome.Done)
                    {
                        lock (_lock)
                            job.Made.Add(result.Destination);
                        Save();
                    }

                    SetFile(job, IndexOf(job, result.File), new RenderFileStatus(result.File.Name, Describe(result.Outcome),
                        result.Outcome == ExportOutcome.Done ? 1 : 0, result.Outcome, result.Error));
                },
                cancellation);

            if (run.Cancelled && stopping.IsCancellationRequested)
            {
                // Still running, as far as the next start is concerned, so it starts again then
                log($"Stopped \"{name}\" part way: it starts again from the beginning when the server next runs.");
                return;
            }

            int done, failed;
            lock (_lock)
            {
                done = job.Files.Count(f => f.Outcome == ExportOutcome.Done);
                failed = job.Files.Count(f => f.Outcome is ExportOutcome.Failed or ExportOutcome.Skipped);
            }

            var summary = $"Exported {done} of {Plural(job.Files.Count, "file")} to {root}"
                + (run.MetadataErrors.Count > 0 ? $"; couldn't write {Plural(run.MetadataErrors.Count, "details or artwork file")}: {run.MetadataErrors[0]}" : ".");
            Finish(job, run.Cancelled ? RenderJobState.Cancelled : failed > 0 ? RenderJobState.Failed : RenderJobState.Done, summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DiscException
                                       or DiscIdentificationException)
        {
            Finish(job, RenderJobState.Failed, ex.Message);
        }
    }

    private void Finish(Job job, RenderJobState state, string message)
    {
        lock (_lock)
        {
            job.State = state;
            job.Message = message;
            job.Finished = DateTime.UtcNow;
            if (state == RenderJobState.Done)
                job.Overall = 1;
        }

        RemoveDiscs(job);
        log($"{(state == RenderJobState.Done ? "Finished" : state.ToString())} \"{job.Request.ProjectName}\": {message}");
    }

    // The job's files are in the order they were asked for, and named as they were then
    private static int IndexOf(Job job, ExportFile file) =>
        job.Request.Files.FindIndex(f => f.Key == file.Key);

    private void SetFile(Job job, int index, RenderFileStatus status)
    {
        if (index < 0)
            return;
        lock (_lock)
            job.Files[index] = status;
    }

    private static string Describe(ExportOutcome outcome) => outcome switch
    {
        ExportOutcome.Done => "Done",
        ExportOutcome.Failed => "Failed",
        ExportOutcome.Cancelled => "Cancelled",
        _ => "Skipped",
    };

    private Job? Find(string id)
    {
        lock (_lock)
            return _jobs.Find(j => j.Id == id);
    }

    // ---- Keeping them ----

    private void Save()
    {
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(_jobs, FileOptions);
        try
        {
            Directory.CreateDirectory(dataFolder);
            var path = Path.Combine(dataFolder, ListFileName);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Couldn't save the list of jobs: {ex.Message}");
        }
    }

    /// <summary>
    /// The jobs from before the server last stopped. Those that were waiting go back in the queue, and so does one that
    /// was running, to start again from the beginning, with what it had made thrown away (a file can't be trusted if
    /// the server stopped short while making it). One that was still being sent failed.
    /// </summary>
    private void Load()
    {
        var path = Path.Combine(dataFolder, ListFileName);
        List<Job>? jobs = null;
        try
        {
            if (File.Exists(path))
                jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path), FileOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log($"Couldn't read the list of jobs: {ex.Message}");
        }

        var queued = 0;
        lock (_lock)
        {
            foreach (var job in jobs ?? [])
            {
                if (job.State == RenderJobState.Running)
                {
                    StartAgain(job);
                    log($"Starting \"{job.Request.ProjectName}\" again from the beginning: the server stopped while it was exporting.");
                }

                if (job.State == RenderJobState.Receiving)
                {
                    job.Message = "The server stopped while it was being sent.";
                    job.State = RenderJobState.Failed;
                    job.Finished = DateTime.UtcNow;
                    RemoveDiscs(job);
                }
                else if (job.State == RenderJobState.Queued)
                {
                    queued++;
                }

                _jobs.Add(job);
            }
        }

        if (queued > 0)
        {
            log($"{Plural(queued, "job")} waiting from before.");
            _queued.Release(queued);
        }
    }

    /// <summary>
    /// Puts a job that was cut short back in the queue as it was sent: what it made goes, and its working files (the
    /// half-made one is in the library's hidden partial folder, which the export clears as it starts).
    /// </summary>
    private void StartAgain(Job job)
    {
        foreach (var made in job.Made)
        {
            try
            {
                if (Directory.Exists(made))
                    Directory.Delete(made, recursive: true);
                else
                    File.Delete(made);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Couldn't delete \"{made}\": {ex.Message}");
            }
        }

        if (job.ProjectFile is not null)
            TryDeleteFolder(Path.Combine(Path.GetDirectoryName(job.ProjectFile)!, ProjectFolder.VideoFilesFolderName));
        job.Made.Clear();
        job.Files = job.Request.Files.Select(f => new RenderFileStatus(f.Name, "Waiting", 0, null, null)).ToList();
        job.Overall = 0;
        job.Message = null;
        job.State = RenderJobState.Queued;
    }

    /// <summary>Deletes the discs sent for a job: they were for it alone.</summary>
    private void RemoveDiscs(Job job)
    {
        TryDeleteFolder(DiscsFolderOf(job.Id));
        TryDeleteFolder(Path.Combine(FolderOf(job.Id), IncomingFolderName));
        lock (_lock)
            job.Discs.Clear();
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Plural(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";
}
