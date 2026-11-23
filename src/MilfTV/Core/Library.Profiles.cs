using CommunityToolkit.Mvvm.ComponentModel;
using MilfTV.Data;

namespace MilfTV.Core;

/// <summary>Série en cours pour le tableau de bord : dernier épisode regardé.</summary>
public sealed class SeriesProgress
{
    public required Series Series { get; init; }
    public required WatchEntry LastEpisode { get; init; }
    public int WatchedCount { get; init; }
    public string Label => LastEpisode.Subtitle ?? "";
    public double Progress => LastEpisode.Watched ? 100 : LastEpisode.Progress;
}

/// <summary>Profils : historique, « Ma liste », états « déjà vu / en cours », suggestions.</summary>
public sealed partial class Library
{
    [ObservableProperty] private UserProfile? _profile;

    public string ProfileId => Profile?.Id ?? "";

    private static readonly ProfilePrefs DefaultPrefs = new();

    /// <summary>Préférences du profil actif (valeurs par défaut s'il n'y en a pas).</summary>
    public ProfilePrefs Prefs => Profile?.Prefs ?? DefaultPrefs;
    public bool IsKidProfile => Profile?.IsKid == true;

    /// <summary>Déclenché (thread UI) après un changement de profil.</summary>
    public event Action? ProfileChanged;

    /// <summary>Déclenché quand les préférences du profil actif changent (tableau de bord, suggestions…).</summary>
    public event Action? PrefsChanged;
    public void NotifyPrefsChanged() => PrefsChanged?.Invoke();

    private List<string> _watchlist = new();

    /// <summary>Active un profil : recharge son historique et sa liste, met à jour les affiches.</summary>
    public async Task SetProfileAsync(UserProfile profile)
    {
        Profile = profile;
        S.LastProfileId = profile.Id;
        SettingsStore.Save();
        if (Catalog is { } c && Source is { } src)
        {
            await Task.Run(() => LoadProfileState(src));
            ApplyProfileFlags(c);
            ApplyParental(c);
        }
        ProfileChanged?.Invoke();
        ParentalChanged?.Invoke();
        FavoritesChanged?.Invoke();
    }

    private void LoadProfileState(SourceConfig src)
    {
        var profileId = ProfileId;
        try
        {
            var watch = UserDataStore.GetAllWatch(src.Id, profileId);
            var watchlist = UserDataStore.GetWatchlist(src.Id, profileId);
            lock (_watchGate)
            {
                // Profil changé pendant la lecture de la base : ces données ne sont plus les bonnes.
                if (ProfileId != profileId) return;
                _watch = watch;
                _watchlist = watchlist;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Données du profil : " + ex.Message);
            lock (_watchGate)
            {
                _watch = new();
                _watchlist = new();
            }
        }
    }

    private void ApplyProfileFlags(Catalog c)
    {
        var inList = WatchlistKeys().ToHashSet();
        Dictionary<string, WatchEntry> watch;
        lock (_watchGate) watch = new(_watch);
        var seriesActivity = watch.Values.Where(e => e.Kind == ContentKind.Episode && e.ParentId is not null)
            .Select(e => e.ParentId!).ToHashSet();
        foreach (var m in c.Movies)
        {
            m.IsFavorite = inList.Contains(m.Key);
            var w = watch.GetValueOrDefault(m.Key);
            m.IsWatched = w?.Watched == true;
            m.WatchProgress = w is { Watched: false } ? w.Progress : 0;
        }
        foreach (var s in c.Series)
        {
            s.IsFavorite = inList.Contains(s.Key);
            s.IsInProgress = seriesActivity.Contains(s.Id);
        }
    }

    private static void UpdateItemFlags(Catalog c, WatchEntry e)
    {
        if (e.Kind == ContentKind.Movie && c.MovieById.TryGetValue(e.ItemId, out var m))
        {
            m.IsWatched = e.Watched;
            m.WatchProgress = e.Watched ? 0 : e.Progress;
        }
        else if (e.Kind == ContentKind.Episode && e.ParentId is not null && c.SeriesById.TryGetValue(e.ParentId, out var s))
        {
            s.IsInProgress = true;
        }
    }

    // ───────────── Listes du profil ─────────────

    /// <summary>« Ma liste » : films et séries à voir, du plus récemment ajouté au plus ancien.</summary>
    public List<CatalogItem> GetWatchlistItems()
    {
        var c = Catalog;
        if (c is null) return new();
        var list = new List<CatalogItem>();
        foreach (var key in WatchlistKeys())
        {
            var parts = key.Split(':', 2);
            if (parts.Length != 2) continue;
            CatalogItem? it = parts[0] switch
            {
                "Movie" => c.MovieById.GetValueOrDefault(parts[1]),
                "Series" => c.SeriesById.GetValueOrDefault(parts[1]),
                _ => null
            };
            if (it is not null && !IsHidden(it)) list.Add(it);
        }
        return list;
    }

    /// <summary>Séries dont le profil a regardé au moins un épisode, la plus récente d'abord.</summary>
    public List<SeriesProgress> GetSeriesInProgress(int limit = 30)
    {
        var c = Catalog;
        if (c is null) return new();
        return WatchValues()
            .Where(e => e.Kind == ContentKind.Episode && e.ParentId is not null)
            .GroupBy(e => e.ParentId!)
            .Select(g => (Series: c.SeriesById.GetValueOrDefault(g.Key), Last: g.OrderByDescending(e => e.UpdatedAt).First(), Count: g.Count(e => e.Watched)))
            .Where(x => x.Series is not null && !IsHidden(x.Series))
            .OrderByDescending(x => x.Last.UpdatedAt)
            .Take(limit)
            .Select(x => new SeriesProgress { Series = x.Series!, LastEpisode = x.Last, WatchedCount = x.Count })
            .ToList();
    }

    /// <summary>Films entamés (non terminés) du profil.</summary>
    public List<Movie> GetMoviesInProgress() =>
        Catalog is not { } c ? new() :
        WatchValues().Where(e => e.Kind == ContentKind.Movie && !e.Watched && e.Position > 30)
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => c.MovieById.GetValueOrDefault(e.ItemId))
            .Where(m => m is not null && !IsHidden(m)).Cast<Movie>().ToList();

    /// <summary>Films vus en entier par le profil.</summary>
    public List<Movie> GetWatchedMovies() =>
        Catalog is not { } c ? new() :
        WatchValues().Where(e => e.Kind == ContentKind.Movie && e.Watched)
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => c.MovieById.GetValueOrDefault(e.ItemId))
            .Where(m => m is not null && !IsHidden(m)).Cast<Movie>().ToList();

    public List<WatchEntry> GetContinueWatching(int limit = 20) =>
        Source is { } src ? UserDataStore.GetContinueWatching(src.Id, ProfileId, limit) : new();

    public WatchEntry? GetLastEpisode(string seriesId) =>
        Source is { } src ? UserDataStore.GetLastEpisode(src.Id, ProfileId, seriesId) : null;

    /// <summary>Copie des entrées du profil (progressions des épisodes d'une série…).</summary>
    public Dictionary<string, WatchEntry> GetAllWatchEntries()
    {
        lock (_watchGate) return new(_watch);
    }

    /// <summary>Efface l'historique d'une série (tous ses épisodes) pour le profil.</summary>
    public void ClearSeriesHistory(Series s)
    {
        if (Source is null) return;
        UserDataStore.RemoveSeriesHistory(Source.Id, ProfileId, s.Id);
        lock (_watchGate)
            foreach (var k in _watch.Where(kv => kv.Value.ParentId == s.Id).Select(kv => kv.Key).ToList()) _watch.Remove(k);
        s.IsInProgress = false;
    }

    // ───────────── Suggestions ─────────────

    private static IEnumerable<string> Genres(CatalogItem it)
    {
        var g = it switch { Movie m => m.Genre, Series s => s.Genre, _ => null };
        if (string.IsNullOrWhiteSpace(g)) yield break;
        foreach (var part in g.Split(new[] { ',', '/', '|', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = TextNorm.Normalize(part);
            if (n.Length > 1) yield return n;
        }
    }

    /// <summary>Éléments (films / séries) que le profil a regardés, du plus récent au plus ancien.</summary>
    public List<CatalogItem> GetWatchedItems(int limit = 50)
    {
        var c = Catalog;
        if (c is null) return new();
        var seen = new HashSet<string>();
        var list = new List<CatalogItem>();
        foreach (var e in WatchValues().Where(e => e.Kind is ContentKind.Movie or ContentKind.Episode).OrderByDescending(e => e.UpdatedAt))
        {
            CatalogItem? it = e.Kind == ContentKind.Movie
                ? c.MovieById.GetValueOrDefault(e.ItemId)
                : e.ParentId is not null ? c.SeriesById.GetValueOrDefault(e.ParentId) : null;
            if (it is null || IsHidden(it) || !seen.Add(it.Key)) continue;
            list.Add(it);
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>Suggestions d'après l'historique du profil (ou d'après un seul titre si <paramref name="seed"/> est donné) :
    /// même catégorie, genres communs, note et nouveauté. Exclut ce qui a déjà été vu ou commencé.</summary>
    public List<CatalogItem> Recommend(int max = 24, CatalogItem? seed = null)
    {
        var c = Catalog;
        if (c is null) return new();
        var history = GetWatchedItems(40);
        var sources = seed is not null ? new List<CatalogItem> { seed } : history;
        if (sources.Count == 0) return new();

        var catW = new Dictionary<string, double>();
        var genreW = new Dictionary<string, double>();
        double w = 1.0;
        foreach (var it in sources)
        {
            var ck = $"{it.Kind}:{it.CategoryId}";
            catW[ck] = catW.GetValueOrDefault(ck) + w;
            foreach (var g in Genres(it)) genreW[g] = genreW.GetValueOrDefault(g) + w;
            w *= 0.85;
        }

        var excluded = history.Select(h => h.Key).ToHashSet();
        if (seed is not null) excluded.Add(seed.Key);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IEnumerable<CatalogItem> pool = seed switch
        {
            Movie => c.Movies,
            Series => c.Series,
            _ => c.Movies.Cast<CatalogItem>().Concat(c.Series)
        };

        return pool
            .Where(it => !excluded.Contains(it.Key) && !IsHidden(it) && !it.IsWatched && !it.IsInProgress)
            .Select(it =>
            {
                double score = catW.GetValueOrDefault($"{it.Kind}:{it.CategoryId}") * 3;
                foreach (var g in Genres(it)) score += genreW.GetValueOrDefault(g) * 2;
                if (score <= 0) return (it, score: 0.0);
                var rating = it switch { Movie m => m.Rating, Series s => s.Rating, _ => null } ?? 6;
                score += rating / 10.0;
                if (it.Added > 0 && now - it.Added < 60L * 86400) score += 0.4;
                if (!string.IsNullOrEmpty(it.ImageUrl)) score += 0.2;
                return (it, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(max)
            .Select(x => x.it)
            .ToList();
    }
}
