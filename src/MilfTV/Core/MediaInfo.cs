using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MilfTV.Localization;
using MilfTV.Player;

namespace MilfTV.Core;

/// <summary>Caractéristiques techniques d'un film ou d'un épisode. Deux sources : le fournisseur (Xtream : analyse
/// ffprobe faite par le serveur, une seule piste audio) et le lecteur, qui relève à la lecture toutes les pistes audio
/// et de sous-titres (<see cref="MediaInfoCache"/>). Les fiches fusionnent les deux, la lecture d'abord.</summary>
public sealed class MediaInfo
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
    public bool Hdr { get; set; }
    public double? Fps { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    /// <summary>Codes de langue (ISO 639) des pistes audio, dans l'ordre du fichier.</summary>
    public List<string> AudioLanguages { get; set; } = new();
    public List<string> SubtitleLanguages { get; set; } = new();
    public int? BitrateKbps { get; set; }
    public int? DurationSecs { get; set; }
    public string? Container { get; set; }
    /// <summary>Relevé par le lecteur (pistes complètes) plutôt qu'annoncé par le fournisseur.</summary>
    public bool FromPlayback { get; set; }
    public DateTimeOffset Updated { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Height is null && Width is null && VideoCodec is null && AudioCodec is null && AudioChannels is null
                           && AudioLanguages.Count == 0 && SubtitleLanguages.Count == 0 && BitrateKbps is null && Container is null;

    // ───────────── Affichage ─────────────

    /// <summary>Classe de définition d'après la largeur OU la hauteur : les films au format cinéma sont moins hauts
    /// que leur classe (3840×1600 = 4K, 1920×800 = 1080p), et les sources HDV moins larges (1440×1080 = 1080p).</summary>
    public static string QualityLabel(long w, long h) =>
        w >= 3200 || h >= 2000 ? "4K"
        : w >= 2400 || h >= 1350 ? "1440p"
        : w >= 1700 || h >= 1000 ? "1080p"
        : w >= 1200 || h >= 700 ? "720p"
        : h > 0 ? $"{h}p" : "SD";

    /// <summary>Nom court d'un codec vidéo ou audio (« hevc », « HEVC (High Efficiency…) » → HEVC).</summary>
    public static string? CodecName(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;
        var c = codec.ToLowerInvariant();
        if (c.Contains("hevc") || c.Contains("h.265") || c.Contains("h265")) return "HEVC";
        if (c.Contains("h264") || c.Contains("h.264") || c.Contains("avc")) return "H.264";
        if (c.Contains("av1")) return "AV1";
        if (c.Contains("mpeg2") || c.Contains("mpeg-2")) return "MPEG-2";
        if (c.Contains("vp9")) return "VP9";
        if (c.Contains("eac3") || c.Contains("e-ac-3")) return "E-AC-3";
        if (c.Contains("ac3") || c.Contains("ac-3")) return "AC-3";
        if (c.Contains("truehd")) return "TrueHD";
        if (c.Contains("dts")) return "DTS";
        if (c.Contains("aac")) return "AAC";
        if (c.Contains("mp3")) return "MP3";
        if (c.Contains("opus")) return "Opus";
        if (c.Contains("flac")) return "FLAC";
        if (c.Contains("vorbis")) return "Vorbis";
        return codec.Split(' ', '(')[0].ToUpperInvariant();
    }

    public static string ChannelsLabel(int channels) => channels switch
    {
        1 => Loc.T("Track.Mono"),
        2 => Loc.T("Track.Stereo"),
        6 => "5.1",
        8 => "7.1",
        _ => Loc.T("Track.Channels", channels),
    };

    [JsonIgnore] public string? Quality => Width is > 0 || Height is > 0 ? QualityLabel(Width ?? 0, Height ?? 0) : null;

    [JsonIgnore]
    public string? ResolutionText => Quality is not { } q ? null
        : Width is > 0 && Height is > 0 ? $"{q}  ·  {Width} × {Height}" : q;

    [JsonIgnore]
    public string? VideoText => Join(CodecName(VideoCodec), Hdr ? "HDR" : null,
        Fps is > 1 ? Loc.T("Stats.Fps", FpsText(Fps.Value)) : null);

    private static readonly double[] StandardRates = { 23.976, 24, 25, 29.97, 30, 48, 50, 59.94, 60, 100, 119.88, 120 };

    /// <summary>Cadence lisible : une mesure proche d'une cadence standard s'y ramène (25,011 → 25 ; 23,976 reste 23,976).</summary>
    private static string FpsText(double fps)
    {
        var nearest = StandardRates.OrderBy(r => Math.Abs(r - fps)).First();
        var value = Math.Abs(nearest - fps) < 0.05 ? nearest : fps;
        return value.ToString("0.###", Loc.Culture);
    }

    [JsonIgnore]
    public string? AudioText => Join(CodecName(AudioCodec), AudioChannels is > 0 ? ChannelsLabel(AudioChannels.Value) : null);

    [JsonIgnore] public string? AudioLanguagesText => Languages(AudioLanguages);
    [JsonIgnore] public string? SubtitlesText => Languages(SubtitleLanguages) ?? (FromPlayback ? Loc.T("Media.None") : null);

    [JsonIgnore]
    public string? BitrateText => BitrateKbps is not > 0 ? null
        : BitrateKbps >= 1000 ? (BitrateKbps.Value / 1000.0).ToString("0.0", Loc.Culture) + " Mb/s"
        : BitrateKbps.Value.ToString(Loc.Culture) + " kb/s";

    /// <summary>Résumé d'une ligne (épisodes) : « 1080p · HEVC · 5.1 · Français ».</summary>
    [JsonIgnore]
    public string? ShortLine => Join(Quality, Hdr ? "HDR" : null, CodecName(VideoCodec),
        AudioChannels is > 0 ? ChannelsLabel(AudioChannels.Value) : null, Languages(AudioLanguages, 2));

    /// <summary>Pastilles de l'en-tête des fiches : définition, HDR, codec, son, langues.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Chips => new[]
    {
        Quality, Hdr ? "HDR" : null, CodecName(VideoCodec),
        AudioChannels is > 0 ? ChannelsLabel(AudioChannels.Value) : null, Languages(AudioLanguages, 3),
    }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();

    private static string? Languages(IReadOnlyList<string> codes, int max = int.MaxValue)
    {
        var names = codes.Select(c => TrackInfo.LanguageName(c)).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return null;
        return names.Count <= max ? string.Join(", ", names) : string.Join(", ", names.Take(max)) + $" +{names.Count - max}";
    }

    private static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() is { Length: > 0 } ok ? string.Join("  ·  ", ok) : null;

    // ───────────── Sources ─────────────

    /// <summary>Complète les valeurs de <paramref name="primary"/> par celles de <paramref name="fallback"/>.</summary>
    public static MediaInfo? Merge(MediaInfo? primary, MediaInfo? fallback)
    {
        if (primary is null || primary.IsEmpty) return fallback is { IsEmpty: false } ? fallback : null;
        if (fallback is null) return primary;
        return new MediaInfo
        {
            Width = primary.Width ?? fallback.Width,
            Height = primary.Height ?? fallback.Height,
            VideoCodec = primary.VideoCodec ?? fallback.VideoCodec,
            // Le lecteur a mesuré le flux : son verdict sur le HDR prime sur l'annonce du fournisseur.
            Hdr = primary.FromPlayback ? primary.Hdr : primary.Hdr || fallback.Hdr,
            Fps = primary.Fps ?? fallback.Fps,
            AudioCodec = primary.AudioCodec ?? fallback.AudioCodec,
            AudioChannels = primary.AudioChannels ?? fallback.AudioChannels,
            AudioLanguages = primary.AudioLanguages.Count > 0 ? primary.AudioLanguages : fallback.AudioLanguages,
            SubtitleLanguages = primary.SubtitleLanguages.Count > 0 || primary.FromPlayback ? primary.SubtitleLanguages : fallback.SubtitleLanguages,
            BitrateKbps = primary.BitrateKbps ?? fallback.BitrateKbps,
            DurationSecs = primary.DurationSecs ?? fallback.DurationSecs,
            Container = primary.Container ?? fallback.Container,
            FromPlayback = primary.FromPlayback,
            Updated = primary.Updated,
        };
    }

    /// <summary>Synthèse d'une saison : la meilleure vidéo et le meilleur son disponibles (chacun tiré d'un même épisode,
    /// pour rester cohérent), et toutes les langues.</summary>
    public static MediaInfo? Summarize(IEnumerable<MediaInfo?> items)
    {
        var list = items.Where(m => m is { IsEmpty: false }).Select(m => m!).ToList();
        if (list.Count == 0) return null;
        static T? Most<T>(IEnumerable<T?> values) where T : class =>
            values.Where(v => v is not null).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var best = list.OrderByDescending(m => (long)(m.Width ?? 0) * (m.Height ?? 0)).ThenByDescending(m => m.Hdr).First();
        var sound = list.OrderByDescending(m => m.AudioChannels ?? 0).First();
        return new MediaInfo
        {
            Width = best.Width,
            Height = best.Height,
            VideoCodec = best.VideoCodec,
            Hdr = best.Hdr,
            AudioCodec = sound.AudioCodec,
            AudioChannels = sound.AudioChannels,
            AudioLanguages = list.SelectMany(m => m.AudioLanguages).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SubtitleLanguages = list.SelectMany(m => m.SubtitleLanguages).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Container = Most(list.Select(m => m.Container)),
            FromPlayback = list.All(m => m.FromPlayback),
        };
    }

    /// <summary>Champ « info » d'un film ou d'un épisode Xtream : objets « video » et « audio » (ffprobe), « bitrate »
    /// (kb/s) et « duration_secs ». Rend null si le fournisseur ne donne rien d'exploitable.</summary>
    public static MediaInfo? FromXtream(JsonElement info, string? container)
    {
        var m = new MediaInfo { DurationSecs = info.Int("duration_secs") is > 0 and var d ? d : null, Container = ContainerName(container) };
        var bitrate = info.Int("bitrate");
        if (bitrate is > 0) m.BitrateKbps = bitrate;
        if (info.Obj("video") is { ValueKind: JsonValueKind.Object } v)
        {
            m.Width = v.Int("width") is > 0 and var w ? w : null;
            m.Height = v.Int("height") is > 0 and var h ? h : null;
            m.VideoCodec = v.Str("codec_name");
            m.Hdr = v.Str("color_transfer") is "smpte2084" or "arib-std-b67";
            m.Fps = ParseRate(v.Str("avg_frame_rate")) ?? ParseRate(v.Str("r_frame_rate"));
        }
        if (info.Obj("audio") is { } a)
        {
            // Une piste (objet) le plus souvent ; certains panneaux en donnent une liste.
            var streams = a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new List<JsonElement> { a };
            foreach (var s in streams.Where(s => s.ValueKind == JsonValueKind.Object))
            {
                m.AudioCodec ??= s.Str("codec_name");
                m.AudioChannels ??= s.Int("channels") is > 0 and var ch ? ch : null;
                var lang = s.Obj("tags") is { } tags ? tags.Str("language", "LANGUAGE") : null;
                if (!string.IsNullOrWhiteSpace(lang) && lang != "und" && !m.AudioLanguages.Contains(lang)) m.AudioLanguages.Add(lang);
            }
        }
        // La durée et le format seuls ne font pas une fiche technique (déjà affichés ailleurs).
        return m.Height is null && m.VideoCodec is null && m.AudioCodec is null && m.AudioLanguages.Count == 0 && m.BitrateKbps is null ? null : m;
    }

    private static double? ParseRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return null;
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                              && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            return n / dd is > 1 and < 300 and var fps ? Math.Round(fps, 3) : null;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 1 and < 300 ? f : null;
    }

    /// <summary>Nom lisible d'un conteneur (extension Xtream ou format annoncé par mpv : « mov,mp4,m4a… » → MP4).</summary>
    public static string? ContainerName(string? f) => f?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        var x when x.StartsWith("mov,mp4", StringComparison.Ordinal) || x is "mp4" or "m4v" or "mov" => "MP4",
        var x when x.StartsWith("matroska", StringComparison.Ordinal) || x is "mkv" or "webm" => "MKV",
        "hls" or "applehttp" or "m3u8" => "HLS",
        "mpegts" or "ts" => "MPEG-TS",
        "avi" => "AVI",
        var x => x.Split(',')[0].ToUpperInvariant(),
    };
}

/// <summary>Informations relevées par le lecteur, par source et par élément (« Movie:123 », « Episode:456 »),
/// gardées dans le cache local : la fiche les montre ensuite sans relire le flux.</summary>
public static class MediaInfoCache
{
    private const int MaxEntries = 3000;
    private static readonly object Gate = new();
    private static Dictionary<string, MediaInfo>? _items;
    private static int _saveSerial;

    private static string FilePath => Path.Combine(AppPaths.Local, "cache", "mediainfo.json");

    private static string? Key(string itemKey) => Library.Instance.Source?.Id is { } src ? src + "|" + itemKey : null;

    private static Dictionary<string, MediaInfo> Items()
    {
        if (_items is not null) return _items;
        try
        {
            _items = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, MediaInfo>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Warn("Informations média illisibles : " + ex.Message);
            _items = new();
        }
        return _items;
    }

    public static MediaInfo? Get(string itemKey)
    {
        if (Key(itemKey) is not { } key) return null;
        lock (Gate) return Items().GetValueOrDefault(key);
    }

    public static void Put(string itemKey, MediaInfo info)
    {
        if (Key(itemKey) is not { } key) return;
        string json;
        int serial;
        lock (Gate)
        {
            var items = Items();
            items[key] = info;
            if (items.Count > MaxEntries)
                foreach (var old in items.OrderBy(kv => kv.Value.Updated).Take(items.Count - MaxEntries).Select(kv => kv.Key).ToList())
                    items.Remove(old);
            json = JsonSerializer.Serialize(items);
            serial = ++_saveSerial;
        }
        _ = Task.Run(() =>
        {
            try
            {
                lock (Gate) if (serial != _saveSerial) return; // une écriture plus récente suit
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn("Informations média non enregistrées : " + ex.Message);
            }
        });
    }
}
