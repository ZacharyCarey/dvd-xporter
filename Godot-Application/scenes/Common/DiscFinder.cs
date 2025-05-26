using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using Godot;

namespace DvdXporter.UI;

public static class DiscFinder
{
    /// <summary>
    /// Where the app looks for a project's discs: on this computer, or failing that on the render server
    /// (<paramref name="server"/>, if there is one), to be used through it. Discs are read when they're added or found
    /// (see <see cref="DiscReader"/>), not here.
    /// </summary>
    public static CombinedDiscProvider For(ProjectFolder folder, DiscServices services, RenderServerClient? server)
    {
        var local = new LocalDiscProvider(services, folder.Cache);
        return new CombinedDiscProvider(server is null ? [local] : [local, new RemoteDiscProvider(server)], GD.PushWarning);
    }
}
