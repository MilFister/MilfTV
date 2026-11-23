using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;
using MilfTV.Theming;

namespace MilfTV.ViewModels;

/// <summary>Entrée du menu des paramètres.</summary>
public sealed partial class SettingsNavEntry : ObservableObject
{
    public required string Key { get; init; }
    public required string Icon { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    /// <summary>Mots-clés supplémentaires pour la recherche dans les réglages.</summary>
    public string Keywords { get; init; } = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isShown = true;
}

/// <summary>Rangée du tableau de bord (ordre et visibilité choisis par le profil).</summary>
public sealed partial class DashRowItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public Action? Changed { get; set; }
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    partial void OnIsVisibleChanged(bool value) => Changed?.Invoke();
}

/// <summary>Source IPTV vue depuis le profil actif (badge de partage, droits de modification).</summary>
public sealed class SourceRow
{
    public required SourceConfig Source { get; init; }
    public required bool CanEdit { get; init; }
    public required string Badge { get; init; }
    public required bool IsActive { get; init; }
    public bool IsReadOnly => !CanEdit;
    public string Name => Source.Name;
    public string KindLabel => Source.KindLabel;
    public SourceKind Kind => Source.Kind;
}

/// <summary>Paramètres, organisés en sections : « Ce profil » puis « Application ».</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private AppSettings S => SettingsStore.Current;
    private UserProfile? P => Library.Instance.Profile;
    private ProfilePrefs Prefs => Library.Instance.Prefs;
    private bool _loading;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _loading = true;

        // Apparence (propre au profil, sinon réglage général)
        _themeMode = Prefs.ThemeMode ?? S.ThemeMode;
        _colorTheme = ThemeService.Themes.FirstOrDefault(t => t.Id == (Prefs.ColorTheme ?? S.ColorTheme)) ?? ThemeService.Themes[0];
        _posterSize = S.PosterSize;

        // Profil : historique, lecture automatique, aperçus
        _trackHistory = Prefs.TrackHistory;
        _resumePlayback = Prefs.ResumePlayback;
        _watchedThreshold = Prefs.WatchedThresholdPercent;
        _suggestionsFromHistory = Prefs.SuggestionsFromHistory;
        _autoPlayNext = Prefs.AutoPlayNext;
        _autoPlayCountdown = Prefs.AutoPlayCountdown;
        _autoPlayMaxEpisodes = AutoPlayMaxChoices.FirstOrDefault(o => o.Value == Prefs.AutoPlayMaxEpisodes) ?? AutoPlayMaxChoices[0];
        _hoverPreview = Prefs.HoverPreview;
        _hoverDelaySeconds = Prefs.HoverDelayMs / 1000.0;
        _previewSound = Prefs.PreviewSound;
        _trailerLanguage = TrailerLanguages.FirstOrDefault(o => o.Key == Prefs.TrailerLanguage) ?? TrailerLanguages[0];

        // Lecture
        _timeshiftMinutes = S.TimeshiftMinutes;
        _hwDecode = HwOptions.FirstOrDefault(o => o.Key == S.HwDecode) ?? HwOptions[0];
        _highQuality = S.HighQuality;
        _smoothRendering = S.SmoothRendering;
        _autoUpdate = S.AutoUpdate;
        _updateChannelIndex = Updates.IsBeta ? 1 : 0;
        _miniPlayerOnBack = S.MiniPlayerOnBack;
        _miniPlayerSize = MiniPresetIndex();
        _audioLanguages = S.AudioLanguages;
        _subtitleLanguages = S.SubtitleLanguages;
        _subtitlesByDefault = S.SubtitlesByDefault;
        _userAgent = S.DefaultUserAgent ?? "";

        // Enregistrements
        _recordingsFolder = S.RecordingsFolder;
        _marginBefore = S.RecordingMarginBeforeMin;
        _marginAfter = S.RecordingMarginAfterMin;
        _recordingFormat = S.RecordingFormat;
        _recordingQuality = S.RecordingQuality;
        _recordingNameTemplate = S.RecordingNameTemplate;
        _instantStop = InstantStopChoices.FirstOrDefault(o => o.Value == S.InstantRecordingStopMinutes) ?? InstantStopChoices[1];
        _minFreeGb = S.RecordingMinFreeGb;
        _maxSimultaneous = S.MaxSimultaneousRecordings;

        // Autres
        _tmdbKey = Library.TmdbKeyCache ?? "";
        _autoLockAdult = S.AutoLockAdult;
        _protectSettings = S.ProtectSettings;
        _catalogRefreshHours = S.CatalogRefreshHours;

        BuildNav();
        BuildDashRows();
        RefreshSourceRows();
        MarkTheme();
        RefreshAudioOutputs();
        _loading = false;
        SelectSection("profile");

        _main.Sources.CollectionChanged += OnSourcesChanged;

        // Changement de langue : la page est reconstruite pour traduire tous les libellés.
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void OnSourcesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshSourceRows();

    public override void Detach()
    {
        Updates.Changed -= OnUpdatesChanged;
        _main.Sources.CollectionChanged -= OnSourcesChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        _main.Player.PropertyChanged -= OnPlayerChanged;
    }

    public override bool KeepView => false;
    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;

    public override void OnNavigatedFrom()
    {
        _main.Player.PropertyChanged -= OnPlayerChanged;
        Updates.Changed -= OnUpdatesChanged;
    }

    public override void OnNavigatedTo()
    {
        // Le mode clair / sombre peut avoir été basculé depuis la barre latérale ; le mini-lecteur redimensionné à la souris.
        _loading = true;
        ThemeMode = Prefs.ThemeMode ?? S.ThemeMode;
        MiniPlayerSize = MiniPresetIndex();
        _loading = false;
        RefreshProfileInfo();
        RefreshSourceRows();
        _main.Player.PropertyChanged -= OnPlayerChanged;
        _main.Player.PropertyChanged += OnPlayerChanged;
        Updates.Changed -= OnUpdatesChanged;
        Updates.Changed += OnUpdatesChanged;
        OnUpdatesChanged();
        OnPropertyChanged(nameof(HasPin));
        OnPropertyChanged(nameof(LockedCategories));
    }

    private void OnLanguageChanged()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _main.ReopenSettings(this, Section);
    }

    public sealed record Option(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record IntOption(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    // ═══════════════════ Menu des sections ═══════════════════

    public ObservableCollection<SettingsNavEntry> ProfileSections { get; } = new();
    public ObservableCollection<SettingsNavEntry> AppSections { get; } = new();
    private IEnumerable<SettingsNavEntry> AllSections => ProfileSections.Concat(AppSections);

    [ObservableProperty] private string _section = "profile";
    [ObservableProperty] private string _sectionTitle = "";
    [ObservableProperty] private string _sectionHint = "";
    [ObservableProperty] private string _navFilter = "";
    [ObservableProperty] private bool _noSectionFound;
    [ObservableProperty] private bool _showProfileGroup = true;
    [ObservableProperty] private bool _showAppGroup = true;

    private void BuildNav()
    {
        SettingsNavEntry E(string key, string icon, string kw = "") => new()
        {
            Key = key,
            Icon = icon,
            Title = Loc.T("Settings.Nav." + key),
            Hint = Loc.T("Settings.Nav." + key + ".Hint"),
            Keywords = kw,
        };
        ProfileSections.Add(E("profile", "", Loc.T("Profile.Name") + " " + Loc.T("Profile.PasswordProtect") + " " + Loc.T("Profile.KidMode")));
        ProfileSections.Add(E("appearance", "", Loc.T("Settings.Language") + " " + Loc.T("Ui.Mode") + " " + Loc.T("Ui.Couleur") + " " + Loc.T("Ui.TailleDesAffiches")));
        ProfileSections.Add(E("home", "", Loc.T("Ui.ReprendreLaLecture") + " " + Loc.T("Home.Recommended") + " " + Loc.T("Home.MyList")));
        ProfileSections.Add(E("history", "", Loc.T("Settings.TrackHistory") + " " + Loc.T("Settings.Resume")));
        ProfileSections.Add(E("autoplay", "", Loc.T("Settings.AutoPlayNext") + " " + Loc.T("Settings.AutoPlayCountdown")));
        ProfileSections.Add(E("previews", "", Loc.T("Settings.TrailerLanguage") + " " + Loc.T("Settings.HoverPreview")));
        AppSections.Add(E("sources", "", "Xtream M3U MAC Stalker"));
        AppSections.Add(E("playback", "", Loc.T("Settings.MiniGroup") + " pip " + Loc.T("Ui.DirectDiffere") + " " + Loc.T("Settings.AudioOutput") + " " + Loc.T("Ui.DecodageMateriel") + " " + Loc.T("Ui.LanguesDesSousTitres")));
        AppSections.Add(E("recordings", "", Loc.T("Settings.RecFormat") + " " + Loc.T("Settings.RecQuality") + " " + Loc.T("Ui.Dossier")));
        AppSections.Add(E("parental", "", "PIN " + Loc.T("Ui.CategoriesVerrouillees")));
        AppSections.Add(E("profiles", "", Loc.T("Profile.AskAtStartup")));
        AppSections.Add(E("tmdb", "", "TMDB API"));
        AppSections.Add(E("data", "", Loc.T("Ui.ViderLeCacheDesImages") + " " + Loc.T("Ui.RaccourcisClavierDuLecteur") + " " + Loc.T("Update.Title") + " " + Loc.T("Update.Channel") + " " + Loc.T("Backup.Title") + " " + Loc.T("Changelog.Title")));
    }

    [RelayCommand]
    private void SelectSection(string key)
    {
        var entry = AllSections.FirstOrDefault(e => e.Key == key) ?? ProfileSections[0];
        foreach (var e in AllSections) e.IsActive = e == entry;
        Section = entry.Key;
        SectionTitle = entry.Title;
        SectionHint = entry.Hint;
    }

    partial void OnNavFilterChanged(string value)
    {
        var q = TextNorm.Normalize(value ?? "").Trim();
        foreach (var e in AllSections)
            e.IsShown = q.Length == 0 || TextNorm.Normalize(e.Title + " " + e.Hint + " " + e.Keywords).Contains(q, StringComparison.Ordinal);
        NoSectionFound = !AllSections.Any(e => e.IsShown);
        ShowProfileGroup = ProfileSections.Any(e => e.IsShown);
        ShowAppGroup = AppSections.Any(e => e.IsShown);
        // La section affichée suit la recherche.
        if (AllSections.FirstOrDefault(e => e.Key == Section) is { IsShown: false } && AllSections.FirstOrDefault(e => e.IsShown) is { } first)
            SelectSection(first.Key);
    }

    // ═══════════════════ Ce profil : identité ═══════════════════

    public string ProfileName => P?.Name ?? "";
    public string ProfileColor => P?.Color ?? "#7C5CFF";
    public string ProfileInitial => P?.Initial ?? "?";
    public string? ProfileAvatar => P?.Avatar;
    public bool ProfileHasPassword => P?.HasPassword == true;
    public bool ProfileIsKid => P?.IsKid == true;
    public string ProfileGroupTitle => Loc.T("Settings.GroupProfile", ProfileName);
    public string ProfileStats
    {
        get
        {
            if (P is null) return "";
            var watched = Lib.GetWatchedItems(10_000).Count;
            var list = Lib.GetWatchlistItems().Count;
            return Loc.T("Settings.ProfileStats", watched, list);
        }
    }

    private void RefreshProfileInfo()
    {
        foreach (var n in new[] { nameof(ProfileName), nameof(ProfileColor), nameof(ProfileInitial), nameof(ProfileAvatar), nameof(ProfileHasPassword),
                     nameof(ProfileIsKid), nameof(ProfileGroupTitle), nameof(ProfileStats) })
            OnPropertyChanged(n);
    }

    [RelayCommand]
    private async Task EditProfileAsync()
    {
        if (P is { } p && await _main.EnsureProfileAdminAsync()) _main.ShowDialog(new ProfileEditorViewModel(_main, p));
    }

    [RelayCommand] private void SwitchProfile() => _main.ShowProfilePicker(null);

    // ═══════════════════ Ce profil : apparence et langue ═══════════════════

    public IReadOnlyList<ThemeChoice> Themes { get; } = ThemeService.Themes.Select(t => new ThemeChoice { Theme = t }).ToList();
    public IReadOnlyList<LanguageInfo> LanguageOptions => LanguageChoice.Options;
    public LanguageInfo SelectedLanguage
    {
        get => LanguageChoice.Current;
        set => LanguageChoice.Apply(value);
    }

    [ObservableProperty] private string _themeMode;
    [ObservableProperty] private ColorTheme _colorTheme;
    [ObservableProperty] private int _posterSize;

    partial void OnThemeModeChanged(string value)
    {
        if (_loading) return;
        S.ThemeMode = value;
        if (P is { } p) p.Prefs.ThemeMode = value;
        Save();
        ThemeService.Apply(value, ColorTheme.Id);
    }

    partial void OnColorThemeChanged(ColorTheme value)
    {
        if (_loading) return;
        S.ColorTheme = value.Id;
        if (P is { } p) p.Prefs.ColorTheme = value.Id;
        Save();
        ThemeService.Apply(ThemeMode, value.Id);
        MarkTheme();
    }

    private void MarkTheme() { foreach (var t in Themes) t.IsSelected = t.Theme.Id == ColorTheme.Id; }
    partial void OnPosterSizeChanged(int value) { if (!_loading) { S.PosterSize = value; Save(); } }

    [RelayCommand] private void SetThemeMode(string mode) => ThemeMode = mode;
    [RelayCommand] private void SetColorTheme(ThemeChoice t) => ColorTheme = t.Theme;

    // ═══════════════════ Ce profil : tableau de bord ═══════════════════

    public ObservableCollection<DashRowItem> DashRows { get; } = new();

    private static string RowLabel(string key) => key switch
    {
        "hero" => Loc.T("Dash.Hero"),
        "continue" => Loc.T("Ui.ReprendreLaLecture"),
        "series" => Loc.T("Home.SeriesInProgress"),
        "mylist" => Loc.T("Home.MyList"),
        "recommended" => Loc.T("Home.Recommended"),
        "because" => Loc.T("Dash.Because"),
        "favchannels" => Loc.T("Ui.VosChainesFavorites"),
        "recentchannels" => Loc.T("Ui.ChainesRecemmentRegardees"),
        "recentmovies" => Loc.T("Ui.FilmsAjoutesRecemment"),
        "recentseries" => Loc.T("Ui.SeriesAjouteesRecemment"),
        _ => key,
    };

    private void BuildDashRows()
    {
        DashRows.Clear();
        var order = Prefs.HomeRows.Where(ProfilePrefs.AllRows.Contains).Concat(ProfilePrefs.AllRows.Except(Prefs.HomeRows)).Distinct();
        foreach (var key in order)
            DashRows.Add(new DashRowItem { Key = key, Label = RowLabel(key), IsVisible = !Prefs.HiddenRows.Contains(key), Changed = SaveDashRows });
        MarkDashEnds();
    }

    private void MarkDashEnds()
    {
        for (var i = 0; i < DashRows.Count; i++)
        {
            DashRows[i].IsFirst = i == 0;
            DashRows[i].IsLast = i == DashRows.Count - 1;
        }
    }

    private void SaveDashRows()
    {
        if (_loading || P is not { } p) return;
        p.Prefs.HomeRows = DashRows.Select(r => r.Key).ToList();
        p.Prefs.HiddenRows = DashRows.Where(r => !r.IsVisible).Select(r => r.Key).ToList();
        Save();
        Lib.NotifyPrefsChanged();
    }

    [RelayCommand]
    private void MoveRowUp(DashRowItem row)
    {
        var i = DashRows.IndexOf(row);
        if (i <= 0) return;
        DashRows.Move(i, i - 1);
        MarkDashEnds();
        SaveDashRows();
    }

    [RelayCommand]
    private void MoveRowDown(DashRowItem row)
    {
        var i = DashRows.IndexOf(row);
        if (i < 0 || i >= DashRows.Count - 1) return;
        DashRows.Move(i, i + 1);
        MarkDashEnds();
        SaveDashRows();
    }

    [RelayCommand]
    private void ResetDashboard()
    {
        if (P is not { } p) return;
        p.Prefs.HomeRows = ProfilePrefs.AllRows.ToList();
        p.Prefs.HiddenRows = new List<string>();
        _loading = true;
        BuildDashRows();
        _loading = false;
        Save();
        Lib.NotifyPrefsChanged();
        _main.Toast(Loc.T("Settings.DashReset"));
    }

    // ═══════════════════ Ce profil : historique et suggestions ═══════════════════

    [ObservableProperty] private bool _trackHistory;
    [ObservableProperty] private bool _resumePlayback;
    [ObservableProperty] private int _watchedThreshold;
    [ObservableProperty] private bool _suggestionsFromHistory;

    partial void OnTrackHistoryChanged(bool value) => SetPref(p => p.TrackHistory = value);
    partial void OnResumePlaybackChanged(bool value) => SetPref(p => p.ResumePlayback = value);
    partial void OnWatchedThresholdChanged(int value) => SetPref(p => p.WatchedThresholdPercent = Math.Clamp(value, 50, 100), notify: false);
    partial void OnSuggestionsFromHistoryChanged(bool value) => SetPref(p => p.SuggestionsFromHistory = value);

    private void SetPref(Action<ProfilePrefs> set, bool notify = true)
    {
        if (_loading || P is not { } p) return;
        set(p.Prefs);
        Save();
        if (notify) Lib.NotifyPrefsChanged();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (P is not { } p) return;
        if (!await _main.ConfirmAsync(Loc.T("Settings.ClearHistory"), Loc.T("Settings.ClearHistoryConfirm", p.Name), Loc.T("Common.Delete"))) return;
        UserDataStore.ClearHistory(p.Id);
        await Lib.SetProfileAsync(p);
        RefreshProfileInfo();
        _main.Toast(Loc.T("Settings.HistoryCleared"));
    }

    [RelayCommand]
    private async Task ClearMyListAsync()
    {
        if (P is not { } p) return;
        if (!await _main.ConfirmAsync(Loc.T("Settings.ClearMyList"), Loc.T("Settings.ClearMyListConfirm", p.Name), Loc.T("Common.Delete"))) return;
        UserDataStore.ClearWatchlist(p.Id);
        await Lib.SetProfileAsync(p);
        RefreshProfileInfo();
        _main.Toast(Loc.T("Settings.MyListCleared"));
    }

    // ═══════════════════ Ce profil : lecture automatique ═══════════════════

    public IReadOnlyList<IntOption> AutoPlayMaxChoices { get; } = new[]
    {
        new IntOption(0, Loc.T("Settings.Unlimited")),
        new IntOption(2, Loc.T("Settings.Episodes", 2)),
        new IntOption(3, Loc.T("Settings.Episodes", 3)),
        new IntOption(4, Loc.T("Settings.Episodes", 4)),
        new IntOption(5, Loc.T("Settings.Episodes", 5)),
        new IntOption(8, Loc.T("Settings.Episodes", 8)),
        new IntOption(10, Loc.T("Settings.Episodes", 10)),
    };

    [ObservableProperty] private bool _autoPlayNext;
    [ObservableProperty] private int _autoPlayCountdown;
    [ObservableProperty] private IntOption _autoPlayMaxEpisodes;

    partial void OnAutoPlayNextChanged(bool value) => SetPref(p => p.AutoPlayNext = value, notify: false);
    partial void OnAutoPlayCountdownChanged(int value) => SetPref(p => p.AutoPlayCountdown = Math.Clamp(value, 3, 60), notify: false);
    partial void OnAutoPlayMaxEpisodesChanged(IntOption value) => SetPref(p => p.AutoPlayMaxEpisodes = value.Value, notify: false);

    // ═══════════════════ Ce profil : aperçus et bandes-annonces ═══════════════════

    public IReadOnlyList<Option> TrailerLanguages { get; } =
        new[] { new Option("auto", Loc.T("Settings.TrailerLangAuto")) }
            .Concat(Loc.Languages.Select(l => new Option(l.Code, l.NativeName))).ToList();

    [ObservableProperty] private bool _hoverPreview;
    [ObservableProperty] private double _hoverDelaySeconds;
    [ObservableProperty] private bool _previewSound;
    [ObservableProperty] private Option _trailerLanguage;

    public string HoverDelayText => Loc.T("Settings.Seconds", HoverDelaySeconds.ToString("0.0#", Loc.Culture));

    partial void OnHoverPreviewChanged(bool value) => SetPref(p => p.HoverPreview = value, notify: false);
    partial void OnHoverDelaySecondsChanged(double value)
    {
        OnPropertyChanged(nameof(HoverDelayText));
        SetPref(p => p.HoverDelayMs = (int)Math.Clamp(Math.Round(value * 4) / 4 * 1000, 300, 5000), notify: false);
    }
    partial void OnPreviewSoundChanged(bool value) => SetPref(p => p.PreviewSound = value, notify: false);
    partial void OnTrailerLanguageChanged(Option value) => SetPref(p => p.TrailerLanguage = value.Key, notify: false);

    // ═══════════════════ Application : sources ═══════════════════

    public ObservableCollection<SourceRow> SourceRows { get; } = new();
    public bool HasSources => SourceRows.Count > 0;

    public void RefreshSourceRows()
    {
        SourceRows.Clear();
        foreach (var src in SettingsStore.SourcesFor(P))
        {
            var mine = src.OwnerProfileId is null || src.OwnerProfileId == P?.Id;
            var badge = !mine ? Loc.T("Source.SharedBy", MainViewModel.OwnerName(src))
                : src.IsShared ? Loc.T("Source.Shared") : Loc.T("Source.Private");
            SourceRows.Add(new SourceRow
            {
                Source = src,
                CanEdit = SettingsStore.CanEdit(src, P),
                Badge = badge,
                IsActive = Lib.Source?.Id == src.Id,
            });
        }
        OnPropertyChanged(nameof(HasSources));
    }

    [RelayCommand] private void AddSource(string kind) => _main.AddSource(kind);
    [RelayCommand] private void EditSource(SourceRow s) => _main.EditSource(s.Source);

    [RelayCommand]
    private async Task DeleteSourceAsync(SourceRow s)
    {
        await _main.DeleteSourceAsync(s.Source);
        RefreshSourceRows();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task UseSourceAsync(SourceRow s)
    {
        if (Lib.Source?.Id == s.Source.Id) return;
        await Lib.ActivateAsync(s.Source);
        RefreshSourceRows();
    }

    [RelayCommand] private Task RefreshAsync() => _main.RefreshCatalogCommand.ExecuteAsync(null);

    // ═══════════════════ Application : lecture ═══════════════════

    public IReadOnlyList<Option> HwOptions { get; } = new[]
    {
        new Option("auto-copy", Loc.T("Hw.Auto")),
        new Option("auto", Loc.T("Hw.AutoNoCopy")),
        new Option("d3d11va-copy", "Direct3D 11"),
        new Option("nvdec-copy", "NVIDIA NVDEC"),
        new Option("no", Loc.T("Hw.Off")),
    };

    public IReadOnlyList<int> TimeshiftChoices { get; } = new[] { 15, 30, 60, 90, 120, 180 };

    [ObservableProperty] private int _timeshiftMinutes;
    [ObservableProperty] private Option _hwDecode;
    [ObservableProperty] private bool _highQuality;
    [ObservableProperty] private string _audioLanguages;
    [ObservableProperty] private string _subtitleLanguages;
    [ObservableProperty] private bool _subtitlesByDefault;
    [ObservableProperty] private string _userAgent;

    partial void OnTimeshiftMinutesChanged(int value) { if (!_loading) { S.TimeshiftMinutes = value; SaveEngine(); } }
    partial void OnHwDecodeChanged(Option value) { if (!_loading) { S.HwDecode = value.Key; SaveEngine(); } }
    partial void OnHighQualityChanged(bool value) { if (!_loading) { S.HighQuality = value; SaveEngine(); } }

    // Mises à jour (copie installée seulement)
    public bool CanCheckUpdates => Updates.IsAvailable && Updates.State is not (Updates.UpdateState.Checking or Updates.UpdateState.Downloading);
    public bool UpdateReady => Updates.State == Updates.UpdateState.Ready;
    public string UpdateStatus => Updates.State switch
    {
        // Sans adresse de publication : rien à chercher, mais le choix du canal est gardé pour plus tard.
        Updates.UpdateState.Off when !Updates.HasFeed => Loc.T("Update.NoFeed", Updates.CurrentVersion),
        Updates.UpdateState.Off => Loc.T("Update.NotInstalled"),
        Updates.UpdateState.Checking => Loc.T("Update.Checking"),
        Updates.UpdateState.Downloading => Loc.T("Update.Downloading", Updates.Progress),
        Updates.UpdateState.Ready => Loc.T(Updates.ReadyIsDowngrade ? "Update.ReadyDowngrade" : "Update.Ready", Updates.ReadyVersion ?? ""),
        Updates.UpdateState.UpToDate => Loc.T("Update.UpToDate", Updates.CurrentVersion),
        Updates.UpdateState.Failed => Loc.T("Update.Failed", Updates.Error ?? ""),
        _ => Loc.T("Update.Current", Updates.CurrentVersion),
    };

    private void OnUpdatesChanged()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(UpdateReady));
        OnPropertyChanged(nameof(CanCheckUpdates));
    }

    [ObservableProperty] private bool _autoUpdate;
    partial void OnAutoUpdateChanged(bool value)
    {
        if (_loading) return;
        S.AutoUpdate = value;
        Save();
    }

    [RelayCommand] private Task CheckUpdatesAsync() => Updates.CheckAsync();
    [RelayCommand] private void RestartForUpdate() => _main.RestartForUpdate();

    // Canal des mises à jour : 0 = stable, 1 = bêta
    [ObservableProperty] private int _updateChannelIndex;
    partial void OnUpdateChannelIndexChanged(int value)
    {
        if (_loading) return;
        _ = ChangeChannelAsync(value == 1);
    }

    private async Task ChangeChannelAsync(bool beta)
    {
        var ok = beta
            ? await _main.ConfirmAsync(Loc.T("Update.ToBetaTitle"), Loc.T("Update.ToBetaMsg"), Loc.T("Update.ToBetaConfirm"))
            : await _main.ConfirmAsync(Loc.T("Update.ToStableTitle"), Loc.T("Update.ToStableMsg"), Loc.T("Update.ToStableConfirm"));
        if (!ok)
        {
            _loading = true;
            UpdateChannelIndex = beta ? 0 : 1;
            _loading = false;
            return;
        }
        if (beta)
        {
            // Point de retour : la configuration d'avant la bêta, restaurable depuis « Sauvegardes automatiques ».
            try
            {
                await Backup.CreateAutomaticAsync("avant-beta");
                _main.Toast(Loc.T("Backup.AutoDone"), Loc.T("Backup.AutoDoneBeta"));
            }
            catch (Exception ex)
            {
                Log.Warn("Sauvegarde avant la bêta impossible : " + ex.Message);
            }
        }
        Updates.SetChannel(beta);
        OnUpdatesChanged();
    }

    // Sauvegarde et restauration (code parental demandé : la sauvegarde contient les sources et le code)
    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (!await _main.RequestPinAsync(forContent: false, force: true)) return;
        _main.ShowDialog(new BackupDialogViewModel(_main, BackupDialogMode.Create));
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (!await _main.RequestPinAsync(forContent: false, force: true)) return;
        _main.ShowDialog(new BackupDialogViewModel(_main, BackupDialogMode.Restore));
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        try { Process.Start(new ProcessStartInfo(Backup.AutoFolder) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Dossier des sauvegardes : " + ex.Message); }
    }

    // Affichage fluide (mode de composition de la fenêtre, appliqué au prochain démarrage)
    [ObservableProperty] private bool _smoothRendering;
    partial void OnSmoothRenderingChanged(bool value)
    {
        if (_loading) return;
        S.SmoothRendering = value;
        Save();
        _main.Toast(Loc.T("Settings.RestartNeeded"), Loc.T("Settings.RestartNeededMsg"));
    }

    // Mini-lecteur
    [ObservableProperty] private bool _miniPlayerOnBack;
    [ObservableProperty] private int _miniPlayerSize;
    partial void OnMiniPlayerOnBackChanged(bool value)
    {
        if (_loading) return;
        S.MiniPlayerOnBack = value;
        Save();
        _main.Player.RefreshLeaveTip();
    }
    partial void OnMiniPlayerSizeChanged(int value)
    {
        if (_loading || value is < 0 or > 2) return;
        S.MiniPlayerSize = value;
        S.MiniPlayerWidth = AppSettings.MiniPresetWidths[value];
        Save();
        _main.Player.NotifyMiniLayoutChanged();
    }
    [RelayCommand] private void SetMiniSize(string size) => MiniPlayerSize = int.Parse(size, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Taille prédéfinie correspondant à la largeur actuelle (-1 : taille libre, aucune pastille cochée).</summary>
    private int MiniPresetIndex() => S.MiniPlayerWidth is { } w
        ? Array.FindIndex(AppSettings.MiniPresetWidths, p => Math.Abs(p - w) < 1)
        : Math.Clamp(S.MiniPlayerSize, 0, 2);

    /// <summary>Le mini-lecteur a été redimensionné à la souris pendant que la page est ouverte.</summary>
    private void OnPlayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != PlayerViewModel.MiniLayoutProperty) return;
        _loading = true;
        MiniPlayerSize = MiniPresetIndex();
        _loading = false;
    }

    /// <summary>Remet le mini-lecteur à sa taille moyenne, en bas à droite.</summary>
    [RelayCommand]
    private void ResetMini()
    {
        S.MiniPlayerWidth = null;
        S.MiniPlayerX = S.MiniPlayerY = null;
        S.MiniPlayerCorner = "br";
        S.MiniPlayerSize = 1;
        Save();
        _loading = true;
        MiniPlayerSize = 1;
        _loading = false;
        _main.Player.NotifyMiniLayoutChanged();
    }
    partial void OnAudioLanguagesChanged(string value) { if (!_loading) { S.AudioLanguages = value; SaveEngine(); } }
    partial void OnSubtitleLanguagesChanged(string value) { if (!_loading) { S.SubtitleLanguages = value; SaveEngine(); } }
    partial void OnSubtitlesByDefaultChanged(bool value) { if (!_loading) { S.SubtitlesByDefault = value; SaveEngine(); } }
    partial void OnUserAgentChanged(string value) { if (!_loading) { S.DefaultUserAgent = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); SaveEngine(); } }

    private void SaveEngine()
    {
        Save();
        if (_main.Player.Engine.IsInitialized)
            _main.Player.Engine.ApplyEngineSettings(PlayerViewModel.EngineSettings(S));
    }

    // Sortie audio
    public IReadOnlyList<Option> AudioOutputs { get; private set; } = Array.Empty<Option>();
    private Option? _audioOutput;
    public Option? AudioOutput
    {
        get => _audioOutput;
        set
        {
            if (value is null || value == _audioOutput) return;
            _audioOutput = value;
            S.AudioDevice = value.Key;
            Save();
            _main.Player.Engine.SetAudioDevice(value.Key);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void RefreshAudioOutputs()
    {
        AudioOutputs = _main.Player.Engine.GetAudioDevices()
            .Select(d => new Option(d.Name, d.Name == "auto" ? Loc.T("Player.AudioDefault") : d.Description)).ToList();
        _audioOutput = AudioOutputs.FirstOrDefault(o => o.Key == (S.AudioDevice ?? "auto")) ?? AudioOutputs.FirstOrDefault();
        OnPropertyChanged(nameof(AudioOutputs));
        OnPropertyChanged(nameof(AudioOutput));
    }

    // ═══════════════════ Application : enregistrements ═══════════════════

    public IReadOnlyList<IntOption> InstantStopChoices { get; } = new[]
    {
        new IntOption(0, Loc.T("Settings.RecStopNever")),
        new IntOption(-1, Loc.T("Settings.RecStopEndOfProgram")),
        new IntOption(15, Loc.T("Settings.Minutes", 15)),
        new IntOption(30, Loc.T("Settings.Minutes", 30)),
        new IntOption(60, Loc.T("Settings.Hours", 1)),
        new IntOption(120, Loc.T("Settings.Hours", 2)),
        new IntOption(180, Loc.T("Settings.Hours", 3)),
        new IntOption(240, Loc.T("Settings.Hours", 4)),
    };

    [ObservableProperty] private string _recordingsFolder;
    [ObservableProperty] private int _marginBefore;
    [ObservableProperty] private int _marginAfter;
    [ObservableProperty] private string _recordingFormat;
    [ObservableProperty] private string _recordingQuality;
    [ObservableProperty] private string _recordingNameTemplate;
    [ObservableProperty] private IntOption _instantStop;
    [ObservableProperty] private int _minFreeGb;
    [ObservableProperty] private int _maxSimultaneous;

    partial void OnRecordingsFolderChanged(string value) { if (!_loading) { S.RecordingsFolder = value; Save(); OnPropertyChanged(nameof(FreeSpaceText)); } }
    partial void OnMarginBeforeChanged(int value) { if (!_loading) { S.RecordingMarginBeforeMin = Math.Clamp(value, 0, 30); Save(); } }
    partial void OnMarginAfterChanged(int value) { if (!_loading) { S.RecordingMarginAfterMin = Math.Clamp(value, 0, 60); Save(); } }
    partial void OnRecordingFormatChanged(string value) { if (!_loading) { S.RecordingFormat = value; Save(); } OnPropertyChanged(nameof(RecordingNamePreview)); }
    partial void OnRecordingQualityChanged(string value) { if (!_loading) { S.RecordingQuality = value; Save(); } }
    partial void OnRecordingNameTemplateChanged(string value)
    {
        if (!_loading)
        {
            S.RecordingNameTemplate = string.IsNullOrWhiteSpace(value) ? new AppSettings().RecordingNameTemplate : value.Trim();
            Save();
        }
        OnPropertyChanged(nameof(RecordingNamePreview));
    }
    partial void OnInstantStopChanged(IntOption value) { if (!_loading) { S.InstantRecordingStopMinutes = value.Value; Save(); } }
    partial void OnMinFreeGbChanged(int value) { if (!_loading) { S.RecordingMinFreeGb = Math.Clamp(value, 0, 500); Save(); } }
    partial void OnMaxSimultaneousChanged(int value) { if (!_loading) { S.MaxSimultaneousRecordings = Math.Clamp(value, 1, 8); Save(); } }

    [RelayCommand] private void SetRecordingFormat(string f) => RecordingFormat = f;
    [RelayCommand] private void SetRecordingQuality(string q) => RecordingQuality = q;
    [RelayCommand] private void InsertNameToken(string token) => RecordingNameTemplate = (RecordingNameTemplate ?? "").TrimEnd() + " " + token;
    [RelayCommand] private void ResetNameTemplate() => RecordingNameTemplate = new AppSettings().RecordingNameTemplate;

    /// <summary>Exemple de nom de fichier obtenu avec le modèle actuel.</summary>
    public string RecordingNamePreview =>
        Recording.RecordingNames.Build(RecordingNameTemplate, Loc.T("Settings.RecSampleTitle"), Loc.T("Settings.RecSampleChannel"), DateTimeOffset.Now, RecordingFormat);

    /// <summary>Espace libre sur le disque du dossier d'enregistrement.</summary>
    public string FreeSpaceText
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(RecordingsFolder));
                if (string.IsNullOrEmpty(root)) return "";
                var d = new DriveInfo(root);
                return Loc.T("Settings.FreeSpace", (d.AvailableFreeSpace / 1024d / 1024 / 1024).ToString("0.0", Loc.Culture), root);
            }
            catch { return ""; }
        }
    }

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingsFolder);
            Process.Start(new ProcessStartInfo(RecordingsFolder) { UseShellExecute = true });
        }
        catch { /* ignoré */ }
    }

    // ═══════════════════ Application : contrôle parental ═══════════════════

    [ObservableProperty] private bool _autoLockAdult;
    [ObservableProperty] private bool _protectSettings;
    public bool HasPin => S.HasPin;

    partial void OnAutoLockAdultChanged(bool value)
    {
        if (!_loading) _ = GuardParentalAsync(() => S.AutoLockAdult = value, () => AutoLockAdult = !value);
    }
    partial void OnProtectSettingsChanged(bool value)
    {
        if (!_loading) _ = GuardParentalAsync(() => S.ProtectSettings = value, () => ProtectSettings = !value);
    }

    /// <summary>Les réglages du contrôle parental ne changent qu'avec le code parental ; sinon l'interrupteur revient en arrière.</summary>
    private async Task GuardParentalAsync(Action apply, Action revert)
    {
        if (!await _main.RequestPinAsync(forContent: false))
        {
            _loading = true;
            try { revert(); } finally { _loading = false; }
            return;
        }
        apply();
        Save();
        Lib.RefreshParental();
    }

    public IEnumerable<Category> LockedCategories =>
        Lib.Catalog is { } c
            ? c.LiveCategories.Concat(c.MovieCategories).Concat(c.SeriesCategories).Where(x => x.IsLocked)
            : Enumerable.Empty<Category>();

    [RelayCommand]
    private async Task SetPinAsync()
    {
        if (S.HasPin && !await _main.RequestPinAsync(forContent: false, force: true)) return;
        if (await _main.CreatePinAsync()) _main.Toast(Loc.T("Toast.PinSaved"), Loc.T("Toast.PinSavedMsg"));
        OnPropertyChanged(nameof(HasPin));
        OnPropertyChanged(nameof(LockedCategories));
    }

    [RelayCommand]
    private async Task RemovePinAsync()
    {
        // Sans code, un profil enfant pourrait retirer son propre mode enfant.
        if (S.Profiles.Any(p => p.IsKid))
        {
            _main.Toast(Loc.T("Pin.RemoveBlocked"), Loc.T("Pin.RemoveBlockedMsg"), true);
            return;
        }
        if (!await _main.RequestPinAsync()) return;
        S.ParentalPinHash = null;
        Save();
        Lib.ParentalUnlocked = false;
        Lib.RefreshParental();
        OnPropertyChanged(nameof(HasPin));
        OnPropertyChanged(nameof(LockedCategories));
        _main.Toast(Loc.T("Toast.ParentalOff"));
    }

    [RelayCommand]
    private void Relock()
    {
        Lib.ParentalUnlocked = false;
        _main.Toast(Loc.T("Toast.Relocked"));
    }

    [RelayCommand]
    private async Task UnlockAsync(Category c)
    {
        if (!await _main.RequestPinAsync(forContent: false)) return;
        Lib.SetCategoryLock(c, false);
        OnPropertyChanged(nameof(LockedCategories));
    }

    // ═══════════════════ Application : profils ═══════════════════

    [ObservableProperty] private bool _askProfileAtStartup = SettingsStore.Current.AskProfileAtStartup;
    partial void OnAskProfileAtStartupChanged(bool value) { S.AskProfileAtStartup = value; Save(); }
    [RelayCommand] private void ManageProfiles() => _main.ShowProfilePicker("manage");
    public string ProfilesCountText => Loc.T("Settings.ProfilesCount", S.Profiles.Count);

    // ═══════════════════ Application : TMDB ═══════════════════

    [ObservableProperty] private string _tmdbKey;
    [ObservableProperty] private string? _tmdbStatus;
    [ObservableProperty] private bool _tmdbOk;
    [ObservableProperty] private bool _tmdbBusy;
    public bool HasTmdbKey => !string.IsNullOrEmpty(S.TmdbKey);

    [RelayCommand]
    private async Task SaveTmdbAsync()
    {
        var key = TmdbKey.Trim();
        if (key.Length == 0)
        {
            S.TmdbKey = null;
            Save();
            TmdbStatus = Loc.T("Tmdb.KeyRemoved");
            TmdbOk = true;
            OnPropertyChanged(nameof(HasTmdbKey));
            return;
        }
        TmdbBusy = true;
        TmdbStatus = Loc.T("Common.Checking");
        try
        {
            var (ok, msg) = await Lib.Tmdb.TestKeyAsync(key, CancellationToken.None);
            TmdbOk = ok;
            TmdbStatus = msg;
            if (ok)
            {
                S.TmdbKey = key;
                Save();
            }
        }
        finally
        {
            TmdbBusy = false;
            OnPropertyChanged(nameof(HasTmdbKey));
        }
    }

    [RelayCommand]
    private void OpenTmdbSite()
    {
        try { Process.Start(new ProcessStartInfo("https://www.themoviedb.org/settings/api") { UseShellExecute = true }); }
        catch { /* ignoré */ }
    }

    // ═══════════════════ Application : données et à propos ═══════════════════

    [ObservableProperty] private int _catalogRefreshHours;
    partial void OnCatalogRefreshHoursChanged(int value)
    {
        if (_loading) return;
        S.CatalogRefreshHours = Math.Clamp(value, 1, 168);
        S.EpgRefreshHours = S.CatalogRefreshHours;
        Save();
    }

    [RelayCommand]
    private void ClearImageCache()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(AppPaths.ImageCache))
                try { Directory.Delete(d, true); } catch { /* ignoré */ }
            _main.Toast(Loc.T("Toast.ImageCacheCleared"));
        }
        catch (Exception ex) { _main.Toast(Loc.T("Common.Cache"), ex.Message, true); }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true }); }
        catch { /* ignoré */ }
    }

    /// <summary>Licences des composants tiers, livrées à côté de l'exécutable.</summary>
    private static string LicensesFile =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
    public bool HasLicenses => File.Exists(LicensesFile);

    [RelayCommand]
    private void OpenLicenses()
    {
        // Explorateur, fichier sélectionné : un .md n'a pas toujours d'application associée.
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LicensesFile}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Licences : " + ex.Message); }
    }

    public string Version => "MilfTV " + Updates.CurrentVersion;

    private static void Save() => SettingsStore.Save();
}
