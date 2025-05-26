using System.Text;

namespace FFmpeg;

/// <summary>ffmpeg arguments as text: one command line to show or paste into a terminal, and options typed as text.</summary>
public static class FFmpegArguments
{
    /// <summary>The arguments as one ffmpeg command line, each quoted as needed.</summary>
    public static string ToCommandLine(IEnumerable<string> args) =>
        string.Join(' ', args.Prepend("ffmpeg").Select(Quote));

    /// <summary>Splits typed options on spaces, keeping "quoted parts" together, e.g. <c>-x265-params "a=1:b=2"</c>.</summary>
    public static List<string> Split(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        bool any = false;
        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote.Value)
                    quote = null;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                any = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || any)
                    args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0 || any)
            args.Add(current.ToString());
        return args;
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=+@%,".Contains(c))
            ? arg
            : "'" + arg.Replace("'", "'\\''") + "'";
}
