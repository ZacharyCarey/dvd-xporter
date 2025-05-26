using DvdXporter.Core.Discs;
using DvdXporter.Core.Online;
using DvdXporter.Core.Remote;

namespace DvdXporter.UI;

/// <summary>
/// The app-wide services the pages use: finding and reading discs, the online metadata sites, and the render server
/// (if there is one).
/// </summary>
public sealed record AppServices(DiscServices Discs, OnlineServices Online, RenderServerLink Server);
