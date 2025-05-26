using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DiscDecoders;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DvdXporter.Server;

/// <summary>What the app can ask the server (see <see cref="RenderServerProtocol"/>). Refusals are plain text, for the app to show.</summary>
public static class ServerApi
{
    public static RenderServerInfo Info(ServerConfig config) =>
        new(config.Name, config.Id, RenderServerProtocol.Version, config.Port, config.ExportsTo, config.DiscLibraryPath is not null);

    public static void Map(WebApplication app, ServerConfig config, ServerDiscs discs, RenderJobs jobs)
    {
        var expected = Encoding.UTF8.GetBytes($"Bearer {config.AccessKey}");
        app.Use(async (context, next) =>
        {
            var given = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (context.Request.Path != RenderServerProtocol.InfoPath && !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("The access key is wrong.");
                return;
            }

            await next(context);
        });

        app.MapGet(RenderServerProtocol.InfoPath, () => Info(config));
        app.MapGet("/api/check", () => Results.Ok());

        // ---- Discs ----

        app.MapPost("/api/discs/find", (List<string> fingerprints) => discs.FindForProject(fingerprints));

        app.MapGet("/api/library", () => discs.ListLibrary());

        // The app's Rescan: looking through the Disc Library again, e.g. after discs were renamed there
        app.MapPost("/api/library/rescan", async () =>
        {
            Console.WriteLine($"{Now} Looking for discs again, as the app asked...");
            var problems = await Task.Run(discs.Rescan);
            Console.WriteLine($"{Now} {RenderJobs.Plural(discs.LibraryCount, "disc")} in the Disc Library"
                + (problems.Count > 0 ? $"; {problems.Count} couldn't be read." : "."));
            return new RemoteRescan(discs.LibraryCount, problems);
        });

        // Reading a disc that's only here, for a project in the app: a line of JSON as each thing happens
        app.MapPost("/api/discs/{fingerprint}/contents", async (HttpContext context, string fingerprint, bool? everyFrame) =>
        {
            var source = discs.Find(fingerprint);
            if (source is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("This server doesn't have that disc.");
                return;
            }

            Console.WriteLine($"{Now} Reading \"{source.Name}\" for the app...");
            var updates = Channel.CreateUnbounded<RemoteReadUpdate>();
            var cancellation = context.RequestAborted;
            var reading = Task.Run(async () =>
            {
                RemoteReadUpdate last;
                try
                {
                    var contents = await discs.ReadContentsAsync(fingerprint, everyFrame == true,
                        new Reporter(p => updates.Writer.TryWrite(new RemoteReadUpdate(p.CheckingPictures, p.Done, p.Total, p.Fraction))),
                        warning => updates.Writer.TryWrite(new RemoteReadUpdate(Warning: warning)), cancellation);
                    last = new RemoteReadUpdate(Contents: contents);
                    Console.WriteLine($"{Now} Read \"{source.Name}\".");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"{Now} Stopped reading \"{source.Name}\": the app stopped it.");
                    updates.Writer.Complete();
                    return;
                }
                catch (Exception ex)
                {
                    last = new RemoteReadUpdate(Error: ex.Message);
                    Console.WriteLine($"{Now} Couldn't read \"{source.Name}\": {ex.Message}");
                }

                updates.Writer.TryWrite(last);
                updates.Writer.Complete();
            });

            context.Response.ContentType = "application/x-ndjson";
            try
            {
                await foreach (var update in updates.Reader.ReadAllAsync(cancellation))
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(update, RenderServerProtocol.Json) + "\n", cancellation);
                    await context.Response.Body.FlushAsync(cancellation);
                }
            }
            catch (OperationCanceledException)
            {
                // The app's gone: the reading stops too
            }

            await reading;
        });

        // Discs sent to keep in the Disc Library, and for one job only
        MapReceiving(app, "/api/library/{fingerprint}", (context, fingerprint) => discs.LibraryIncomingFolder(fingerprint),
            (context, fingerprint, name) =>
            {
                var source = discs.FinishForLibrary(fingerprint, name);
                Console.WriteLine($"{Now} Added \"{name}\" to the Disc Library: {source.Path}");
            });
        MapReceiving(app, "/api/jobs/{id}/discs/{fingerprint}", (context, fingerprint) => jobs.IncomingFolder(Id(context), fingerprint),
            (context, fingerprint, name) => jobs.FinishDisc(Id(context), fingerprint, name));

        // One of its files outside the video structure (PDFs, pictures, ...), for the Information step's Files
        app.MapGet("/api/discs/{fingerprint}/other-files", (string fingerprint, string path) => Refusing(() =>
        {
            var source = discs.Find(fingerprint);
            return source is not null
                ? Results.Stream(source.OpenOtherFile(path), "application/octet-stream")
                : Results.Text("This server doesn't have that disc.", statusCode: StatusCodes.Status404NotFound);
        }));

        // Clips read as they are, or runs of them read as one stream, for ffmpeg in the app: "a,b" and "offset:length,..."
        app.MapGet("/api/discs/{fingerprint}/read", (string fingerprint, string clips, string? blank) => Refusing(() =>
        {
            var source = discs.Find(fingerprint);
            if (source is null)
                return Results.Text("This server doesn't have that disc.", statusCode: StatusCodes.Status404NotFound);
            var names = clips.Split(',');
            var ranges = blank?.Split(',').Select(r => r.Split(':')).Select(r => new ByteRange(long.Parse(r[0]), int.Parse(r[1]))).ToList();
            var stream = names.Length == 1 && ranges is null ? source.OpenClip(names[0]) : source.OpenClips(names, ranges);
            return Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
        }));

        app.MapGet("/api/discs/{fingerprint}/clips/{clip}/info", (string fingerprint, string clip, string? order) => WithClip(discs, fingerprint, clip, order,
            (source, playback) =>
            {
                using var stream = playback.Stream;
                return Results.Ok(new RemoteClipInfo(stream.Length, source.Format == DiscFormat.Bluray, playback.TracksInOrder,
                    playback.SeekOffset is not null));
            }));

        app.MapGet("/api/discs/{fingerprint}/clips/{clip}/seek", (string fingerprint, string clip, long ms) => WithClip(discs, fingerprint, clip, null,
            (_, playback) =>
            {
                playback.Stream.Dispose();
                return Results.Ok(playback.SeekOffset?.Invoke(TimeSpan.FromMilliseconds(ms)) ?? 0);
            }));

        // Players seek with range requests, each with a stream of its own
        app.MapGet("/api/discs/{fingerprint}/clips/{clip}", (string fingerprint, string clip, string? order) => WithClip(discs, fingerprint, clip, order,
            (_, playback) => Results.Stream(playback.Stream, "application/octet-stream", enableRangeProcessing: true)));

        // ---- Jobs ----

        app.MapGet("/api/jobs", () => jobs.List());

        app.MapPost("/api/jobs", (RenderJobRequest request) => request.Files is { Count: > 0 }
            ? Results.Ok(jobs.Create(request))
            : Results.Text("There are no files to export.", statusCode: StatusCodes.Status400BadRequest));

        app.MapPut("/api/jobs/{id}/project", async (string id, HttpRequest request, CancellationToken cancellation) =>
        {
            try
            {
                return Results.Ok(await jobs.ReceiveProjectAsync(id, request.Body, cancellation));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (InvalidDataException ex)
            {
                return Results.Text($"The project couldn't be opened here: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/api/jobs/{id}/cancel", (string id) => jobs.Cancel(id)
            ? Results.Ok()
            : Results.Text("That job isn't running or waiting.", statusCode: StatusCodes.Status404NotFound));

        app.MapDelete("/api/jobs/{id}", (string id) => jobs.Remove(id)
            ? Results.Ok()
            : Results.Text("That job isn't finished, or isn't there.", statusCode: StatusCodes.Status404NotFound));
    }

    public static string Now => DateTime.Now.ToString("HH:mm:ss");

    /// <summary>Opens a clip on one of the server's discs for <paramref name="use"/>, or says why it can't be.</summary>
    /// <param name="order">The order to announce its streams in, as "128,32" (see <see cref="LocalDiscSource.OpenForPlayback"/>).</param>
    private static IResult WithClip(ServerDiscs discs, string fingerprint, string clip, string? order, Func<LocalDiscSource, ClipPlayback, IResult> use)
    {
        try
        {
            var source = discs.Find(fingerprint);
            if (source is null)
                return Results.Text("This server doesn't have that disc.", statusCode: StatusCodes.Status404NotFound);
            var tracks = order?.Split(',').Select(int.Parse).ToList();
            return use(source, source.OpenForPlayback(clip, tracks));
        }
        catch (Exception ex) when (ex is DiscException or IOException or FormatException)
        {
            return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Being sent a disc, file by file, at <paramref name="route"/>: what's been sent (<c>files</c>), each file
    /// (<c>files/path</c>), and that it's all there (<c>finish</c>).
    /// </summary>
    /// <param name="folderOf">Where it's put together.</param>
    /// <param name="finish">Takes it once it's all there, by its name (the rip's folder, or its image).</param>
    private static void MapReceiving(WebApplication app, string route, Func<HttpContext, string, string> folderOf, Action<HttpContext, string, string> finish)
    {
        app.MapGet($"{route}/files", (HttpContext context, string fingerprint) => Refusing(() => Results.Ok(ServerDiscs.ListIncoming(folderOf(context, fingerprint)))));

        app.MapPut($"{route}/files/{{**path}}", async (HttpContext context, string fingerprint, string path, CancellationToken cancellation) =>
        {
            try
            {
                await ServerDiscs.ReceiveFileAsync(folderOf(context, fingerprint), path, context.Request.Body, cancellation);
                return Results.Ok();
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost($"{route}/finish", (HttpContext context, string fingerprint, RemoteDiscFinish sent) => Refusing(() =>
        {
            finish(context, fingerprint, sent.Name);
            return Results.Ok();
        }));
    }

    // Passes progress on as it's reported, on whichever thread (Progress<T> would post it to the thread pool, out of order)
    private sealed class Reporter(Action<DiscContentsProgress> report) : IProgress<DiscContentsProgress>
    {
        public void Report(DiscContentsProgress value) => report(value);
    }

    private static string Id(HttpContext context) => context.Request.RouteValues["id"]?.ToString() ?? "";

    private static IResult Refusing(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or IOException or UnauthorizedAccessException
                                       or DiscException or FormatException or IndexOutOfRangeException)
        {
            return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
