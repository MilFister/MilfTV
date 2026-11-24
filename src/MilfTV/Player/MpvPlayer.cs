using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.Player;

/// <summary>Options appliquées à l'ouverture d'un média.</summary>
public sealed record MediaOpenOptions(
    bool IsLive,
    double StartSeconds = 0,
    string? UserAgent = null,
    string? Referrer = null);

/// <summary>Réglages globaux du moteur.</summary>
public sealed record PlayerEngineSettings(
    int TimeshiftMinutes,
    string HwDecode,
    bool HighQuality,
    string AudioLanguages,
    string SubtitleLanguages,
    bool SubtitlesByDefault,
    double Volume,
    string DefaultUserAgent,
    string AudioDevice = "auto");

public sealed class TrackInfo
{
    public long Id { get; init; }
    public string Type { get; init; } = "";
    public string? Lang { get; init; }
    public string? Title { get; init; }
    public string? Codec { get; init; }
    public bool Selected { get; init; }
    public bool External { get; init; }
    public int? Channels { get; init; }
    public long? Width { get; init; }
    public long? Height { get; init; }

    public string Label
    {
        get
        {
            var parts = new List<string>();
            var lang = LanguageName(Lang);
            if (!string.IsNullOrEmpty(lang)) parts.Add(lang);
            if (!string.IsNullOrWhiteSpace(Title) && !string.Equals(Title, Lang, StringComparison.OrdinalIgnoreCase)) parts.Add(Title!);
            if (Type == "audio")
            {
                if (!string.IsNullOrEmpty(Codec)) parts.Add(Codec!.ToUpperInvariant());
                if (Channels is > 0) parts.Add(Channels switch { 1 => Loc.T("Track.Mono"), 2 => Loc.T("Track.Stereo"), 6 => "5.1", 8 => "7.1", _ => Loc.T("Track.Channels", Channels) });
            }
            else if (Type == "sub" && !string.IsNullOrEmpty(Codec)) parts.Add(Codec!);
            return parts.Count == 0 ? Loc.T("Track.Number", Id) : string.Join(" · ", parts);
        }
    }

    public static string? LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        null or "" or "und" => null,
        "fr" or "fre" or "fra" or "fr-fr" => Loc.T("Lang.fr"),
        "en" or "eng" or "en-us" or "en-gb" => Loc.T("Lang.en"),
        "es" or "spa" => Loc.T("Lang.es"),
        "de" or "ger" or "deu" => Loc.T("Lang.de"),
        "it" or "ita" => Loc.T("Lang.it"),
        "pt" or "por" => Loc.T("Lang.pt"),
        "ar" or "ara" => Loc.T("Lang.ar"),
        "nl" or "dut" or "nld" => Loc.T("Lang.nl"),
        "tr" or "tur" => Loc.T("Lang.tr"),
        "ru" or "rus" => Loc.T("Lang.ru"),
        "ja" or "jpn" => Loc.T("Lang.ja"),
        "ko" or "kor" => Loc.T("Lang.ko"),
        "zh" or "chi" or "zho" => Loc.T("Lang.zh"),
        "pl" or "pol" => Loc.T("Lang.pl"),
        "qaa" => Loc.T("Track.OriginalVersion"),
        "qad" => Loc.T("Track.AudioDescription"),
        _ => code.ToUpperInvariant()
    };
}

/// <summary>Enveloppe d'une instance libmpv : options, commandes, propriétés observées.
/// Les valeurs continues (position, cache…) sont mises à jour par le thread d'événements et lues par l'interface.</summary>
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _handle;
    private Thread? _eventThread;
    private volatile bool _disposed;

    private static readonly string[] Observed =
    {
        "time-pos", "duration", "pause", "paused-for-cache", "core-idle", "idle-active", "eof-reached",
        "volume", "mute", "seekable", "track-list", "demuxer-cache-state", "video-params/w", "video-params/h",
        "video-codec", "estimated-vf-fps", "cache-buffering-state", "stream-record", "aid", "sid", "hwdec-current"
    };

    // État courant (écrit par le thread mpv)
    public double TimePos { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public bool PausedForCache { get; private set; }
    public bool CoreIdle { get; private set; } = true;
    public bool IdleActive { get; private set; } = true;
    public bool EofReached { get; private set; }
    public double Volume { get; private set; } = 100;
    public bool Mute { get; private set; }
    public bool Seekable { get; private set; }
    public long VideoWidth { get; private set; }
    public long VideoHeight { get; private set; }
    public string? VideoCodec { get; private set; }
    public double Fps { get; private set; }
    public long BufferingPercent { get; private set; }
    public string? HwDecodeCurrent { get; private set; }
    public string? RecordingPath { get; private set; }
    public double CacheStart { get; private set; } = double.NaN;
    public double CacheEnd { get; private set; } = double.NaN;
    public double CacheDuration { get; private set; }
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = Array.Empty<TrackInfo>();

    /// <summary>Incrémenté à chaque changement d'état, pour que l'interface sache quand rafraîchir.</summary>
    public int Version;

    public string? LastError { get; private set; }

    // Événements ponctuels, déclenchés sur le thread de l'interface
    public event Action? FileLoaded;
    /// <summary>Fin de fichier : erreur (null = fin normale), position et durée au moment de la fin
    /// (relevées sur le fil des événements, avant que le moteur ne les remette à zéro).</summary>
    public event Action<string?, double, double>? EndFile;
    public event Action? TracksChanged;
    public event Action? PlaybackRestarted;

    internal IntPtr Handle => _handle;
    public bool IsInitialized => _handle != IntPtr.Zero;

    private PlayerEngineSettings _settings = new(60, "auto-copy", true, "fr,fre,fra,en,eng", "fr,fre,fra", false, 100, Http.DefaultUserAgent);

    public void Initialize(PlayerEngineSettings settings)
    {
        _settings = settings;
        _handle = MpvApi.mpv_create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException(Loc.T("Player.EngineCreateFailed"));

        // Rendu par l'API libmpv (OpenGL dans l'interface)
        Opt("vo", "libmpv");
        Opt("idle", "yes");
        Opt("keep-open", "no");
        Opt("osc", "no");
        Opt("osd-level", "0");
        Opt("input-default-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("input-cursor", "no");
        Opt("ytdl", "no");
        Opt("terminal", "no");
        Opt("config", "no");
        Opt("load-scripts", "no");
        Opt("audio-client-name", "MilfTV");
        Opt("title", "MilfTV");
        Opt("volume-max", "150");
        Opt("hr-seek", "yes");
        Opt("sub-auto", "fuzzy");
        Opt("sub-font-size", "44");
        Opt("sub-border-size", "2.5");
        Opt("sub-shadow-offset", "1");
        Opt("vd-lavc-threads", "0");
        Opt("demuxer-lavf-analyzeduration", "2");
        Opt("network-timeout", "20");
        Opt("stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=5");
        Opt("cache", "yes");
        Opt("cache-pause-initial", "no");
        Opt("cache-pause-wait", "1.5");
        Opt("demuxer-seekable-cache", "yes");
        Opt("cache-on-disk", "yes");
        Opt("demuxer-cache-dir", AppPaths.PlayerCache);
        Opt("screenshot-directory", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        ApplyEngineSettings(settings, initial: true);

        var err = MpvApi.mpv_initialize(_handle);
        if (err < 0)
        {
            MpvApi.mpv_terminate_destroy(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException(Loc.T("Player.EngineInitFailed", MpvApi.ErrorText(err)));
        }

        MpvApi.mpv_request_log_messages(_handle, MpvApi.Utf8("warn"));

        for (int i = 0; i < Observed.Length; i++)
        {
            var fmt = Observed[i] switch
            {
                "time-pos" or "duration" or "volume" or "estimated-vf-fps" => MpvFormat.Double,
                "pause" or "paused-for-cache" or "core-idle" or "idle-active" or "eof-reached" or "mute" or "seekable" => MpvFormat.Flag,
                "video-params/w" or "video-params/h" or "cache-buffering-state" => MpvFormat.Int64,
                _ => MpvFormat.String
            };
            MpvApi.mpv_observe_property(_handle, (ulong)(i + 1), MpvApi.Utf8(Observed[i]), fmt);
        }

        var handle = _handle;
        _eventThread = new Thread(() => EventLoop(handle)) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
        Log.Info($"mpv initialisé (API client {MpvApi.mpv_client_api_version() >> 16}.{MpvApi.mpv_client_api_version() & 0xFFFF}, Vulkan : {MpvApi.VulkanPath() ?? "absent"})");
    }

    public void ApplyEngineSettings(PlayerEngineSettings s, bool initial = false)
    {
        _settings = s;
        Action<string, string> set = initial ? Opt : (n, v) => SetString(n, v);
        set("hwdec", string.IsNullOrWhiteSpace(s.HwDecode) ? "auto-copy" : s.HwDecode);
        set("alang", s.AudioLanguages);
        set("slang", s.SubtitleLanguages);
        set("sid", s.SubtitlesByDefault ? "auto" : "no");
        set("audio-device", string.IsNullOrWhiteSpace(s.AudioDevice) ? "auto" : s.AudioDevice);
        if (initial) set("volume", s.Volume.ToString(CultureInfo.InvariantCulture));
        if (s.HighQuality)
        {
            // spline36 : très proche d'ewa_lanczossharp à l'œil, mais 5 à 10 fois moins coûteux
            // (indispensable sur une carte graphique intégrée qui agrandit du 1080p en 4K).
            set("scale", "spline36");
            set("cscale", "spline36");
            set("dscale", "mitchell");
            set("correct-downscaling", "yes");
            set("linear-downscaling", "yes");
            set("sigmoid-upscaling", "yes");
            // Débandage : atténue les « marches » dans les dégradés, fréquentes sur les flux IPTV compressés.
            set("deband", "yes");
            set("dither-depth", "auto");
        }
        else
        {
            set("scale", "bilinear");
            set("cscale", "bilinear");
            set("dscale", "bilinear");
            set("correct-downscaling", "no");
            set("linear-downscaling", "no");
            set("sigmoid-upscaling", "no");
            set("deband", "no");
        }
        // Désentrelacement automatique (fréquent en IPTV : 1080i/576i)
        if (initial && MpvApi.mpv_set_option_string(_handle, MpvApi.Utf8("deinterlace"), MpvApi.Utf8("auto")) < 0)
            Opt("deinterlace", "no");
    }

    private void Opt(string name, string value)
    {
        var r = MpvApi.mpv_set_option_string(_handle, MpvApi.Utf8(name), MpvApi.Utf8(value));
        if (r < 0) Log.Warn($"mpv option {name}={value} refusée : {MpvApi.ErrorText(r)}");
    }

    public void SetString(string name, string value)
    {
        if (_handle == IntPtr.Zero) return;
        var r = MpvApi.mpv_set_property_string(_handle, MpvApi.Utf8(name), MpvApi.Utf8(value));
        if (r < 0) Log.Warn($"mpv propriété {name}={value} refusée : {MpvApi.ErrorText(r)}");
    }

    public void SetDouble(string name, double value)
    {
        if (_handle == IntPtr.Zero) return;
        MpvApi.mpv_set_property(_handle, MpvApi.Utf8(name), MpvFormat.Double, ref value);
    }

    public void SetFlag(string name, bool value) => SetString(name, value ? "yes" : "no");

    public string? GetString(string name)
    {
        if (_handle == IntPtr.Zero) return null;
        var p = MpvApi.mpv_get_property_string(_handle, MpvApi.Utf8(name));
        if (p == IntPtr.Zero) return null;
        try { return MpvApi.FromUtf8(p); }
        finally { MpvApi.mpv_free(p); }
    }

    public double? GetDouble(string name)
    {
        if (_handle == IntPtr.Zero) return null;
        return MpvApi.mpv_get_property(_handle, MpvApi.Utf8(name), MpvFormat.Double, out double v) >= 0 ? v : null;
    }

    public int Command(params string[] args)
    {
        if (_handle == IntPtr.Zero) return -1;
        var argv = MpvApi.AllocArgs(args);
        try
        {
            var r = MpvApi.mpv_command(_handle, argv);
            if (r < 0) Log.Warn($"mpv commande {args.FirstOrDefault()} : {MpvApi.ErrorText(r)}");
            return r;
        }
        finally { MpvApi.FreeArgs(argv); }
    }

    public void CommandAsync(params string[] args)
    {
        if (_handle == IntPtr.Zero) return;
        var argv = MpvApi.AllocArgs(args);
        try { MpvApi.mpv_command_async(_handle, 0, argv); }
        finally { MpvApi.FreeArgs(argv); }
    }

    // ───────────── Commandes de haut niveau ─────────────

    public void Open(string url, MediaOpenOptions o)
    {
        // Adresse venue d'une source (non fiable) : seuls les flux réseau et les fichiers locaux sont ouverts.
        // (Les tests automatisés ouvrent des mires « av://lavfi » : permis seulement avec les outils de test.)
        if (!StreamUrlPolicy.IsAllowed(url) && !(ViewModels.DevScript.IsEnabled && url.StartsWith("av://", StringComparison.Ordinal)))
        {
            Log.Warn("Adresse de flux refusée (partage réseau ou protocole non pris en charge)");
            throw new InvalidOperationException(Loc.T("Player.UnsafeUrl"));
        }
        LastError = null;
        CacheStart = CacheEnd = double.NaN;
        TimePos = 0;
        Duration = 0;
        SetString("user-agent", string.IsNullOrWhiteSpace(o.UserAgent) ? _settings.DefaultUserAgent : o.UserAgent!);
        SetString("referrer", o.Referrer ?? "");
        if (o.IsLive)
        {
            // Direct différé : on continue de remplir le cache (sur disque) pendant la pause.
            var minutes = Math.Clamp(_settings.TimeshiftMinutes, 5, 240);
            var mib = minutes * 60 * 2; // ≈ 16 Mbit/s
            try
            {
                // Tampon sur disque : jamais plus de 40 % de la place libre (petit SSD, disque presque plein).
                var free = new DriveInfo(Path.GetPathRoot(AppPaths.PlayerCache)!).AvailableFreeSpace / (1024 * 1024);
                mib = (int)Math.Max(256, Math.Min(mib, free * 0.4));
            }
            catch { /* lecteur inconnu : valeur par défaut */ }
            SetString("cache-secs", (minutes * 60).ToString(CultureInfo.InvariantCulture));
            SetString("demuxer-max-bytes", $"{mib}MiB");
            SetString("demuxer-max-back-bytes", $"{mib}MiB");
            SetString("force-seekable", "yes");
            SetString("demuxer-readahead-secs", "2");
        }
        else
        {
            SetString("cache-secs", "180");
            SetString("demuxer-max-bytes", "600MiB");
            SetString("demuxer-max-back-bytes", "300MiB");
            SetString("force-seekable", "no");
            SetString("demuxer-readahead-secs", "20");
        }
        SetString("start", o.StartSeconds > 1 ? o.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "none");
        SetString("pause", "no");
        var r = Command("loadfile", url, "replace");
        Interlocked.Increment(ref Version);
        if (r < 0) throw new InvalidOperationException(Loc.T("Player.OpenFailed", MpvApi.ErrorText(r)));
    }

    public void Stop()
    {
        StopRecording();
        Command("stop");
        Interlocked.Increment(ref Version);
    }

    public void TogglePause() => Command("cycle", "pause");
    public void SetPause(bool paused) => SetFlag("pause", paused);
    public void SeekRelative(double seconds) => Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "relative+exact");
    public void SeekAbsolute(double seconds) => Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
    public void SetVolume(double v) => SetDouble("volume", Math.Clamp(v, 0, 150));
    public void ToggleMute() => Command("cycle", "mute");
    public void SetAudioTrack(long id) => SetString("aid", id.ToString(CultureInfo.InvariantCulture));
    public void SetSubtitleTrack(long? id) => SetString("sid", id?.ToString(CultureInfo.InvariantCulture) ?? "no");
    public void SetAspect(string aspect) => SetString("video-aspect-override", aspect);
    public void SetSpeed(double speed) => SetDouble("speed", speed);

    /// <summary>Périphériques de sortie audio (nom technique mpv, description lisible).</summary>
    public IReadOnlyList<(string Name, string Description)> GetAudioDevices()
    {
        var json = GetString("audio-device-list");
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(json)) return list;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                var name = d.Str("name");
                if (string.IsNullOrEmpty(name)) continue;
                // On ne garde que la sortie automatique et les périphériques WASAPI (les autres sont des doublons).
                if (name != "auto" && !name.StartsWith("wasapi/", StringComparison.Ordinal)) continue;
                list.Add((name, d.Str("description") ?? name));
            }
        }
        catch { /* liste illisible */ }
        return list;
    }

    public void SetAudioDevice(string name) => SetString("audio-device", string.IsNullOrWhiteSpace(name) ? "auto" : name);

    /// <summary>Copie le flux reçu dans un fichier (sans réencodage). mpv choisit le conteneur d'après
    /// l'extension : .ts garde le flux d'origine, .mkv / .mp4 changent seulement d'enveloppe.</summary>
    public void StartRecording(string path)
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException(Loc.T("Rec.MpvUnavailable"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var r = MpvApi.mpv_set_property_string(_handle, MpvApi.Utf8("stream-record"), MpvApi.Utf8(path));
        if (r < 0) throw new InvalidOperationException(MpvApi.ErrorText(r));
        // Mise à jour immédiate (l'observation de la propriété arrive un peu plus tard par le thread mpv).
        RecordingPath = path;
        Interlocked.Increment(ref Version);
    }

    public void StopRecording()
    {
        if (string.IsNullOrEmpty(RecordingPath)) return;
        SetString("stream-record", "");
        RecordingPath = null;
        Interlocked.Increment(ref Version);
    }

    // ───────────── Boucle d'événements ─────────────

    private void EventLoop(IntPtr handle)
    {
        while (!_disposed)
        {
            var evp = MpvApi.mpv_wait_event(handle, 0.5);
            if (evp == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<MpvEvent>(evp);
            try
            {
                switch (ev.EventId)
                {
                    case MpvEventId.None:
                        break;
                    case MpvEventId.Shutdown:
                        return;
                    case MpvEventId.PropertyChange:
                        OnProperty(Marshal.PtrToStructure<MpvEventProperty>(ev.Data));
                        break;
                    case MpvEventId.FileLoaded:
                        Post(() => FileLoaded?.Invoke());
                        break;
                    case MpvEventId.PlaybackRestart:
                        Post(() => PlaybackRestarted?.Invoke());
                        break;
                    case MpvEventId.EndFile:
                        var ef = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        var endPos = TimePos;
                        var endDuration = Duration;
                        if (ef.Reason == (int)MpvEndFileReason.Error)
                        {
                            var error = LastError = MpvApi.ErrorText(ef.Error);
                            Log.Warn("Fin de lecture sur erreur : " + error);
                            Post(() => EndFile?.Invoke(error, endPos, endDuration));
                        }
                        else if (ef.Reason == (int)MpvEndFileReason.Eof)
                        {
                            Post(() => EndFile?.Invoke(null, endPos, endDuration));
                        }
                        break;
                    case MpvEventId.LogMessage:
                        var lm = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                        Log.Warn($"mpv[{MpvApi.FromUtf8(lm.Prefix)}] {MpvApi.FromUtf8(lm.Text)?.TrimEnd()}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Erreur dans la boucle mpv", ex);
            }
        }
    }

    private void OnProperty(MpvEventProperty p)
    {
        var name = MpvApi.FromUtf8(p.Name);
        if (name is null) return;
        bool none = p.Format == MpvFormat.None || p.Data == IntPtr.Zero;
        double D() => none ? 0 : Marshal.PtrToStructure<double>(p.Data);
        bool F() => !none && Marshal.ReadInt32(p.Data) != 0;
        long L() => none ? 0 : Marshal.ReadInt64(p.Data);
        string? S() => none ? null : MpvApi.FromUtf8(Marshal.ReadIntPtr(p.Data));

        switch (name)
        {
            case "time-pos": TimePos = D(); break;
            case "duration": Duration = D(); break;
            case "pause": Paused = F(); break;
            case "paused-for-cache": PausedForCache = F(); break;
            case "core-idle": CoreIdle = F(); break;
            case "idle-active": IdleActive = F(); break;
            case "eof-reached": EofReached = F(); break;
            case "volume": Volume = D(); break;
            case "mute": Mute = F(); break;
            case "seekable": Seekable = F(); break;
            case "video-params/w": VideoWidth = L(); break;
            case "video-params/h": VideoHeight = L(); break;
            case "video-codec": VideoCodec = S(); break;
            case "estimated-vf-fps": Fps = D(); break;
            case "cache-buffering-state": BufferingPercent = L(); break;
            case "hwdec-current": HwDecodeCurrent = S(); break;
            case "stream-record":
                var rec = S();
                RecordingPath = string.IsNullOrEmpty(rec) ? null : rec;
                break;
            case "track-list":
                Tracks = ParseTracks(S());
                Post(() => TracksChanged?.Invoke());
                break;
            case "aid":
            case "sid":
                Tracks = ParseTracks(GetString("track-list"));
                Post(() => TracksChanged?.Invoke());
                break;
            case "demuxer-cache-state":
                ParseCache(S());
                break;
        }
        Interlocked.Increment(ref Version);
    }

    private static IReadOnlyList<TrackInfo> ParseTracks(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<TrackInfo>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var list = new List<TrackInfo>();
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                list.Add(new TrackInfo
                {
                    Id = t.Long("id") ?? 0,
                    Type = t.Str("type") ?? "",
                    Lang = t.Str("lang"),
                    Title = t.Str("title"),
                    Codec = t.Str("codec"),
                    Selected = t.Str("selected") == "1",
                    External = t.Str("external") == "1",
                    Channels = t.Int("demux-channel-count") ?? t.Int("audio-channels"),
                    Width = t.Long("demux-w"),
                    Height = t.Long("demux-h"),
                });
            }
            return list;
        }
        catch
        {
            return Array.Empty<TrackInfo>();
        }
    }

    private void ParseCache(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            CacheStart = CacheEnd = double.NaN;
            CacheDuration = 0;
            return;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            CacheDuration = root.Dbl("cache-duration") ?? 0;
            double start = double.NaN, end = double.NaN;
            if (root.TryGetProperty("seekable-ranges", out var ranges) && ranges.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var r in ranges.EnumerateArray())
                {
                    var s = r.Dbl("start");
                    var e = r.Dbl("end");
                    if (s is null || e is null) continue;
                    // La plage qui contient la position courante a priorité ; sinon, la plus grande.
                    var contains = s <= TimePos && TimePos <= e + 1;
                    var currentContains = !double.IsNaN(start) && start <= TimePos && TimePos <= end + 1;
                    if (double.IsNaN(start) || (contains && !currentContains) || (contains == currentContains && e - s > end - start))
                    {
                        start = s.Value;
                        end = e.Value;
                    }
                }
            }
            if (double.IsNaN(end)) end = root.Dbl("cache-end") ?? double.NaN;
            CacheStart = start;
            CacheEnd = end;
        }
        catch
        {
            // ignoré : état de cache illisible
        }
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var h = _handle;
        _handle = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            MpvApi.mpv_wakeup(h);
            if (_eventThread is null || _eventThread.Join(3000)) MpvApi.mpv_terminate_destroy(h);
            else Log.Warn("Fil des événements mpv bloqué : moteur laissé à la fermeture du processus");
        }
    }
}
