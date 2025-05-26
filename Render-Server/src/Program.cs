using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;
using DvdXporter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// dvdx-server [run | setup | key | new-key | help] [--data <folder>]
var command = "run";
var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dvd-xporter-server");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--data" && i + 1 < args.Length)
        dataFolder = Path.GetFullPath(args[++i]);
    else
        command = args[i].TrimStart('-');
}

ServerConfig? config;
List<string> fromEnvironment;
var setUpNow = false;
try
{
    config = ServerConfig.Load(dataFolder);
    // Settings given by environment variables (e.g. in a Docker compose file) override those saved, and are enough
    // to set it up without asking
    var withEnvironment = config ?? new ServerConfig();
    fromEnvironment = withEnvironment.ApplyEnvironment();
    if (fromEnvironment.Count > 0)
    {
        setUpNow = config is null && command == "run";
        config = withEnvironment;
        if (setUpNow)
            config.Save(dataFolder);
    }
}
catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

const string NotSetUp = "The server hasn't been set up yet. Run \"dvdx-server setup\", or give its settings as environment "
    + "variables (DVDX_DISC_LIBRARY, DVDX_MOVIES_LIBRARY and so on: see README.md).";

switch (command)
{
    case "setup":
        if (fromEnvironment.Count > 0)
            Console.WriteLine($"{string.Join(", ", fromEnvironment)} {(fromEnvironment.Count == 1 ? "is" : "are")} set, and replace what's answered here each time it starts.\n");
        return Setup.Run(config, dataFolder) is null ? 1 : 0;
    case "key" when config is not null:
        Setup.WriteKey(config);
        return 0;
    case "new-key" when config is not null:
        config.AccessKey = ServerConfig.NewAccessKey();
        config.Save(dataFolder);
        Setup.WriteKey(config);
        return 0;
    case "key" or "new-key":
        Console.Error.WriteLine(NotSetUp);
        return 1;
    case "run":
        break;
    default:
        Console.WriteLine("""
            DVD Xporter Render Server: exports projects sent from DVD Xporter, on this computer.

            Usage: dvdx-server [command] [--data <folder>]

              run       Run the server (the default). Type "help" while it runs for what it can do.
              setup     Set up the server: its name, Disc Library, where exports go, and its access key.
              key       Show the access key, to enter in the app.
              new-key   Make a new access key (the app will need it).

              --data    The folder for the server's settings, the discs sent to it and its jobs.
            """);
        Console.WriteLine($"              (Here: {dataFolder})");
        return command is "help" or "h" or "?" ? 0 : 1;
}

if (config is null)
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine(NotSetUp);
        return 1;
    }

    config = Setup.Run(null, dataFolder);
    if (config is null)
        return 1;
    Console.WriteLine();
}

Console.WriteLine($"DVD Xporter Render Server {RenderServerProtocol.Version}: \"{config.Name}\"");
Setup.WriteWarning();
Console.WriteLine();
if (setUpNow)
{
    Console.WriteLine($"Set up from {string.Join(", ", fromEnvironment)}, and saved in {Path.Combine(dataFolder, ServerConfig.FileName)}.");
    Setup.WriteKey(config);
    Console.WriteLine();
}

var missing = FFmpegRunner.FindMissing();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"{string.Join(" and ", missing)} {(missing.Count == 1 ? "isn't" : "aren't")} installed or on PATH, and exporting needs them.");
    return 1;
}

// A folder that isn't there isn't used: in a container, it's one that wasn't mounted, and what's written there would be
// lost with the container
string? present(string? path, string what)
{
    if (path is null || Directory.Exists(path))
        return path;
    Console.WriteLine($"{what} \"{path}\" isn't there, so it isn't used.");
    return null;
}

config.DiscLibraryPath = present(config.DiscLibraryPath, "The Disc Library");
config.MoviesLibraryPath = present(config.MoviesLibraryPath, "The Emby Movies library");
config.ShowsLibraryPath = present(config.ShowsLibraryPath, "The Emby TV Shows library");
config.ExportFolderPath = present(config.ExportFolderPath, "The export folder");

var discs = new ServerDiscs(config, dataFolder);
void Rescan()
{
    Console.WriteLine(config.DiscLibraryPath is null ? "There's no Disc Library: discs are sent for each job." : $"Looking for discs in {config.DiscLibraryPath}...");
    var problems = discs.Rescan();
    foreach (var problem in problems.Take(10))
        Console.WriteLine($"  Couldn't read {problem}");
    if (problems.Count > 10)
        Console.WriteLine($"  ... and {problems.Count - 10} more.");
    Console.WriteLine($"{RenderJobs.Plural(discs.LibraryCount, "disc")} in the Disc Library.");
}

Rescan();

var jobs = new RenderJobs(config, discs, dataFolder, message => Console.WriteLine($"{ServerApi.Now} {message}"));

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = dataFolder });
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(config.Port);
    // Whole discs are sent in one file (an image)
    kestrel.Limits.MaxRequestBodySize = null;
});
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Logging.ClearProviders().AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
ServerApi.Map(app, config, discs, jobs);

using var stopping = new CancellationTokenSource();
// Ctrl+C, or SIGTERM (what "docker stop" and service managers send): stops what's running cleanly (its half-made file
// goes) before the server stops
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
void Stop(PosixSignalContext signal)
{
    signal.Cancel = true;
    stopping.Cancel();
}

try
{
    await app.StartAsync(stopping.Token);
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Couldn't listen on port {config.Port}: {ex.Message}");
    return 1;
}

var running = jobs.RunAsync(stopping.Token);
var discovery = DiscoveryResponder.RunAsync(config, stopping.Token);
var addresses = DiscoveryResponder.LocalAddresses().Select(a => $"{a}:{config.Port}").ToList();
Console.WriteLine($"Listening on {(addresses.Count > 0 ? string.Join(", ", addresses) : $"port {config.Port}")}. Press Ctrl+C to stop.");
Console.WriteLine();

if (!Console.IsInputRedirected)
    _ = Task.Run(() => ReadCommands());

try
{
    await Task.Delay(Timeout.Infinite, stopping.Token);
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Stopping...");
await running;
await discovery;
await app.StopAsync();
return 0;

// What can be typed while it runs
void ReadCommands()
{
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        switch (line.Trim().ToLowerInvariant())
        {
            case "":
                break;
            case "jobs":
                var list = jobs.List();
                if (list.Count == 0)
                    Console.WriteLine("No jobs.");
                foreach (var job in list)
                    Console.WriteLine($"  {job.Submitted.ToLocalTime():g}  {job.ProjectName}: {job.State}" + (job.State == RenderJobState.Running ? $" ({job.Overall:P0})" : ""));
                break;
            case "rescan":
                Rescan();
                break;
            case "key":
                Setup.WriteKey(config);
                break;
            case "quit" or "exit" or "stop":
                stopping.Cancel();
                return;
            default:
                Console.WriteLine("Commands: jobs (list them), rescan (look for discs again), key (show the access key), quit.");
                break;
        }
    }
}
