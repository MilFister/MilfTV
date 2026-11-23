using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.Providers;

/// <summary>
/// Portail Stalker / Ministra (« portail MAC ») : protocole JsHttpRequest utilisé par les boîtiers MAG.
/// Détection du point d'entrée (portal.php, server/load.php, stalker_portal/server/load.php),
/// handshake + get_profile, puis appels authentifiés par jeton (renouvelé automatiquement).
/// </summary>
public sealed class StalkerProvider : IProvider
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (QtEmbedded; U; Linux; C) AppleWebKit/533.3 (KHTML, like Gecko) MAG200 stbapp ver: 2 rev: 250 Safari/533.3";

    private const string XUserAgent = "Model: MAG250; Link: WiFi";

    private const string StbVersion =
        "ImageDescription: 0.2.18-r23-250; ImageDate: Thu Sep 13 11:31:16 EEST 2018; PORTAL version: 5.6.2; " +
        "API Version: JS API version: 343; STB API version: 146; Player Engine version: 0x58c";

    /// <summary>Préfixe des séries issues du module VOD (évite les collisions avec le module « series »).</summary>
    private const string VodPrefix = "vod:";
    private const string OtherCategoryId = "__autres";
    private const int MaxParallel = 4;
    private const int MaxPagesPerList = 5000;
    private const int MaxPagesUnknownTotal = 300;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MinReauthInterval = TimeSpan.FromSeconds(5);
    /// <summary>Culture française : seulement pour reconnaître les dates renvoyées par certains portails.</summary>
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string TimeZoneIana = GetIanaTimeZone();
    private static readonly char[] JsonStarts = ['{', '['];
    private static readonly char[] JsonEnds = ['}', ']'];
    private static readonly char[] ListSeparators = [',', ';', '/', '|'];
    private static readonly string[] RatingKeys = ["rating_imdb", "rating_kinopoisk", "rating"];

    private static readonly JsonDocumentOptions JsonOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd",
        "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "dd-MM-yyyy",
        "MMMM d, yyyy, h:mm tt", "MMMM d, yyyy, h:mm:ss tt", "MMMM d, yyyy h:mm tt", "MMMM d, yyyy", "MMM d, yyyy", "d MMMM yyyy",
    ];

    private static readonly Regex YearRegex = new(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IntRegex = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberRegex = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EmptyStreamRegex = new(@"([?&]stream=)(?=&|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StreamParamRegex = new(@"[?&]stream=([^&]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PathIdRegex = new(@"/(\d+)(?:_[^/]*)?(?:\.[A-Za-z0-9]+)?/?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Base64Regex = new(@"^[A-Za-z0-9+/_-]{8,}={0,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly ConcurrentDictionary<string, VodExtra> _extras = new();
    private Session? _session;
    private Portal? _lastPortal;

    public StalkerProvider(SourceConfig source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public SourceConfig Source { get; }

    private string CurrentUserAgent => NullIfBlank(Source.UserAgent)?.Trim() ?? DefaultUserAgent;

    // ───────────────────────────── IProvider ─────────────────────────────

    public async Task<AccountInfo> TestAsync(CancellationToken ct)
    {
        try
        {
            Session session;
            await _authLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                session = await AuthenticateAsync(null, ct).ConfigureAwait(false);
                _lastPortal = session.Portal;
                Volatile.Write(ref _session, session);
            }
            finally
            {
                _authLock.Release();
            }

            JsonElement main = default;
            try
            {
                main = await CallAsync("type=account_info&action=get_main_info", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Stalker « {Source.Name} » : get_main_info indisponible ({ex.Message})");
            }

            var profile = session.Profile;
            var expires = FirstDate(main, "end_date", "phone", "expire_billing_date")
                          ?? FirstDate(profile, "expire_billing_date", "end_date");
            bool unlimited = LooksUnlimited(main, "end_date", "phone", "expire_billing_date");

            var info = new AccountInfo
            {
                Ok = true,
                ExpiresAt = expires,
                Status = expires is { } e && e < DateTimeOffset.Now ? Loc.T("Account.Expired") : Loc.T("Account.Active"),
            };
            var parts = new List<string> { Loc.T("Stalker.ConnectedTo", session.Portal.Endpoint) };
            if (NullIfBlank(main.Str("tariff_plan")) is { } plan) parts.Add(Loc.T("Stalker.Plan", plan.Trim()));
            if (expires == null && unlimited) parts.Add(Loc.T("Stalker.NoExpiry"));
            info.Message = string.Join(" · ", parts);
            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : test de connexion échoué : {ex.Message}");
            return new AccountInfo { Ok = false, Message = Describe(ex) };
        }
    }

    public async Task<Catalog> LoadCatalogAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report(Loc.T("Stalker.Connecting"));
            var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
            _extras.Clear();
            var catalog = new Catalog();

            await LoadLiveAsync(session.Portal, catalog, progress, ct).ConfigureAwait(false);
            await LoadVodAsync(session.Portal, catalog, progress, ct).ConfigureAwait(false);
            await LoadSeriesModuleAsync(session.Portal, catalog, progress, ct).ConfigureAwait(false);

            catalog.XmltvUrl = null;
            catalog.Fetched = DateTimeOffset.Now;
            Log.Info($"Stalker « {Source.Name} » : {catalog.Channels.Count} chaînes, {catalog.Movies.Count} films, {catalog.Series.Count} séries");
            return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProviderException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : chargement du catalogue échoué : {ex}");
            throw new ProviderException(Describe(ex), ex);
        }
    }

    public Task<MediaDetails?> GetMovieDetailsAsync(Movie movie, CancellationToken ct)
    {
        var d = new MediaDetails
        {
            Title = movie.Name,
            Plot = movie.Plot,
            PosterUrl = movie.ImageUrl,
            Year = movie.Year,
            Rating = movie.Rating,
            TmdbId = movie.TmdbId,
            Genres = SplitList(movie.Genre),
        };
        if (_extras.TryGetValue("m:" + movie.Id, out var x)) ApplyExtra(d, x);

        bool empty = d.Plot == null && d.PosterUrl == null && d.Year == null && d.Rating == null && d.Genres.Count == 0
                     && d.Director == null && d.Cast.Count == 0 && d.RuntimeMinutes == null && d.OriginalTitle == null;
        return Task.FromResult<MediaDetails?>(empty ? null : d);
    }

    public async Task<SeriesDetails?> GetSeriesDetailsAsync(Series series, CancellationToken ct)
    {
        var details = new SeriesDetails { Info = BuildSeriesInfo(series) };
        if (series.Episodes is { Count: > 0 } known)
        {
            details.Seasons = known
                .GroupBy(e => e.Season > 0 ? e.Season : 1)
                .OrderBy(g => g.Key)
                .Select(g => new SeasonInfo { Number = g.Key, Name = Loc.T("Common.SeasonN", g.Key), Episodes = g.OrderBy(e => e.Number).ToList() })
                .ToList();
            return details;
        }

        try
        {
            bool fromVod = series.Id.StartsWith(VodPrefix, StringComparison.Ordinal);
            var module = fromVod ? "vod" : "series";
            var rawId = fromVod ? series.Id[VodPrefix.Length..] : series.Id;
            var seasons = await LoadSeasonsAsync(module, rawId, series.Id, ct).ConfigureAwait(false);
            if (seasons.Count == 0) return null;
            details.Seasons = seasons;
            return details;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : épisodes de « {series.Name} » indisponibles : {ex.Message}");
            return null;
        }
    }

    public async Task<PlaybackInfo> ResolveAsync(CatalogItem item, Episode? episode, CancellationToken ct)
    {
        string? cmd, direct, fallbackId = null;
        bool live = false, trySeriesModule = false;
        string seriesParam = "";

        if (episode != null)
        {
            cmd = episode.StalkerCmd;
            direct = episode.StreamUrl;
            if (episode.Number > 0) seriesParam = episode.Number.ToString(CultureInfo.InvariantCulture);
            trySeriesModule = item is Series s && !s.Id.StartsWith(VodPrefix, StringComparison.Ordinal);
        }
        else
        {
            switch (item)
            {
                case LiveChannel ch:
                    cmd = ch.StalkerCmd; direct = ch.StreamUrl; fallbackId = ch.Id; live = true;
                    break;
                case Movie m:
                    cmd = m.StalkerCmd; direct = m.StreamUrl; fallbackId = m.Id;
                    break;
                default:
                    throw new ProviderException(Loc.T("Stalker.ChooseEpisode"));
            }
        }

        if (string.IsNullOrWhiteSpace(cmd))
        {
            if (!string.IsNullOrWhiteSpace(direct)) return new PlaybackInfo(direct, CurrentUserAgent);
            throw new ProviderException(Loc.T("Stalker.NoLink"));
        }

        var enc = E(cmd);
        var query = live
            ? $"type=itv&action=create_link&cmd={enc}&series=&forced_storage=undefined&disable_ad=0&download=0"
            : $"type=vod&action=create_link&cmd={enc}&series={seriesParam}&forced_storage=&disable_ad=0&download=0";
        var original = StripCmd(cmd);

        Session session;
        JsonElement js;
        try
        {
            session = await EnsureSessionAsync(ct).ConfigureAwait(false);
            js = await CallAsync(query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : create_link échoué ({ex.Message})");
            if (IsPlayableUrl(original)) return new PlaybackInfo(original, CurrentUserAgent);
            throw new ProviderException(Loc.T("Stalker.NoLinkDetail", Describe(ex)), ex);
        }

        var url = ExtractLink(js, original, fallbackId, session.Portal, out var error);

        if (url == null && trySeriesModule)
        {
            try
            {
                var js2 = await CallAsync("type=series&" + query["type=vod&".Length..], ct).ConfigureAwait(false);
                url = ExtractLink(js2, original, null, session.Portal, out var error2);
                error ??= error2;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Stalker « {Source.Name} » : create_link (series) échoué ({ex.Message})");
            }
        }

        if (url == null)
        {
            Log.Warn($"Stalker « {Source.Name} » : aucun lien renvoyé (erreur : {error ?? "aucune"})");
            throw new ProviderException(error?.ToLowerInvariant() switch
            {
                "limit" => Loc.T("Stalker.MaxConnections"),
                "nothing_to_play" or "not_available" => Loc.T("Stalker.NotAvailable"),
                _ => Loc.T("Stalker.NoLink"),
            });
        }
        return new PlaybackInfo(url, session.Identity.UserAgent);
    }

    public async Task<IReadOnlyList<EpgProgram>> GetChannelEpgAsync(LiveChannel channel, CancellationToken ct)
    {
        var list = new List<EpgProgram>();
        if (string.IsNullOrWhiteSpace(channel.Id)) return list;
        try
        {
            var js = await CallAsync($"type=itv&action=get_short_epg&ch_id={E(channel.Id)}&size=10", ct).ConfigureAwait(false);
            var key = NullIfBlank(channel.EpgKey) ?? NullIfBlank(channel.EpgId) ?? "ch:" + channel.Id;
            foreach (var e in DataItems(js))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var start = Unix(e.Long("start_timestamp")) ?? ParseDate(e.Str("time"));
                if (start == null) continue;
                var end = Unix(e.Long("stop_timestamp")) ?? ParseDate(e.Str("time_to"));
                if (end == null || end <= start)
                {
                    var dur = e.Int("duration") ?? 0;
                    end = start.Value.AddSeconds(dur > 0 ? dur : 1800);
                }
                list.Add(new EpgProgram
                {
                    ChannelKey = key,
                    Start = start.Value,
                    End = end.Value,
                    Title = NullIfBlank(e.Str("name"))?.Trim() ?? Loc.T("Epg.UntitledProgram"),
                    Description = NullIfBlank(e.Str("descr"))?.Trim(),
                    Category = NullIfBlank(e.Str("category"))?.Trim(),
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : EPG de « {channel.Name} » indisponible ({ex.Message})");
            return Array.Empty<EpgProgram>();
        }
        return list.OrderBy(p => p.Start).ToList();
    }

    public string? GetXmltvUrl(Catalog catalog) => NullIfBlank(Source.EpgUrl)?.Trim();

    public void Dispose()
    {
        // Rien de lourd : Http.Iptv est partagé et ne doit pas être libéré ici.
        Volatile.Write(ref _session, null);
    }

    // ───────────────────────────── Catalogue ─────────────────────────────

    private async Task LoadLiveAsync(Portal portal, Catalog catalog, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("Stalker.ChannelsCategories"));
        catalog.LiveCategories = await GetCategoriesAsync("itv", "get_genres", ContentKind.Live, null, ct).ConfigureAwait(false);

        progress?.Report(Loc.T("Stalker.ChannelsLoading"));
        var channels = new List<LiveChannel>();
        try
        {
            var js = await CallAsync("type=itv&action=get_all_channels", ct, LongRequestTimeout).ConfigureAwait(false);
            foreach (var e in DataItems(js))
                if (MapChannel(e, portal) is { } ch) channels.Add(ch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : get_all_channels a échoué ({ex.Message}), lecture page par page");
        }

        if (channels.Count == 0)
        {
            channels = await FetchAllPagesAsync<LiveChannel>(
                [("*", "type=itv&action=get_ordered_list&genre=*&force_ch_link_check=&fav=0&sortby=number&hd=0")],
                (e, _) => MapChannel(e, portal), Loc.T("Common.Channels"), progress, ct).ConfigureAwait(false);
        }

        var seen = new HashSet<string>();
        foreach (var ch in channels)
            if (seen.Add(ch.Id)) catalog.Channels.Add(ch);

        // Chaînes dont le genre est inconnu : regroupées dans une catégorie de secours.
        var catIds = new HashSet<string>(catalog.LiveCategories.Select(c => c.Id));
        bool orphans = false;
        foreach (var ch in catalog.Channels)
        {
            if (catIds.Contains(ch.CategoryId)) continue;
            ch.CategoryId = OtherCategoryId;
            orphans = true;
        }
        if (orphans)
        {
            catalog.LiveCategories.Add(new Category
            {
                Id = OtherCategoryId,
                Name = catalog.LiveCategories.Count == 0 ? Loc.T("Stalker.AllChannels") : Loc.T("Stalker.OtherChannels"),
                Kind = ContentKind.Live,
            });
        }
        progress?.Report(Loc.T("Stalker.ChannelsCount", catalog.Channels.Count));
    }

    private async Task LoadVodAsync(Portal portal, Catalog catalog, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("Stalker.MoviesCategories"));
        List<Category> cats;
        try
        {
            cats = await GetCategoriesAsync("vod", "get_categories", ContentKind.Movie, Loc.T("Stalker.AllMovies"), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!IsNetworkFailure(ex))
        {
            Log.Warn($"Stalker « {Source.Name} » : module VOD indisponible ({ex.Message})");
            return;
        }
        if (cats.Count == 0) return;

        var jobs = cats.Select(c => (c.Id, $"type=vod&action=get_ordered_list&category={E(c.Id)}&genre=*&sortby=added")).ToList();
        var items = await FetchAllPagesAsync<CatalogItem>(jobs, (e, cid) => MapVodItem(e, cid, portal), Loc.T("Common.Movies"), progress, ct)
            .ConfigureAwait(false);

        var movieIds = new HashSet<string>();
        var seriesIds = new HashSet<string>(catalog.Series.Select(s => s.Id));
        var movieCats = new HashSet<string>();
        var seriesCats = new HashSet<string>();
        foreach (var it in items)
        {
            if (it is Movie m)
            {
                if (!movieIds.Add(m.Id)) continue;
                catalog.Movies.Add(m);
                movieCats.Add(m.CategoryId);
            }
            else if (it is Series s)
            {
                if (!seriesIds.Add(s.Id)) continue;
                catalog.Series.Add(s);
                seriesCats.Add(s.CategoryId);
            }
        }

        catalog.MovieCategories.AddRange(cats.Where(c => movieCats.Contains(c.Id)));
        foreach (var c in cats)
        {
            if (seriesCats.Contains(VodPrefix + c.Id))
                catalog.SeriesCategories.Add(new Category { Id = VodPrefix + c.Id, Name = c.Name, Kind = ContentKind.Series });
        }
    }

    private async Task LoadSeriesModuleAsync(Portal portal, Catalog catalog, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report(Loc.T("Stalker.SeriesCategories"));
            var cats = await GetCategoriesAsync("series", "get_categories", ContentKind.Series, Loc.T("Stalker.AllSeries"), ct).ConfigureAwait(false);
            if (cats.Count == 0) return;

            var jobs = cats.Select(c => (c.Id, $"type=series&action=get_ordered_list&category={E(c.Id)}&genre=*&sortby=added")).ToList();
            var items = await FetchAllPagesAsync<Series>(jobs, (e, cid) => MapSeriesItem(e, cid, portal), Loc.T("Common.Series"), progress, ct)
                .ConfigureAwait(false);

            var ids = new HashSet<string>(catalog.Series.Select(s => s.Id));
            var used = new HashSet<string>();
            foreach (var s in items)
            {
                if (!ids.Add(s.Id)) continue;
                catalog.Series.Add(s);
                used.Add(s.CategoryId);
            }
            var existing = new HashSet<string>(catalog.SeriesCategories.Select(c => c.Id));
            foreach (var c in cats)
                if (used.Contains(c.Id) && existing.Add(c.Id)) catalog.SeriesCategories.Add(c);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Stalker « {Source.Name} » : module séries ignoré ({ex.Message})");
        }
    }

    private async Task<List<Category>> GetCategoriesAsync(string type, string action, ContentKind kind, string? allName, CancellationToken ct)
    {
        var js = await CallAsync($"type={type}&action={action}", ct).ConfigureAwait(false);
        var list = new List<Category>();
        var seen = new HashSet<string>();
        bool sawAll = false;
        foreach (var e in DataItems(js))
        {
            var id = NullIfBlank(e.Str("id"))?.Trim();
            if (id == null) continue;
            if (id == "*") { sawAll = true; continue; }
            if (!seen.Add(id)) continue;
            var name = NullIfBlank(e.Str("title", "name", "alias"))?.Trim();
            list.Add(new Category { Id = id, Name = name ?? Loc.T("Stalker.CategoryN", id), Kind = kind });
        }
        if (list.Count == 0 && sawAll && allName != null)
            list.Add(new Category { Id = "*", Name = allName, Kind = kind });
        return list;
    }

    private static LiveChannel? MapChannel(JsonElement e, Portal portal)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = NullIfBlank(e.Str("id"))?.Trim();
        if (id == null) return null;

        var cmd = NullIfBlank(e.Str("cmd"));
        if (cmd == null && e.Obj("cmds") is { } cmds)
        {
            foreach (var c in cmds.Items())
                if (NullIfBlank(c.Str("url")) is { } u) { cmd = u; break; }
        }

        var number = e.Int("number") ?? 0;
        var name = NullIfBlank(e.Str("name"))?.Trim() ?? Loc.T("Common.ChannelN", number > 0 ? number.ToString(CultureInfo.InvariantCulture) : id);

        int archiveDays = 0;
        if (e.Str("tv_archive") == "1" || e.Str("archive") == "1" || e.Str("enable_tv_archive") == "1")
        {
            var hours = e.Int("tv_archive_duration") ?? 0;
            if (hours > 0) archiveDays = (hours + 23) / 24;
        }

        return new LiveChannel
        {
            Id = id,
            Name = name,
            Number = number,
            CategoryId = NullIfBlank(e.Str("tv_genre_id"))?.Trim() ?? "",
            ImageUrl = AbsUrl(e.Str("logo"), portal, portal.LogoDir),
            StalkerCmd = cmd,
            EpgId = NullIfBlank(e.Str("xmltv_id"))?.Trim(),
            ArchiveDays = archiveDays,
        };
    }

    private CatalogItem? MapVodItem(JsonElement e, string catId, Portal portal)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var rawId = NullIfBlank(e.Str("id"))?.Trim();
        if (rawId == null) return null;

        var name = NullIfBlank(e.Str("name"))?.Trim() ?? NullIfBlank(e.Str("o_name"))?.Trim() ?? Loc.T("Stalker.Untitled", rawId);
        var numbers = SeriesNumbers(e);
        bool isSeries = e.Str("is_series") == "1" || numbers.Count > 0;
        var cmd = NullIfBlank(e.Str("cmd"));
        var extra = ReadExtra(e, name);
        int? year = ParseYear(e.Str("year")) ?? TextNorm.SplitYear(name).Year;
        var rating = ReadRating(e);
        var plot = NullIfBlank(e.Str("description"))?.Trim();
        var genre = NullIfBlank(e.Str("genres_str"))?.Trim();
        var image = AbsUrl(e.Str("screenshot_uri"), portal, null);
        long added = ParseDate(e.Str("added"))?.ToUnixTimeSeconds() ?? 0;

        if (isSeries)
        {
            var id = VodPrefix + rawId;
            List<Episode>? episodes = null;
            if (numbers.Count > 0 && cmd != null)
            {
                episodes = numbers.Select(n => new Episode
                {
                    Id = $"{id}:{n}",
                    SeriesId = id,
                    Season = 1,
                    Number = n,
                    Title = Loc.T("Common.EpisodeN", n),
                    StalkerCmd = cmd,
                }).ToList();
            }
            if (extra != null) _extras["s:" + id] = extra;
            return new Series
            {
                Id = id,
                Name = name,
                CategoryId = VodPrefix + catId,
                ImageUrl = image,
                Added = added,
                Year = year,
                Rating = rating,
                Plot = plot,
                Genre = genre,
                Cast = extra?.Actors,
                Episodes = episodes,
            };
        }

        if (extra != null) _extras["m:" + rawId] = extra;
        return new Movie
        {
            Id = rawId,
            Name = name,
            CategoryId = catId,
            ImageUrl = image,
            Added = added,
            Year = year,
            Rating = rating,
            Plot = plot,
            Genre = genre,
            StalkerCmd = cmd,
        };
    }

    private Series? MapSeriesItem(JsonElement e, string catId, Portal portal)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = NullIfBlank(e.Str("id"))?.Trim();
        if (id == null) return null;

        var name = NullIfBlank(e.Str("name"))?.Trim() ?? NullIfBlank(e.Str("o_name"))?.Trim() ?? Loc.T("Stalker.Untitled", id);
        var extra = ReadExtra(e, name);
        if (extra != null) _extras["s:" + id] = extra;
        return new Series
        {
            Id = id,
            Name = name,
            CategoryId = catId,
            ImageUrl = AbsUrl(e.Str("screenshot_uri"), portal, null),
            Added = ParseDate(e.Str("added"))?.ToUnixTimeSeconds() ?? 0,
            Year = ParseYear(e.Str("year")) ?? TextNorm.SplitYear(name).Year,
            Rating = ReadRating(e),
            Plot = NullIfBlank(e.Str("description"))?.Trim(),
            Genre = NullIfBlank(e.Str("genres_str"))?.Trim(),
            Cast = extra?.Actors,
            Episodes = null,
        };
    }

    /// <summary>Saisons d'une série via <c>get_ordered_list&amp;movie_id=…</c> (module « series » ou « vod »).</summary>
    private async Task<List<SeasonInfo>> LoadSeasonsAsync(string module, string rawId, string seriesId, CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
        var portal = session.Portal;
        var items = await FetchSequentialAsync(
            $"type={module}&action=get_ordered_list&movie_id={E(rawId)}&season_id=0&episode_id=0", 20, ct).ConfigureAwait(false);

        var seasons = new SortedDictionary<int, SeasonInfo>();
        SeasonInfo SeasonFor(int n)
        {
            if (!seasons.TryGetValue(n, out var s))
                seasons[n] = s = new SeasonInfo { Number = n, Name = Loc.T("Common.SeasonN", n) };
            return s;
        }

        int index = 0, perSeasonFetches = 0;
        foreach (var it in items)
        {
            index++;
            if (it.ValueKind != JsonValueKind.Object) continue;
            var itemId = NullIfBlank(it.Str("id"))?.Trim();
            var cmd = NullIfBlank(it.Str("cmd"));
            var numbers = SeriesNumbers(it);
            bool isEpisode = it.Str("is_episode") == "1";
            bool isSeason = !isEpisode && (numbers.Count > 0 || it.Str("is_season") == "1");

            if (isSeason)
            {
                int sn = PositiveOrNull(it.Int("season_number")) ?? NumberAfterColon(itemId) ?? FirstNumber(it.Str("name")) ?? index;
                var season = SeasonFor(sn);
                var seasonKey = itemId ?? $"{seriesId}:{sn}";
                if (numbers.Count > 0 && cmd != null)
                {
                    var image = AbsUrl(it.Str("screenshot_uri"), portal, null);
                    foreach (var n in numbers)
                    {
                        season.Episodes.Add(new Episode
                        {
                            Id = $"{seasonKey}:{n}",
                            SeriesId = seriesId,
                            Season = sn,
                            Number = n,
                            Title = Loc.T("Common.EpisodeN", n),
                            StalkerCmd = cmd,
                            ImageUrl = image,
                        });
                    }
                }
                else if (itemId != null && perSeasonFetches++ < 50)
                {
                    // Variante : les épisodes de la saison sont listés séparément, chacun avec sa propre commande.
                    var eps = await FetchSequentialAsync(
                        $"type={module}&action=get_ordered_list&movie_id={E(rawId)}&season_id={E(itemId)}&episode_id=0", 20, ct)
                        .ConfigureAwait(false);
                    int ei = 0;
                    foreach (var ep in eps)
                    {
                        ei++;
                        if (MapEpisode(ep, seriesId, sn, ei, cmd, portal) is { } episode) season.Episodes.Add(episode);
                    }
                }
            }
            else if (cmd != null)
            {
                int sn = PositiveOrNull(it.Int("season_number")) ?? 1;
                if (MapEpisode(it, seriesId, sn, index, null, portal) is { } episode) SeasonFor(sn).Episodes.Add(episode);
            }
        }

        var list = new List<SeasonInfo>();
        foreach (var s in seasons.Values)
        {
            if (s.Episodes.Count == 0) continue;
            s.Episodes = s.Episodes.GroupBy(e => e.Id).Select(g => g.First()).OrderBy(e => e.Number).ToList();
            list.Add(s);
        }
        return list;
    }

    private static Episode? MapEpisode(JsonElement ep, string seriesId, int season, int index, string? fallbackCmd, Portal portal)
    {
        if (ep.ValueKind != JsonValueKind.Object) return null;
        var cmd = NullIfBlank(ep.Str("cmd")) ?? fallbackCmd;
        if (cmd == null) return null;
        var name = NullIfBlank(ep.Str("name"))?.Trim();
        int n = PositiveOrNull(ep.Int("series_number")) ?? PositiveOrNull(ep.Int("episode_number")) ?? FirstNumber(name) ?? index;
        var rawId = NullIfBlank(ep.Str("id"))?.Trim();
        var minutes = ParseMinutes(ep.Str("time"));
        return new Episode
        {
            Id = rawId != null ? $"{seriesId}:{rawId}" : $"{seriesId}:{season}:{n}",
            SeriesId = seriesId,
            Season = season,
            Number = n,
            Title = name ?? Loc.T("Common.EpisodeN", n),
            Plot = NullIfBlank(ep.Str("description"))?.Trim(),
            ImageUrl = AbsUrl(ep.Str("screenshot_uri"), portal, null),
            DurationSecs = minutes * 60,
            StalkerCmd = cmd,
        };
    }

    /// <summary>
    /// Récupère toutes les pages de plusieurs listes (une par catégorie) avec un parallélisme limité.
    /// Les pages en échec sont ignorées (avec un avertissement), sauf si l'échec devient majoritaire.
    /// </summary>
    private async Task<List<T>> FetchAllPagesAsync<T>(
        IReadOnlyList<(string CatId, string Query)> jobs,
        Func<JsonElement, string, T?> map,
        string label,
        IProgress<string>? progress,
        CancellationToken ct) where T : class
    {
        using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);
        var stats = new PageStats();

        void Report()
        {
            if (progress == null) return;
            var done = Interlocked.Read(ref stats.Done);
            var total = Math.Max(Interlocked.Read(ref stats.Total), done);
            progress.Report(Loc.T("Stalker.PageProgress", label, done, total));
        }

        async Task<PageResult<T>?> PageAsync(string catId, string query, int page, EmptyMark? mark)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (mark != null && page > Volatile.Read(ref mark.From)) return null;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        var js = await CallAsync($"{query}&p={page}", ct).ConfigureAwait(false);
                        var raw = DataItems(js).ToList();
                        var items = new List<T>(raw.Count);
                        foreach (var e in raw)
                        {
                            try
                            {
                                if (map(e, catId) is { } m) items.Add(m);
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"Stalker : élément ignoré ({ex.Message})");
                            }
                        }
                        Interlocked.Increment(ref stats.Ok);
                        var firstId = raw.Count > 0 ? raw[0].Str("id") : null;
                        return new PageResult<T>(items, raw.Count, js.Int("total_items"), js.Int("max_page_items"), firstId);
                    }
                    catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested
                                               && ex is not ProviderException and not StalkerAuthException)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref stats.Failed);
                stats.LastError = ex;
                Log.Warn($"Stalker « {Source.Name} » : page {page} ignorée ({query}) : {ex.Message}");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        async Task<PageResult<T>?> NextPageAsync((string CatId, string Query) job, int page, EmptyMark mark)
        {
            var r = await PageAsync(job.CatId, job.Query, page, mark).ConfigureAwait(false);
            if (r == null) return null;
            if (r.Raw == 0)
            {
                // Page vide : inutile de demander les suivantes pour cette liste.
                int cur;
                while (page < (cur = Volatile.Read(ref mark.From))
                       && Interlocked.CompareExchange(ref mark.From, page, cur) != cur) { }
            }
            else
            {
                Interlocked.Add(ref stats.Done, r.Raw);
                Report();
            }
            return r;
        }

        async Task<List<T>> JobAsync((string CatId, string Query) job)
        {
            var first = await PageAsync(job.CatId, job.Query, 1, null).ConfigureAwait(false);
            if (first == null) return new List<T>();
            var result = new List<T>(first.Items);
            Interlocked.Add(ref stats.Total, Math.Max(first.Total ?? 0, first.Raw));
            Interlocked.Add(ref stats.Done, first.Raw);
            Report();

            int per = first.PerPage is > 0 ? first.PerPage.Value : first.Raw;
            if (first.Raw == 0 || per <= 0) return result;

            if (first.Total is int total)
            {
                long pagesL = (total + (long)per - 1) / per;
                if (pagesL > MaxPagesPerList)
                    Log.Warn($"Stalker « {Source.Name} » : liste tronquée à {MaxPagesPerList} pages ({job.Query})");
                int pages = (int)Math.Min(pagesL, MaxPagesPerList);
                if (pages <= 1) return result;

                var mark = new EmptyMark();
                var tasks = new List<Task<PageResult<T>?>>(pages - 1);
                for (int p = 2; p <= pages; p++) tasks.Add(NextPageAsync(job, p, mark));
                foreach (var r in await Task.WhenAll(tasks).ConfigureAwait(false))
                    if (r != null) result.AddRange(r.Items);
            }
            else
            {
                // Total inconnu : pages successives jusqu'à une page vide, courte ou répétée.
                var prevFirst = first.FirstId;
                for (int p = 2; p <= MaxPagesUnknownTotal; p++)
                {
                    var r = await PageAsync(job.CatId, job.Query, p, null).ConfigureAwait(false);
                    if (r == null || r.Raw == 0 || (r.FirstId != null && r.FirstId == prevFirst)) break;
                    prevFirst = r.FirstId;
                    result.AddRange(r.Items);
                    Interlocked.Add(ref stats.Total, r.Raw);
                    Interlocked.Add(ref stats.Done, r.Raw);
                    Report();
                    if (r.Raw < per) break;
                }
            }
            return result;
        }

        var all = await Task.WhenAll(jobs.Select(JobAsync)).ConfigureAwait(false);
        if (stats.Failed > 0 && stats.Failed * 3 > stats.Ok + stats.Failed)
        {
            var err = stats.LastError ?? new ProviderException(Loc.T("Stalker.Unreachable"));
            throw new ProviderException(Describe(err), err);
        }
        return all.SelectMany(x => x).ToList();
    }

    /// <summary>Liste paginée lue page par page (petites listes : saisons, épisodes).</summary>
    private async Task<List<JsonElement>> FetchSequentialAsync(string baseQuery, int maxPages, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        string? prevFirst = null;
        for (int p = 1; p <= maxPages; p++)
        {
            var js = await CallAsync($"{baseQuery}&p={p}", ct).ConfigureAwait(false);
            var items = DataItems(js).ToList();
            if (items.Count == 0) break;
            var firstId = items[0].Str("id");
            if (p > 1 && firstId != null && firstId == prevFirst) break;
            prevFirst = firstId;
            all.AddRange(items);
            var total = js.Int("total_items");
            var per = js.Int("max_page_items") ?? items.Count;
            if (total is null || per <= 0 || (long)p * per >= total) break;
        }
        return all;
    }

    // ───────────────────────────── Détails ─────────────────────────────

    private MediaDetails BuildSeriesInfo(Series s)
    {
        var d = new MediaDetails
        {
            Title = s.Name,
            Plot = s.Plot,
            PosterUrl = s.ImageUrl,
            BackdropUrl = s.Backdrop,
            Year = s.Year,
            Rating = s.Rating,
            TmdbId = s.TmdbId,
            Genres = SplitList(s.Genre),
        };
        if (_extras.TryGetValue("s:" + s.Id, out var x)) ApplyExtra(d, x);
        if (d.Cast.Count == 0)
            d.Cast = SplitList(s.Cast).Take(30).Select(n => new CastMember { Name = n }).ToList();
        return d;
    }

    private static void ApplyExtra(MediaDetails d, VodExtra x)
    {
        if (x.OriginalName != null && !string.Equals(x.OriginalName, d.Title, StringComparison.OrdinalIgnoreCase))
            d.OriginalTitle = x.OriginalName;
        d.Director = x.Director;
        d.RuntimeMinutes = x.Minutes;
        d.Certification = x.Age;
        var cast = SplitList(x.Actors);
        if (cast.Count > 0) d.Cast = cast.Take(30).Select(n => new CastMember { Name = n }).ToList();
    }

    private static VodExtra? ReadExtra(JsonElement e, string name)
    {
        var original = NullIfBlank(e.Str("o_name"))?.Trim();
        if (original != null && string.Equals(original, name, StringComparison.OrdinalIgnoreCase)) original = null;
        var director = NullIfBlank(e.Str("director"))?.Trim();
        var actors = NullIfBlank(e.Str("actors"))?.Trim();
        var minutes = ParseMinutes(e.Str("time"));
        var age = NullIfBlank(e.Str("age"))?.Trim();
        if (age is "0" or "0+") age = null;
        if (original == null && director == null && actors == null && minutes == null && age == null) return null;
        return new VodExtra(original, director, actors, minutes, age);
    }

    // ───────────────────────────── Session ─────────────────────────────

    private async Task<Session> EnsureSessionAsync(CancellationToken ct)
    {
        var s = Volatile.Read(ref _session);
        if (s != null) return s;
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            s = Volatile.Read(ref _session);
            if (s != null) return s;
            s = await AuthenticateAsync(_lastPortal, ct).ConfigureAwait(false);
            _lastPortal = s.Portal;
            Volatile.Write(ref _session, s);
            return s;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>Renouvelle le jeton (une seule fois pour toutes les requêtes concurrentes qui ont échoué).</summary>
    private async Task<Session> ReauthAsync(Session stale, CancellationToken ct)
    {
        await _authLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = Volatile.Read(ref _session);
            if (cur != null && !ReferenceEquals(cur, stale)) return cur;
            if (DateTime.UtcNow - stale.CreatedUtc < MinReauthInterval)
                throw new StalkerAuthException(); // jeton tout neuf déjà refusé : inutile d'insister
            Log.Info($"Stalker « {Source.Name} » : jeton refusé, nouvelle authentification");
            Volatile.Write(ref _session, null);
            var fresh = await AuthenticateAsync(stale.Portal, ct).ConfigureAwait(false);
            _lastPortal = fresh.Portal;
            Volatile.Write(ref _session, fresh);
            return fresh;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>Appel authentifié ; ré-authentifie une fois si le portail refuse le jeton.</summary>
    private async Task<JsonElement> CallAsync(string query, CancellationToken ct, TimeSpan? timeout = null)
    {
        var s = await EnsureSessionAsync(ct).ConfigureAwait(false);
        try
        {
            return Js(await SendAsync(s.Portal, s.Identity, s.Token, query, ct, timeout).ConfigureAwait(false));
        }
        catch (StalkerAuthException)
        {
            var fresh = await ReauthAsync(s, ct).ConfigureAwait(false);
            return Js(await SendAsync(fresh.Portal, fresh.Identity, fresh.Token, query, ct, timeout).ConfigureAwait(false));
        }
    }

    /// <summary>Détection du point d'entrée, handshake, get_profile (et do_auth si le portail l'exige).</summary>
    private async Task<Session> AuthenticateAsync(Portal? preferred, CancellationToken ct)
    {
        var id = CreateIdentity(Source)
                 ?? throw new ProviderException(Loc.T("Stalker.InvalidMac"));
        var candidates = BuildCandidates();
        if (preferred != null)
        {
            int idx = candidates.FindIndex(c => string.Equals(c.Endpoint, preferred.Endpoint, StringComparison.OrdinalIgnoreCase));
            if (idx > 0)
            {
                var p = candidates[idx];
                candidates.RemoveAt(idx);
                candidates.Insert(0, p);
            }
        }

        Portal? portal = null;
        string? token = null;
        string random = "";
        Exception? lastError = null, netError = null;
        bool refused = false;

        foreach (var c in candidates)
        {
            try
            {
                var root = await SendAsync(c, id, null, "type=stb&action=handshake&token=&prehash=0", ct).ConfigureAwait(false);
                var js = Js(root);
                var t = NullIfBlank(js.Str("token")) ?? NullIfBlank(root.Str("token"));
                if (t != null)
                {
                    portal = c;
                    token = t.Trim();
                    random = js.Str("random") ?? "";
                    break;
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("js", out _)) refused = true;
                Log.Warn($"Stalker « {Source.Name} » : handshake sans jeton sur {c.Endpoint}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (StalkerAuthException ex)
            {
                refused = true;
                lastError = ex;
                Log.Warn($"Stalker « {Source.Name} » : handshake refusé sur {c.Endpoint}");
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                netError = lastError = ex;
                Log.Warn($"Stalker « {Source.Name} » : {c.Endpoint} injoignable ({ex.Message})");
                break; // hôte injoignable : inutile d'essayer les autres chemins
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Info($"Stalker « {Source.Name} » : {c.Endpoint} ignoré ({ex.Message})");
            }
        }

        if (portal == null || token == null)
        {
            if (refused) throw new ProviderException(Loc.T("Stalker.MacRefused"), lastError);
            if (netError != null)
                throw new ProviderException(netError is TimeoutException ? Loc.T("Stalker.UnreachableTimeout") : Loc.T("Stalker.Unreachable"), netError);
            throw new ProviderException(Loc.T("Stalker.NoPortal"), lastError);
        }

        JsonElement profile;
        try
        {
            profile = await GetProfileAsync(portal, id, token, random, ct).ConfigureAwait(false);
            var status = profile.Int("status");
            if (status == 2)
            {
                var user = NullIfBlank(Source.Username)?.Trim();
                var pass = Source.Password;
                if (user == null || string.IsNullOrEmpty(pass))
                    throw new ProviderException(Loc.T("Stalker.LoginRequired"));
                var auth = Js(await SendAsync(portal, id, token,
                    $"type=stb&action=do_auth&login={E(user)}&password={E(pass)}&device_id={id.DeviceId}&device_id2={id.DeviceId}",
                    ct).ConfigureAwait(false));
                bool ok = auth.ValueKind == JsonValueKind.True
                          || (auth.ValueKind is JsonValueKind.String or JsonValueKind.Number && auth.ToString() is "1" or "true");
                if (!ok) throw new ProviderException(Loc.T("Stalker.LoginRefused"));
                profile = await GetProfileAsync(portal, id, token, random, ct).ConfigureAwait(false);
                status = profile.Int("status");
            }
            if (status is int st && st != 0)
            {
                var msg = CleanMessage(profile.Str("msg", "block_msg"));
                throw new ProviderException(msg == null ? Loc.T("Stalker.MacRefused") : Loc.T("Stalker.AccessDenied", msg));
            }
            if (profile.Str("blocked") == "1")
                throw new ProviderException(Loc.T("Stalker.Blocked"));
        }
        catch (StalkerAuthException ex)
        {
            throw new ProviderException(Loc.T("Stalker.MacRefused"), ex);
        }

        Log.Info($"Stalker « {Source.Name} » : connecté via {portal.Endpoint}");
        return new Session(portal, id, token, profile, DateTime.UtcNow);
    }

    private static async Task<JsonElement> GetProfileAsync(Portal portal, Identity id, string token, string random, CancellationToken ct)
    {
        var metrics = $"{{\"mac\":\"{id.Mac}\",\"sn\":\"{id.Sn}\",\"model\":\"MAG250\",\"type\":\"STB\",\"uid\":\"{id.DeviceId}\",\"random\":\"{JsonEncodedText.Encode(random)}\"}}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var query =
            "type=stb&action=get_profile&hd=1" +
            $"&ver={E(StbVersion)}&num_banks=2&sn={id.Sn}&stb_type=MAG250&client_type=STB&image_version=218&video_out=hdmi" +
            $"&device_id={id.DeviceId}&device_id2={id.DeviceId}&signature={id.Signature}&auth_second_step=1&hw_version=1.7-BD-00" +
            $"&not_valid_token=0&metrics={E(metrics)}&hw_version_2={id.HwVersion2}&timestamp={ts}&api_signature=262&prehash=";
        return Js(await SendAsync(portal, id, token, query, ct).ConfigureAwait(false));
    }

    /// <summary>Requête brute vers le portail (en-têtes MAG, cookie, jeton) ; renvoie la racine JSON.</summary>
    private static async Task<JsonElement> SendAsync(Portal portal, Identity id, string? token, string query, CancellationToken ct, TimeSpan? timeout = null)
    {
        var url = $"{portal.Endpoint}?{query}&JsHttpRequest=1-xml";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? RequestTimeout);
        using var req = Http.Request(url, id.UserAgent);
        req.Headers.TryAddWithoutValidation("X-User-Agent", XUserAgent);
        req.Headers.TryAddWithoutValidation("Referer", portal.Referer);
        req.Headers.TryAddWithoutValidation("Cookie", id.Cookie);
        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        string body;
        try
        {
            using var resp = await Http.Iptv.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new StalkerAuthException();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
            body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Stalker.Timeout"));
        }

        if (body.Length < 4096 && body.Contains("Authorization failed", StringComparison.OrdinalIgnoreCase))
            throw new StalkerAuthException();
        return ParseJson(body);
    }

    private static JsonElement ParseJson(string body)
    {
        int start = body.IndexOfAny(JsonStarts);
        int end = body.LastIndexOfAny(JsonEnds);
        if (start < 0 || end < start) throw new StalkerFormatException(Loc.T("Stalker.BadResponse"));
        try
        {
            using var doc = JsonDocument.Parse(body.AsMemory(start, end - start + 1), JsonOpts);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new StalkerFormatException(Loc.T("Stalker.BadResponse"), ex);
        }
    }

    /// <summary>Points d'entrée possibles déduits de l'adresse saisie (ordre d'essai).</summary>
    private List<Portal> BuildCandidates()
    {
        var raw = (Source.Url ?? "").Trim();
        if (raw.Length == 0) throw new ProviderException(Loc.T("Stalker.MissingUrl"));
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = "http://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ProviderException(Loc.T("Stalker.InvalidUrl"));

        var scheme = uri.Scheme;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath;
        string? explicitEndpoint = path.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ? origin + path : null;

        string prefix;
        bool hasStalker;
        int sp = path.IndexOf("/stalker_portal", StringComparison.OrdinalIgnoreCase);
        if (sp >= 0)
        {
            prefix = path[..sp];
            hasStalker = true;
        }
        else
        {
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (segs.Count > 0)
            {
                var last = segs[^1];
                if (last.Equals("c", StringComparison.OrdinalIgnoreCase) || last.Equals("server", StringComparison.OrdinalIgnoreCase)
                    || last.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || last.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    || last.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    segs.RemoveAt(segs.Count - 1);
                else
                    break;
            }
            prefix = segs.Count == 0 ? "" : "/" + string.Join('/', segs);
            hasStalker = false;
        }
        var b = origin + prefix.TrimEnd('/');

        var list = new List<Portal>();
        void Add(string endpoint, string root)
        {
            if (list.Any(p => string.Equals(p.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new Portal(scheme, origin, root, endpoint, root + "/c/"));
        }

        if (explicitEndpoint != null)
        {
            bool inStalker = explicitEndpoint.Contains("/stalker_portal/", StringComparison.OrdinalIgnoreCase);
            Add(explicitEndpoint, inStalker ? b + "/stalker_portal" : b);
        }
        if (hasStalker) Add(b + "/stalker_portal/server/load.php", b + "/stalker_portal");
        Add(b + "/portal.php", b);
        Add(b + "/server/load.php", b);
        Add(b + "/stalker_portal/server/load.php", b + "/stalker_portal");
        return list;
    }

    private static Identity? CreateIdentity(SourceConfig src)
    {
        var mac = NormalizeMac(src.MacAddress);
        if (mac == null) return null;
        var macBytes = Encoding.UTF8.GetBytes(mac);
        var sn = Convert.ToHexString(MD5.HashData(macBytes))[..13];
        var deviceId = Convert.ToHexString(SHA256.HashData(macBytes));
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sn + mac)));
        var hw2 = Convert.ToHexString(SHA1.HashData(macBytes)).ToLowerInvariant();
        var ua = NullIfBlank(src.UserAgent)?.Trim() ?? DefaultUserAgent;
        var cookie = $"mac={Uri.EscapeDataString(mac)}; stb_lang=en; timezone={Uri.EscapeDataString(TimeZoneIana)}";
        return new Identity(mac, sn, deviceId, signature, hw2, ua, cookie);
    }

    private static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static string GetIanaTimeZone()
    {
        try
        {
            var tz = TimeZoneInfo.Local;
            if (tz.HasIanaId && tz.Id.Contains('/')) return tz.Id;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) && !string.IsNullOrEmpty(iana) && iana.Contains('/'))
                return iana;
        }
        catch
        {
            // ignoré : valeur par défaut
        }
        return "Europe/Paris";
    }

    // ───────────────────────────── Lecture ─────────────────────────────

    /// <summary>Extrait l'URL de <c>js.cmd</c> et corrige les liens incomplets (stream= vide, chemin relatif…).</summary>
    private static string? ExtractLink(JsonElement js, string original, string? fallbackId, Portal portal, out string? error)
    {
        error = null;
        string? raw = js.ValueKind == JsonValueKind.String ? js.GetString() : js.Str("cmd", "url");
        if (js.ValueKind == JsonValueKind.Object) error = NullIfBlank(js.Str("error"))?.Trim();

        var url = string.IsNullOrWhiteSpace(raw) ? "" : StripCmd(raw);
        if (url.StartsWith("//", StringComparison.Ordinal)) url = portal.Scheme + ":" + url;
        else if (url.StartsWith('/')) url = portal.Origin + url;

        if (url.Length > 0 && EmptyStreamRegex.IsMatch(url))
        {
            var streamId = StreamIdFrom(original) ?? fallbackId;
            if (!string.IsNullOrEmpty(streamId))
                url = EmptyStreamRegex.Replace(url, m => m.Groups[1].Value + Uri.EscapeDataString(streamId), 1);
            else if (IsPlayableUrl(original))
                url = original;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
            url = IsPlayableUrl(original) ? original : "";
        return url.Length > 0 ? url : null;
    }

    /// <summary>Retire les préfixes de lecteur (« ffmpeg », « ffrt », « auto »…) d'une commande.</summary>
    private static string StripCmd(string cmd)
    {
        var s = cmd.Trim();
        while (true)
        {
            int sp = s.IndexOf(' ');
            if (sp <= 0) break;
            var head = s[..sp];
            if (head.Contains("://", StringComparison.Ordinal) || head.StartsWith('/')) break;
            s = s[(sp + 1)..].TrimStart();
        }
        return s.Trim();
    }

    private static string? StreamIdFrom(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var m = StreamParamRegex.Match(cmd);
        if (m.Success && m.Groups[1].Value.Length > 0) return Uri.UnescapeDataString(m.Groups[1].Value);

        if (cmd.Contains("://", StringComparison.Ordinal))
        {
            var path = cmd;
            int q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            var pm = PathIdRegex.Match(path);
            return pm.Success ? pm.Groups[1].Value : null;
        }

        if (Base64Regex.IsMatch(cmd))
        {
            try
            {
                var b64 = cmd.Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var bytes = Convert.FromBase64String(b64);
                using var doc = JsonDocument.Parse(bytes);
                var id = doc.RootElement.Str("stream_id", "stream", "id");
                if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            }
            catch
            {
                // pas du JSON en base64
            }
        }
        return null;
    }

    private static bool IsPlayableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https" or "rtmp" or "rtsp" or "udp" or "rtp" or "hls")) return false;
        var host = u.Host.ToLowerInvariant();
        return host.Length > 0 && host is not ("localhost" or "127.0.0.1" or "0.0.0.0" or "[::1]");
    }

    // ───────────────────────────── Utilitaires ─────────────────────────────

    private static JsonElement Js(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("js", out var js) ? js : root;

    /// <summary>Éléments d'une réponse : <c>js</c> tableau, <c>js.data</c>, ou objet indexé par identifiant.</summary>
    private static IEnumerable<JsonElement> DataItems(JsonElement js)
    {
        if (js.ValueKind == JsonValueKind.Array) return js.Items();
        if (js.ValueKind != JsonValueKind.Object) return [];
        if (js.TryGetProperty("data", out var data))
            return data.ValueKind is JsonValueKind.Array or JsonValueKind.Object ? data.Items() : [];

        int n = 0;
        foreach (var p in js.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) return [];
            n++;
        }
        return n > 0 ? js.Items() : [];
    }

    private static List<int> SeriesNumbers(JsonElement e)
    {
        var list = new List<int>();
        if (e.Obj("series") is not { } arr) return list;
        foreach (var x in arr.Items())
        {
            int? n = x.ValueKind switch
            {
                JsonValueKind.Number when x.TryGetInt32(out var i) => i,
                JsonValueKind.String when int.TryParse(x.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) => j,
                _ => null,
            };
            if (n is > 0 && !list.Contains(n.Value)) list.Add(n.Value);
        }
        list.Sort();
        return list;
    }

    private static string? AbsUrl(string? raw, Portal portal, string? bareFileDir)
    {
        var s = NullIfBlank(raw)?.Trim().Replace("\\/", "/");
        if (s == null || s is "0" or "null" or "undefined") return null;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        if (s.StartsWith("//", StringComparison.Ordinal)) return portal.Scheme + ":" + s;
        if (s.StartsWith('/')) return portal.Origin + s;
        if (s.Contains("://", StringComparison.Ordinal)) return s;
        if (bareFileDir != null && !s.Contains('/')) return bareFileDir + s;
        return portal.Root + "/" + s;
    }

    private static double? ReadRating(JsonElement e)
    {
        foreach (var key in RatingKeys)
        {
            var m = NumberRegex.Match(e.Str(key) ?? "");
            if (m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d > 0 && d <= 10)
                return Math.Round(d, 1);
        }
        return null;
    }

    private static int? ParseYear(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = YearRegex.Match(s);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    private static int? ParseMinutes(string? s)
    {
        s = NullIfBlank(s)?.Trim();
        if (s == null) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            if (v <= 0) return null;
            return v > 1000 ? v / 60 : v; // valeur probablement en secondes
        }
        if (s.Contains(':') && TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts.TotalMinutes >= 1)
            return (int)Math.Round(ts.TotalMinutes);
        return null;
    }

    private static int? FirstNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = IntRegex.Match(s);
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static int? NumberAfterColon(string? id)
    {
        if (id == null) return null;
        int i = id.LastIndexOf(':');
        if (i < 0 || i == id.Length - 1) return null;
        return int.TryParse(id[(i + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static int? PositiveOrNull(int? v) => v is > 0 ? v : null;

    private static DateTimeOffset? Unix(long? v)
    {
        if (v is not > 0) return null;
        var secs = v.Value > 100_000_000_000 ? v.Value / 1000 : v.Value;
        try { return DateTimeOffset.FromUnixTimeSeconds(secs); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTimeOffset? ParseDate(string? raw)
    {
        var s = NullIfBlank(raw)?.Trim();
        if (s == null || s.StartsWith("0000", StringComparison.Ordinal)) return null;

        if (s.All(char.IsAsciiDigit))
        {
            if (s.Length < 9 || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)) return null;
            var r = Unix(unix);
            return r is { Year: >= 2000 and <= 2200 } ? r : null;
        }

        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, styles, out var dt)
            || DateTime.TryParse(s, CultureInfo.InvariantCulture, styles, out dt)
            || DateTime.TryParse(s, Fr, styles, out dt))
        {
            if (dt.Year is < 2000 or > 2200) return null;
            try { return new DateTimeOffset(dt); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }

    private static DateTimeOffset? FirstDate(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (ParseDate(e.Str(k)) is { } d) return d;
        return null;
    }

    private static bool LooksUnlimited(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = e.Str(k);
            if (v != null && (v.Contains("unlim", StringComparison.OrdinalIgnoreCase) || v.Contains("illimit", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static List<string> SplitList(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Where(x => x.Length > 0)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();

    private static string? CleanMessage(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = SpacesRegex.Replace(HtmlTagRegex.Replace(WebUtility.HtmlDecode(s), " "), " ").Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string E(string? s) => Uri.EscapeDataString(s ?? "");

    private static bool IsNetworkFailure(Exception ex) => ex switch
    {
        TimeoutException or SocketException or IOException => true,
        HttpRequestException { StatusCode: null } => true,
        ProviderException { InnerException: { } inner } => IsNetworkFailure(inner),
        _ => false,
    };

    private static string Describe(Exception ex) => ex switch
    {
        ProviderException pe => pe.Message,
        StalkerAuthException => Loc.T("Stalker.MacRefused"),
        TimeoutException => Loc.T("Stalker.UnreachableTimeout"),
        HttpRequestException { StatusCode: { } code } => Loc.T("Stalker.HttpError", (int)code),
        HttpRequestException or SocketException or IOException => Loc.T("Stalker.Unreachable"),
        StalkerFormatException or JsonException => Loc.T("Stalker.BadResponse"),
        _ => Loc.T("Stalker.Error", ex.Message),
    };

    // ───────────────────────────── Types internes ─────────────────────────────

    /// <summary>Point d'entrée du portail : <paramref name="Root"/> sert de base aux chemins relatifs (logos…).</summary>
    private sealed record Portal(string Scheme, string Origin, string Root, string Endpoint, string Referer)
    {
        public string LogoDir => Root + "/misc/logos/320/";
    }

    private sealed record Identity(string Mac, string Sn, string DeviceId, string Signature, string HwVersion2, string UserAgent, string Cookie);

    private sealed class Session(Portal portal, Identity identity, string token, JsonElement profile, DateTime createdUtc)
    {
        public Portal Portal { get; } = portal;
        public Identity Identity { get; } = identity;
        public string Token { get; } = token;
        public JsonElement Profile { get; } = profile;
        public DateTime CreatedUtc { get; } = createdUtc;
    }

    private sealed record VodExtra(string? OriginalName, string? Director, string? Actors, int? Minutes, string? Age);

    private sealed record PageResult<T>(List<T> Items, int Raw, int? Total, int? PerPage, string? FirstId);

    private sealed class PageStats
    {
        public long Total;
        public long Done;
        public int Ok;
        public int Failed;
        public Exception? LastError;
    }

    private sealed class EmptyMark
    {
        public int From = int.MaxValue;
    }

    private sealed class StalkerAuthException() : Exception("Authorization failed");

    private sealed class StalkerFormatException(string message, Exception? inner = null) : Exception(message, inner);
}
