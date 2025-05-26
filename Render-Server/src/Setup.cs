using DvdXporter.Core.Remote;

namespace DvdXporter.Server;

/// <summary><c>dvdx-server setup</c>: asks for the server's settings in the console, starting from those it has.</summary>
public static class Setup
{
    public const string Warning =
        "Only run this server on a local network you trust. What the app sends it (projects and whole discs) isn't "
        + "encrypted, and anyone on the network with the access key can export with it, writing into its libraries. "
        + "Don't make its port reachable from the internet.";

    /// <summary>Returns the settings, saved, or null if the user stopped.</summary>
    public static ServerConfig? Run(ServerConfig? current, string dataFolder)
    {
        var config = current ?? new ServerConfig();
        Console.WriteLine("DVD Xporter Render Server setup");
        Console.WriteLine();
        WriteWarning();
        Console.WriteLine();
        if (!AskYesNo("Set up the server on this network?", current is not null))
            return null;

        Console.WriteLine();
        config.Name = Ask("Server name (shown in the app)", config.Name);
        config.Port = AskNumber("Port", config.Port);

        Console.WriteLine();
        Console.WriteLine("The Disc Library is a folder of disc rips (ISOs, VIDEO_TS or BDMV folders), in folders of any depth.");
        Console.WriteLine("Discs a job needs that aren't in it are sent from the app with the job, and deleted when it's done.");
        Console.WriteLine("The app can also send discs to keep in it.");
        config.DiscLibraryPath = AskFolder("Disc Library folder (blank for none)", config.DiscLibraryPath, optional: true);

        Console.WriteLine();
        Console.WriteLine("Where should exports go?");
        Console.WriteLine("  1. Straight into Emby libraries (one for movies, one for TV shows)");
        Console.WriteLine("  2. Into a folder, laid out as an Emby library would be");
        var emby = AskNumber("Choice", config.ExportsTo == RenderServerDestination.Folder ? 2 : 1, 1, 2) == 1;
        config.ExportsTo = emby ? RenderServerDestination.Emby : RenderServerDestination.Folder;
        if (emby)
        {
            config.MoviesLibraryPath = AskFolder("Emby Movies library folder (blank for none)", config.MoviesLibraryPath, optional: true);
            config.ShowsLibraryPath = AskFolder("Emby TV Shows library folder (blank for none)", config.ShowsLibraryPath, optional: true);
        }
        else
        {
            config.ExportFolderPath = AskFolder("Export folder", config.ExportFolderPath, optional: false);
        }

        Console.WriteLine();
        if (current is not null && !AskYesNo("Make a new access key? (The app will need the new one.)", false))
            Console.WriteLine("Keeping the access key.");
        else
            config.AccessKey = ServerConfig.NewAccessKey();

        config.Save(dataFolder);
        Console.WriteLine();
        Console.WriteLine($"Saved in {Path.Combine(dataFolder, ServerConfig.FileName)}.");
        Console.WriteLine();
        WriteKey(config);
        return config;
    }

    public static void WriteWarning()
    {
        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Warning: {Warning}");
        Console.ForegroundColor = color;
    }

    public static void WriteKey(ServerConfig config)
    {
        Console.WriteLine($"Access key: {config.AccessKey}");
        Console.WriteLine("Enter it in the app, in Settings › Server, to use this server.");
    }

    private static string Ask(string question, string current)
    {
        Console.Write(current.Length > 0 ? $"{question} [{current}]: " : $"{question}: ");
        var answer = Console.ReadLine()?.Trim() ?? throw new EndOfStreamException();
        return answer.Length > 0 ? answer : current;
    }

    private static int AskNumber(string question, int current, int min = 1, int max = 65535)
    {
        while (true)
        {
            if (int.TryParse(Ask(question, current.ToString()), out var number) && number >= min && number <= max)
                return number;
            Console.WriteLine($"Enter a number from {min} to {max}.");
        }
    }

    private static bool AskYesNo(string question, bool current)
    {
        while (true)
        {
            var answer = Ask($"{question} (y/n)", current ? "y" : "n").ToLowerInvariant();
            if (answer is "y" or "yes")
                return true;
            if (answer is "n" or "no")
                return false;
        }
    }

    /// <summary>
    /// A folder that's there, offering to make it if it isn't. With <paramref name="optional"/>, "-" clears one that
    /// was set.
    /// </summary>
    private static string? AskFolder(string question, string? current, bool optional)
    {
        while (true)
        {
            var answer = Ask(optional && current is not null ? $"{question}, - to clear" : question, current ?? "");
            if (optional && (answer.Length == 0 || answer == "-"))
                return null;
            if (answer.Length == 0)
            {
                Console.WriteLine("Enter a folder.");
                continue;
            }

            // The shell isn't there to expand "~" when it's typed in answer to a question
            if (answer == "~" || answer.StartsWith("~/"))
                answer = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + answer[1..];
            var path = Path.GetFullPath(answer);
            if (Directory.Exists(path))
                return path;
            if (File.Exists(path))
            {
                Console.WriteLine($"\"{path}\" is a file, not a folder.");
                continue;
            }
            if (!AskYesNo($"\"{path}\" doesn't exist. Make it?", true))
                continue;
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Couldn't make it: {e.Message}");
            }
        }
    }
}
