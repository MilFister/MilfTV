using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using MilfTV.Localization;

namespace MilfTV.Core;

public enum SourceKind { Xtream, M3u, Stalker }

public enum ContentKind { Live, Movie, Series, Episode }

/// <summary>Une source IPTV configurée par l'utilisateur (compte Xtream, playlist M3U, portail MAC).</summary>
public sealed class SourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }

    /// <summary>Xtream : adresse du serveur. M3U : URL ou chemin de fichier. Stalker : adresse du portail.</summary>
    public string Url { get; set; } = "";
    public string? Username { get; set; }

    /// <summary>Mot de passe chiffré avec DPAPI (compte Windows courant).</summary>
    public string? PasswordProtected { get; set; }

    [JsonIgnore]
    public string? Password
    {
        get => Secret.Unprotect(PasswordProtected);
        set => PasswordProtected = Secret.Protect(value);
    }

    public string? MacAddress { get; set; }

    /// <summary>URL XMLTV facultative qui remplace celle fournie par la source.</summary>
    public string? EpgUrl { get; set; }

    /// <summary>User-Agent HTTP facultatif (certains fournisseurs l'exigent).</summary>
    public string? UserAgent { get; set; }

    /// <summary>Profil qui a ajouté la source (seul à pouvoir la modifier). Null = ancienne source.</summary>
    public string? OwnerProfileId { get; set; }

    /// <summary>Source visible par les autres profils (en lecture seule pour eux).</summary>
    public bool IsShared { get; set; }

    public DateTimeOffset? LastCatalogRefresh { get; set; }
    public DateTimeOffset? LastEpgRefresh { get; set; }

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        SourceKind.Xtream => Loc.T("Kind.Xtream"),
        SourceKind.M3u => Loc.T("Kind.M3u"),
        SourceKind.Stalker => Loc.T("Kind.Stalker"),
        _ => Kind.ToString()
    };

    public SourceConfig Clone() => (SourceConfig)MemberwiseClone();
}

public sealed partial class Category : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ContentKind Kind { get; set; }

    [JsonIgnore] public int Count { get; set; }
    [JsonIgnore] public bool IsAdult { get; set; }

    [JsonIgnore, ObservableProperty] private bool _isLocked;

    [JsonIgnore] public bool IsVirtual { get; set; }

    public override string ToString() => Name;
}

public abstract partial class CatalogItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CategoryId { get; set; } = "";

    [ObservableProperty] private string? _imageUrl;

    /// <summary>Date d'ajout (secondes Unix), 0 si inconnue.</summary>
    public long Added { get; set; }

    [JsonIgnore] public string SearchKey { get; set; } = "";
    [JsonIgnore, ObservableProperty] private bool _isFavorite;
    /// <summary>Profil courant : film vu en entier.</summary>
    [JsonIgnore, ObservableProperty] private bool _isWatched;
    /// <summary>Profil courant : progression d'un film entamé (0–100).</summary>
    [JsonIgnore, ObservableProperty] private double _watchProgress;
    /// <summary>Profil courant : série dont au moins un épisode a été regardé.</summary>
    [JsonIgnore, ObservableProperty] private bool _isInProgress;
    [JsonIgnore] public bool IsLocked { get; set; }

    [JsonIgnore] public abstract ContentKind Kind { get; }

    [JsonIgnore] public string Initials => TextNorm.Initials(Name);

    [JsonIgnore] public string Key => $"{Kind}:{Id}";

    /// <summary>Ligne secondaire des cartes (année, genre…).</summary>
    [JsonIgnore] public virtual string? SubLabel => null;
    /// <summary>Pastille de note sur l'affiche.</summary>
    [JsonIgnore] public virtual string? Badge => null;
}

public sealed partial class LiveChannel : CatalogItem
{
    public override ContentKind Kind => ContentKind.Live;

    public int Number { get; set; }
    /// <summary>Identifiant EPG (tvg-id / epg_channel_id).</summary>
    public string? EpgId { get; set; }
    /// <summary>URL directe (M3U). Vide pour Xtream/Stalker : construite à la lecture.</summary>
    public string? StreamUrl { get; set; }
    public string? StalkerCmd { get; set; }
    public int ArchiveDays { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }

    /// <summary>Clé utilisée pour retrouver les programmes du guide (EpgId ou correspondance par nom).</summary>
    [JsonIgnore] public string? EpgKey { get; set; }

    [JsonIgnore, ObservableProperty] private string? _nowTitle;
    [JsonIgnore, ObservableProperty] private string? _nowTime;
    [JsonIgnore, ObservableProperty] private string? _nextTitle;
    [JsonIgnore, ObservableProperty] private double _nowProgress;
    [JsonIgnore, ObservableProperty] private bool _isPlaying;
    [JsonIgnore, ObservableProperty] private bool _isRecording;
}

public sealed class Movie : CatalogItem
{
    public override ContentKind Kind => ContentKind.Movie;

    public int? Year { get; set; }
    public double? Rating { get; set; }
    public string? Plot { get; set; }
    public string? Genre { get; set; }
    public string? Extension { get; set; }
    public string? StreamUrl { get; set; }
    public string? TmdbId { get; set; }
    public string? StalkerCmd { get; set; }

    [JsonIgnore] public string? YearText => Year?.ToString();
    [JsonIgnore] public string? RatingText => Rating is > 0 ? Rating.Value.ToString("0.0", Loc.Culture) : null;
    [JsonIgnore] public override string? SubLabel => YearText;
    [JsonIgnore] public override string? Badge => RatingText is { } r ? "★ " + r : null;
}

public sealed class Series : CatalogItem
{
    public override ContentKind Kind => ContentKind.Series;

    public int? Year { get; set; }
    public double? Rating { get; set; }
    public string? Plot { get; set; }
    public string? Genre { get; set; }
    public string? Backdrop { get; set; }
    public string? TmdbId { get; set; }
    public string? Cast { get; set; }

    [JsonIgnore] public override string? SubLabel => YearText;
    [JsonIgnore] public override string? Badge => RatingText is { } r ? "★ " + r : null;

    /// <summary>Épisodes connus à l'avance (playlists M3U). Null pour Xtream/Stalker (chargés à la demande).</summary>
    public List<Episode>? Episodes { get; set; }

    [JsonIgnore] public string? YearText => Year?.ToString();
    [JsonIgnore] public string? RatingText => Rating is > 0 ? Rating.Value.ToString("0.0", Loc.Culture) : null;
}

public sealed partial class Episode : ObservableObject
{
    public string Id { get; set; } = "";
    public string SeriesId { get; set; } = "";
    public int Season { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Plot { get; set; }
    public string? ImageUrl { get; set; }
    public int? DurationSecs { get; set; }
    public string? Extension { get; set; }
    public string? StreamUrl { get; set; }
    public string? StalkerCmd { get; set; }
    public string? AirDate { get; set; }
    public double? Rating { get; set; }

    [JsonIgnore, ObservableProperty] private double _progress;
    [JsonIgnore, ObservableProperty] private bool _isWatched;
    [JsonIgnore, ObservableProperty] private bool _isCurrent;

    [JsonIgnore] public string Code => $"S{Season:00}E{Number:00}";
    /// <summary>Titre affiché : celui du fournisseur ou de TMDB, sinon « Épisode N ».</summary>
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Localization.Loc.T("Detail.EpisodeNumber", Number) : Title;
    [JsonIgnore] public string Key => $"Episode:{Id}";
    [JsonIgnore] public string? DurationText => DurationSecs is > 0 ? TextNorm.Duration(DurationSecs.Value) : null;
}

public sealed class SeasonInfo
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public List<Episode> Episodes { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class CastMember
{
    public string Name { get; set; } = "";
    public string? Character { get; set; }
    public string? PhotoUrl { get; set; }
}

/// <summary>Fiche détaillée d'un film ou d'une série (fournisseur et/ou TMDB).</summary>
public sealed class MediaDetails
{
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? Tagline { get; set; }
    public string? Plot { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public int? Year { get; set; }
    public string? ReleaseDate { get; set; }
    public double? Rating { get; set; }
    public int? RuntimeMinutes { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Director { get; set; }
    public List<CastMember> Cast { get; set; } = new();
    public string? TrailerYoutubeKey { get; set; }
    public string? TmdbId { get; set; }
    public string? Certification { get; set; }
}

public sealed class SeriesDetails
{
    public MediaDetails Info { get; set; } = new();
    public List<SeasonInfo> Seasons { get; set; } = new();
}

public sealed class EpgProgram
{
    public string ChannelKey { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? IconUrl { get; set; }
    public string? EpisodeNum { get; set; }

    public bool IsLiveAt(DateTimeOffset t) => Start <= t && t < End;
    public double ProgressAt(DateTimeOffset t) =>
        End <= Start ? 0 : Math.Clamp((t - Start).TotalSeconds / (End - Start).TotalSeconds * 100.0, 0, 100);

    public string TimeRange => $"{Start.ToLocalTime():HH:mm} – {End.ToLocalTime():HH:mm}";
    public string DayLabel => TextNorm.DayLabel(Start);
    public string DurationText => TextNorm.Duration((int)(End - Start).TotalSeconds);
}

public sealed class AccountInfo
{
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? MaxConnections { get; set; }
    public int? ActiveConnections { get; set; }
    public bool IsTrial { get; set; }
    public string? Message { get; set; }
    public string? ServerTimezone { get; set; }
    public List<string> OutputFormats { get; set; } = new();
}

/// <summary>Ce dont le lecteur a besoin pour ouvrir un flux.</summary>
public sealed record PlaybackInfo(string Url, string? UserAgent = null, string? Referrer = null);

/// <summary>Catalogue complet d'une source.</summary>
public sealed class Catalog
{
    public List<Category> LiveCategories { get; set; } = new();
    public List<Category> MovieCategories { get; set; } = new();
    public List<Category> SeriesCategories { get; set; } = new();
    public List<LiveChannel> Channels { get; set; } = new();
    public List<Movie> Movies { get; set; } = new();
    public List<Series> Series { get; set; } = new();

    /// <summary>URL XMLTV annoncée par la source (url-tvg, xmltv.php…).</summary>
    public string? XmltvUrl { get; set; }
    public DateTimeOffset Fetched { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public Dictionary<string, LiveChannel> ChannelById { get; private set; } = new();
    [JsonIgnore] public Dictionary<string, Movie> MovieById { get; private set; } = new();
    [JsonIgnore] public Dictionary<string, Series> SeriesById { get; private set; } = new();
    [JsonIgnore] public Dictionary<string, Category> CategoryByKey { get; private set; } = new();

    /// <summary>Calcule index, clés de recherche et compteurs. À appeler après chargement.</summary>
    public void BuildIndexes()
    {
        ChannelById = new Dictionary<string, LiveChannel>(Channels.Count);
        foreach (var c in Channels) { ChannelById.TryAdd(c.Id, c); c.SearchKey = TextNorm.Normalize(c.Name); }
        MovieById = new Dictionary<string, Movie>(Movies.Count);
        foreach (var m in Movies) { MovieById.TryAdd(m.Id, m); m.SearchKey = TextNorm.Normalize(m.Name); }
        SeriesById = new Dictionary<string, Series>(Series.Count);
        foreach (var s in Series) { SeriesById.TryAdd(s.Id, s); s.SearchKey = TextNorm.Normalize(s.Name); }

        CategoryByKey = new Dictionary<string, Category>();
        Count(LiveCategories, Channels, ContentKind.Live);
        Count(MovieCategories, Movies, ContentKind.Movie);
        Count(SeriesCategories, Series, ContentKind.Series);

        void Count<T>(List<Category> cats, List<T> items, ContentKind kind) where T : CatalogItem
        {
            var counts = new Dictionary<string, int>();
            foreach (var it in items) counts[it.CategoryId] = counts.GetValueOrDefault(it.CategoryId) + 1;
            foreach (var c in cats)
            {
                c.Kind = kind;
                c.Count = counts.GetValueOrDefault(c.Id);
                c.IsAdult = TextNorm.LooksAdult(c.Name);
                CategoryByKey[$"{kind}:{c.Id}"] = c;
            }
        }
    }

    public Category? FindCategory(ContentKind kind, string id) => CategoryByKey.GetValueOrDefault($"{kind}:{id}");
}
