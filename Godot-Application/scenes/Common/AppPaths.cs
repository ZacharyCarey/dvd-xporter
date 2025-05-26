using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Real file system locations of the app's per-user data, which lives in Godot's <c>user://</c> folder.
/// </summary>
public static class AppPaths
{
    public static string Settings => Globalize("user://settings.json");

    public static string RecentProjects => Globalize("user://recent-projects.json");

    public static string FingerprintCache => Globalize("user://disc-fingerprints.json");

    /// <summary>The user's own templates and custom nodes, for adding to any project.</summary>
    public static string TranscodeLibrary => Globalize("user://transcode-library.json");

    public static string DefaultProjectLocation =>
        System.IO.Path.Combine(OS.GetSystemDir(OS.SystemDir.Documents), "DVD Xporter Projects");

    private static string Globalize(string path) => ProjectSettings.GlobalizePath(path);
}
