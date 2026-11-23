using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.Providers;

/// <summary>Playlist M3U / M3U8 (URL ou fichier local), avec classement automatique chaînes / films / séries.</summary>
public sealed partial class M3uProvider : IProvider
{
    public SourceConfig Source { get; }

    public M3uProvider(SourceConfig source) => Source = source;

    private static readonly string[] VodExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".mpg", ".mpeg", ".divx", ".xvid" };

    [GeneratedRegex(@"([\w-]+)=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttrRegex();

    [GeneratedRegex(@"^(?<name>.*?)[\s._\-:|]*\bS(?<s>[0-9]{1,2})\s*[._\- ]?\s*E(?<e>[0-9]{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"^(?<name>.*?)[\s._\-:|]*\b(?:saison|season)\s*(?<s>[0-9]{1,2}).*?\b(?:episode|épisode|ep)\s*(?<e>[0-9]{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRegex2();

    public async Task<AccountInfo> TestAsync(CancellationToken ct)
    {
        try
        {
            var text = await ReadPlaylistAsync(ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (!text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) && !text.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase))
                return new AccountInfo { Ok = false, Message = Loc.T("M3u.NotPlaylist") };
            var count = CountOccurrences(text, "#EXTINF");
            return new AccountInfo { Ok = true, Status = "Active", Message = Loc.T("M3u.EntriesFound", count) };
        }
        catch (ProviderException ex)
        {
            return new AccountInfo { Ok = false, Message = ex.Message };
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += token.Length; }
        return n;
    }

    private async Task<string> ReadPlaylistAsync(CancellationToken ct, TimeSpan timeout)
    {
        var src = Source.Url.Trim().Trim('"');
        try
        {
            if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                src = new Uri(src).LocalPath;
            if (File.Exists(src))
                return await File.ReadAllTextAsync(src, ct).ConfigureAwait(false);
            if (!src.Contains("://")) throw new ProviderException(Loc.T("M3u.FileNotFound", src));
            return await Http.GetStringAsync(Http.Iptv, src, Source.UserAgent, timeout, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ex.StatusCode is { } code ? Loc.T("M3u.HttpError", (int)code) : Loc.T("M3u.Unreachable", ex.Message), ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(Loc.T("M3u.Timeout"));
        }
        catch (IOException ex)
        {
            throw new ProviderException(Loc.T("M3u.ReadFailed", ex.Message), ex);
        }
    }

    public async Task<Catalog> LoadCatalogAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("M3u.Downloading"));
        var text = await ReadPlaylistAsync(ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        progress?.Report(Loc.T("M3u.Parsing"));
        return await Task.Run(() => Parse(text), ct).ConfigureAwait(false);
    }

    private sealed class Entry
    {
        public string Name = "";
        public string Url = "";
        public string Group = "";
        public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase);
        public string? UserAgent;
        public string? Referrer;
    }

    public static Catalog Parse(string text)
    {
        var catalog = new Catalog();
        var entries = new List<Entry>();
        Entry? cur = null;
        string? pendingGroup = null;

        using (var reader = new StringReader(text))
        {
            string? line;
            bool first = true;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.Trim();
                if (first) { line = line.TrimStart('﻿'); first = false; }
                if (line.Length == 0) continue;

                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match m in AttrRegex().Matches(line))
                    {
                        var k = m.Groups[1].Value.ToLowerInvariant();
                        if (k is "url-tvg" or "x-tvg-url" or "tvg-url" && catalog.XmltvUrl is null)
                            catalog.XmltvUrl = m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    }
                    continue;
                }
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    cur = new Entry();
                    var comma = FindTitleComma(line);
                    var head = comma >= 0 ? line[..comma] : line;
                    cur.Name = comma >= 0 ? line[(comma + 1)..].Trim() : "";
                    foreach (Match m in AttrRegex().Matches(head))
                        cur.Attrs[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                    if (cur.Name.Length == 0 && cur.Attrs.TryGetValue("tvg-name", out var tn)) cur.Name = tn;
                    cur.Group = cur.Attrs.GetValueOrDefault("group-title") ?? pendingGroup ?? "";
                    continue;
                }
                if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
                {
                    var g = line[8..].Trim();
                    if (cur is not null && string.IsNullOrEmpty(cur.Group)) cur.Group = g;
                    else pendingGroup = g;
                    continue;
                }
                if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase) && cur is not null)
                {
                    var opt = line[11..];
                    var eq = opt.IndexOf('=');
                    if (eq > 0)
                    {
                        var k = opt[..eq].Trim().ToLowerInvariant();
                        var v = opt[(eq + 1)..].Trim();
                        if (k == "http-user-agent") cur.UserAgent = v;
                        else if (k is "http-referrer" or "http-referer") cur.Referrer = v;
                    }
                    continue;
                }
                if (line.StartsWith('#')) continue;

                // ligne d'URL
                if (cur is null) cur = new Entry { Name = line };
                cur.Url = line;
                // URL "url|User-Agent=xxx&Referer=yyy" (style Kodi)
                var pipe = cur.Url.IndexOf('|');
                if (pipe > 0)
                {
                    var opts = cur.Url[(pipe + 1)..];
                    cur.Url = cur.Url[..pipe];
                    foreach (var kv in opts.Split('&'))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = kv[..eq].Trim().ToLowerInvariant();
                        var v = Uri.UnescapeDataString(kv[(eq + 1)..]);
                        if (k == "user-agent") cur.UserAgent = v;
                        else if (k is "referer" or "referrer") cur.Referrer = v;
                    }
                }
                if (cur.Name.Length == 0) cur.Name = cur.Url;
                entries.Add(cur);
                cur = null;
            }
        }

        var liveCats = new Dictionary<string, Category>();
        var movieCats = new Dictionary<string, Category>();
        var seriesCats = new Dictionary<string, Category>();
        var seriesByKey = new Dictionary<string, Series>();
        var seenUrls = new HashSet<string>();
        int number = 0;

        foreach (var e in entries)
        {
            if (!seenUrls.Add(e.Url)) continue;
            var kind = Classify(e, out var sName, out var season, out var epNum);
            // Entrées sans group-title : nom affiché dans la langue de l'interface, mais identifiant de catégorie
            // calculé sur le nom français historique (verrous parentaux conservés après un changement de langue).
            var (group, groupName) = !string.IsNullOrWhiteSpace(e.Group) ? (e.Group, e.Group) : kind switch
            {
                ContentKind.Live => ("Chaînes", Loc.T("Common.Channels")),
                ContentKind.Movie => ("Films", Loc.T("Common.Movies")),
                _ => ("Séries", Loc.T("Common.Series")),
            };
            var logo = e.Attrs.GetValueOrDefault("tvg-logo");
            if (string.IsNullOrWhiteSpace(logo)) logo = null;

            switch (kind)
            {
                case ContentKind.Live:
                {
                    var cat = GetCat(liveCats, group, groupName);
                    number++;
                    var chno = e.Attrs.GetValueOrDefault("tvg-chno");
                    catalog.Channels.Add(new LiveChannel
                    {
                        Id = Hash(e.Url),
                        Name = e.Name,
                        Number = int.TryParse(chno, out var n) ? n : number,
                        CategoryId = cat.Id,
                        ImageUrl = logo,
                        EpgId = NullIfEmpty(e.Attrs.GetValueOrDefault("tvg-id")),
                        StreamUrl = e.Url,
                        UserAgent = e.UserAgent,
                        Referrer = e.Referrer,
                        ArchiveDays = int.TryParse(e.Attrs.GetValueOrDefault("catchup-days") ?? e.Attrs.GetValueOrDefault("tvg-rec"), out var cd) ? cd : 0,
                    });
                    break;
                }
                case ContentKind.Movie:
                {
                    var cat = GetCat(movieCats, group, groupName);
                    var (_, year) = TextNorm.SplitYear(e.Name);
                    catalog.Movies.Add(new Movie
                    {
                        Id = Hash(e.Url),
                        Name = e.Name,
                        CategoryId = cat.Id,
                        ImageUrl = logo,
                        StreamUrl = e.Url,
                        Year = year,
                        Extension = Path.GetExtension(Uri.TryCreate(e.Url, UriKind.Absolute, out var vodUri) ? vodUri.AbsolutePath : e.Url).TrimStart('.') is { Length: > 0 and < 6 } ext ? ext : null,
                    });
                    break;
                }
                case ContentKind.Episode:
                {
                    var cat = GetCat(seriesCats, group, groupName);
                    var key = cat.Id + "|" + TextNorm.Normalize(sName);
                    if (!seriesByKey.TryGetValue(key, out var series))
                    {
                        series = new Series
                        {
                            Id = Hash(key),
                            Name = sName,
                            CategoryId = cat.Id,
                            ImageUrl = logo,
                            Episodes = new List<Episode>(),
                            Year = TextNorm.SplitYear(sName).Year,
                        };
                        seriesByKey[key] = series;
                        catalog.Series.Add(series);
                    }
                    series.ImageUrl ??= logo;
                    series.Episodes!.Add(new Episode
                    {
                        Id = Hash(e.Url),
                        SeriesId = series.Id,
                        Season = season,
                        Number = epNum,
                        Title = "",
                        StreamUrl = e.Url,
                        ImageUrl = logo,
                    });
                    break;
                }
            }
        }

        catalog.LiveCategories = liveCats.Values.ToList();
        catalog.MovieCategories = movieCats.Values.ToList();
        catalog.SeriesCategories = seriesCats.Values.ToList();
        foreach (var s in catalog.Series)
            s.Episodes!.Sort((a, b) => a.Season != b.Season ? a.Season.CompareTo(b.Season) : a.Number.CompareTo(b.Number));
        catalog.Fetched = DateTimeOffset.Now;
        return catalog;

        static Category GetCat(Dictionary<string, Category> dict, string key, string name)
        {
            if (!dict.TryGetValue(key, out var c))
                dict[key] = c = new Category { Id = Hash(key), Name = name };
            return c;
        }
    }

    /// <summary>Position de la virgule qui sépare les attributs du titre (en ignorant celles entre guillemets).</summary>
    private static int FindTitleComma(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes) return i;
        }
        return -1;
    }

    private static ContentKind Classify(Entry e, out string seriesName, out int season, out int episode)
    {
        seriesName = "";
        season = episode = 0;
        var url = e.Url;
        string path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        var lowerPath = path.ToLowerInvariant();
        var group = e.Group.ToLowerInvariant();

        bool xtreamSeries = lowerPath.Contains("/series/");
        bool xtreamMovie = lowerPath.Contains("/movie/");
        bool xtreamLive = lowerPath.Contains("/live/");
        bool vodExt = VodExtensions.Any(x => lowerPath.EndsWith(x, StringComparison.Ordinal));

        bool isVod = xtreamSeries || xtreamMovie || (!xtreamLive && vodExt);
        if (!isVod) return ContentKind.Live;

        var m = EpisodeRegex().Match(e.Name);
        if (!m.Success) m = EpisodeRegex2().Match(e.Name);
        bool groupSeries = group.Contains("serie") || group.Contains("série") || group.Contains("saison") || group.Contains("season") || group.Contains("tv show");
        if (m.Success || xtreamSeries || groupSeries)
        {
            if (m.Success)
            {
                seriesName = m.Groups["name"].Value.Trim(' ', '-', '_', '.', ':', '|');
                season = int.Parse(m.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture);
                episode = int.Parse(m.Groups["e"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (seriesName.Length == 0)
            {
                seriesName = e.Attrs.GetValueOrDefault("tvg-name") is { Length: > 0 } tn && !EpisodeRegex().IsMatch(tn) ? tn : e.Name;
                if (season == 0) season = 1;
            }
            return ContentKind.Episode;
        }
        return ContentKind.Movie;
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task<MediaDetails?> GetMovieDetailsAsync(Movie movie, CancellationToken ct) =>
        Task.FromResult<MediaDetails?>(null);

    public Task<SeriesDetails?> GetSeriesDetailsAsync(Series series, CancellationToken ct)
    {
        var details = new SeriesDetails
        {
            Info = new MediaDetails { Title = series.Name, PosterUrl = series.ImageUrl, Year = series.Year, Plot = series.Plot }
        };
        if (series.Episodes is { Count: > 0 } eps)
        {
            foreach (var g in eps.GroupBy(e => e.Season).OrderBy(g => g.Key))
            {
                var list = g.OrderBy(e => e.Number).ToList();
                foreach (var ep in list) ep.SeriesId = series.Id;
                details.Seasons.Add(new SeasonInfo { Number = g.Key, Name = Loc.T("Common.SeasonN", g.Key), Episodes = list });
            }
        }
        return Task.FromResult<SeriesDetails?>(details);
    }

    public Task<PlaybackInfo> ResolveAsync(CatalogItem item, Episode? episode, CancellationToken ct)
    {
        if (episode is not null)
            return Task.FromResult(new PlaybackInfo(episode.StreamUrl ?? throw new ProviderException(Loc.T("M3u.EpisodeNoUrl")), Source.UserAgent));
        return Task.FromResult(item switch
        {
            LiveChannel ch => new PlaybackInfo(ch.StreamUrl ?? "", ch.UserAgent ?? Source.UserAgent, ch.Referrer),
            Movie m => new PlaybackInfo(m.StreamUrl ?? "", Source.UserAgent),
            _ => throw new ProviderException(Loc.T("Err.NotPlayable"))
        });
    }

    public Task<IReadOnlyList<EpgProgram>> GetChannelEpgAsync(LiveChannel channel, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EpgProgram>>(Array.Empty<EpgProgram>());

    public string? GetXmltvUrl(Catalog catalog) =>
        !string.IsNullOrWhiteSpace(Source.EpgUrl) ? Source.EpgUrl : catalog.XmltvUrl;

    public void Dispose() { }
}
