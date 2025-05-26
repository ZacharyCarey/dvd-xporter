using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.UI;

/// <summary>A program the app needs that wasn't found.</summary>
/// <param name="Name">What it's called, e.g. "FFmpeg".</param>
/// <param name="Use">What the app needs it for.</param>
/// <param name="Problem">What wasn't found.</param>
/// <param name="Package">Its package, for the install command.</param>
/// <param name="DownloadUrl">Where to get it.</param>
public sealed record MissingDependency(string Name, string Use, string Problem, string Package, string DownloadUrl);

/// <summary>
/// The programs the app can't work without, which aren't part of it: FFmpeg (ffmpeg and ffprobe, run from PATH) to
/// encode, and VLC (the system's libvlc) to preview. Checked at start-up.
/// </summary>
public static class Dependencies
{
    public const string FFmpegUrl = "https://ffmpeg.org/download.html";
    public const string VlcUrl = "https://www.videolan.org/vlc/";

    /// <summary>Those that weren't found. Takes up to a second or so (it runs ffmpeg and starts VLC).</summary>
    public static List<MissingDependency> FindMissing()
    {
        var missing = new List<MissingDependency>();
        var programs = FFmpegRunner.FindMissing();
        if (programs.Count > 0)
        {
            missing.Add(new("FFmpeg", "to test and export videos",
                $"{string.Join(" and ", programs)} {(programs.Count == 1 ? "isn't" : "aren't")} installed or on PATH", "ffmpeg", FFmpegUrl));
        }

        var problem = VlcPlayer.FindProblem();
        if (problem is not null)
            missing.Add(new("VLC", "to preview videos", problem, "vlc", VlcUrl));
        return missing;
    }

    /// <summary>How to install them on this system, e.g. "sudo apt install ffmpeg vlc".</summary>
    public static string InstallHint(IReadOnlyList<MissingDependency> missing)
    {
        var packages = string.Join(' ', missing.Select(m => m.Package));
        var them = missing.Count == 1 ? "it" : "them";
        if (OperatingSystem.IsLinux())
        {
            // The command for the package manager this system has
            string? command = File.Exists("/usr/bin/apt") ? $"sudo apt install {packages}"
                : File.Exists("/usr/bin/dnf") ? $"sudo dnf install {packages}   (from RPM Fusion)"
                : File.Exists("/usr/bin/pacman") ? $"sudo pacman -S {packages}"
                : File.Exists("/usr/bin/zypper") ? $"sudo zypper install {packages}"
                : null;
            var hint = command is null ? $"Install {them} with your system's package manager." : $"To install {them}, run:\n    {command}";
            if (missing.Any(m => m.Package == "vlc"))
                hint += "\n\nVLC from Flatpak or Snap isn't enough: the app needs the system's VLC (libvlc).";
            return hint;
        }

        if (OperatingSystem.IsWindows())
        {
            var ids = missing.Select(m => m.Package == "ffmpeg" ? "Gyan.FFmpeg" : "VideoLAN.VLC");
            return $"To install {them}, run:\n    winget install {string.Join(' ', ids)}\nthen open the app again.";
        }

        if (OperatingSystem.IsMacOS())
        {
            var commands = missing.Select(m => m.Package == "ffmpeg" ? "brew install ffmpeg" : "brew install --cask vlc");
            return $"To install {them}, run:\n    {string.Join("\n    ", commands)}";
        }

        return $"Download {them} from the links below.";
    }
}
