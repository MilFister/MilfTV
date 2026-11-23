using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MilfTV.Data;
using MilfTV.Epg;
using MilfTV.Localization;
using MilfTV.Providers;
using MilfTV.Tmdb;

namespace MilfTV.Core;

public sealed class SearchResults
{
    public List<LiveChannel> Channels { get; init; } = new();
    public List<Movie> Movies { get; init; } = new();
    public List<Series> Series { get; init; } = new();
    public List<ProgramHit> Programs { get; init; } = new();
    public bool IsEmpty => Channels.Count == 0 && Movies.Count == 0 && Series.Count == 0 && Programs.Count == 0;
    public int Total => Channels.Count + Movies.Count + Series.Count + Programs.Count;
}

public sealed class ProgramHit
{
    public required EpgProgram Program { get; init; }
    public required LiveChannel Channel { get; init; }
    public bool IsLive => Program.IsLiveAt(DateTimeOffset.Now);
    public string When => IsLive ? Loc.T("Epg.OnNow") : $"{Program.DayLabel} · {Program.TimeRange}";
}

/// <summary>État central : source active, catalogue, favoris, guide TV, contrôle parental, recherche.</summary>
public sealed partial class Library : ObservableObject
{
    public static Library Instance { get; } = new();

    public EpgStore Epg { get; } = new();
    public TmdbClient Tmdb { get; } = new(() => TmdbKeyCache);

    private static string? _tmdbKey;
    private static string? _tmdbKeySource;

    /// <summary>Clé TMDB déchiffrée une seule fois (DPAPI est coûteux).</summary>
    public static string? TmdbKeyCache
    {
        get
        {
            var p = SettingsStore.Current.TmdbKeyProtected;
            if (p != _tmdbKeySource) { _tmdbKeySource = p; _tmdbKey = SettingsStore.Current.TmdbKey; }
            return _tmdbKey;
        }
    }

    [ObservableProperty] private SourceConfig? _source;
    [ObservableProperty] private Catalog? _catalog;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isEpgLoading;
    [ObservableProperty] private string? _epgStatus;
    [ObservableProperty] private bool _parentalUnlocked;
    [ObservableProperty] private AccountInfo? _account;

    public IProvider? Provider { get; private set; }

    /// <summary>Déclenché (thread UI) quand un nouveau catalogue est disponible.</summary>
    public event Action? CatalogChanged;
    /// <summary>Déclenché (thread UI) quand le guide TV a été mis à jour.</summary>
    public event Action? EpgChanged;
    public event Action? FavoritesChanged;
    public event Action? ParentalChanged;

    private CancellationTokenSource? _cts;
    private Dictionary<string, WatchEntry> _watch = new();
    /// <summary>Protège l'historique et Ma liste en mémoire : la page d'accueil les lit en arrière-plan
    /// pendant que la lecture enregistre la position (sinon « collection modifiée pendant l'énumération »).</summary>
    private readonly object _watchGate = new();

    private AppSettings S => SettingsStore.Current;

    // ───────────── Activation / chargement ─────────────

    public async Task ActivateAsync(SourceConfig? src, bool forceRefresh = false)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        Provider?.Dispose();
        Provider = null;
        Error = null;
        Account = null;

        if (src is null)
        {
            Source = null;
            Catalog = null;
            CatalogChanged?.Invoke();
            return;
        }

        if (Source?.Id != src.Id) Catalog = null;
        Source = src;
        S.ActiveSourceId = src.Id;
        if (Profile is { } activeProfile) activeProfile.ActiveSourceId = src.Id;
        SettingsStore.Save();
        Provider = ProviderFactory.Create(src);
        IsLoading = true;
        Status = Loc.T("Library.LoadingCatalog");

        try
        {
            Catalog? cached = null;
            if (!forceRefresh)
            {
                cached = Catalog?.Fetched is not null && Source?.Id == src.Id ? Catalog : await CatalogCache.LoadAsync(src.Id);
                if (ct.IsCancellationRequested) return;
                if (cached is not null && !ReferenceEquals(cached, Catalog)) await SetCatalogAsync(cached, ct);
            }

            var stale = cached is null || forceRefresh || (DateTimeOffset.Now - cached.Fetched).TotalHours >= Math.Max(1, S.CatalogRefreshHours);
            if (stale)
            {
                try
                {
                    var progress = new Progress<string>(t => Status = t);
                    var fresh = await Provider.LoadCatalogAsync(progress, ct);
                    if (ct.IsCancellationRequested) return;
                    await SetCatalogAsync(fresh, ct);
                    src.LastCatalogRefresh = DateTimeOffset.Now;
                    SettingsStore.Save();
                    _ = CatalogCache.SaveAsync(src.Id, fresh);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error("Chargement du catalogue", ex);
                    Error = cached is null
                        ? ex.Message
                        : Loc.T("Library.UpdateFailedCached", ex.Message);
                }
            }
            else if (src.Kind == SourceKind.Xtream)
            {
                // Vérifie le compte en arrière-plan (format des flux, expiration).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var acc = await Provider.TestAsync(ct);
                        Dispatcher.UIThread.Post(() => Account = acc);
                    }
                    catch { /* ignoré */ }
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }

        if (!ct.IsCancellationRequested && Catalog is not null)
            _ = RefreshEpgAsync(false);
    }

    private async Task SetCatalogAsync(Catalog c, CancellationToken ct)
    {
        var src = Source!;
        var favs = await Task.Run(() =>
        {
            c.BuildIndexes();
            try { Epg.ResolveKeys(src.Id, c.Channels); } catch (Exception ex) { Log.Warn("ResolveKeys : " + ex.Message); }
            LoadProfileState(src);
            return UserDataStore.GetFavorites(src.Id);
        }, ct);
        // Une autre source a été activée pendant la préparation : ce catalogue n'est plus le bon.
        if (ct.IsCancellationRequested || !ReferenceEquals(Source, src)) return;
        foreach (var ch in c.Channels) ch.IsFavorite = favs.Contains(ch.Key);
        ApplyProfileFlags(c);
        ApplyParental(c);
        Catalog = c;
        CatalogChanged?.Invoke();
    }

    public async Task RefreshEpgAsync(bool force)
    {
        var src = Source;
        var provider = Provider;
        var catalog = Catalog;
        if (src is null || provider is null || catalog is null || IsEpgLoading) return;
        var url = provider.GetXmltvUrl(catalog);
        if (string.IsNullOrWhiteSpace(url))
        {
            EpgChanged?.Invoke();
            return;
        }
        var last = Epg.LastImport(src.Id);
        if (!force && last is not null && (DateTimeOffset.Now - last.Value).TotalHours < Math.Max(1, S.EpgRefreshHours))
        {
            EpgChanged?.Invoke();
            return;
        }

        IsEpgLoading = true;
        EpgStatus = Loc.T("Epg.Downloading");
        try
        {
            var progress = new Progress<string>(t => EpgStatus = t);
            var n = await Epg.ImportXmltvAsync(src.Id, url, src.UserAgent, progress, CancellationToken.None);
            await Task.Run(() =>
            {
                Epg.ResolveKeys(src.Id, catalog.Channels);
                Epg.PurgeOld();
                Data.Db.Compact();
            });
            src.LastEpgRefresh = DateTimeOffset.Now;
            SettingsStore.Save();
            EpgStatus = Loc.T("Epg.ProgramCount", n);
            Log.Info($"Guide importé : {n} programmes");
        }
        catch (Exception ex)
        {
            Log.Error("Import du guide TV", ex);
            EpgStatus = Loc.T("Epg.Unavailable", ex.Message);
        }
        finally
        {
            IsEpgLoading = false;
        }
        if (ReferenceEquals(Catalog, catalog)) EpgChanged?.Invoke();
        // Source changée pendant l'import : son guide n'a pas pu être chargé, on le fait maintenant.
        else if (Source is not null && !ReferenceEquals(Source, src)) _ = RefreshEpgAsync(false);
    }

    // ───────────── Guide TV ─────────────

    private int _nowNextGeneration;
    private const int NowNextImmediate = 300;

    /// <summary>Met à jour « en ce moment / à suivre » pour les chaînes données.
    /// Les premières (celles qu'on voit) sont mises à jour tout de suite ; au-delà, la lecture du guide se fait
    /// en arrière-plan pour ne pas figer l'interface sur les gros abonnements (10 000 chaînes et plus).</summary>
    public void UpdateNowNext(IReadOnlyCollection<LiveChannel> channels)
    {
        var src = Source;
        if (src is null || channels.Count == 0) return;
        var generation = ++_nowNextGeneration;
        var now = DateTimeOffset.Now;
        var first = channels.Count <= NowNextImmediate ? channels : channels.Take(NowNextImmediate).ToList();
        ApplyNowNext(first, ReadNowNext(src, first, now), now);
        if (channels.Count <= NowNextImmediate) return;
        var rest = channels.Skip(NowNextImmediate).ToList();
        _ = Task.Run(() => ReadNowNext(src, rest, now)).ContinueWith(t =>
        {
            // Liste plus récente demandée entre-temps (frappe dans le filtre…) : résultat abandonné.
            if (t.IsCompletedSuccessfully && t.Result is { } map && generation == _nowNextGeneration && ReferenceEquals(Source, src))
                ApplyNowNext(rest, map, now);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private Dictionary<string, (EpgProgram? Now, EpgProgram? Next)>? ReadNowNext(SourceConfig src, IReadOnlyCollection<LiveChannel> channels, DateTimeOffset now)
    {
        var keys = channels.Select(c => c.EpgKey).Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        try { return Epg.GetNowNext(src.Id, keys!, now); }
        catch (Exception ex) { Log.Warn("GetNowNext : " + ex.Message); return null; }
    }

    private static void ApplyNowNext(IReadOnlyCollection<LiveChannel> channels, Dictionary<string, (EpgProgram? Now, EpgProgram? Next)>? map, DateTimeOffset now)
    {
        if (map is null) return;
        foreach (var ch in channels)
        {
            if (ch.EpgKey is not null && map.TryGetValue(ch.EpgKey, out var nn))
            {
                ch.NowTitle = nn.Now?.Title;
                ch.NowTime = nn.Now?.TimeRange;
                ch.NowProgress = nn.Now?.ProgressAt(now) ?? 0;
                ch.NextTitle = nn.Next is { } nx ? $"{nx.Start.ToLocalTime():HH:mm}  {nx.Title}" : null;
            }
            else
            {
                ch.NowTitle = null;
                ch.NowTime = null;
                ch.NowProgress = 0;
                ch.NextTitle = null;
            }
        }
    }

    /// <summary>Programmes d'une chaîne ; complète avec l'EPG du fournisseur si le XMLTV n'a rien.</summary>
    public async Task<List<EpgProgram>> GetChannelProgramsAsync(LiveChannel ch, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var src = Source;
        if (src is null) return new();
        var key = ch.EpgKey ?? "ch:" + ch.Id;
        var list = await Task.Run(() => Epg.GetPrograms(src.Id, key, from, to), ct);
        if (list.Count == 0 && Provider is { } p)
        {
            var remote = await p.GetChannelEpgAsync(ch, ct);
            if (remote.Count > 0)
            {
                ch.EpgKey ??= key;
                foreach (var r in remote) r.ChannelKey = ch.EpgKey;
                await Task.Run(() => Epg.Upsert(src.Id, ch.EpgKey, remote), ct);
                list = remote.Where(x => x.End > from && x.Start < to).OrderBy(x => x.Start).ToList();
            }
        }
        return list;
    }

    // ───────────── Favoris / historique ─────────────

    /// <summary>Chaîne : favori commun. Film / série : « Ma liste » du profil.</summary>
    public void ToggleFavorite(CatalogItem item)
    {
        if (Source is null) return;
        item.IsFavorite = !item.IsFavorite;
        if (item is LiveChannel)
            UserDataStore.SetFavorite(Source.Id, item.Key, item.IsFavorite);
        else
        {
            UserDataStore.SetWatchlist(Source.Id, ProfileId, item.Key, item.IsFavorite);
            lock (_watchGate)
            {
                _watchlist.Remove(item.Key);
                if (item.IsFavorite) _watchlist.Insert(0, item.Key);
            }
        }
        FavoritesChanged?.Invoke();
    }

    public WatchEntry? GetWatch(string key)
    {
        lock (_watchGate) return _watch.GetValueOrDefault(key);
    }

    /// <summary>Copie des entrées d'historique, sûre à parcourir depuis n'importe quel fil.</summary>
    private List<WatchEntry> WatchValues()
    {
        lock (_watchGate) return _watch.Values.ToList();
    }

    private List<string> WatchlistKeys()
    {
        lock (_watchGate) return _watchlist.ToList();
    }

    public void SaveWatch(WatchEntry e)
    {
        if (Source is null) return;
        // Le profil peut désactiver l'historique des films et séries (les chaînes restent communes).
        if (e.Kind != ContentKind.Live && !Prefs.TrackHistory) return;
        e.SourceId = Source.Id;
        e.ProfileId = e.Kind == ContentKind.Live ? "" : ProfileId;
        e.UpdatedAt = DateTimeOffset.Now;
        if (e.Kind != ContentKind.Live)
        {
            lock (_watchGate) _watch[e.Key] = e;
            if (Catalog is { } cat) UpdateItemFlags(cat, e);
        }
        try { UserDataStore.SaveWatch(e); }
        catch (Exception ex) { Log.Warn("Historique non enregistré : " + ex.Message); }
    }

    public void RemoveWatch(string key)
    {
        if (Source is null) return;
        lock (_watchGate) _watch.Remove(key);
        UserDataStore.RemoveWatch(Source.Id, ProfileId, key);
        if (Catalog is { } c) ApplyProfileFlags(c);
    }

    // ───────────── Contrôle parental ─────────────

    private string CatKey(Category c) => $"{Source?.Id}|{c.Kind}:{c.Id}";

    public bool IsCategoryLockedByRule(Category c)
    {
        var key = CatKey(c);
        // Profil enfant : catégories adultes et verrouillées toujours bloquées, même sans code.
        if (IsKidProfile && (c.IsAdult || S.LockedCategories.Contains(key))) return true;
        if (!S.HasPin) return false;
        if (S.LockedCategories.Contains(key)) return true;
        return S.AutoLockAdult && c.IsAdult && !S.UnlockedCategories.Contains(key);
    }

    public void SetCategoryLock(Category c, bool locked)
    {
        var key = CatKey(c);
        S.LockedCategories.Remove(key);
        S.UnlockedCategories.Remove(key);
        if (locked) S.LockedCategories.Add(key);
        else if (c.IsAdult) S.UnlockedCategories.Add(key);
        SettingsStore.Save();
        RefreshParental();
    }

    public void RefreshParental()
    {
        if (Catalog is { } c) ApplyParental(c);
        ParentalChanged?.Invoke();
    }

    partial void OnParentalUnlockedChanged(bool value) => RefreshParental();

    private void ApplyParental(Catalog c)
    {
        var locked = new HashSet<string>();
        foreach (var cat in c.LiveCategories.Concat(c.MovieCategories).Concat(c.SeriesCategories))
        {
            cat.IsLocked = IsCategoryLockedByRule(cat);
            if (cat.IsLocked) locked.Add($"{cat.Kind}:{cat.Id}");
        }
        foreach (var ch in c.Channels) ch.IsLocked = locked.Contains($"Live:{ch.CategoryId}");
        foreach (var m in c.Movies) m.IsLocked = locked.Contains($"Movie:{m.CategoryId}");
        foreach (var s in c.Series) s.IsLocked = locked.Contains($"Series:{s.CategoryId}");
    }

    /// <summary>Vrai si l'élément doit être masqué (catégorie verrouillée et code non saisi).</summary>
    public bool IsHidden(CatalogItem item) => item.IsLocked && (!ParentalUnlocked || IsKidProfile);
    public bool IsHidden(Category cat) => cat.IsLocked && (!ParentalUnlocked || IsKidProfile);

    public bool CheckPin(string pin) => S.HasPin && Secret.HashPin(pin) == S.ParentalPinHash;

    // ───────────── Recherche ─────────────

    public SearchResults Search(string query, int perKind = 40)
    {
        var c = Catalog;
        if (c is null || string.IsNullOrWhiteSpace(query)) return new SearchResults();
        var norm = TextNorm.Normalize(query);
        var tokens = TextNorm.Tokens(query);
        if (tokens.Length == 0) return new SearchResults();

        List<T> Find<T>(List<T> items) where T : CatalogItem
        {
            var hits = new List<(T Item, int Score)>();
            foreach (var it in items)
            {
                if (IsHidden(it)) continue;
                var sc = TextNorm.Score(it.SearchKey, tokens, norm);
                if (sc > 0) hits.Add((it, sc + (it.IsFavorite ? 15 : 0)));
            }
            return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Item.Name.Length).Take(perKind).Select(h => h.Item).ToList();
        }

        var result = new SearchResults
        {
            Channels = Find(c.Channels),
            Movies = Find(c.Movies),
            Series = Find(c.Series),
        };

        if (Source is { } src && norm.Length >= 3)
        {
            try
            {
                var byKey = c.Channels.Where(ch => ch.EpgKey is not null && !IsHidden(ch))
                    .GroupBy(ch => ch.EpgKey!).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IsFavorite).First());
                foreach (var p in Epg.Search(src.Id, query, DateTimeOffset.Now, 80))
                {
                    if (!byKey.TryGetValue(p.ChannelKey, out var ch)) continue;
                    result.Programs.Add(new ProgramHit { Program = p, Channel = ch });
                    if (result.Programs.Count >= perKind) break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Recherche dans le guide : " + ex.Message);
            }
        }
        return result;
    }

    // ───────────── Lecture ─────────────

    public Task<PlaybackInfo> ResolveAsync(CatalogItem item, Episode? episode, CancellationToken ct = default) =>
        Provider?.ResolveAsync(item, episode, ct) ?? throw new ProviderException(Loc.T("Err.NoActiveSource"));

    /// <summary>Supprime toutes les données locales d'une source.</summary>
    public void ForgetSource(SourceConfig src)
    {
        // En arrière-plan : effacer un gros guide TV peut prendre plusieurs secondes.
        var id = src.Id;
        _ = Task.Run(() =>
        {
            try { CatalogCache.Delete(id); } catch (Exception ex) { Log.Warn("Suppression du cache : " + ex.Message); }
            try { Epg.DeleteSource(id); } catch (Exception ex) { Log.Warn("Suppression du guide : " + ex.Message); }
            try { UserDataStore.DeleteSource(id); } catch (Exception ex) { Log.Warn("Suppression des données : " + ex.Message); }
        });
    }
}
