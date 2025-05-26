namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Dynamic Range node: keeps HDR video HDR, or tone maps it from HDR (PQ or HLG) to SDR (BT.709), on the CPU with
/// zscale or the GPU with libplacebo.
/// </summary>
public sealed class DynamicRangeNode(CanvasNode node)
{
    public const string ModeKey = "mode";
    public const string CurveKey = "curve";
    public const string GpuKey = "gpu";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.DynamicRangeNode,
        Title = "Dynamic Range",
        Category = NodeCategory.VideoFilters,
        Description = "Keeps HDR video HDR, or tone maps it to SDR for screens and players without HDR.\n\n" + PictureHelp.Hdr,
        PassKind = PortKind.Video,
        Settings =
        [
            new(ModeKey, "Output", SettingKind.Choice, "sdr", [new("sdr", "Tone map to SDR"), new("keep", "Keep HDR")],
                Tooltip: "Keep HDR needs H.265 or AV1 at 10-bit, which carry the HDR10 metadata over."),
            new(CurveKey, "Tone mapping", SettingKind.Choice, "mobius",
            [
                new("mobius", "Mobius (bright, natural)"),
                new("hable", "Hable (film-like, keeps highlights)"),
                new("reinhard", "Reinhard (soft)"),
                new("clip", "Clip (cuts off highlights)"),
            ], Visible: n => new DynamicRangeNode(n).TonesMap,
                Tooltip: "How HDR's bright highlights are squeezed into SDR's range."),
            new(GpuKey, "Use the GPU", SettingKind.Toggle, "false", Visible: n => new DynamicRangeNode(n).TonesMap,
                Tooltip: "Tone map on the graphics card with libplacebo: faster, especially for 4K. Needs a Vulkan graphics driver."),
        ],
    };

    /// <summary>Whether it tone maps to SDR, rather than keeping HDR.</summary>
    public bool TonesMap => node.Get(ModeKey, "sdr") == "sdr";

    /// <summary>The tone mapping curve, e.g. "mobius".</summary>
    public string Curve => node.Get(CurveKey, "mobius");

    /// <summary>Whether it tone maps on the graphics card (libplacebo, which needs Vulkan).</summary>
    public bool UsesGpu => node.GetBool(GpuKey);

    /// <summary>The ffmpeg filter: the tone mapping, or "null" to keep HDR.</summary>
    public string Filter
    {
        get
        {
            if (!TonesMap)
                return "null";
            if (UsesGpu)
                return $"libplacebo=tonemapping={Curve}:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:format=yuv420p";

            // To linear light (100 nits = SDR white), into BT.709's colours, tone mapped, then back to SDR video
            return $"zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap={Curve}:desat=0,"
                + "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
        }
    }
}
