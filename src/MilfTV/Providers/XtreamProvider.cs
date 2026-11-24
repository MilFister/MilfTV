using System.Globalization;
using System.Text;
using System.Text.Json;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.Providers;

/// <summary>Source Xtream Codes (player_api.php).</summary>
public sealed class XtreamProvider : IProvider
{
    public SourceConfig Source { get; }

    private readonly string _base;
    private readonly string _user;
    private readonly string _pass;
    private string _liveExt = "ts";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(4);

    public XtreamProvider(SourceConfig source)
    {
        Source = source;
        _base = NormalizeServer(source.Url);
        _user = source.Username?.Trim() ?? "";
        _pass = source.Password?.Trim() ?? "";
    }

    /// <summary>Nettoie l'adresse saisie : schéma, suppression de player_api.php / get.php, barre finale.</summary>
    public static string NormalizeServer(string url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return u;
        if (!u.Contains("://")) u = "http://" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;
            foreach (var marker in new[] { "/player_api.php", "/get.php", "/xmltv.php", "/panel_api.php", "/c/" })
            {
                var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) path = path[..i];
            }
            path = path.TrimEnd('/');
            return $"{uri.Scheme}://{uri.Authority}{path}";
        }
        return u.TrimEnd('/');
    }

    /// <summary>Reconnaît un lien M3U Xtream (get.php?username=…&password=…) et en extrait serveur et identifiants.</summary>
    public static bool TryParseM3uLink(string link, out string server, out string user, out string pass)
    {
        server = user = pass = "";
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!uri.AbsolutePath.EndsWith("get.php", StringComparison.OrdinalIgnoreCase) &&
            !uri.AbsolutePath.EndsWith("player_api.php", StringComparison.OrdinalIgnoreCase)) return false;
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        user = q["username"] ?? "";
        pass = q["password"] ?? "";
        if (user.Length == 0 || pass.Length == 0) return false;
        server = NormalizeServer(link);
        return true;
    }

    private string Api(string? action = null, string? extra = null)
    {
        var sb = new StringBuilder($"{_base}/player_api.php?username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}");
        if (action is not null) sb.Append("&action=").Append(action);
        if (extra is not null) sb.Append('&').Append(extra);
        return sb.ToString();
    }

    private async Task<JsonDocument> GetAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await Http.GetJsonAsync(Http.ForSource(Source), url, Source.UserAgent, timeout, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new ProviderException(Loc.T("Xtream.AccessDenied"), ex);
        }
        catch (HttpRequestException ex) when (Http.IsCertificateError(ex))
        {
            throw new ProviderException(Loc.T("Source.CertInvalid"), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(Loc.T("Err.ServerUnreachable", ex.Message), ex);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(Loc.T("Xtream.BadResponse"), ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(Loc.T("Xtream.Timeout"));
        }
    }

    public async Task<AccountInfo> TestAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_base) || _user.Length == 0)
            return new AccountInfo { Ok = false, Message = Loc.T("Xtream.ServerAndUserRequired") };
        try
        {
            using var doc = await GetAsync(Api(), ShortTimeout, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.Obj("user_info") is not { } ui)
                return new AccountInfo { Ok = false, Message = Loc.T("Xtream.UnexpectedResponse") };
            var auth = ui.Str("auth");
            if (auth == "0")
                return new AccountInfo { Ok = false, Message = Loc.T("Xtream.WrongCredentials") };
            var info = new AccountInfo
            {
                Ok = true,
                Status = ui.Str("status"),
                MaxConnections = ui.Int("max_connections"),
                ActiveConnections = ui.Int("active_cons"),
                IsTrial = ui.Str("is_trial") == "1",
                Message = ui.Str("message"),
            };
            var exp = ui.Long("exp_date");
            if (exp is > 0) info.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp.Value);
            if (ui.Obj("allowed_output_formats") is { } fmts)
                foreach (var f in fmts.Items())
                    if (f.ValueKind == JsonValueKind.String) info.OutputFormats.Add(f.GetString()!);
            if (root.Obj("server_info") is { } si) info.ServerTimezone = si.Str("timezone");

            if (info.OutputFormats.Count > 0 && !info.OutputFormats.Contains("ts"))
                _liveExt = info.OutputFormats.Contains("m3u8") ? "m3u8" : info.OutputFormats[0];

            if (!string.Equals(info.Status, "Active", StringComparison.OrdinalIgnoreCase) && info.Status is not null)
            {
                info.Ok = false;
                info.Message ??= info.Status switch
                {
                    "Expired" => Loc.T("Xtream.Expired"),
                    "Banned" => Loc.T("Xtream.Banned"),
                    "Disabled" => Loc.T("Xtream.Disabled"),
                    _ => Loc.T("Xtream.AccountStatus", info.Status)
                };
            }
            return info;
        }
        catch (ProviderException ex)
        {
            return new AccountInfo { Ok = false, Message = ex.Message };
        }
    }

    public async Task<Catalog> LoadCatalogAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("Xtream.Connecting"));
        var account = await TestAsync(ct).ConfigureAwait(false);
        if (!account.Ok) throw new ProviderException(account.Message ?? Loc.T("Err.ConnectionRefused"));

        progress?.Report(Loc.T("Xtream.DownloadingCategories"));
        var catTasks = new[]
        {
            GetAsync(Api("get_live_categories"), ShortTimeout, ct),
            GetAsync(Api("get_vod_categories"), ShortTimeout, ct),
            GetAsync(Api("get_series_categories"), ShortTimeout, ct),
        };
        var catalog = new Catalog();
        using (var live = await catTasks[0].ConfigureAwait(false)) catalog.LiveCategories = ParseCategories(live.RootElement);
        using (var vod = await catTasks[1].ConfigureAwait(false)) catalog.MovieCategories = ParseCategories(vod.RootElement);
        using (var ser = await catTasks[2].ConfigureAwait(false)) catalog.SeriesCategories = ParseCategories(ser.RootElement);

        progress?.Report(Loc.T("Xtream.DownloadingContent"));
        var liveTask = GetAsync(Api("get_live_streams"), ListTimeout, ct);
        var vodTask = GetAsync(Api("get_vod_streams"), ListTimeout, ct);
        var seriesTask = GetAsync(Api("get_series"), ListTimeout, ct);

        using (var doc = await liveTask.ConfigureAwait(false))
        {
            int n = 0;
            foreach (var e in doc.RootElement.Items())
            {
                var id = e.Str("stream_id");
                if (string.IsNullOrEmpty(id)) continue;
                n++;
                catalog.Channels.Add(new LiveChannel
                {
                    Id = id,
                    Name = e.Str("name")?.Trim() ?? "",
                    Number = e.Int("num") ?? n,
                    CategoryId = e.Str("category_id") ?? "",
                    ImageUrl = NullIfEmpty(e.Str("stream_icon")),
                    EpgId = NullIfEmpty(e.Str("epg_channel_id")),
                    ArchiveDays = e.Str("tv_archive") == "1" ? e.Int("tv_archive_duration") ?? 0 : 0,
                    Added = e.Long("added") ?? 0,
                });
            }
            progress?.Report(Loc.T("Progress.ChannelCount", catalog.Channels.Count));
        }

        using (var doc = await vodTask.ConfigureAwait(false))
        {
            foreach (var e in doc.RootElement.Items())
            {
                var id = e.Str("stream_id");
                if (string.IsNullOrEmpty(id)) continue;
                var name = e.Str("name")?.Trim() ?? "";
                var year = ParseYear(e.Str("year")) ?? TextNorm.SplitYear(name).Year;
                catalog.Movies.Add(new Movie
                {
                    Id = id,
                    Name = name,
                    CategoryId = e.Str("category_id") ?? "",
                    ImageUrl = NullIfEmpty(e.Str("stream_icon", "cover")),
                    Added = e.Long("added") ?? 0,
                    Rating = NormalizeRating(e.Dbl("rating"), e.Dbl("rating_5based")),
                    Extension = NullIfEmpty(e.Str("container_extension")),
                    TmdbId = NullIfZero(e.Str("tmdb", "tmdb_id")),
                    Year = year,
                    Plot = NullIfEmpty(e.Str("plot")),
                    Genre = NullIfEmpty(e.Str("genre")),
                });
            }
            progress?.Report(Loc.T("Progress.MovieCount", catalog.Movies.Count));
        }

        using (var doc = await seriesTask.ConfigureAwait(false))
        {
            foreach (var e in doc.RootElement.Items())
            {
                var id = e.Str("series_id");
                if (string.IsNullOrEmpty(id)) continue;
                var name = e.Str("name")?.Trim() ?? "";
                string? backdrop = null;
                if (e.Obj("backdrop_path") is { } bp)
                    backdrop = bp.Items().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                var lastModified = e.Long("last_modified") ?? 0;
                catalog.Series.Add(new Series
                {
                    Id = id,
                    Name = name,
                    CategoryId = e.Str("category_id") ?? "",
                    ImageUrl = NullIfEmpty(e.Str("cover")),
                    Plot = NullIfEmpty(e.Str("plot")),
                    Genre = NullIfEmpty(e.Str("genre")),
                    Cast = NullIfEmpty(e.Str("cast")),
                    Rating = NormalizeRating(e.Dbl("rating"), e.Dbl("rating_5based")),
                    Year = ParseYear(e.Str("releaseDate", "release_date", "year")) ?? TextNorm.SplitYear(name).Year,
                    Backdrop = backdrop,
                    TmdbId = NullIfZero(e.Str("tmdb", "tmdb_id")),
                    Added = lastModified,
                });
            }
            progress?.Report(Loc.T("Progress.SeriesCount", catalog.Series.Count));
        }

        // Adresse du guide calculée à la demande (GetXmltvUrl) : elle contient le mot de passe et ne doit pas être mise en cache.
        catalog.XmltvUrl = null;
        catalog.Fetched = DateTimeOffset.Now;
        return catalog;
    }

    private static List<Category> ParseCategories(JsonElement root)
    {
        var list = new List<Category>();
        foreach (var e in root.Items())
        {
            var id = e.Str("category_id");
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new Category { Id = id, Name = e.Str("category_name")?.Trim() ?? id });
        }
        return list;
    }

    public async Task<MediaDetails?> GetMovieDetailsAsync(Movie movie, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync(Api("get_vod_info", "vod_id=" + Uri.EscapeDataString(movie.Id)), ShortTimeout, ct).ConfigureAwait(false);
            if (doc.RootElement.Obj("info") is not { } info) return null;
            var d = new MediaDetails
            {
                Title = info.Str("name") ?? movie.Name,
                OriginalTitle = info.Str("o_name"),
                Plot = info.Str("plot", "description"),
                PosterUrl = info.Str("cover_big", "movie_image") ?? movie.ImageUrl,
                ReleaseDate = info.Str("releasedate", "release_date"),
                Rating = NormalizeRating(info.Dbl("rating"), info.Dbl("rating_5based")),
                Director = info.Str("director"),
                TmdbId = NullIfZero(info.Str("tmdb_id", "tmdb")),
                TrailerYoutubeKey = NullIfEmpty(info.Str("youtube_trailer")),
                Certification = info.Str("age", "mpaa_rating"),
            };
            if (info.Obj("backdrop_path") is { } bp)
                d.BackdropUrl = bp.Items().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            var secs = info.Int("duration_secs");
            if (secs is > 0) d.RuntimeMinutes = secs / 60;
            d.Year = ParseYear(d.ReleaseDate) ?? movie.Year;
            var genre = info.Str("genre");
            if (!string.IsNullOrWhiteSpace(genre))
                d.Genres = genre.Split(new[] { ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var cast = info.Str("cast", "actors");
            if (!string.IsNullOrWhiteSpace(cast))
                d.Cast = cast.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(15).Select(n => new CastMember { Name = n }).ToList();
            if (doc.RootElement.Obj("movie_data") is { } md && NullIfEmpty(md.Str("container_extension")) is { } ext)
                movie.Extension = ext;
            d.Media = MediaInfo.FromXtream(info, movie.Extension);
            return d;
        }
        catch (ProviderException ex)
        {
            Log.Warn($"get_vod_info {movie.Id} : {ex.Message}");
            return null;
        }
    }

    public async Task<SeriesDetails?> GetSeriesDetailsAsync(Series series, CancellationToken ct)
    {
        using var doc = await GetAsync(Api("get_series_info", "series_id=" + Uri.EscapeDataString(series.Id)), ShortTimeout, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var result = new SeriesDetails();
        if (root.Obj("info") is { } info)
        {
            result.Info = new MediaDetails
            {
                Title = info.Str("name") ?? series.Name,
                Plot = info.Str("plot") ?? series.Plot,
                PosterUrl = info.Str("cover") ?? series.ImageUrl,
                ReleaseDate = info.Str("releaseDate", "release_date"),
                Rating = NormalizeRating(info.Dbl("rating"), info.Dbl("rating_5based")) ?? series.Rating,
                Director = info.Str("director"),
                TrailerYoutubeKey = NullIfEmpty(info.Str("youtube_trailer")),
                TmdbId = NullIfZero(info.Str("tmdb", "tmdb_id")) ?? series.TmdbId,
            };
            result.Info.Year = ParseYear(result.Info.ReleaseDate) ?? series.Year;
            if (info.Obj("backdrop_path") is { } bp)
                result.Info.BackdropUrl = bp.Items().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            var genre = info.Str("genre");
            if (!string.IsNullOrWhiteSpace(genre))
                result.Info.Genres = genre.Split(new[] { ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var cast = info.Str("cast");
            if (!string.IsNullOrWhiteSpace(cast))
                result.Info.Cast = cast.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(15).Select(n => new CastMember { Name = n }).ToList();
        }
        else
        {
            result.Info = new MediaDetails { Title = series.Name, Plot = series.Plot, PosterUrl = series.ImageUrl };
        }

        var seasonNames = new Dictionary<int, string>();
        if (root.Obj("seasons") is { } seasons)
            foreach (var s in seasons.Items())
            {
                var n = s.Int("season_number");
                if (n is null) continue;
                var nm = s.Str("name");
                seasonNames[n.Value] = string.IsNullOrWhiteSpace(nm) ? Loc.T("Common.SeasonN", n.Value) : nm!;
            }

        var bySeason = new SortedDictionary<int, List<Episode>>();
        if (root.Obj("episodes") is { } episodes)
        {
            IEnumerable<(int? key, JsonElement list)> groups = episodes.ValueKind == JsonValueKind.Object
                ? episodes.EnumerateObject().Select(p => ((int?)(int.TryParse(p.Name, out var k) ? k : null), p.Value))
                : episodes.EnumerateArray().Select(v => ((int?)null, v));
            foreach (var (key, list) in groups)
            {
                foreach (var ep in list.Items())
                {
                    var id = ep.Str("id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var season = ep.Int("season") ?? key ?? 1;
                    var epInfo = ep.Obj("info");
                    var episode = new Episode
                    {
                        Id = id,
                        SeriesId = series.Id,
                        Season = season,
                        Number = ep.Int("episode_num") ?? 0,
                        Title = CleanEpisodeTitle(ep.Str("title") ?? "", series.Name),
                        Extension = NullIfEmpty(ep.Str("container_extension")),
                        Plot = epInfo?.Str("plot", "overview"),
                        ImageUrl = NullIfEmpty(epInfo?.Str("movie_image", "cover_big")),
                        DurationSecs = epInfo?.Int("duration_secs"),
                        AirDate = epInfo?.Str("releasedate", "air_date"),
                        Rating = epInfo is { } ei ? NormalizeRating(ei.Dbl("rating"), null) : null,
                        ProviderMedia = epInfo is { } em ? MediaInfo.FromXtream(em, ep.Str("container_extension")) : null,
                    };
                    if (!bySeason.TryGetValue(season, out var l)) bySeason[season] = l = new List<Episode>();
                    l.Add(episode);
                }
            }
        }

        foreach (var (num, eps) in bySeason)
        {
            eps.Sort((a, b) => a.Number.CompareTo(b.Number));
            result.Seasons.Add(new SeasonInfo
            {
                Number = num,
                Name = seasonNames.GetValueOrDefault(num) is { Length: > 0 } nm && !nm.StartsWith("Season", StringComparison.OrdinalIgnoreCase) ? nm : Loc.T("Common.SeasonN", num),
                Episodes = eps
            });
        }
        return result;
    }

    private static string CleanEpisodeTitle(string title, string seriesName)
    {
        var t = title.Trim();
        // "Nom de la série - S01E02 - Titre" → "Titre"
        var m = System.Text.RegularExpressions.Regex.Match(t, @"S\d{1,2}\s*E\d{1,3}\s*[-–:.]?\s*(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value.Trim(' ', '-', '–');
        if (m.Success) return "";
        if (t.StartsWith(seriesName, StringComparison.OrdinalIgnoreCase)) t = t[seriesName.Length..].Trim(' ', '-', '–', ':');
        return t;
    }

    public Task<PlaybackInfo> ResolveAsync(CatalogItem item, Episode? episode, CancellationToken ct)
    {
        string u = Uri.EscapeDataString(_user), p = Uri.EscapeDataString(_pass);
        string url = episode is not null
            ? $"{_base}/series/{u}/{p}/{episode.Id}.{episode.Extension ?? "mp4"}"
            : item switch
            {
                LiveChannel ch => $"{_base}/live/{u}/{p}/{ch.Id}.{_liveExt}",
                Movie m => $"{_base}/movie/{u}/{p}/{m.Id}.{m.Extension ?? "mp4"}",
                _ => throw new ProviderException(Loc.T("Err.NotPlayable"))
            };
        return Task.FromResult(new PlaybackInfo(url, Source.UserAgent));
    }

    public async Task<IReadOnlyList<EpgProgram>> GetChannelEpgAsync(LiveChannel channel, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync(Api("get_short_epg", $"stream_id={Uri.EscapeDataString(channel.Id)}&limit=12"), ShortTimeout, ct).ConfigureAwait(false);
            if (doc.RootElement.Obj("epg_listings") is not { } listings) return Array.Empty<EpgProgram>();
            var key = channel.EpgKey ?? channel.EpgId?.ToLowerInvariant() ?? "ch:" + channel.Id;
            var list = new List<EpgProgram>();
            foreach (var e in listings.Items())
            {
                var start = e.Long("start_timestamp");
                var stop = e.Long("stop_timestamp");
                if (start is null || stop is null || stop <= start) continue;
                list.Add(new EpgProgram
                {
                    ChannelKey = key,
                    Start = DateTimeOffset.FromUnixTimeSeconds(start.Value),
                    End = DateTimeOffset.FromUnixTimeSeconds(stop.Value),
                    Title = DecodeB64(e.Str("title")) ?? "",
                    Description = DecodeB64(e.Str("description")),
                });
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"get_short_epg {channel.Id} : {ex.Message}");
            return Array.Empty<EpgProgram>();
        }
    }

    public string? GetXmltvUrl(Catalog catalog) =>
        !string.IsNullOrWhiteSpace(Source.EpgUrl) ? Source.EpgUrl
            : $"{_base}/xmltv.php?username={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_pass)}";

    private static string? DecodeB64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return s; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? NullIfZero(string? s) => string.IsNullOrWhiteSpace(s) || s == "0" ? null : s.Trim();

    private static double? NormalizeRating(double? rating, double? rating5)
    {
        if (rating is > 0 and <= 10) return Math.Round(rating.Value, 1);
        if (rating5 is > 0 and <= 5) return Math.Round(rating5.Value * 2, 1);
        return null;
    }

    private static int? ParseYear(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(19[2-9][0-9]|20[0-4][0-9])");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    public void Dispose() { }
}
