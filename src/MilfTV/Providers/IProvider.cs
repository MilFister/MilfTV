using MilfTV.Core;

namespace MilfTV.Providers;

/// <summary>Accès à une source IPTV (Xtream Codes, playlist M3U, portail MAC/Stalker).</summary>
public interface IProvider : IDisposable
{
    SourceConfig Source { get; }

    /// <summary>Vérifie la connexion et renvoie l'état du compte (expiration, connexions…).</summary>
    Task<AccountInfo> TestAsync(CancellationToken ct);

    /// <summary>Télécharge le catalogue complet (catégories, chaînes, films, séries).</summary>
    Task<Catalog> LoadCatalogAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Fiche détaillée d'un film côté fournisseur (null si indisponible).</summary>
    Task<MediaDetails?> GetMovieDetailsAsync(Movie movie, CancellationToken ct);

    /// <summary>Saisons et épisodes d'une série.</summary>
    Task<SeriesDetails?> GetSeriesDetailsAsync(Series series, CancellationToken ct);

    /// <summary>URL lisible pour une chaîne, un film, ou un épisode (episode non null).</summary>
    Task<PlaybackInfo> ResolveAsync(CatalogItem item, Episode? episode, CancellationToken ct);

    /// <summary>Programmes d'une chaîne directement auprès du fournisseur (complément du XMLTV). Liste vide si non pris en charge.</summary>
    Task<IReadOnlyList<EpgProgram>> GetChannelEpgAsync(LiveChannel channel, CancellationToken ct);

    /// <summary>URL XMLTV à utiliser pour le guide (configurée, ou annoncée par la source). Null si aucune.</summary>
    string? GetXmltvUrl(Catalog catalog);
}

public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);
