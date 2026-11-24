using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;

namespace MilfTV.Tmdb;

/// <summary>Épisode d'une saison TMDB (titre et résumé dans la langue de l'interface quand ils existent).</summary>
public sealed record TmdbEpisode(int Season, int Number, string? Name, string? Overview, string? StillUrl, string? AirDate, double? Rating, int? RuntimeMinutes);

/// <summary>
/// Client TMDB (The Movie Database) : fiches de films et de séries dans la langue de l'interface, affiches manquantes, épisodes.
/// Les réponses sont mises en cache dans SQLite (table <c>tmdb_cache</c>), par langue. Les méthodes Get*/Find* ne lèvent jamais
/// d'exception (null / liste vide en cas d'échec), sauf <see cref="OperationCanceledException"/> si le jeton de l'appelant est annulé.
/// </summary>
public sealed partial class TmdbClient
{
    private const string ApiBase = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(3);
    /// <summary>Au-delà, les entrées sont purgées (jusque-là, une entrée périmée sert de secours si TMDB est injoignable).</summary>
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(90);

    /// <summary>Langue des fiches, relevée une fois au début de chaque appel public (requêtes, clés de cache et mise en forme cohérentes).</summary>
    internal sealed record TmdbLang(string Code, string Api, CultureInfo Culture, string DateFormat)
    {
        public static TmdbLang Current() => new(Loc.Lang, Loc.TmdbLanguage, Loc.Culture, Loc.T("Date.LongFormat"));

        public bool IsEnglish => Code == "en";

        /// <summary>Suffixe des clés de cache. Vide en français : les clés historiques (fiches déjà en cache) restent valables.</summary>
        public string CacheSuffix => Api == "fr-FR" ? "" : ":" + Api;

        /// <summary>Langues des vidéos demandées : langue active, puis anglais, puis sans langue.</summary>
        public string VideoLanguages => IsEnglish ? "en,null" : $"{Code},en,null";
    }

    /// <summary>Limite le nombre de requêtes simultanées vers TMDB (toutes instances confondues).</summary>
    private static readonly SemaphoreSlim HttpGate = new(6, 6);

    private static readonly object SchemaGate = new();
    private static volatile bool _schemaReady;
    private static string? _rejectedKeyLogged;

    private readonly Func<string?> _apiKeyProvider;
    private readonly ConcurrentDictionary<string, Flight> _flights = new();

    /// <param name="apiKeyProvider">Fournit la clé API (v3) ou le jeton d'accès (v4) ; lu à chaque appel, l'utilisateur pouvant la changer.</param>
    public TmdbClient(Func<string?> apiKeyProvider)
    {
        ArgumentNullException.ThrowIfNull(apiKeyProvider);
        _apiKeyProvider = apiKeyProvider;
    }

    /// <summary>Vrai si une clé TMDB est renseignée.</summary>
    public bool IsConfigured => CurrentKey() is not null;

    // ───────────────────────────── API publique ─────────────────────────────

    /// <summary>Vérifie une clé avec GET /configuration et renvoie un message dans la langue de l'interface.</summary>
    public async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct)
    {
        key = key?.Trim() ?? "";
        if (key.Length == 0) return (false, Loc.T("Tmdb.NoKey"));
        if (key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)) return (false, Loc.T("Tmdb.InvalidChars"));
        try
        {
            var (status, doc) = await GetAsync("/configuration", key, ct).ConfigureAwait(false);
            using (doc)
            {
                if (doc is not null) return (true, Loc.T("Tmdb.KeyValid"));
                return status switch
                {
                    401 => (false, Loc.T("Tmdb.KeyRejected")),
                    429 => (false, Loc.T("Tmdb.TooManyRequests")),
                    >= 500 => (false, Loc.T("Tmdb.Unavailable", status)),
                    _ => (false, Loc.T("Tmdb.UnexpectedResponse", status)),
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return (false, Loc.T("Tmdb.Timeout"));
        }
        catch (JsonException)
        {
            return (false, Loc.T("Tmdb.BadResponse"));
        }
        catch (Exception ex)
        {
            Log.Warn("TMDB : test de la clé impossible : " + ex.Message);
            return (false, Loc.T("Tmdb.Unreachable"));
        }
    }

    /// <summary>Fiche d'un film dans la langue de l'interface : par identifiant TMDB s'il est connu, sinon par recherche titre/année.</summary>
    public Task<MediaDetails?> GetMovieAsync(string? tmdbId, string title, int? year, CancellationToken ct) =>
        GetDetailsAsync(tv: false, tmdbId, title, year, ct);

    /// <summary>Fiche d'une série dans la langue de l'interface : par identifiant TMDB s'il est connu, sinon par recherche titre/année.</summary>
    public Task<MediaDetails?> GetSeriesAsync(string? tmdbId, string title, int? year, CancellationToken ct) =>
        GetDetailsAsync(tv: true, tmdbId, title, year, ct);

    /// <summary>Affiche (w342) trouvée par simple recherche, pour compléter les grilles. Null si rien trouvé.</summary>
    public async Task<string?> FindPosterAsync(ContentKind kind, string title, int? year, CancellationToken ct)
    {
        if (kind == ContentKind.Live) return null;
        var key = CurrentKey();
        if (key is null) return null;
        var tv = kind is ContentKind.Series or ContentKind.Episode;
        var (clean, y) = CleanSearchTitle(title, year);
        var norm = TextNorm.Normalize(clean);
        if (norm.Length == 0) return null;
        var lang = TmdbLang.Current();

        return await CoalesceAsync<string?>($"poster:{Kind(tv)}:{norm}:{y}{lang.CacheSuffix}", null, async t =>
        {
            var hit = await SearchAsync(tv, clean, y, lang, key, t).ConfigureAwait(false);
            return Image(hit?.Poster, "w342");
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Épisodes d'une saison (vignettes w300, titres et résumés dans la langue de l'interface).</summary>
    public async Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(string tmdbTvId, int season, CancellationToken ct)
    {
        var key = CurrentKey();
        var id = ParseId(tmdbTvId);
        if (key is null || id is null || season < 0) return Array.Empty<TmdbEpisode>();
        var lang = TmdbLang.Current();
        var cacheKey = $"season:{id}:{season}{lang.CacheSuffix}";

        return await CoalesceAsync<IReadOnlyList<TmdbEpisode>>(cacheKey, Array.Empty<TmdbEpisode>(), async t =>
        {
            var json = await CachedAsync(cacheKey, async () =>
            {
                using var doc = await FetchAsync($"/tv/{id}/season/{season}?language={lang.Api}", key, t).ConfigureAwait(false);
                if (doc is null) return null;
                var episodes = MapSeason(doc.RootElement, season);
                if (!lang.IsEnglish && episodes.Any(e => e.Overview is null))
                    episodes = await FillEnglishOverviewsAsync(episodes, id, season, key, t).ConfigureAwait(false);
                return JsonSerializer.Serialize(episodes, Json.Options);
            }).ConfigureAwait(false);
            if (json is null) return Array.Empty<TmdbEpisode>();
            return JsonSerializer.Deserialize<List<TmdbEpisode>>(json, Json.Options) ?? (IReadOnlyList<TmdbEpisode>)Array.Empty<TmdbEpisode>();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clé YouTube de la bande-annonce dans la langue <paramref name="lang"/> (fr, en, es… ; « fr-FR » accepté) :
    /// GET /{movie|tv}/{id}/videos avec les vidéos de cette langue, puis anglaises, puis sans langue.
    /// Préférence : langue demandée, puis anglais, puis sans langue ; à langue égale « Trailer » avant « Teaser », officielle d'abord.
    /// Mise en cache par langue. Null si aucune bande-annonce, sans clé TMDB ou identifiant invalide.
    /// </summary>
    public async Task<string?> GetTrailerKeyAsync(ContentKind kind, string tmdbId, string lang, CancellationToken ct)
    {
        if (kind == ContentKind.Live) return null;
        var key = CurrentKey();
        var id = ParseId(tmdbId);
        if (key is null || id is null) return null;
        var tv = kind is ContentKind.Series or ContentKind.Episode;
        var code = VideoLanguageCode(lang);
        var cacheKey = $"videos:{Kind(tv)}:{id}:{code}";

        return await CoalesceAsync<string?>(cacheKey, null, t => CachedAsync(cacheKey, async () =>
        {
            var languages = code == "en" ? "en,null" : $"{code},en,null";
            using var doc = await FetchAsync($"/{Kind(tv)}/{id}/videos?language={VideoApiLanguage(code)}&include_video_language={languages}", key, t).ConfigureAwait(false);
            // Négatif (null) mis en cache : pas de nouvelle requête pendant quelques jours.
            return doc is null ? null : PickLocalizedTrailer(doc.RootElement, code);
        }), ct).ConfigureAwait(false);
    }

    /// <summary>Choix de la bande-annonce dans une réponse /videos : langue d'abord, puis type, puis vidéo officielle, puis la plus récente.</summary>
    internal static string? PickLocalizedTrailer(JsonElement root, string lang) =>
        Arr(root, "results")
            .Where(v => string.Equals(v.Str("site"), "YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Str("key")))
            .Select(v => (Video: v, Type: v.Str("type") switch { "Trailer" => 0, "Teaser" => 1, _ => 9 }))
            .Where(x => x.Type < 9)
            .OrderBy(x => LanguageRank(x.Video.Str("iso_639_1"), lang))
            .ThenBy(x => x.Type)
            .ThenBy(x => x.Video.Str("official") == "1" ? 0 : 1)
            .ThenByDescending(x => x.Video.Str("published_at") ?? "", StringComparer.Ordinal)
            .Select(x => x.Video.Str("key")!.Trim())
            .FirstOrDefault();

    /// <summary>Code de langue sur deux lettres (« fr-FR » → « fr ») ; anglais par défaut.</summary>
    private static string VideoLanguageCode(string? lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        return l.Length >= 2 && char.IsAsciiLetterLower(l[0]) && char.IsAsciiLetterLower(l[1]) ? l[..2] : "en";
    }

    /// <summary>Langue avec région attendue par TMDB (« fr » → « fr-FR »).</summary>
    private static string VideoApiLanguage(string code) => code switch
    {
        "fr" => "fr-FR",
        "en" => "en-US",
        "es" => "es-ES",
        "de" => "de-DE",
        "it" => "it-IT",
        "pt" => "pt-PT",
        "ja" => "ja-JP",
        "ko" => "ko-KR",
        "zh" => "zh-CN",
        _ => code + "-" + code.ToUpperInvariant(),
    };

    // ───────────────────────────── Fiches détaillées ─────────────────────────────

    private async Task<MediaDetails?> GetDetailsAsync(bool tv, string? tmdbId, string? title, int? year, CancellationToken ct)
    {
        var key = CurrentKey();
        if (key is null) return null;
        var id = ParseId(tmdbId);
        var (clean, y) = CleanSearchTitle(title, year);
        if (id is null && clean.Length == 0) return null;
        var lang = TmdbLang.Current();

        // Le travail partagé renvoie du JSON : chaque appelant reçoit sa propre instance de MediaDetails.
        var json = await CoalesceAsync<string?>($"details:{Kind(tv)}:{id}:{TextNorm.Normalize(clean)}:{y}{lang.CacheSuffix}", null, async t =>
        {
            if (id is not null && await GetDetailsByIdAsync(tv, id, lang, key, t).ConfigureAwait(false) is { } byId)
                return byId;
            // Identifiant absent ou inconnu de TMDB (les fournisseurs en donnent parfois de faux) : recherche par titre.
            if (clean.Length == 0) return null;
            var hit = await SearchAsync(tv, clean, y, lang, key, t).ConfigureAwait(false);
            if (hit is null || hit.Id == id) return null;
            return await GetDetailsByIdAsync(tv, hit.Id, lang, key, t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<MediaDetails>(json, Json.Options);
        }
        catch (Exception ex)
        {
            Log.Warn("TMDB : fiche en cache illisible : " + ex.Message);
            return null;
        }
    }

    /// <summary>JSON de <see cref="MediaDetails"/> pour un identifiant (cache puis TMDB), null si TMDB ne le connaît pas.</summary>
    private static Task<string?> GetDetailsByIdAsync(bool tv, string id, TmdbLang lang, string key, CancellationToken ct)
    {
        var kind = Kind(tv);
        return CachedAsync($"{kind}:{id}{lang.CacheSuffix}", async () =>
        {
            var path = tv
                ? $"/tv/{id}?language={lang.Api}&append_to_response=credits,aggregate_credits,videos,content_ratings&include_video_language={lang.VideoLanguages}"
                : $"/movie/{id}?language={lang.Api}&append_to_response=credits,videos,release_dates&include_video_language={lang.VideoLanguages}";
            using var doc = await FetchAsync(path, key, ct).ConfigureAwait(false);
            if (doc is null) return null;
            var details = MapDetails(doc.RootElement, tv, lang);
            if (!lang.IsEnglish && string.IsNullOrWhiteSpace(details.Plot))
                details.Plot = await FetchEnglishOverviewAsync($"/{kind}/{id}?language=en-US", key, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(details, Json.Options);
        });
    }

    /// <summary>Résumé anglais de secours quand la traduction dans la langue de l'interface manque.</summary>
    private static async Task<string?> FetchEnglishOverviewAsync(string path, string key, CancellationToken ct)
    {
        try
        {
            using var doc = await FetchAsync(path, key, ct).ConfigureAwait(false);
            return doc is null ? null : NullIfEmpty(doc.RootElement.Str("overview"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<List<TmdbEpisode>> FillEnglishOverviewsAsync(List<TmdbEpisode> episodes, string id, int season, string key, CancellationToken ct)
    {
        try
        {
            using var doc = await FetchAsync($"/tv/{id}/season/{season}?language=en-US", key, ct).ConfigureAwait(false);
            if (doc is null) return episodes;
            var english = new Dictionary<int, string>();
            foreach (var e in MapSeason(doc.RootElement, season))
                if (e.Overview is not null) english.TryAdd(e.Number, e.Overview);
            return episodes
                .Select(e => e.Overview is null && english.TryGetValue(e.Number, out var o) ? e with { Overview = o } : e)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return episodes;
        }
    }

    // ───────────────────────────── Recherche ─────────────────────────────

    /// <summary>Résultat de recherche mémorisé : identifiant TMDB et chemin de l'affiche.</summary>
    internal sealed record SearchHit(string Id, string? Poster);

    /// <summary>Recherche (titre déjà nettoyé) avec cache. Null si rien trouvé.
    /// Mémorisée par langue : les titres comparés et l'affiche renvoyée dépendent de la langue demandée.</summary>
    private static async Task<SearchHit?> SearchAsync(bool tv, string cleanTitle, int? year, TmdbLang lang, string key, CancellationToken ct)
    {
        var norm = TextNorm.Normalize(cleanTitle);
        if (norm.Length == 0) return null;
        var json = await CachedAsync($"search:{Kind(tv)}:{norm}:{year}{lang.CacheSuffix}", async () =>
        {
            var hit = await SearchRemoteAsync(tv, cleanTitle, norm, year, filterByYear: year is not null, lang, key, ct).ConfigureAwait(false);
            // Rien avec l'année (souvent fausse chez les fournisseurs) : nouvel essai sans.
            if (hit is null && year is not null)
                hit = await SearchRemoteAsync(tv, cleanTitle, norm, year, filterByYear: false, lang, key, ct).ConfigureAwait(false);
            return hit is null ? null : JsonSerializer.Serialize(hit, Json.Options);
        }).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<SearchHit>(json, Json.Options);
    }

    private static async Task<SearchHit?> SearchRemoteAsync(bool tv, string title, string norm, int? year, bool filterByYear, TmdbLang lang, string key, CancellationToken ct)
    {
        var path = $"/search/{Kind(tv)}?query={Uri.EscapeDataString(title)}&language={lang.Api}&include_adult=false";
        if (filterByYear && year is { } y)
            path += tv ? $"&first_air_date_year={y}" : $"&year={y}";
        using var doc = await FetchAsync(path, key, ct).ConfigureAwait(false);
        if (doc is null) return null;
        if (PickBest(doc.RootElement, norm, year, tv) is not { } best) return null;
        var id = ParseId(best.Str("id"));
        return id is null ? null : new SearchHit(id, NullIfEmpty(best.Str("poster_path")));
    }

    /// <summary>
    /// Choisit le meilleur résultat de recherche : titre (ou titre original) identique après normalisation,
    /// puis année identique (ou à un an près), puis ordre de TMDB (popularité).
    /// </summary>
    internal static JsonElement? PickBest(JsonElement root, string normTitle, int? year, bool tv)
    {
        JsonElement? best = null;
        (int Title, int Year) bestScore = (-1, -1);
        foreach (var r in Arr(root, "results").Take(20))
        {
            if (ParseId(r.Str("id")) is null) continue;
            var title = TextNorm.Normalize(r.Str(tv ? "name" : "title"));
            var original = TextNorm.Normalize(r.Str(tv ? "original_name" : "original_title"));
            var titleScore = title == normTitle || original == normTitle ? 1 : 0;
            var yearScore = 0;
            if (year is { } y && YearOf(r.Str(tv ? "first_air_date" : "release_date")) is { } ry)
                yearScore = ry == y ? 2 : Math.Abs(ry - y) == 1 ? 1 : 0;
            if (titleScore > bestScore.Title || (titleScore == bestScore.Title && yearScore > bestScore.Year))
            {
                best = r;
                bestScore = (titleScore, yearScore);
            }
        }
        return best;
    }

    /// <summary>Sigles qui font partie du titre (« NCIS : Los Angeles ») et que StripPrefix prendrait pour un préfixe de pays.</summary>
    [GeneratedRegex(@"\b(NCIS|CSI|FBI|CIA|LOL|JAG|SOS|MIB|UFC|WWE|SWAT|TMNT|XIII)\s*[:|\-](?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex TitleAcronymRegex();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"\b(?:S\d{1,2}(?:\s*-\s*S?\d{1,2})?(?:\s*E\d{1,4})?|\d{1,2}x\d{2,3}|(?:saisons?|seasons?|temporada|staffel)\s*\d{1,2}(?:\s*(?:-|à|a|to|et|&)\s*\d{1,2})?|[ée]pisodes?\s*\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();

    /// <summary>
    /// Nettoie un nom de catalogue IPTV pour la recherche : préfixes de pays, année, « S01E02 », « Saison 2 »,
    /// texte entre crochets/parenthèses, marqueurs de qualité. L'année fournie a priorité sur celle extraite du nom.
    /// </summary>
    internal static (string Title, int? Year) CleanSearchTitle(string? name, int? year)
    {
        if (string.IsNullOrWhiteSpace(name)) return ("", year);
        var input = TitleAcronymRegex().Replace(name.Trim(), "$1");
        var (title, found) = TextNorm.SplitYear(input);
        if (found > DateTime.Now.Year + 2)
        {
            // « Blade Runner 2049 » : ce nombre fait partie du titre, ce n'est pas une année.
            title = TextNorm.CleanTitle(TextNorm.StripPrefix(input));
            found = null;
        }
        var t = BracketedRegex().Replace(title, " ");
        t = SeasonEpisodeRegex().Replace(t, " ");
        t = TextNorm.CleanTitle(SpacesRegex().Replace(t, " "));
        if (TextNorm.Normalize(t).Length == 0)
            t = TextNorm.CleanTitle(title.Replace('[', ' ').Replace(']', ' ').Replace('(', ' ').Replace(')', ' ').Trim());
        return (t, year ?? found);
    }

    // ───────────────────────────── Correspondance JSON → modèle ─────────────────────────────

    /// <summary>Convertit la réponse /movie/{id} ou /tv/{id} (avec append_to_response) en <see cref="MediaDetails"/>.</summary>
    internal static MediaDetails MapDetails(JsonElement root, bool tv, TmdbLang lang)
    {
        var date = root.Str(tv ? "first_air_date" : "release_date");
        var directors = tv
            ? Arr(root, "created_by").Select(c => c.Str("name"))
            : Arr(root, "credits", "crew").Where(c => c.Str("job") == "Director").Select(c => c.Str("name"));
        var director = string.Join(", ", directors.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct());

        return new MediaDetails
        {
            Title = tv ? root.Str("name", "original_name") ?? "" : root.Str("title", "original_title") ?? "",
            OriginalTitle = NullIfEmpty(root.Str(tv ? "original_name" : "original_title")),
            Tagline = NullIfEmpty(root.Str("tagline")),
            Plot = NullIfEmpty(root.Str("overview")),
            PosterUrl = Image(root.Str("poster_path"), "w500"),
            BackdropUrl = Image(root.Str("backdrop_path"), "w1280"),
            Year = YearOf(date),
            ReleaseDate = LocalDate(date, lang),
            Rating = RatingOf(root),
            RuntimeMinutes = RuntimeOf(root),
            Genres = Arr(root, "genres").Select(g => g.Str("name")).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList(),
            Director = NullIfEmpty(director),
            Cast = CastOf(root, tv),
            TrailerYoutubeKey = PickTrailer(root, lang.Code),
            TmdbId = ParseId(root.Str("id")),
            Certification = tv ? TvCertification(root, lang.Code) : MovieCertification(root, lang.Code),
            OriginalLanguage = NullIfEmpty(root.Str("original_language")),
        };
    }

    /// <summary>Convertit la réponse /tv/{id}/season/{n} en liste d'épisodes.</summary>
    internal static List<TmdbEpisode> MapSeason(JsonElement root, int season)
    {
        var list = new List<TmdbEpisode>();
        foreach (var e in Arr(root, "episodes"))
        {
            if (e.Int("episode_number") is not { } number) continue;
            list.Add(new TmdbEpisode(
                e.Int("season_number") ?? season,
                number,
                NullIfEmpty(e.Str("name")),
                NullIfEmpty(e.Str("overview")),
                Image(e.Str("still_path"), "w300"),
                NullIfEmpty(e.Str("air_date")),
                RatingOf(e),
                Positive(e.Int("runtime"))));
        }
        return list;
    }

    /// <summary>Distribution principale (15 premiers) ; pour une série, les crédits cumulés de toutes les saisons si présents.</summary>
    internal static List<CastMember> CastOf(JsonElement root, bool tv)
    {
        var source = tv ? Arr(root, "aggregate_credits", "cast").ToList() : new List<JsonElement>();
        if (source.Count == 0) source = Arr(root, "credits", "cast").ToList();
        var cast = new List<CastMember>();
        foreach (var c in source)
        {
            var name = c.Str("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var character = c.Str("character")
                // crédits cumulés : le rôle tenu dans le plus d'épisodes
                ?? Arr(c, "roles").OrderByDescending(r => r.Int("episode_count") ?? 0)
                    .Select(r => r.Str("character")).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            cast.Add(new CastMember
            {
                Name = name.Trim(),
                Character = NullIfEmpty(character?.Trim()),
                PhotoUrl = Image(c.Str("profile_path"), "w185"),
            });
            if (cast.Count == 15) break;
        }
        return cast;
    }

    /// <summary>Clé YouTube de la bande-annonce : type Trailer puis Teaser, dans la langue <paramref name="lang"/> de préférence
    /// (puis en anglais), officielle de préférence.</summary>
    internal static string? PickTrailer(JsonElement root, string lang) =>
        Arr(root, "videos", "results")
            .Where(v => string.Equals(v.Str("site"), "YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Str("key")))
            .Select(v => (Video: v, Type: v.Str("type") switch { "Trailer" => 0, "Teaser" => 1, _ => 9 }))
            .Where(x => x.Type < 9)
            .OrderBy(x => x.Type)
            .ThenBy(x => LanguageRank(x.Video.Str("iso_639_1"), lang))
            .ThenBy(x => x.Video.Str("official") == "1" ? 0 : 1)
            .Select(x => x.Video.Str("key"))
            .FirstOrDefault();

    private static int LanguageRank(string? videoLang, string lang) => videoLang == lang ? 0 : videoLang == "en" ? 1 : 2;

    /// <summary>Pays dont la classification est préférée pour une langue d'interface, l'américaine servant de repli.</summary>
    private static string[] CertificationCountries(string lang) => lang switch
    {
        "fr" => ["FR", "US"],
        "en" => ["GB", "US"],
        "es" => ["ES", "US"],
        "de" => ["DE", "US"],
        "it" => ["IT", "US"],
        "pt" => ["PT", "US"],
        _ => ["US"],
    };

    /// <summary>Classification du pays de la langue d'interface (release_dates), sinon américaine.</summary>
    internal static string? MovieCertification(JsonElement root, string lang)
    {
        foreach (var country in CertificationCountries(lang))
            foreach (var r in Arr(root, "release_dates", "results").Where(r => r.Str("iso_3166_1") == country))
            {
                var cert = Arr(r, "release_dates")
                    .OrderBy(d => d.Int("type") == 3 ? 0 : 1) // sortie en salles d'abord
                    .Select(d => d.Str("certification")?.Trim())
                    .FirstOrDefault(c => !string.IsNullOrEmpty(c));
                if (cert is not null) return cert;
            }
        return null;
    }

    /// <summary>Classification du pays de la langue d'interface (content_ratings), sinon américaine.</summary>
    internal static string? TvCertification(JsonElement root, string lang)
    {
        foreach (var country in CertificationCountries(lang))
        {
            var rating = Arr(root, "content_ratings", "results")
                .Where(r => r.Str("iso_3166_1") == country)
                .Select(r => r.Str("rating")?.Trim())
                .FirstOrDefault(c => !string.IsNullOrEmpty(c));
            if (rating is not null) return rating;
        }
        return null;
    }

    internal static int? RuntimeOf(JsonElement root)
    {
        if (Positive(root.Int("runtime")) is { } runtime) return runtime;
        foreach (var e in Arr(root, "episode_run_time"))
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var m) && m > 0) return m;
        return root.Obj("last_episode_to_air") is { } last ? Positive(last.Int("runtime")) : null;
    }

    private static double? RatingOf(JsonElement e) =>
        e.Dbl("vote_average") is { } v && v > 0 ? Math.Round(v, 1) : null;

    /// <summary>« 2021-03-12 » → « 12 mars 2021 » selon la langue (format Date.LongFormat ; « 1er » pour le premier du mois en français).</summary>
    internal static string? LocalDate(string? isoDate, TmdbLang lang)
    {
        if (!DateTime.TryParseExact(isoDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        if (d.Day == 1 && lang.Code == "fr") return "1er " + d.ToString("MMMM yyyy", lang.Culture);
        return d.ToString(lang.DateFormat, lang.Culture);
    }

    internal static int? YearOf(string? isoDate) =>
        isoDate is { Length: >= 4 } && int.TryParse(isoDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 1800 ? y : null;

    internal static string? Image(string? path, string size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        return ImageBase + size + (path.StartsWith('/') ? path : "/" + path);
    }

    /// <summary>Identifiant TMDB valide (entier strictement positif) ou null (« 0 », vide, identifiant IMDb…).</summary>
    internal static string? ParseId(string? s) =>
        long.TryParse(s?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v.ToString(CultureInfo.InvariantCulture)
            : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var p in path)
        {
            if (cur.Obj(p) is not { } next) return Array.Empty<JsonElement>();
            cur = next;
        }
        return cur.ValueKind == JsonValueKind.Array ? cur.Items() : Array.Empty<JsonElement>();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static int? Positive(int? v) => v > 0 ? v : null;
    private static string Kind(bool tv) => tv ? "tv" : "movie";

    private string? CurrentKey()
    {
        try
        {
            var k = _apiKeyProvider()?.Trim();
            return string.IsNullOrEmpty(k) ? null : k;
        }
        catch (Exception ex)
        {
            Log.Warn("TMDB : lecture de la clé impossible : " + ex.Message);
            return null;
        }
    }

    // ───────────────────────────── HTTP ─────────────────────────────

    /// <summary>Clé refusée (401) : déjà signalée une fois dans le journal, inutile de la répéter à chaque affiche.</summary>
    private sealed class KeyRejectedException() : Exception("Clé TMDB refusée (401)");

    /// <summary>Appel API : document JSON si succès, null si TMDB répond 404 (introuvable), exception pour tout autre échec.</summary>
    private static async Task<JsonDocument?> FetchAsync(string path, string key, CancellationToken ct)
    {
        var (status, doc) = await GetAsync(path, key, ct).ConfigureAwait(false);
        if (doc is not null) return doc;
        if (status == 404) return null;
        if (status == 401)
        {
            if (Interlocked.Exchange(ref _rejectedKeyLogged, key) != key)
                Log.Warn("TMDB : clé API refusée (401), vérifiez-la dans les paramètres");
            throw new KeyRejectedException();
        }
        throw new HttpRequestException($"HTTP {status}");
    }

    /// <summary>
    /// GET sur l'API (au plus 6 en parallèle, 15 s maximum). Sur 429, attend Retry-After (2 s par défaut) et réessaie une fois.
    /// Renvoie le statut et le document JSON (seulement en cas de succès). Délai dépassé → <see cref="TimeoutException"/>.
    /// </summary>
    private static async Task<(int Status, JsonDocument? Doc)> GetAsync(string pathAndQuery, string key, CancellationToken ct)
    {
        // Jeton d'accès v4 (JWT « eyJ… ») en en-tête, clé v3 en paramètre.
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = ApiBase + pathAndQuery;
        if (!bearer) url += (pathAndQuery.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key);

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan retryDelay;
            await HttpGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);
                try
                {
                    using var req = Http.Request(url);
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (bearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    using var resp = await Http.Web.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    var status = (int)resp.StatusCode;
                    if (status == 429 && attempt == 0)
                    {
                        retryDelay = RetryDelay(resp);
                    }
                    else if (resp.IsSuccessStatusCode)
                    {
                        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                        var doc = await JsonDocument.ParseAsync(stream, default, cts.Token).ConfigureAwait(false);
                        return (status, doc);
                    }
                    else
                    {
                        return (status, null);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"TMDB ne répond pas ({pathAndQuery.Split('?')[0]})");
                }
            }
            finally
            {
                HttpGate.Release();
            }
            // Attente hors du sémaphore pour ne pas bloquer les autres requêtes.
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        var delay = ra?.Delta ?? (ra?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is not { } d || d <= TimeSpan.Zero) return TimeSpan.FromSeconds(2);
        return d > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : d;
    }

    // ───────────────────────────── Regroupement des requêtes identiques ─────────────────────────────

    /// <summary>Requête en cours partagée par tous les appelants qui demandent la même chose.</summary>
    private sealed class Flight
    {
        public readonly CancellationTokenSource Cts = new();
        public Task? Work;
        public int Waiters;
        public bool Abandoned;
    }

    /// <summary>
    /// Exécute <paramref name="work"/> une seule fois pour tous les appels simultanés de même clé, hors du thread appelant.
    /// Chaque appelant peut abandonner avec son propre jeton ; le travail n'est annulé que lorsque tous ont abandonné.
    /// Les erreurs sont journalisées une fois et remplacées par <paramref name="fallback"/>.
    /// </summary>
    private async Task<T> CoalesceAsync<T>(string key, T fallback, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Flight flight;
        Task<T> task;
        while (true)
        {
            flight = _flights.GetOrAdd(key, static _ => new Flight());
            lock (flight)
            {
                if (!flight.Abandoned)
                {
                    flight.Waiters++;
                    flight.Work ??= StartFlight(key, flight, fallback, work);
                    task = (Task<T>)flight.Work;
                    break;
                }
            }
            // Requête abandonnée par tous ses demandeurs mais pas encore retirée : on en démarre une nouvelle.
            _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
        }

        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return fallback;
        }
        finally
        {
            bool abandon;
            lock (flight)
            {
                abandon = --flight.Waiters == 0 && !task.IsCompleted;
                if (abandon) flight.Abandoned = true;
            }
            if (abandon)
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
                try { flight.Cts.Cancel(); } catch { /* ignoré */ }
            }
        }
    }

    private Task<T> StartFlight<T>(string key, Flight flight, T fallback, Func<CancellationToken, Task<T>> work)
    {
        var token = flight.Cts.Token;
        return Task.Run(async () =>
        {
            try
            {
                return await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (KeyRejectedException)
            {
                return fallback;
            }
            catch (Exception ex)
            {
                WarnThrottled($"échec pour « {key} »", ex.Message);
                return fallback;
            }
            finally
            {
                _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
            }
        }, CancellationToken.None);
    }

    // ───────────────────────────── Cache SQLite ─────────────────────────────

    /// <summary>Entrée du cache : JSON (null = TMDB ne connaît pas l'élément) et date de récupération (secondes Unix).</summary>
    internal readonly record struct CacheEntry(string? Json, long Fetched)
    {
        public bool IsNegative => string.IsNullOrEmpty(Json);
        public bool IsFresh => UnixNow() - Fetched < (IsNegative ? MissTtl : HitTtl).TotalSeconds;
    }

    /// <summary>
    /// Renvoie le JSON en cache s'il est frais ; sinon appelle <paramref name="fetch"/> (JSON à mémoriser, ou null si
    /// TMDB ne connaît pas l'élément : entrée négative) et met le cache à jour. Si TMDB est injoignable,
    /// une entrée positive périmée sert de secours ; sinon l'exception remonte.
    /// </summary>
    private static async Task<string?> CachedAsync(string cacheKey, Func<Task<string?>> fetch)
    {
        var entry = CacheRead(cacheKey);
        if (entry is { IsFresh: true } fresh) return fresh.IsNegative ? null : fresh.Json;
        try
        {
            var json = await fetch().ConfigureAwait(false);
            CacheWrite(cacheKey, json);
            return json;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && entry is { IsNegative: false })
        {
            if (ex is not KeyRejectedException)
                WarnThrottled($"{cacheKey} (données en cache plus anciennes utilisées)", ex.Message);
            return entry.Value.Json;
        }
    }

    private static SqliteConnection OpenCache()
    {
        var c = Db.Open();
        if (!_schemaReady)
        {
            lock (SchemaGate)
            {
                if (!_schemaReady)
                {
                    Db.Exec(c, "CREATE TABLE IF NOT EXISTS tmdb_cache(key TEXT PRIMARY KEY, json TEXT, fetched INTEGER)");
                    Db.Exec(c, "DELETE FROM tmdb_cache WHERE fetched < $old", ("$old", UnixNow() - (long)CacheMaxAge.TotalSeconds));
                    _schemaReady = true;
                }
            }
        }
        return c;
    }

    internal static CacheEntry? CacheRead(string key)
    {
        try
        {
            using var c = OpenCache();
            using var cmd = Db.Command(c, "SELECT json, fetched FROM tmdb_cache WHERE key = $k", ("$k", key));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new CacheEntry(r.GetStringOrNull(0), r.IsDBNull(1) ? 0 : r.GetInt64(1));
        }
        catch (Exception ex)
        {
            Log.Warn("TMDB : lecture du cache impossible : " + ex.Message);
            return null;
        }
    }

    /// <summary>Mémorise un JSON (null = résultat négatif) avec la date courante.</summary>
    internal static void CacheWrite(string key, string? json, long? fetched = null)
    {
        try
        {
            using var c = OpenCache();
            Db.Exec(c, "INSERT OR REPLACE INTO tmdb_cache(key, json, fetched) VALUES($k, $j, $f)",
                ("$k", key), ("$j", json), ("$f", fetched ?? UnixNow()));
        }
        catch (Exception ex)
        {
            Log.Warn("TMDB : écriture du cache impossible : " + ex.Message);
        }
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static readonly object WarnGate = new();
    private static string? _lastWarnReason;
    private static DateTime _lastWarnAt;

    /// <summary>Journalise un échec sans répéter la même cause plus d'une fois par minute (grille d'affiches hors ligne, par exemple).</summary>
    private static void WarnThrottled(string context, string reason)
    {
        lock (WarnGate)
        {
            var now = DateTime.UtcNow;
            if (reason == _lastWarnReason && now - _lastWarnAt < TimeSpan.FromMinutes(1)) return;
            _lastWarnReason = reason;
            _lastWarnAt = now;
        }
        Log.Warn($"TMDB : {context} : {reason}");
    }
}
