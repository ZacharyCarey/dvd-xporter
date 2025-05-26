using DvdXporter.Core.Metadata;

namespace DvdXporter.UI;

/// <summary>A movie's or show's IDs on TMDB, TVDB and IMDb.</summary>
public partial class IdsSection : InfoSection
{
    private ProviderIds _ids = new();

    public void Setup(ProviderIds ids)
    {
        _ids = ids;
        Fill("%TmdbEdit", ids.Tmdb);
        Fill("%TvdbEdit", ids.Tvdb);
        Fill("%ImdbEdit", ids.Imdb);
    }

    private void On_TmdbEdit_TextChanged(string text)
    {
        _ids.Tmdb = Clean(text);
        MarkEdited();
    }

    private void On_TvdbEdit_TextChanged(string text)
    {
        _ids.Tvdb = Clean(text);
        MarkEdited();
    }

    private void On_ImdbEdit_TextChanged(string text)
    {
        _ids.Imdb = Clean(text);
        MarkEdited();
    }
}
