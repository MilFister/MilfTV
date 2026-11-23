using System.Collections.ObjectModel;
using MilfTV.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Player;

namespace MilfTV.ViewModels;

public sealed partial class TrackOption : ObservableObject
{
    public long? Id { get; init; }
    public string? DeviceName { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Une ligne du panneau « Informations sur la lecture ».</summary>
public sealed partial class StatRow : ObservableObject
{
    public string Key { get; init; } = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "";
}

/// <summary>Lecteur plein écran : chaînes en direct (avec direct différé), films, épisodes.</summary>
public sealed partial class PlayerViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private int _lastVersion = -1;
    private DateTime _lastSave = DateTime.MinValue;
    private CancellationTokenSource? _openCts;
    private DateTime _lastActivity = DateTime.UtcNow;
    /// <summary>Vrai quand l'utilisateur a mis en pause ou reculé dans le direct.</summary>
    private bool _timeshifted;

    public MpvPlayer Engine { get; } = new();
    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFullView), nameof(ShowsApp))] private bool _isOpen;
    /// <summary>Lecture réduite dans le mini-lecteur flottant : l'application reste utilisable dessous.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFullView), nameof(ShowsApp))] private bool _isMini;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private string? _logoUrl;
    [ObservableProperty] private string? _nowTitle;
    [ObservableProperty] private string? _nowTime;
    [ObservableProperty] private double _nowProgress;
    [ObservableProperty] private string? _nextTitle;
    [ObservableProperty] private string? _nowDescription;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _rangeStart;
    [ObservableProperty] private double _rangeEnd;
    [ObservableProperty] private string _positionText = "00:00";
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private bool _isAtLiveEdge = true;
    [ObservableProperty] private string? _behindLiveText;
    [ObservableProperty] private double _volume = 100;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string? _videoInfo;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _recordingText;
    [ObservableProperty] private bool _controlsVisible = true;
    [ObservableProperty] private bool _isSidePanelOpen;
    [ObservableProperty] private bool _isTracksPanelOpen;
    [ObservableProperty] private string _aspectLabel = Loc.T("Player.AspectAuto");
    [ObservableProperty] private int _nextEpisodeCountdown;
    [ObservableProperty] private Episode? _nextEpisode;
    [ObservableProperty] private string? _osdMessage;

    public ObservableCollection<TrackOption> AudioTracks { get; } = new();
    public ObservableCollection<TrackOption> SubtitleTracks { get; } = new();
    public ObservableCollection<TrackOption> AudioDevices { get; } = new();

    // Liste latérale des chaînes : catégorie et recherche propres au lecteur
    public ObservableCollection<Category> PanelCategories { get; } = new();
    [ObservableProperty] private Category? _panelCategory;
    [ObservableProperty] private string _panelFilter = "";
    [ObservableProperty] private IReadOnlyList<LiveChannel> _panelChannels = Array.Empty<LiveChannel>();
    [ObservableProperty] private LiveChannel? _panelSelection;
    public ObservableCollection<LiveChannel> ZapChannels { get; } = new();
    public ObservableCollection<Episode> ZapEpisodes { get; } = new();

    [ObservableProperty] private LiveChannel? _currentChannel;
    [ObservableProperty] private Movie? _currentMovie;
    [ObservableProperty] private Series? _currentSeries;
    [ObservableProperty] private Episode? _currentEpisode;

    public bool HasEpisodes => ZapEpisodes.Count > 0;
    public bool IsVod => !IsLive;

    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(IsVod));

    private static readonly string[] Aspects = { "no", "16:9", "4:3", "21:9", "2.35:1" };
    private int _aspectIndex;

    public PlayerViewModel(MainViewModel main)
    {
        _main = main;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick());
        Engine.EndFile += OnEndFile;
        Engine.FileLoaded += OnFileLoaded;
        Engine.TracksChanged += RefreshTracks;
        Engine.PlaybackRestarted += () => IsLoading = false;
    }

    public void InitializeEngine()
    {
        var s = SettingsStore.Current;
        Engine.Initialize(EngineSettings(s));
        Volume = s.Volume;
    }

    public static PlayerEngineSettings EngineSettings(AppSettings s) => new(
        s.TimeshiftMinutes, s.HwDecode, s.HighQuality, s.AudioLanguages, s.SubtitleLanguages,
        s.SubtitlesByDefault, s.Volume, string.IsNullOrWhiteSpace(s.DefaultUserAgent) ? Http.DefaultUserAgent : s.DefaultUserAgent!,
        string.IsNullOrWhiteSpace(s.AudioDevice) ? "auto" : s.AudioDevice);

    // ───────────── Ouverture ─────────────

    public async Task PlayChannelAsync(LiveChannel ch, IEnumerable<LiveChannel>? zapList = null)
    {
        SaveProgress(final: true);
        if (zapList is not null)
        {
            ZapChannels.Clear();
            foreach (var c in zapList) ZapChannels.Add(c);
        }
        if (!ZapChannels.Contains(ch)) { ZapChannels.Clear(); ZapChannels.Add(ch); }
        ZapEpisodes.Clear();
        OnPropertyChanged(nameof(HasEpisodes));

        if (CurrentChannel is not null) CurrentChannel.IsPlaying = false;
        CurrentChannel = ch;
        ch.IsPlaying = true;
        CurrentMovie = null;
        CurrentSeries = null;
        CurrentEpisode = null;
        IsLive = true;
        Title = ch.Name;
        Subtitle = ch.Number > 0 ? Loc.T("Player.ChannelNumber", ch.Number) : null;
        LogoUrl = ch.ImageUrl;
        await OpenAsync(ch, null, new MediaOpenOptions(true));
        _ = RefreshNowAsync();

        Lib.SaveWatch(new WatchEntry
        {
            Key = ch.Key, Kind = ContentKind.Live, ItemId = ch.Id, Title = ch.Name, ImageUrl = ch.ImageUrl,
        });
    }

    public async Task PlayMovieAsync(Movie m, bool fromStart = false)
    {
        SaveProgress(final: true);
        ResetForVod();
        CurrentMovie = m;
        Title = TextNorm.SplitYear(m.Name).Title;
        Subtitle = string.Join(" · ", new[] { m.YearText, m.Genre }.Where(x => !string.IsNullOrEmpty(x)));
        LogoUrl = m.ImageUrl;
        var resume = fromStart ? null : Lib.GetWatch(m.Key);
        var start = Lib.Prefs.ResumePlayback && resume is { Watched: false, Position: > 30 } ? resume.Position - 3 : 0;
        await OpenAsync(m, null, new MediaOpenOptions(false, start));
    }

    public async Task PlayEpisodeAsync(Series s, Episode e, IEnumerable<Episode> seasonEpisodes, bool fromStart = false)
    {
        SaveProgress(final: true);
        ResetForVod();
        ZapEpisodes.Clear();
        foreach (var x in seasonEpisodes) ZapEpisodes.Add(x);
        OnPropertyChanged(nameof(HasEpisodes));
        if (CurrentEpisode is not null) CurrentEpisode.IsCurrent = false;
        CurrentSeries = s;
        CurrentEpisode = e;
        e.IsCurrent = true;
        Title = TextNorm.StripPrefix(s.Name);
        Subtitle = string.IsNullOrWhiteSpace(e.Title) ? e.Code : $"{e.Code} · {e.Title}";
        LogoUrl = s.ImageUrl;
        var resume = fromStart ? null : Lib.GetWatch(e.Key);
        var start = Lib.Prefs.ResumePlayback && resume is { Watched: false, Position: > 30 } ? resume.Position - 3 : 0;
        await OpenAsync(s, e, new MediaOpenOptions(false, start));
    }

    /// <summary>Lecture d'un fichier local (enregistrement).</summary>
    public void PlayFile(string path, string title)
    {
        SaveProgress(final: true);
        // stream-record s'applique à tout fichier ouvert : sans arrêt, l'enregistrement en cours serait écrasé.
        if (_recordingEntry is not null || Engine.RecordingPath is not null) StopRecording();
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        _openPending = true;
        ResetForVod();
        Title = title;
        Subtitle = Loc.T("Player.Recording");
        LogoUrl = null;
        Show();
        ErrorMessage = null;
        IsLoading = true;
        _ = OpenFileWhenReadyAsync(path, cts);
    }

    private async Task OpenFileWhenReadyAsync(string path, CancellationTokenSource cts)
    {
        await MpvVideoView.WaitReadyAsync(2000);
        if (cts.IsCancellationRequested || !IsOpen || !EnsureEngine()) return;
        try
        {
            _openPending = false;
            Engine.Open(path, new MediaOpenOptions(false));
        }
        catch (Exception ex)
        {
            Log.Error("Ouverture du fichier", ex);
            IsLoading = false;
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Une ouverture est en cours : les fins de fichier qui arrivent concernent l'ancien média.</summary>
    private bool _openPending;
    private bool _noVideoWarned;

    /// <summary>Moteur de lecture indisponible (libmpv absent, composant système manquant) : message clair
    /// au lieu d'un chargement sans fin. Sortie vidéo absente : avertissement unique, la lecture continue.</summary>
    private bool EnsureEngine()
    {
        if (!Engine.IsInitialized)
        {
            IsLoading = false;
            ErrorMessage = Loc.T("Player.EngineMissing");
            return false;
        }
        if (!MpvVideoView.IsReady && !_noVideoWarned)
        {
            _noVideoWarned = true;
            Log.Warn("Surface vidéo indisponible (OpenGL) : lecture sans image");
            _main.Toast(Loc.T("Player.NoVideoOutput"), Loc.T("Player.NoVideoOutputMsg"), true);
        }
        return true;
    }

    private void ResetForVod()
    {
        if (CurrentChannel is not null) CurrentChannel.IsPlaying = false;
        CurrentChannel = null;
        CurrentMovie = null;
        CurrentSeries = null;
        CurrentEpisode = null;
        IsLive = false;
        NowTitle = NowTime = NextTitle = NowDescription = null;
        BehindLiveText = null;
        NextEpisode = null;
        NextEpisodeCountdown = 0;
        ZapChannels.Clear();
    }

    private async Task OpenAsync(CatalogItem item, Episode? episode, MediaOpenOptions opts)
    {
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        _openPending = true;
        Show();
        ErrorMessage = null;
        IsLoading = true;
        IsTracksPanelOpen = false;
        if (!IsLive) IsSidePanelOpen = IsSidePanelOpen && item is Series;
        NextEpisode = null;
        NextEpisodeCountdown = 0;
        _timeshifted = false;
        try
        {
            if (Engine.RecordingPath is not null || _recordingEntry is not null) StopRecording();
            var info = await Lib.ResolveAsync(item, episode, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(info.Url)) throw new InvalidOperationException(Loc.T("Player.EmptyUrl"));
            await MpvVideoView.WaitReadyAsync(2000);
            if (cts.IsCancellationRequested || !EnsureEngine()) return;
            Log.Info($"Lecture : {Title} ({(opts.IsLive ? "direct" : "VOD")})");
            // La vitesse de lecture de mpv persiste d'un fichier à l'autre : chaque nouvelle lecture repart à ×1.
            if (PlaybackSpeed != 1)
            {
                PlaybackSpeed = 1;
                Engine.SetSpeed(1);
            }
            _openPending = false;
            Engine.Open(info.Url, opts with { UserAgent = info.UserAgent ?? opts.UserAgent, Referrer = info.Referrer });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Ouverture remplacée par une autre entre-temps : son erreur ne concerne plus la lecture en cours.
            if (cts.IsCancellationRequested || !ReferenceEquals(_openCts, cts)) return;
            _openPending = false;
            Log.Error("Ouverture du flux", ex);
            IsLoading = false;
            ErrorMessage = ex.Message;
        }
    }

    private void Show()
    {
        if (!IsOpen)
        {
            IsOpen = true;
            _timer.Start();
        }
        ControlsVisible = true;
        _lastActivity = DateTime.UtcNow;
        _main.OnPlayerOpened();
    }

    // ───────────── Événements du moteur ─────────────

    private void OnFileLoaded()
    {
        IsLoading = false;
        ErrorMessage = null;
        RefreshTracks();
        if (Volume != Engine.Volume) Engine.SetVolume(Volume);
    }

    private int _liveRetries;
    private DateTime _liveRetryWindow = DateTime.MinValue;

    private void OnEndFile(string? error, double endPos, double endDuration)
    {
        // Fin de l'ancien média pendant qu'un autre s'ouvre (épisode suivant cliqué pendant le générique…) : sans objet.
        if (!IsOpen || _openPending) return;
        if (error is not null)
        {
            IsLoading = false;
            ErrorMessage = IsLive
                ? Loc.T("Player.LiveError")
                : Loc.T("Player.VodError", error);
            return;
        }
        if (IsLive)
        {
            // Un flux direct ne devrait pas se terminer : on relance, avec un délai croissant et une limite
            // (sinon boucle sans fin sur un message « abonnement expiré » ou une playlist terminée).
            if (DateTime.UtcNow - _liveRetryWindow > TimeSpan.FromMinutes(2))
            {
                _liveRetryWindow = DateTime.UtcNow;
                _liveRetries = 0;
            }
            if (++_liveRetries > 5)
            {
                IsLoading = false;
                ErrorMessage = Loc.T("Player.LiveError");
                return;
            }
            ErrorMessage = null;
            if (CurrentChannel is { } ch) _ = RetryLiveAsync(ch, TimeSpan.FromSeconds(Math.Min(16, 1 << (_liveRetries - 1))));
            return;
        }
        SaveProgress(final: true, ended: true, endPos, endDuration);
        if (CurrentEpisode is { } ep)
        {
            var idx = ZapEpisodes.IndexOf(ep);
            if (idx >= 0 && idx + 1 < ZapEpisodes.Count)
            {
                var prefs = Lib.Prefs;
                NextEpisode = ZapEpisodes[idx + 1];
                // « Toujours là ? » après N épisodes enchaînés automatiquement.
                StillWatchingPrompt = prefs.AutoPlayNext && prefs.AutoPlayMaxEpisodes > 0 && _autoChain >= prefs.AutoPlayMaxEpisodes;
                NextEpisodeCountdown = prefs.AutoPlayNext && !StillWatchingPrompt ? Math.Clamp(prefs.AutoPlayCountdown, 1, 60) : 0;
                OnPropertyChanged(nameof(NextEpisodeText));
                return;
            }
        }
        Close();
    }

    private async Task RetryLiveAsync(LiveChannel ch, TimeSpan delay)
    {
        IsLoading = true;
        await Task.Delay(delay);
        if (IsOpen && ReferenceEquals(CurrentChannel, ch) && !_openPending) await PlayChannelAsync(ch);
    }

    private void RefreshTracks()
    {
        var tracks = Engine.Tracks;
        AudioTracks.Clear();
        foreach (var t in tracks.Where(t => t.Type == "audio"))
            AudioTracks.Add(new TrackOption { Id = t.Id, Label = t.Label, IsSelected = t.Selected });
        SubtitleTracks.Clear();
        var subs = tracks.Where(t => t.Type == "sub").ToList();
        SubtitleTracks.Add(new TrackOption { Id = null, Label = Loc.T("Player.SubsOff"), IsSelected = !subs.Any(s => s.Selected) });
        foreach (var t in subs)
            SubtitleTracks.Add(new TrackOption { Id = t.Id, Label = t.Label, IsSelected = t.Selected });
    }

    private int _tickCount;

    private void Tick()
    {
        if (!IsOpen) return;
        _tickCount++;

        // compte à rebours de l'épisode suivant
        if (NextEpisodeCountdown > 0 && _tickCount % 5 == 0)
        {
            NextEpisodeCountdown--;
            if (NextEpisodeCountdown == 0 && NextEpisode is not null)
            {
                _autoChain++;
                StartNextEpisode();
            }
        }

        // masquage automatique des contrôles
        var idle = (DateTime.UtcNow - _lastActivity).TotalSeconds;
        var keep = IsPaused || IsSidePanelOpen || IsTracksPanelOpen || IsMenuOpen || IsStatsOpen || ErrorMessage is not null || IsLoading;
        if (IsStatsOpen && _tickCount % 5 == 0) UpdateStats();
        if (ControlsVisible && idle > 3.5 && !keep) ControlsVisible = false;

        // enregistrement immédiat : arrêt automatique, repli en .ts, espace disque
        TickRecording();

        var v = Engine.Version;
        if (v == _lastVersion) return;
        _lastVersion = v;

        IsPaused = Engine.Paused;
        IsBuffering = (Engine.PausedForCache || (Engine.CoreIdle && !Engine.Paused && !Engine.IdleActive)) && ErrorMessage is null;
        IsMuted = Engine.Mute;
        IsRecording = Engine.RecordingPath is not null;
        if (Engine.VideoHeight > 0)
        {
            var q = QualityLabel(Engine.VideoWidth, Engine.VideoHeight);
            var fps = Engine.Fps > 1 ? Loc.T("Player.Fps", Math.Round(Engine.Fps)) : "";
            var codec = string.IsNullOrEmpty(Engine.VideoCodec) ? "" : " · " + ShortCodec(Engine.VideoCodec);
            VideoInfo = q + fps + codec;
        }

        var pos = Engine.TimePos;
        if (IsLive)
        {
            var start = Engine.CacheStart;
            var end = Engine.CacheEnd;
            if (double.IsNaN(start) || double.IsNaN(end) || end <= start)
            {
                RangeStart = 0; RangeEnd = 1; Position = 1;
                IsAtLiveEdge = true;
                BehindLiveText = null;
                PositionText = "";
                DurationText = "";
            }
            else
            {
                RangeStart = start;
                RangeEnd = Math.Max(end, pos);
                Position = Math.Clamp(pos, RangeStart, RangeEnd);
                var behind = RangeEnd - Position;
                if (_timeshifted && behind < 3 && !IsPaused) _timeshifted = false;
                IsAtLiveEdge = !_timeshifted && !IsPaused;
                BehindLiveText = IsAtLiveEdge ? null : Loc.T("Player.Delayed", "−" + TextNorm.Clock(behind));
                var buffered = RangeEnd - RangeStart;
                PositionText = buffered >= 60 ? Loc.T("Player.Rewind", TextNorm.Clock(buffered)) : "";
                DurationText = "";
            }
            if (_tickCount % 25 == 0) UpdateNowFromCache();
        }
        else
        {
            Duration = Engine.Duration;
            BehindLiveText = null;
            IsAtLiveEdge = true;
            RangeStart = 0;
            RangeEnd = Math.Max(1, Duration);
            Position = Math.Clamp(pos, 0, RangeEnd);
            PositionText = TextNorm.Clock(pos);
            DurationText = Duration > 0 ? TextNorm.Clock(Duration) : "";
            if ((DateTime.UtcNow - _lastSave).TotalSeconds > 10) SaveProgress(final: false);
        }

        if (IsRecording)
        {
            try
            {
                var fi = new FileInfo(Engine.RecordingPath!);
                RecordingText = fi.Exists ? Loc.T("Player.RecSize", fi.Length / 1048576.0) : "REC";
            }
            catch { RecordingText = "REC"; }
        }
    }

    /// <summary>Classe de définition d'après la largeur OU la hauteur : les films au format cinéma sont moins hauts
    /// que leur classe (3840×1600 = 4K, 1920×800 = 1080p), et les sources HDV moins larges (1440×1080 = 1080p).</summary>
    internal static string QualityLabel(long w, long h) =>
        w >= 3200 || h >= 2000 ? "4K"
        : w >= 2400 || h >= 1350 ? "1440p"
        : w >= 1700 || h >= 1000 ? "1080p"
        : w >= 1200 || h >= 700 ? "720p"
        : $"{h}p";

    private static string ShortCodec(string codec)
    {
        var c = codec.ToLowerInvariant();
        if (c.Contains("hevc") || c.Contains("h.265") || c.Contains("h265")) return "HEVC";
        if (c.Contains("h264") || c.Contains("h.264") || c.Contains("avc")) return "H.264";
        if (c.Contains("av1")) return "AV1";
        if (c.Contains("mpeg2") || c.Contains("mpeg-2")) return "MPEG-2";
        if (c.Contains("vp9")) return "VP9";
        return codec.Split(' ', '(')[0].ToUpperInvariant();
    }

    // ───────────── Guide TV de la chaîne en cours ─────────────

    private List<EpgProgram> _channelPrograms = new();

    private async Task RefreshNowAsync()
    {
        var ch = CurrentChannel;
        if (ch is null) return;
        try
        {
            var now = DateTimeOffset.Now;
            _channelPrograms = await Lib.GetChannelProgramsAsync(ch, now.AddHours(-6), now.AddHours(24));
            if (CurrentChannel == ch) UpdateNowFromCache();
        }
        catch (Exception ex)
        {
            Log.Warn("EPG du lecteur : " + ex.Message);
        }
    }

    private void UpdateNowFromCache()
    {
        if (!IsLive) return;
        // En différé, on affiche le programme correspondant à l'instant regardé.
        var behind = IsAtLiveEdge ? 0 : RangeEnd - Position;
        var t = DateTimeOffset.Now.AddSeconds(-behind);
        var now = _channelPrograms.FirstOrDefault(p => p.IsLiveAt(t));
        var next = now is null ? _channelPrograms.FirstOrDefault(p => p.Start > t) : _channelPrograms.FirstOrDefault(p => p.Start >= now.End);
        NowTitle = now?.Title;
        NowTime = now?.TimeRange;
        NowProgress = now?.ProgressAt(t) ?? 0;
        NowDescription = now?.Description;
        NextTitle = next is null ? null : Loc.T("Player.Next", next.Start.ToLocalTime(), next.Title);
    }

    // ───────────── Progression / reprise ─────────────

    private void SaveProgress(bool final, bool ended = false, double? endPos = null, double? endDuration = null)
    {
        _lastSave = DateTime.UtcNow;
        if (IsLive || !IsOpen || _openPending) return;
        var pos = endPos ?? Engine.TimePos;
        var dur = endDuration is > 0 ? endDuration.Value : Engine.Duration;
        if (ended && dur > 0) pos = Math.Max(pos, dur);
        if (dur <= 0 || (pos < 5 && !ended)) return;
        var watched = ended || pos / dur * 100 >= Math.Clamp(Lib.Prefs.WatchedThresholdPercent, 50, 100);
        if (CurrentEpisode is { } ep && CurrentSeries is { } s)
        {
            Lib.SaveWatch(new WatchEntry
            {
                Key = ep.Key, Kind = ContentKind.Episode, ItemId = ep.Id, ParentId = s.Id,
                Title = TextNorm.StripPrefix(s.Name), Subtitle = Subtitle, ImageUrl = ep.ImageUrl ?? s.ImageUrl,
                Position = watched ? dur : pos, Duration = dur, Watched = watched,
            });
            ep.Progress = watched ? 100 : pos / dur * 100;
            ep.IsWatched = watched;
        }
        else if (CurrentMovie is { } m)
        {
            Lib.SaveWatch(new WatchEntry
            {
                Key = m.Key, Kind = ContentKind.Movie, ItemId = m.Id, Title = TextNorm.StripPrefix(m.Name),
                Subtitle = m.YearText, ImageUrl = m.ImageUrl, Position = watched ? dur : pos, Duration = dur, Watched = watched,
            });
        }
        if (final) _main.OnWatchChanged();
    }

    // ───────────── Commandes ─────────────

    partial void OnControlsVisibleChanged(bool value)
    {
        if (Engine.IsInitialized) Engine.SetString("sub-margin-y", value ? "120" : "34");
    }

    public void NotifyActivity()
    {
        _lastActivity = DateTime.UtcNow;
        if (!ControlsVisible) ControlsVisible = true;
    }

    [RelayCommand]
    private void TogglePause()
    {
        NotifyActivity();
        if (IsLive && !Engine.Paused) _timeshifted = true;
        Engine.TogglePause();
    }

    [RelayCommand]
    private void SeekBy(string seconds)
    {
        NotifyActivity();
        if (!double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) return;
        if (IsLive)
        {
            // Cache encore vide (juste après un changement de chaîne) : on laisse mpv gérer le saut relatif.
            if (RangeEnd - RangeStart < 2)
            {
                Engine.SeekRelative(s);
                return;
            }
            var target = Math.Clamp(Engine.TimePos + s, RangeStart, Math.Max(RangeStart, RangeEnd - 1));
            _timeshifted = target < RangeEnd - 4;
            Engine.SeekAbsolute(target);
            Flash(s < 0 ? Loc.T("Player.BackSeconds", -s) : Loc.T("Player.ForwardSeconds", s));
        }
        else
        {
            Engine.SeekRelative(s);
        }
    }

    public void SeekTo(double value)
    {
        NotifyActivity();
        if (IsLive)
        {
            if (RangeEnd - RangeStart < 2) return;
            value = Math.Clamp(value, RangeStart, Math.Max(RangeStart, RangeEnd - 1));
            _timeshifted = value < RangeEnd - 4;
        }
        Engine.SeekAbsolute(value);
    }

    [RelayCommand]
    private void GoLive()
    {
        NotifyActivity();
        if (!IsLive) return;
        _timeshifted = false;
        if (IsPaused) Engine.SetPause(false);
        if (!double.IsNaN(Engine.CacheEnd)) Engine.SeekAbsolute(Engine.CacheEnd - 1);
        Flash(Loc.T("Player.BackToLive"));
    }

    partial void OnVolumeChanged(double value)
    {
        if (Engine.IsInitialized && Math.Abs(Engine.Volume - value) > 0.5) Engine.SetVolume(value);
        SettingsStore.Current.Volume = value;
        SettingsStore.Save();
    }

    [RelayCommand]
    private void ToggleMute()
    {
        NotifyActivity();
        Engine.ToggleMute();
    }

    public void ChangeVolume(double delta)
    {
        NotifyActivity();
        Volume = Math.Clamp(Volume + delta, 0, 150);
        Flash(Loc.T("Player.Volume", Volume));
    }

    [RelayCommand]
    private void SelectAudio(TrackOption t)
    {
        if (t.Id is { } id) Engine.SetAudioTrack(id);
        foreach (var a in AudioTracks) a.IsSelected = a == t;
    }

    [RelayCommand]
    private void SelectSubtitle(TrackOption t)
    {
        Engine.SetSubtitleTrack(t.Id);
        foreach (var a in SubtitleTracks) a.IsSelected = a == t;
    }

    /// <summary>Formats d'image proposés (« no » = format d'origine).</summary>
    public static IReadOnlyList<string> AspectChoices => Aspects;
    public string CurrentAspect => Aspects[_aspectIndex];

    [RelayCommand]
    private void SetAspect(string ratio)
    {
        var i = Array.IndexOf(Aspects, ratio);
        if (i < 0) return;
        _aspectIndex = i;
        Engine.SetAspect(Aspects[i]);
        AspectLabel = i == 0 ? Loc.T("Player.AspectAuto") : Aspects[i];
        Flash(Loc.T("Player.Aspect", AspectLabel));
    }

    // ───────────── Vitesse de lecture (films et séries) ─────────────

    public static readonly double[] SpeedChoices = { 0.5, 0.75, 1, 1.25, 1.5, 2 };
    [ObservableProperty] private double _playbackSpeed = 1;

    [RelayCommand]
    private void SetSpeed(double speed)
    {
        if (IsLive) return;
        PlaybackSpeed = speed;
        Engine.SetSpeed(speed);
        Flash(Loc.T("Ctx.SpeedSet", SpeedLabel(speed)));
    }

    public static string SpeedLabel(double s) => s == 1 ? Loc.T("Ctx.SpeedNormal") : "×" + s.ToString("0.##", Loc.Culture);

    // ───────────── Menu du clic droit et informations sur la lecture ─────────────

    /// <summary>Menu contextuel ouvert : les commandes restent affichées.</summary>
    [ObservableProperty] private bool _isMenuOpen;
    [ObservableProperty] private bool _isStatsOpen;
    public ObservableCollection<StatRow> Stats { get; } = new();

    [RelayCommand]
    private void ToggleStats()
    {
        IsStatsOpen = !IsStatsOpen;
        if (IsStatsOpen)
        {
            if (AudioDevices.Count == 0) RefreshAudioDevices();
            UpdateStats();
        }
    }

    /// <summary>Relit les caractéristiques du flux auprès de mpv (une fois par seconde tant que le panneau est ouvert).</summary>
    private void UpdateStats()
    {
        var ci = Loc.Culture;
        string? Str(string p) => Engine.GetString(p) is { Length: > 0 } v ? v : null;
        double? Num(string p) => Engine.GetDouble(p);
        static string Join(params string?[] parts) =>
            parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() is { Length: > 0 } ok ? string.Join("  ·  ", ok) : "—";
        string Rate(double bps) => bps >= 1_000_000
            ? (bps / 1_000_000).ToString("0.0", ci) + " Mb/s"
            : (bps / 1000).ToString("0", ci) + " kb/s";

        var w = Num("video-params/w");
        var h = Num("video-params/h");
        var fps = Num("container-fps") ?? Num("estimated-vf-fps");
        var hw = Str("hwdec-current");
        var ch = Num("audio-params/channel-count");
        var sr = Num("audio-params/samplerate");
        var alang = Str("current-tracks/audio/lang");
        var vb = Num("video-bitrate");
        var ab = Num("audio-bitrate");
        var cache = Num("demuxer-cache-duration");
        var drops = (Num("frame-drop-count") ?? 0) + (Num("decoder-frame-drop-count") ?? 0);
        var rows = new (string Key, string Value)[]
        {
            ("Stats.Video", Join((Str("current-tracks/video/codec") ?? Str("video-format"))?.ToUpperInvariant(),
                w is > 0 && h is > 0 ? $"{w:0}×{h:0}" : null,
                fps is > 0 ? Loc.T("Stats.Fps", fps.Value.ToString("0.##", ci)) : null)),
            ("Stats.Decoder", hw is null or "no" ? Loc.T("Stats.Software") : Loc.T("Stats.Hardware", hw)),
            ("Stats.Audio", Join(Str("current-tracks/audio/codec")?.ToUpperInvariant(),
                ch is > 0 ? Loc.T("Stats.Channels", (int)ch.Value) : null,
                sr is > 0 ? (sr.Value / 1000).ToString("0.#", ci) + " kHz" : null,
                TrackInfo.LanguageName(alang) ?? alang)),
            ("Stats.Bitrate", Join(vb is > 0 ? Loc.T("Stats.VideoRate", Rate(vb.Value)) : null, ab is > 0 ? Loc.T("Stats.AudioRate", Rate(ab.Value)) : null)),
            ("Stats.Buffer", cache is { } c ? Loc.T("Stats.Seconds", c.ToString("0.0", ci)) : "—"),
            ("Stats.Dropped", ((long)drops).ToString(ci)),
            ("Stats.Latency", MpvVideoView.Timing.RecentMs > 0 ? Loc.T("Stats.Ms", MpvVideoView.Timing.RecentMs.ToString("0", ci)) : "—"),
            ("Stats.Container", ContainerName(Str("file-format"))),
            ("Stats.Output", AudioDevices.FirstOrDefault(a => a.IsSelected)?.Label ?? Loc.T("Player.AudioDefault")),
            ("Stats.Source", Lib.Source is { } src ? src.Name + "  ·  " + src.KindLabel : "—"),
        };
        if (Stats.Count != rows.Length)
        {
            Stats.Clear();
            foreach (var r in rows) Stats.Add(new StatRow { Key = r.Key });
        }
        for (var i = 0; i < rows.Length; i++)
        {
            Stats[i].Label = Loc.T(rows[i].Key);
            Stats[i].Value = rows[i].Value;
        }
    }

    /// <summary>Nom lisible du conteneur annoncé par mpv (« mov,mp4,m4a… » → MP4).</summary>
    private static string ContainerName(string? f) => f switch
    {
        null or "" => "—",
        _ when f.StartsWith("mov,mp4", StringComparison.Ordinal) => "MP4",
        _ when f.StartsWith("matroska", StringComparison.Ordinal) => "MKV",
        "hls" or "applehttp" => "HLS",
        "mpegts" => "MPEG-TS",
        "flv" => "FLV",
        "dash" => "DASH",
        _ => f.Split(',')[0].ToUpperInvariant(),
    };

    [RelayCommand]
    private void CycleAspect()
    {
        NotifyActivity();
        _aspectIndex = (_aspectIndex + 1) % Aspects.Length;
        Engine.SetAspect(Aspects[_aspectIndex]);
        AspectLabel = _aspectIndex == 0 ? Loc.T("Player.AspectAuto") : Aspects[_aspectIndex];
        Flash(Loc.T("Player.Aspect", AspectLabel));
    }

    [RelayCommand]
    private void ToggleSidePanel()
    {
        NotifyActivity();
        IsTracksPanelOpen = false;
        if (!IsSidePanelOpen && IsLive) BuildPanel();
        IsSidePanelOpen = !IsSidePanelOpen;
    }

    /// <summary>Prépare la liste des chaînes : catégories, catégorie de la chaîne en cours, sélection.</summary>
    private void BuildPanel()
    {
        var c = Lib.Catalog;
        PanelCategories.Clear();
        if (c is null) return;
        var fav = new Category { Id = "__fav", Name = Loc.T("Common.Favorites"), Kind = ContentKind.Live, IsVirtual = true };
        var all = new Category { Id = "__all", Name = Loc.T("Live.AllChannels"), Kind = ContentKind.Live, IsVirtual = true };
        if (c.Channels.Any(x => x.IsFavorite && !Lib.IsHidden(x))) PanelCategories.Add(fav);
        PanelCategories.Add(all);
        foreach (var cat in c.LiveCategories.Where(x => x.Count > 0 && !Lib.IsHidden(x))) PanelCategories.Add(cat);
        // Remise à zéro sans relancer le filtrage (fait juste après selon la catégorie choisie).
#pragma warning disable MVVMTK0034
        _panelFilter = "";
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(PanelFilter));
        var current = CurrentChannel;
        var target = current is null ? all
            : ZapChannels.Count > 1 && ZapChannels.All(x => x.IsFavorite) && PanelCategories.Contains(fav) ? fav
            : PanelCategories.FirstOrDefault(x => x.Id == current.CategoryId) ?? all;
        if (PanelCategory == target) ApplyPanelFilter(); else PanelCategory = target;
    }

    partial void OnPanelCategoryChanged(Category? value) => ApplyPanelFilter();
    partial void OnPanelFilterChanged(string value) => ApplyPanelFilter();

    private void ApplyPanelFilter()
    {
        var c = Lib.Catalog;
        var cat = PanelCategory;
        if (c is null || cat is null) { PanelChannels = Array.Empty<LiveChannel>(); return; }
        IEnumerable<LiveChannel> q = cat.Id switch
        {
            "__all" => c.Channels,
            "__fav" => c.Channels.Where(x => x.IsFavorite),
            _ => c.Channels.Where(x => x.CategoryId == cat.Id)
        };
        q = q.Where(x => !Lib.IsHidden(x));
        var tokens = TextNorm.Tokens(PanelFilter);
        if (tokens.Length > 0)
        {
            var norm = TextNorm.Normalize(PanelFilter);
            q = q.Where(x => TextNorm.Score(x.SearchKey, tokens, norm) > 0);
        }
        var list = q.ToList();
        PanelChannels = list;
        Lib.UpdateNowNext(list.Count > 600 ? list.Take(600).ToList() : list);
        PanelSelection = CurrentChannel is { } cur && list.Contains(cur) ? cur : list.FirstOrDefault();
    }

    /// <summary>Lance une chaîne depuis la liste latérale (la liste reste ouverte).</summary>
    [RelayCommand]
    private async Task PlayFromPanelAsync(LiveChannel? ch)
    {
        ch ??= PanelSelection;
        if (ch is null) return;
        NotifyActivity();
        if (ch == CurrentChannel) return;
        if (Lib.IsHidden(ch) && !await _main.RequestPinAsync()) return;
        await PlayChannelAsync(ch, PanelChannels);
    }

    [RelayCommand]
    private void ToggleTracksPanel()
    {
        NotifyActivity();
        IsSidePanelOpen = false;
        if (!IsTracksPanelOpen) RefreshAudioDevices();
        IsTracksPanelOpen = !IsTracksPanelOpen;
    }

    public void RefreshAudioDevices()
    {
        var current = SettingsStore.Current.AudioDevice ?? "auto";
        AudioDevices.Clear();
        foreach (var (name, desc) in Engine.GetAudioDevices())
            AudioDevices.Add(new TrackOption
            {
                DeviceName = name,
                Label = name == "auto" ? Loc.T("Player.AudioDefault") : desc,
                IsSelected = name == current,
            });
        if (AudioDevices.Count > 0 && !AudioDevices.Any(a => a.IsSelected)) AudioDevices[0].IsSelected = true;
    }

    [RelayCommand]
    private void SelectAudioDevice(TrackOption t)
    {
        if (t.DeviceName is null) return;
        Engine.SetAudioDevice(t.DeviceName);
        SettingsStore.Current.AudioDevice = t.DeviceName;
        SettingsStore.Save();
        foreach (var a in AudioDevices) a.IsSelected = a == t;
        Flash(Loc.T("Player.AudioOutputSet", t.Label));
    }

    [RelayCommand]
    private void ToggleRecord()
    {
        NotifyActivity();
        if (!IsLive || CurrentChannel is null) return;
        if (Engine.RecordingPath is not null || _recordingEntry is not null)
        {
            StopRecording();
            return;
        }
        var s = SettingsStore.Current;
        var folder = s.RecordingsFolder;
        // Limite commune aux enregistrements programmés et immédiats : pas de file d'attente.
        if (_main.Recorder.LimitReached)
        {
            _main.Toast(Loc.T("Toast.RecFailed"), Recording.RecordingService.LimitMessage, true);
            return;
        }
        if (Recording.DiskSpace.IsLow(folder, out var freeGb))
        {
            _main.Toast(Loc.T("Toast.RecFailed"), Recording.DiskSpace.LowMessage(freeGb), true);
            return;
        }
        var format = Recording.RecordingNames.NormalizeFormat(s.RecordingFormat);
        var now = DateTimeOffset.Now;
        var path = Recording.RecordingNames.BuildPath(folder, s.RecordingNameTemplate, NowTitle, CurrentChannel.Name, now, format);
        var name = Path.GetFileName(path);
        try
        {
            // .ts : flux d'origine ; .mkv / .mp4 : mpv change seulement le conteneur (voir RecordingService).
            Engine.StartRecording(path);
            _recordingChannel = CurrentChannel;
            CurrentChannel.IsRecording = true;
            _recordingEntry = new RecordingEntry
            {
                SourceId = Lib.Source?.Id ?? "", ChannelId = CurrentChannel.Id, ChannelName = CurrentChannel.Name,
                Title = NowTitle ?? CurrentChannel.Name, Start = now, End = now,
                Status = RecordingStatus.Recording, FilePath = path,
            };
            UserDataStore.AddRecording(_recordingEntry);
            _recordingStartedUtc = DateTime.UtcNow;
            _lastRecordingSpaceCheck = DateTime.UtcNow;
            _recordingFormatProbe = format != "ts";
            _recordingStopAt = InstantStopTime(now);
            _main.Toast(Loc.T("Toast.RecStarted"),
                _recordingStopAt is { } stop ? Loc.T("Toast.RecStartedUntil", name, stop.ToLocalTime()) : name);
        }
        catch (Exception ex)
        {
            Log.Warn("Enregistrement immédiat impossible : " + ex.Message);
            _main.Toast(Loc.T("Toast.RecFailed"), ex.Message, true);
        }
    }

    private RecordingEntry? _recordingEntry;
    /// <summary>Chaîne enregistrée (peut différer de CurrentChannel pendant un changement de chaîne).</summary>
    private LiveChannel? _recordingChannel;
    /// <summary>Heure d'arrêt automatique de l'enregistrement immédiat (null = pas d'arrêt).</summary>
    private DateTimeOffset? _recordingStopAt;
    /// <summary>Arrêt « fin du programme » demandé mais guide pas encore chargé : nouvel essai dans Tick.</summary>
    private bool _recordingStopPending;
    /// <summary>Enregistrement mkv/mp4 dont il faut vérifier qu'il écrit bien des données.</summary>
    private bool _recordingFormatProbe;
    private DateTime _recordingStartedUtc;
    private DateTime _lastRecordingSpaceCheck;
    private bool _recordingSpaceCheckRunning;

    /// <summary>Vrai pendant un enregistrement immédiat (compté dans la limite d'enregistrements simultanés).</summary>
    public bool HasInstantRecording => _recordingEntry is not null;

    /// <summary>Heure d'arrêt automatique selon le réglage : 0 = jamais, -1 = fin du programme en cours
    /// (d'après le guide ; pas d'arrêt si le guide ne le connaît pas), sinon nombre de minutes.</summary>
    private DateTimeOffset? InstantStopTime(DateTimeOffset from)
    {
        _recordingStopPending = false;
        var s = SettingsStore.Current;
        if (s.InstantRecordingStopMinutes > 0) return from.AddMinutes(s.InstantRecordingStopMinutes);
        if (s.InstantRecordingStopMinutes == 0) return null;

        // Programmes de la chaîne enregistrée uniquement (la liste peut encore être celle de la chaîne précédente).
        var ch = _recordingChannel ?? CurrentChannel;
        var key = ch is null ? null : ch.EpgKey ?? "ch:" + ch.Id;
        var programs = _channelPrograms.Where(p => string.IsNullOrEmpty(p.ChannelKey) || string.Equals(p.ChannelKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (programs.Count == 0)
        {
            _recordingStopPending = true;
            return null;
        }
        var current = programs.FirstOrDefault(p => p.IsLiveAt(from));
        if (current is null) return null;
        // Programme sur le point de finir : on enregistre le suivant en entier.
        if (current.End - from < TimeSpan.FromMinutes(2) && programs.FirstOrDefault(p => p.Start >= current.End) is { } next)
            current = next;
        // Même marge de fin que pour les enregistrements programmés (horaires du guide approximatifs).
        return current.End.AddMinutes(Math.Max(0, s.RecordingMarginAfterMin));
    }

    private long _recLastSize = -1;
    private DateTime _recLastGrowthUtc = DateTime.UtcNow;

    /// <summary>Surveillance de l'enregistrement immédiat, appelée à chaque tick (200 ms).</summary>
    private void TickRecording()
    {
        if (_recordingEntry is not { } entry || Engine.RecordingPath is not { } path) return;

        // Guide arrivé après le début de l'enregistrement : calcul de l'heure d'arrêt.
        if (_recordingStopPending && _tickCount % 25 == 0)
        {
            _recordingStopAt = InstantStopTime(entry.Start);
            if (_recordingStopAt is { } stop) Flash(Loc.T("Player.RecStopsAt", stop.ToLocalTime()));
        }

        if (_recordingStopAt is { } stopAt && DateTimeOffset.Now >= stopAt)
        {
            Log.Info($"Enregistrement immédiat arrêté automatiquement : {path}");
            StopRecording(Loc.T("Toast.RecAutoStopped"));
            return;
        }

        // Fichier qui ne grossit plus alors que la lecture avance (dossier inaccessible, disque débranché…) : arrêt signalé.
        if (_tickCount % 25 == 0 && !Engine.Paused && !IsBuffering && !IsLoading)
        {
            var size = Recording.RecordingService.Length(path);
            if (size != _recLastSize)
            {
                _recLastSize = size;
                _recLastGrowthUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _recLastGrowthUtc > TimeSpan.FromSeconds(30) && DateTime.UtcNow - _recordingStartedUtc > TimeSpan.FromSeconds(30))
            {
                Log.Warn($"Enregistrement immédiat bloqué : le fichier ne grossit plus ({path})");
                entry.Error = Loc.T("Rec.Stalled");
                StopRecording(Loc.T("Rec.Failed"), Loc.T("Rec.FailedBody", entry.Title, entry.Error), error: true);
                return;
            }
        }

        if (_recordingFormatProbe && DateTime.UtcNow - _recordingStartedUtc >= Recording.RecordingService.RemuxProbeDelay)
        {
            _recordingFormatProbe = false;
            if (Recording.RecordingService.Length(path) == 0) FallbackRecordingToTs(entry, path);
        }

        if (!_recordingSpaceCheckRunning && DateTime.UtcNow - _lastRecordingSpaceCheck >= Recording.DiskSpace.CheckInterval)
        {
            _lastRecordingSpaceCheck = DateTime.UtcNow;
            _ = CheckRecordingSpaceAsync(entry, Path.GetDirectoryName(path));
        }
    }

    /// <summary>Le conteneur mkv/mp4 n'a rien écrit (pistes refusées) : l'enregistrement repasse en .ts.</summary>
    private void FallbackRecordingToTs(RecordingEntry entry, string oldPath)
    {
        var format = Path.GetExtension(oldPath).TrimStart('.').ToUpperInvariant();
        Log.Warn($"Enregistrement immédiat : aucune donnée en {format} après {Recording.RecordingService.RemuxProbeDelay.TotalSeconds:0} s, repli sur le format TS");
        Engine.StopRecording();
        Recording.RecordingService.TryDelete(oldPath);
        var path = Recording.RecordingNames.UniquePath(Path.ChangeExtension(oldPath, ".ts"));
        entry.FilePath = path;
        UserDataStore.UpdateRecording(entry);
        try
        {
            Engine.StartRecording(path);
            _main.Toast(Loc.T("Rec.FormatFallbackTitle"), Loc.T("Rec.FormatFallback", entry.Title, format));
        }
        catch (Exception ex)
        {
            Log.Warn("Repli en TS impossible : " + ex.Message);
            entry.Error = ex.Message;
            StopRecording();
        }
    }

    /// <summary>Espace libre contrôlé hors du fil de l'interface ; sous le seuil, l'enregistrement s'arrête.</summary>
    private async Task CheckRecordingSpaceAsync(RecordingEntry entry, string? folder)
    {
        _recordingSpaceCheckRunning = true;
        try
        {
            var low = await Task.Run(() => Recording.DiskSpace.IsLow(folder, out var free) ? free : (double?)null);
            if (low is { } gb && _recordingEntry == entry)
            {
                entry.Error = Recording.DiskSpace.LowMessage(gb);
                Log.Warn($"Enregistrement immédiat arrêté : espace disque insuffisant ({gb:0.0} Go libres)");
                StopRecording(Loc.T("Rec.LowDiskStopped"), entry.Error, error: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Contrôle de l'espace disque : " + ex.Message);
        }
        finally
        {
            _recordingSpaceCheckRunning = false;
        }
    }

    /// <summary>Arrête l'enregistrement immédiat. Sans titre : notification habituelle (terminé / vide) ;
    /// avec un titre (arrêt automatique, disque plein) : notification correspondante.</summary>
    private void StopRecording(string? toastTitle = null, string? toastMessage = null, bool error = false)
    {
        var path = Engine.RecordingPath ?? _recordingEntry?.FilePath;
        Engine.StopRecording();
        var ch = _recordingChannel ?? CurrentChannel;
        if (ch is not null) ch.IsRecording = false;
        _recordingChannel = null;
        _recordingStopAt = null;
        _recordingStopPending = false;
        _recordingFormatProbe = false;
        if (_recordingEntry is { } e)
        {
            e.End = DateTimeOffset.Now;
            e.Status = RecordingStatus.Completed;
            e.SizeBytes = Recording.RecordingService.Length(path);
            if (e.SizeBytes == 0)
            {
                e.Status = RecordingStatus.Failed;
                e.Error ??= Loc.T("Rec.NoDataShort");
            }
            UserDataStore.UpdateRecording(e);
            _recordingEntry = null;
            if (toastTitle is not null)
                _main.Toast(toastTitle, toastMessage ?? Path.GetFileName(path ?? ""), error || e.Status == RecordingStatus.Failed);
            else if (e.Status == RecordingStatus.Failed) _main.Toast(Loc.T("Toast.RecEmpty"), Loc.T("Toast.RecEmptyMsg"), true);
            else _main.Toast(Loc.T("Toast.RecDone"), Path.GetFileName(path ?? ""));
            _main.OnRecordingsChanged();
        }
    }

    [RelayCommand]
    private async Task ZapAsync(LiveChannel ch)
    {
        if (ch == CurrentChannel) return;
        if (Lib.IsHidden(ch) && !await _main.RequestPinAsync()) return;
        await PlayChannelAsync(ch);
    }

    public void ZapRelative(int delta)
    {
        if (!IsLive || CurrentChannel is null || ZapChannels.Count < 2) return;
        var idx = ZapChannels.IndexOf(CurrentChannel);
        var next = ZapChannels[(idx + delta + ZapChannels.Count) % ZapChannels.Count];
        _ = PlayChannelAsync(next);
    }

    [RelayCommand]
    private async Task PlayEpisodeFromPanelAsync(Episode ep)
    {
        _autoChain = 0;
        if (CurrentSeries is { } s) await PlayEpisodeAsync(s, ep, ZapEpisodes.ToList());
    }

    /// <summary>Lecture de l'épisode suivant demandée par l'utilisateur (remet à zéro « Toujours là ? »).</summary>
    [RelayCommand]
    private void PlayNextEpisode()
    {
        _autoChain = 0;
        StartNextEpisode();
    }

    private void StartNextEpisode()
    {
        var next = NextEpisode;
        NextEpisode = null;
        NextEpisodeCountdown = 0;
        StillWatchingPrompt = false;
        if (next is not null && CurrentSeries is { } s) _ = PlayEpisodeAsync(s, next, ZapEpisodes.ToList(), fromStart: true);
    }

    private int _autoChain;
    [ObservableProperty] private bool _stillWatchingPrompt;

    public string NextEpisodeText => StillWatchingPrompt ? Loc.T("Player.StillWatching")
        : NextEpisodeCountdown > 0 ? Loc.T("Player.NextIn", NextEpisodeCountdown) : Loc.T("Player.NextEpisode");

    partial void OnNextEpisodeCountdownChanged(int value) => OnPropertyChanged(nameof(NextEpisodeText));
    partial void OnStillWatchingPromptChanged(bool value) => OnPropertyChanged(nameof(NextEpisodeText));

    [RelayCommand]
    private void CancelNextEpisode()
    {
        NextEpisode = null;
        NextEpisodeCountdown = 0;
        Close();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (CurrentChannel is { } ch) await PlayChannelAsync(ch);
        else if (CurrentMovie is { } m) await PlayMovieAsync(m);
        else if (CurrentSeries is { } s && CurrentEpisode is { } e) await PlayEpisodeAsync(s, e, ZapEpisodes.ToList());
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        CatalogItem? item = CurrentChannel ?? (CatalogItem?)CurrentMovie ?? CurrentSeries;
        if (item is null) return;
        Lib.ToggleFavorite(item);
        Flash(item.IsFavorite ? Loc.T("Player.FavAdded") : Loc.T("Player.FavRemoved"));
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        SaveProgress(final: true);
        if (Engine.RecordingPath is not null || _recordingEntry is not null) StopRecording();
        _openCts?.Cancel();
        _openPending = false;
        _autoChain = 0;
        _liveRetries = 0;
        Engine.Stop();
        if (CurrentChannel is not null) CurrentChannel.IsPlaying = false;
        IsOpen = false;
        IsMini = false;
        _timer.Stop();
        IsSidePanelOpen = IsTracksPanelOpen = false;
        ErrorMessage = null;
        NextEpisode = null;
        NextEpisodeCountdown = 0;
        _main.OnPlayerClosed();
    }

    // ───────────── Mini-lecteur ─────────────

    /// <summary>Le lecteur occupe toute la fenêtre (ouvert et pas réduit).</summary>
    public bool IsFullView => IsOpen && !IsMini;

    /// <summary>L'application est visible : aucun lecteur, ou lecteur réduit en mini-lecteur.</summary>
    public bool ShowsApp => !IsOpen || IsMini;

    /// <summary>Texte d'aide du bouton retour du lecteur, selon le réglage.</summary>
    public string LeaveTip => SettingsStore.Current.MiniPlayerOnBack ? Loc.T("Player.BackToMini") : Loc.T("Ui.FermerLeLecteurEchap");

    /// <summary>Réduit la vidéo dans une fenêtre flottante : la lecture continue pendant la navigation.</summary>
    [RelayCommand]
    public void EnterMini()
    {
        if (!IsOpen || IsMini) return;
        if (_main.IsFullscreen) _main.IsFullscreen = false;
        IsSidePanelOpen = IsTracksPanelOpen = false;
        IsMini = true;
        Log.Info("Lecteur réduit en mini-lecteur");
    }

    /// <summary>Repasse du mini-lecteur au lecteur complet.</summary>
    [RelayCommand]
    public void ExpandMini()
    {
        if (!IsOpen || !IsMini) return;
        IsMini = false;
        NotifyActivity();
    }

    /// <summary>Retour / Échap depuis le lecteur : mini-lecteur ou arrêt, selon le réglage.</summary>
    [RelayCommand]
    public void Leave()
    {
        if (SettingsStore.Current.MiniPlayerOnBack) EnterMini();
        else Close();
    }

    public void RefreshLeaveTip() => OnPropertyChanged(nameof(LeaveTip));

    /// <summary>Notification « taille ou coin du mini-lecteur modifiés » (réglages) : la fenêtre recalcule sa place.</summary>
    public const string MiniLayoutProperty = "MiniLayout";
    public void NotifyMiniLayoutChanged() => OnPropertyChanged(MiniLayoutProperty);

    private DispatcherTimer? _osdTimer;

    public void Flash(string message)
    {
        OsdMessage = message;
        _osdTimer?.Stop();
        _osdTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.4), DispatcherPriority.Background, (s, _) =>
        {
            OsdMessage = null;
            ((DispatcherTimer)s!).Stop();
        });
        _osdTimer.Start();
    }

    /// <summary>Met à jour les textes calculés après un changement de langue.</summary>
    public void RefreshTexts()
    {
        AspectLabel = _aspectIndex == 0 ? Loc.T("Player.AspectAuto") : Aspects[_aspectIndex];
        _lastVersion = -1;
        if (IsLive) UpdateNowFromCache();
        RefreshTracks();
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(NextEpisodeCountdown));
        OnPropertyChanged(nameof(LeaveTip));
    }

    public void Shutdown()
    {
        SaveProgress(final: true);
        var recording = Engine.RecordingPath is not null || _recordingEntry is not null;
        if (recording) StopRecording();
        _timer.Stop();
        if (recording && Engine.IsInitialized)
        {
            // Arrêt de la lecture, puis on attend que mpv ait refermé le fichier (au plus 3 s).
            Engine.Stop();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!Engine.IdleActive && clock.ElapsedMilliseconds < 3000) Thread.Sleep(50);
        }
    }
}
