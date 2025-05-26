using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;

namespace DvdXporter.Core.Remote;

/// <summary>Something went wrong talking to the render server: it couldn't be reached, turned the key down, or said no.</summary>
public sealed class RenderServerException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The app's side of a render server (see <see cref="RenderServerProtocol"/>): which discs it has, sending it discs and
/// projects to export, its jobs, and playing clips off its discs.
/// </summary>
public sealed class RenderServerClient : IDisposable
{
    // Quick questions; sending discs and projects takes as long as it takes
    private static readonly TimeSpan QuestionTimeout = TimeSpan.FromSeconds(5);
    private const int UploadBufferSize = 1024 * 1024;

    private readonly HttpClient _http;

    /// <param name="address">"host" or "host:port".</param>
    /// <exception cref="RenderServerException">The address isn't one.</exception>
    public RenderServerClient(string address, string key)
    {
        Address = address.Trim();
        if (!Uri.TryCreate($"http://{Address}/", UriKind.Absolute, out var uri) || uri.AbsolutePath != "/")
            throw new RenderServerException($"\"{Address}\" isn't a server address. Enter its name or IP address, and its port if it isn't {RenderServerProtocol.DefaultPort}.");
        if (uri.IsDefaultPort && !Address.EndsWith(":80", StringComparison.Ordinal))
            uri = new UriBuilder(uri) { Port = RenderServerProtocol.DefaultPort }.Uri;

        _http = new HttpClient { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    public string Address { get; }

    /// <summary>What the server said it is, once <see cref="ConnectAsync"/> has worked.</summary>
    public RenderServerInfo? Info { get; private set; }

    /// <summary>What to call the server: its name, or its address until it's been reached.</summary>
    public string Name => Info?.Name ?? Address;

    /// <summary>Checks the server is there and takes the key, and learns what it is.</summary>
    /// <exception cref="RenderServerException">It isn't, or doesn't.</exception>
    public async Task<RenderServerInfo> ConnectAsync(CancellationToken cancellation = default)
    {
        var info = await AskAsync<RenderServerInfo>(HttpMethod.Get, RenderServerProtocol.InfoPath.TrimStart('/'), null, cancellation);
        await AskAsync<object>(HttpMethod.Get, "api/check", null, cancellation);
        Info = info;
        return info;
    }

    // ---- Discs ----

    /// <summary>Which of the discs (by fingerprint) the server has in its Disc Library, with their other files.</summary>
    /// <exception cref="RenderServerException"/>
    public Task<List<RemoteLibraryDisc>> FindDiscsAsync(IEnumerable<string> fingerprints, CancellationToken cancellation = default) =>
        AskAsync<List<RemoteLibraryDisc>>(HttpMethod.Post, "api/discs/find", fingerprints.ToList(), cancellation);

    /// <summary>Every disc in the server's Disc Library.</summary>
    /// <exception cref="RenderServerException"/>
    public Task<List<RemoteLibraryDisc>> ListLibraryAsync(CancellationToken cancellation = default) =>
        AskAsync<List<RemoteLibraryDisc>>(HttpMethod.Get, "api/library", null, cancellation);

    /// <summary>Has the server look through its Disc Library again, e.g. after discs were renamed or added there.</summary>
    /// <exception cref="RenderServerException"/>
    public Task<RemoteRescan> RescanLibraryAsync(CancellationToken cancellation = default) =>
        AskAsync<RemoteRescan>(HttpMethod.Post, "api/library/rescan", null, cancellation, timeout: false);

    /// <summary>
    /// Has the server read its copy of a disc, as <see cref="LocalDiscSource.ReadInfoAsync"/>
    /// does one here, for a disc that's only on the server. Takes as long as reading the whole disc; cancelling stops it there too.
    /// </summary>
    /// <param name="everyFrame">Whether every frame is checked for interlacing (see <see cref="Projects.ProjectDisc.CheckEveryFrame"/>).</param>
    /// <param name="progress">Told as each clip is read, then as each is checked.</param>
    /// <param name="onWarning">Called with problems that didn't stop it.</param>
    /// <exception cref="RenderServerException">It couldn't be read, or the server couldn't be reached.</exception>
    internal async Task<DiscContents> ReadDiscAsync(string fingerprint, bool everyFrame, IProgress<DiscContentsProgress>? progress,
        Action<string>? onWarning, CancellationToken cancellation)
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"api/discs/{Uri.EscapeDataString(fingerprint)}/contents?everyFrame={(everyFrame ? "true" : "false")}"), cancellation);
        try
        {
            // A line as each thing happens, the last with what was read
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
            string? line;
            while ((line = await reader.ReadLineAsync(cancellation)) is not null)
            {
                if (line.Length == 0)
                    continue;
                var update = System.Text.Json.JsonSerializer.Deserialize<RemoteReadUpdate>(line, RenderServerProtocol.Json)!;
                if (update.Contents is not null)
                    return update.Contents;
                if (update.Error is not null)
                    throw new RenderServerException(update.Error);
                if (update.Warning is not null)
                    onWarning?.Invoke(update.Warning);
                else
                    progress?.Report(new DiscContentsProgress(update.CheckingPictures, update.Done, update.Total, update.Fraction));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            throw Unreachable(ex);
        }

        throw new RenderServerException($"The render server at {Address} stopped before it had read the disc.");
    }

    /// <summary>
    /// Sends the server a disc it hasn't got, file by file: for one job only (<paramref name="jobId"/>, which must still
    /// be being sent), as <paramref name="fingerprint"/> (the project's disc); or, with no job, to keep in its Disc
    /// Library (where it has to be that very disc). Files it already has from a send that was stopped short aren't sent
    /// again.
    /// </summary>
    /// <param name="progress">How far it's got, 0–1, by bytes (from another thread).</param>
    /// <exception cref="RenderServerException"/>
    /// <exception cref="IOException">The disc couldn't be read.</exception>
    public async Task SendDiscAsync(string? jobId, string fingerprint, LocalDiscSource source, Action<double>? progress, CancellationToken cancellation)
    {
        var files = source.ListFiles();

        var discPath = jobId is null
            ? $"api/library/{Uri.EscapeDataString(fingerprint)}"
            : $"api/jobs/{Uri.EscapeDataString(jobId)}/discs/{Uri.EscapeDataString(fingerprint)}";
        var received = (await AskAsync<List<RemoteDiscFile>>(HttpMethod.Get, $"{discPath}/files", null, cancellation))
            .ToDictionary(f => f.Path, f => f.Length);
        long total = Math.Max(1, files.Sum(f => f.Length)), done = 0;
        foreach (var file in files)
        {
            if (received.GetValueOrDefault(file.Name, -1) != file.Length)
            {
                var before = done;
                using var content = new FileContent(file.Path, sent => progress?.Invoke((double)(before + sent) / total));
                var escaped = string.Join('/', file.Name.Split('/').Select(Uri.EscapeDataString));
                await SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{discPath}/files/{escaped}") { Content = content }, cancellation);
            }

            done += file.Length;
            progress?.Invoke((double)done / total);
        }

        await AskAsync<object>(HttpMethod.Post, $"{discPath}/finish", new RemoteDiscFinish(Path.GetFileName(source.Path)), cancellation, timeout: false);
    }

    /// <summary>Plays a clip off the server's copy of a disc, as <see cref="DiscSource.OpenPreviewAsync"/> does off one here.</summary>
    /// <param name="order">The order to announce the clip's streams in (see <see cref="DiscSource.PlaybackOrder"/>).</param>
    /// <exception cref="RenderServerException"/>
    internal async Task<ClipPreview> OpenPreviewAsync(string fingerprint, string clip, IReadOnlyList<int>? order)
    {
        var query = order is null ? "" : $"?order={string.Join(',', order)}";
        var path = $"api/discs/{Uri.EscapeDataString(fingerprint)}/clips/{Uri.EscapeDataString(clip)}";
        var info = await AskAsync<RemoteClipInfo>(HttpMethod.Get, $"{path}/info{query}", null, default);
        var trackIds = info.TransportStream ? PreviewTrackIds.TransportStreamPid
            : info.TracksInOrder ? PreviewTrackIds.PrimedOrder
            : PreviewTrackIds.None;
        return new ClipPreview(null, new RemoteClipStream(_http, path + query, info.Length), trackIds, info.TransportStream ? "ts" : "ps",
            info.CanSeekExactly ? time => SeekOffset(path, time) : null);
    }

    /// <summary>
    /// Copies other files off the server's copy of a disc, as <see cref="DiscDecoders.Disc.CopyOtherFilesAsync"/> does off
    /// one here: one file to <paramref name="destination"/>, or a folder's into the folder <paramref name="destination"/>.
    /// </summary>
    /// <param name="files">The disc's other files, as the server listed them when it was found.</param>
    /// <param name="progress">Called with how far it's got, 0–1.</param>
    /// <exception cref="RenderServerException"/>
    /// <exception cref="IOException">There are none, or they couldn't be written.</exception>
    internal async Task CopyOtherFilesAsync(string fingerprint, IReadOnlyList<DiscDecoders.DiscFile> files, string path, string destination,
        Action<double>? progress, CancellationToken cancellation)
    {
        var folder = DiscDecoders.DiscFiles.IsFolder(path);
        var prefix = path == DiscDecoders.DiscFiles.AllFiles ? "" : path;
        var copies = DiscDecoders.DiscFiles.Under(files, path)
            .Select(f => (f.Path, f.Length, To: folder ? Path.Combine(destination, f.Path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)) : destination))
            .ToList();
        if (copies.Count == 0)
            throw new FileNotFoundException($"There's no \"{path}\" on the disc.");

        long total = Math.Max(1, copies.Sum(c => c.Length)), copied = 0;
        var buffer = new byte[UploadBufferSize];
        foreach (var (from, _, to) in copies)
        {
            var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
                $"api/discs/{Uri.EscapeDataString(fingerprint)}/other-files?path={Uri.EscapeDataString(from)}"), cancellation);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = File.Create(to);
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    copied += read;
                    progress?.Invoke((double)copied / total);
                }
            }
            catch (HttpRequestException ex)
            {
                throw Unreachable(ex);
            }
        }
    }

    /// <summary>
    /// Clips off the server's copy of a disc, read one after another as one stream (see
    /// <see cref="DiscDecoders.Disc.OpenClips"/>), or one clip as it is: for ffmpeg, through
    /// <see cref="Transcoding.ClipStreamServer"/>. Nothing is asked until it's read.
    /// </summary>
    /// <param name="blank">Bytes to turn into padding as they're read (a run's lead-in).</param>
    internal Stream OpenClips(string fingerprint, IReadOnlyList<string> clips, IReadOnlyList<DiscDecoders.Common.ByteRange>? blank)
    {
        var query = $"?clips={Uri.EscapeDataString(string.Join(',', clips))}";
        if (blank is { Count: > 0 })
            query += $"&blank={string.Join(',', blank.Select(r => $"{r.Offset}:{r.Length}"))}";
        return new RemoteClipStream(_http, $"api/discs/{Uri.EscapeDataString(fingerprint)}/read{query}", null);
    }

    /// <summary>Where in a clip's stream a time from its start is, by the disc's index (0 if the server can't say).</summary>
    private long SeekOffset(string clipPath, TimeSpan time)
    {
        try
        {
            return Ask<long>($"{clipPath}/seek?ms={(long)time.TotalMilliseconds}");
        }
        catch (RenderServerException)
        {
            return 0;
        }
    }

    // ---- Jobs ----

    /// <summary>
    /// Asks the server to export some of a project's files. The job waits while the discs it needs that the server
    /// hasn't got are sent for it (<see cref="SendDiscAsync"/>), then the project (<see cref="SendProjectAsync"/>).
    /// Cancel it if they can't be.
    /// </summary>
    /// <exception cref="RenderServerException"/>
    public Task<RenderJobStatus> CreateJobAsync(RenderJobRequest request, CancellationToken cancellation) =>
        AskAsync<RenderJobStatus>(HttpMethod.Post, "api/jobs", request, cancellation);

    /// <summary>Sends a job its project (see <see cref="Projects.ProjectPackage"/>), last, which puts it in the queue.</summary>
    /// <param name="packagePath">The project, packaged.</param>
    /// <param name="progress">How far it's got, 0–1 (from another thread).</param>
    /// <exception cref="RenderServerException"/>
    public async Task<RenderJobStatus> SendProjectAsync(string jobId, string packagePath, Action<double>? progress, CancellationToken cancellation)
    {
        using var content = new FileContent(packagePath, sent => progress?.Invoke((double)sent / Math.Max(1, new FileInfo(packagePath).Length)));
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Put, $"api/jobs/{Uri.EscapeDataString(jobId)}/project") { Content = content },
            cancellation);
        return await ReadAsync<RenderJobStatus>(response, cancellation);
    }

    /// <summary>The server's jobs: running, waiting and finished, newest first.</summary>
    /// <exception cref="RenderServerException"/>
    public Task<List<RenderJobStatus>> GetJobsAsync(CancellationToken cancellation = default) =>
        AskAsync<List<RenderJobStatus>>(HttpMethod.Get, "api/jobs", null, cancellation);

    /// <summary>Stops a job that's running (the file being made is thrown away), or takes one off the queue.</summary>
    /// <exception cref="RenderServerException"/>
    public Task CancelJobAsync(string id) => AskAsync<object>(HttpMethod.Post, $"api/jobs/{Uri.EscapeDataString(id)}/cancel", null, default);

    /// <summary>Forgets a finished job, and the copy of its project the server kept.</summary>
    /// <exception cref="RenderServerException"/>
    public Task RemoveJobAsync(string id) => AskAsync<object>(HttpMethod.Delete, $"api/jobs/{Uri.EscapeDataString(id)}", null, default);

    // ---- Requests ----

    /// <summary>Sends a request with a JSON body (or none) and reads the JSON answer, within <see cref="QuestionTimeout"/>.</summary>
    private async Task<T> AskAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellation, bool timeout = true)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout)
            limit.CancelAfter(QuestionTimeout);
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: RenderServerProtocol.Json);
        try
        {
            var response = await SendAsync(request, limit.Token);
            return await ReadAsync<T>(response, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new RenderServerException($"The render server at {Address} didn't answer.");
        }
    }

    // A blocking question, for players (which read and seek on their own thread)
    private T Ask<T>(string path)
    {
        using var limit = new CancellationTokenSource(QuestionTimeout);
        try
        {
            var response = _http.Send(new HttpRequestMessage(HttpMethod.Get, path), limit.Token);
            ThrowIfFailed(response);
            using var stream = response.Content.ReadAsStream(limit.Token);
            return System.Text.Json.JsonSerializer.Deserialize<T>(stream, RenderServerProtocol.Json)
                ?? throw new RenderServerException("The render server's answer was empty.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            throw Unreachable(ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }

        ThrowIfFailed(response);
        return response;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (typeof(T) == typeof(object))
            return default!;
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(RenderServerProtocol.Json, cancellation)
                ?? throw new RenderServerException("The render server's answer was empty.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new RenderServerException($"The render server's answer couldn't be read: {ex.Message}", ex);
        }
    }

    /// <summary>Turns a refusal into the server's reason (it answers with plain text).</summary>
    private void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RenderServerException($"The render server at {Address} didn't accept the access key. Check it in Settings › Server.");

        string reason;
        try
        {
            reason = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
        }
        catch (HttpRequestException)
        {
            reason = "";
        }

        throw new RenderServerException(reason.Length > 0 ? reason : $"The render server said {(int)response.StatusCode} ({response.ReasonPhrase}).");
    }

    private RenderServerException Unreachable(Exception ex) => ex is RenderServerException server ? server
        : ex is OperationCanceledException ? new RenderServerException($"The render server at {Address} didn't answer.", ex)
        : new RenderServerException($"Couldn't reach the render server at {Address}: {ex.Message}", ex);

    public void Dispose() => _http.Dispose();

    /// <summary>A file sent as a request's body, saying how much has gone.</summary>
    private sealed class FileContent(string path, Action<long> sent) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellation)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, UploadBufferSize, useAsync: true);
            var buffer = new byte[UploadBufferSize];
            long total = 0;
            int read;
            try
            {
                while ((read = await file.ReadAsync(buffer, cancellation)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    total += read;
                    sent(total);
                }
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                // Stopped: the connection's closed under it (so writing fails), and the request ends as cancelled
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(path).Length;
            return true;
        }
    }
}

/// <summary>
/// The app's render server, as Settings has it (none if it isn't set). Pages ask it for <see cref="Client"/> each time,
/// as it changes when the settings do.
/// </summary>
public sealed class RenderServerLink : IDisposable
{
    private string? _address;
    private string? _key;

    public RenderServerClient? Client { get; private set; }

    /// <summary>Why the settings' address can't be used, if it can't.</summary>
    public string? Problem { get; private set; }

    /// <summary>Uses the server at <paramref name="address"/> (none, if it's empty). Returns whether that changed anything.</summary>
    public bool Configure(string? address, string? key)
    {
        address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        if (address == _address && key == _key)
            return false;

        _address = address;
        _key = key;
        Client?.Dispose();
        Client = null;
        Problem = null;
        if (address is not null)
        {
            try
            {
                Client = new RenderServerClient(address, key ?? "");
            }
            catch (RenderServerException ex)
            {
                Problem = ex.Message;
            }
        }

        return true;
    }

    public void Dispose() => Client?.Dispose();
}

