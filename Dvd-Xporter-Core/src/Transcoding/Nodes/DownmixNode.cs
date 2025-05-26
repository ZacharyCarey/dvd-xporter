namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Downmix node: mixes surround sound down to fewer channels with ffmpeg's resampler, at standard levels, with the
/// dialogue boosted, or as Dolby Pro Logic II.
/// </summary>
public sealed class DownmixNode(CanvasNode node)
{
    public const string LayoutKey = "layout";
    public const string ModeKey = "mode";
    public const string LfeKey = "lfe";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Downmix,
        Title = "Downmix",
        Category = NodeCategory.AudioFilters,
        Description = "Mixes surround sound (5.1, 7.1 or any other layout) down to stereo, mono or 5.1, with the centre "
            + "(dialogue) and surround channels at proper levels, for TVs and headphones without surround.",
        PassKind = PortKind.Audio,
        Settings =
        [
            new(LayoutKey, "Channels", SettingKind.Choice, "stereo", [new("stereo", "Stereo"), new("mono", "Mono"), new("5.1", "5.1 (from 7.1)")]),
            new(ModeKey, "Mix", SettingKind.Choice, "standard",
            [
                new("standard", "Standard (ITU)"),
                new("dialogue", "Dialogue boost"),
                new("dplii", "Dolby Pro Logic II"),
            ], Tooltip: "Standard mixes the centre and surrounds in at −3 dB. Dialogue boost keeps the centre (where "
                + "voices are) at full level and lowers the surrounds, for films whose dialogue is hard to hear. Dolby Pro Logic II "
                + "makes stereo that a surround receiver can turn back into surround."),
            new(LfeKey, "Include bass (LFE)", SettingKind.Toggle, "false",
                Visible: n => new DownmixNode(n).Mode != "dplii",
                Tooltip: "Mix the subwoofer channel in. Off by default, as standard downmixes leave it out (it can make speakers boom)."),
        ],
    };

    /// <summary>The channel layout to mix down to, as ffmpeg names it: "stereo", "mono" or "5.1".</summary>
    public string Layout => node.Get(LayoutKey, "stereo");

    /// <summary>"standard", "dialogue" (boosted) or "dplii" (Dolby Pro Logic II).</summary>
    public string Mode => node.Get(ModeKey, "standard");

    /// <summary>Whether the subwoofer channel is mixed in.</summary>
    public bool IncludeLfe => node.GetBool(LfeKey);

    public string Filter
    {
        get
        {
            var lfe = IncludeLfe ? "0.5" : "0";
            return Mode switch
            {
                "dplii" => "aresample=out_chlayout=stereo:matrix_encoding=dplii",
                "dialogue" => $"aresample=out_chlayout={Layout}:center_mix_level=1:surround_mix_level=0.5:lfe_mix_level={lfe}",
                _ => $"aresample=out_chlayout={Layout}:center_mix_level=0.707:surround_mix_level=0.707:lfe_mix_level={lfe}",
            };
        }
    }
}
