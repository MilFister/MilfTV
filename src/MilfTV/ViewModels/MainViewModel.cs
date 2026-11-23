using System.Collections.ObjectModel;
using MilfTV.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Theming;

namespace MilfTV.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _badge;
}

public sealed partial class ToastItem : ObservableObject
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public bool IsError { get; init; }
    /// <summary>Bouton facultatif sous le message (ex. « Voir les nouveautés »).</summary>
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
}

public sealed partial class MainViewModel : ViewModelBase
{
    public Library Lib => Library.Instance;
    public PlayerViewModel Player { get; }
    public Recording.RecordingService Recorder { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new();

    private static NavItem[] CreateNav() => new[]
    {
        new NavItem { Key = "home", Label = Loc.T("Nav.Home"), Icon = Icons.Home },
        new NavItem { Key = "live", Label = Loc.T("Nav.Live"), Icon = Icons.Tv },
        new NavItem { Key = "guide", Label = Loc.T("Nav.Guide"), Icon = Icons.Guide },
        new NavItem { Key = "movies", Label = Loc.T("Nav.Movies"), Icon = Icons.Movie },
        new NavItem { Key = "series", Label = Loc.T("Nav.Series"), Icon = Icons.Series },
        new NavItem { Key = "favorites", Label = Loc.T("Nav.MyList"), Icon = Icons.Heart },
        new NavItem { Key = "recordings", Label = Loc.T("Nav.Recordings"), Icon = Icons.Record },
    };

    private void BuildNav()
    {
        NavItems.Clear();
        foreach (var n in CreateNav())
        {
            n.IsActive = n.Key == ActiveNav;
            NavItems.Add(n);
        }
    }

    /// <summary>Changement de langue : menu et pages reconstruits (les textes XAML se mettent à jour seuls).</summary>
    /// <summary>Vide le cache des pages ; chacune se désabonne (sinon elles continuent de réagir en arrière-plan).</summary>
    private void DropPages()
    {
        var old = _pages.Values.Concat(_back).Append(CurrentPage).Where(p => p is not null).Distinct().ToList();
        _pages.Clear();
        _back.Clear();
        foreach (var p in old) p!.Detach();
    }

    /// <summary>Source active dans la barre latérale (texte d'invitation traduit quand il n'y en a pas).</summary>
    public string SourceTitle => Lib.Source?.Name ?? Loc.T("Source.NoneShort");
    public string SourceSubtitle => Lib.Source?.KindLabel ?? Loc.T("Source.AddShort");

    private void RefreshSourceTexts()
    {
        OnPropertyChanged(nameof(SourceTitle));
        OnPropertyChanged(nameof(SourceSubtitle));
    }

    private void OnLanguageChanged()
    {
        BuildNav();
        RefreshSourceTexts();
        // Sidebar : type de source (« Codes Xtream »…) recalculé dans la nouvelle langue.
        OnPropertyChanged(nameof(Lib));
        Player.RefreshTexts();
        OnPropertyChanged(nameof(IsDark));
        // Changement de profil en cours : il reconstruit lui-même ses pages (évite une page jetable et un code PIN parasite).
        if (_profileSwitching) return;
        DropPages();
        if (CurrentPage is WelcomeViewModel) SetPage(new WelcomeViewModel(this), clearStack: true);
        else Navigate(ActiveNav);
    }

    public ObservableCollection<SourceConfig> Sources { get; } = new();
    public ObservableCollection<ToastItem> Toasts { get; } = new();

    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private ViewModelBase? _dialog;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private SearchResults? _quickResults;
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _isDark = ThemeService.IsDark;
    [ObservableProperty] private bool _isSourceMenuOpen;
    [ObservableProperty] private string _activeNav = "home";

    private readonly Dictionary<string, ViewModelBase> _pages = new();
    private readonly Stack<ViewModelBase> _back = new();
    private DispatcherTimer? _searchTimer;
    private TaskCompletionSource<bool>? _pinTcs;

    public static MainViewModel? Current { get; private set; }

    public MainViewModel()
    {
        Current = this;
        BuildNav();
        Loc.LanguageChanged += OnLanguageChanged;
        Player = new PlayerViewModel(this);
        Recorder = new Recording.RecordingService(this);

        ThemeService.Changed += () => IsDark = ThemeService.IsDark;
        Lib.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Library.Source)) RefreshSourceTexts();
            if (e.PropertyName == nameof(Library.Error) && Lib.Error is { } err && Lib.Catalog is not null)
                Toast(Loc.T("Toast.CatalogUpdate"), err, true);
        };

        try { Player.InitializeEngine(); }
        catch (Exception ex)
        {
            Log.Error("Moteur de lecture", ex);
            Dispatcher.UIThread.Post(() => Toast(Loc.T("Toast.EngineUnavailable"), Loc.T("Player.EngineMissing"), true));
        }

        CleanTimeshiftCache();
        StartWithProfiles();
        Recorder.Start();
        DevScript.TryRun(this);
    }

    private static void CleanTimeshiftCache()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.PlayerCache))
                try { File.Delete(f); } catch { /* en cours d'utilisation */ }
        }
        catch { /* ignoré */ }
    }

    // ───────────── Navigation ─────────────

    [RelayCommand]
    public void Navigate(string key)
    {
        IsSearchOpen = false;
        if (Lib.Source is null && !SettingsStore.SourcesFor(Lib.Profile).Any() && key != "settings")
        {
            SetPage(new WelcomeViewModel(this), clearStack: true);
            return;
        }
        if (key == "settings" && (SettingsStore.Current.ProtectSettings || Lib.IsKidProfile) && SettingsStore.Current.HasPin && !Lib.ParentalUnlocked)
        {
            _ = NavigateProtectedAsync();
            return;
        }
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "home" => new HomeViewModel(this),
                "live" => new LiveViewModel(this),
                "guide" => new GuideViewModel(this),
                "movies" => new VodBrowserViewModel(this, ContentKind.Movie),
                "series" => new VodBrowserViewModel(this, ContentKind.Series),
                "favorites" => new FavoritesViewModel(this),
                "recordings" => new RecordingsViewModel(this),
                "settings" => new SettingsViewModel(this),
                _ => new HomeViewModel(this)
            };
            _pages[key] = page;
        }
        ActiveNav = key;
        foreach (var n in NavItems) n.IsActive = n.Key == key;
        SetPage(page, clearStack: true);
    }

    /// <summary>Reconstruit la page Paramètres (libellés traduits) en restant sur la même section.</summary>
    public void ReopenSettings(SettingsViewModel old, string section)
    {
        if (_pages.TryGetValue("settings", out var p) && p == old) _pages.Remove("settings");
        // La page a peut-être déjà été reconstruite par OnLanguageChanged : on y rouvre la même section.
        if (CurrentPage == old) Navigate("settings");
        if (CurrentPage is SettingsViewModel s && s != old) s.SelectSectionCommand.Execute(section);
    }

    private async Task NavigateProtectedAsync()
    {
        if (await RequestPinAsync(forContent: false)) Navigate("settings");
    }

    /// <summary>Ouvre une page de détail (pile de retour).</summary>
    public void Push(ViewModelBase page)
    {
        IsSearchOpen = false;
        if (CurrentPage is not null) _back.Push(CurrentPage);
        SetPage(page, clearStack: false);
    }

    private void SetPage(ViewModelBase page, bool clearStack)
    {
        if (clearStack)
        {
            // Pages de l'historique qui ne sont pas en cache (liste ouverte depuis la recherche…) : abandonnées.
            foreach (var p in _back.Where(p => !_pages.ContainsValue(p) && p != page)) p.Detach();
            _back.Clear();
        }
        CurrentPage?.OnNavigatedFrom();
        CurrentPage = page;
        CanGoBack = _back.Count > 0;
        page.OnNavigatedTo();
    }

    [RelayCommand]
    public void GoBack()
    {
        if (Player.IsFullView)
        {
            if (IsFullscreen) { IsFullscreen = false; return; }
            Player.Leave();
            return;
        }
        if (Dialog is not null) { CloseDialog(); return; }
        if (IsSearchOpen) { IsSearchOpen = false; return; }
        if (_back.Count == 0) return;
        CurrentPage?.OnNavigatedFrom();
        var left = CurrentPage;
        CurrentPage = _back.Pop();
        if (left is not null && !_pages.ContainsValue(left) && !_back.Contains(left)) left.Detach();
        CanGoBack = _back.Count > 0;
        CurrentPage.OnNavigatedTo();
    }

    public void OpenDetails(CatalogItem item)
    {
        switch (item)
        {
            case Movie m: Push(new MovieDetailViewModel(this, m)); break;
            case Series s: Push(new SeriesDetailViewModel(this, s)); break;
            case LiveChannel ch: _ = PlayChannelAsync(ch, null); break;
        }
    }

    // ───────────── Lecture ─────────────

    public async Task PlayChannelAsync(LiveChannel ch, IEnumerable<LiveChannel>? zapList)
    {
        if (Lib.IsHidden(ch) && !await RequestPinAsync()) return;
        if (zapList is null && Lib.Catalog is { } c)
            zapList = c.Channels.Where(x => x.CategoryId == ch.CategoryId && !Lib.IsHidden(x));
        Player.IsMini = false; // lancer une lecture depuis l'application rouvre le lecteur complet
        await Player.PlayChannelAsync(ch, zapList);
    }

    public async Task PlayMovieAsync(Movie m, bool fromStart = false)
    {
        if (Lib.IsHidden(m) && !await RequestPinAsync()) return;
        Player.IsMini = false;
        await Player.PlayMovieAsync(m, fromStart);
    }

    public void OnPlayerOpened() => IsSearchOpen = false;

    public void OnPlayerClosed()
    {
        IsFullscreen = false;
        OnWatchChanged();
    }

    public event Action? WatchChanged;
    public event Action? RecordingsChanged;
    public void OnWatchChanged() => WatchChanged?.Invoke();
    public void OnRecordingsChanged() => RecordingsChanged?.Invoke();

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    // ───────────── Recherche ─────────────

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer?.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            QuickResults = null;
            IsSearchOpen = false;
            return;
        }
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, async (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            var q = SearchText;
            var res = await Task.Run(() => Lib.Search(q, 30));
            if (q != SearchText) return;
            QuickResults = res;
            IsSearchOpen = true;
        });
        _searchTimer.Start();
    }

    [RelayCommand]
    private void ShowAllResults()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        IsSearchOpen = false;
        Push(new SearchViewModel(this, SearchText));
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
        IsSearchOpen = false;
    }

    [RelayCommand]
    private void OpenSearchResult(object? item)
    {
        IsSearchOpen = false;
        switch (item)
        {
            case LiveChannel ch: _ = PlayChannelAsync(ch, null); break;
            case CatalogItem ci: OpenDetails(ci); break;
            case ProgramHit ph:
                if (ph.IsLive) _ = PlayChannelAsync(ph.Channel, null);
                else Push(new LiveViewModel(this, ph.Channel));
                break;
        }
    }

    // ───────────── Sources ─────────────

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SwitchSourceAsync(SourceConfig src)
    {
        IsSourceMenuOpen = false;
        if (src.Id == Lib.Source?.Id) return;
        Player.Close();
        DropPages();
        Navigate("home");
        await Lib.ActivateAsync(src);
    }

    [RelayCommand]
    public void AddSource(string? kind)
    {
        IsSourceMenuOpen = false;
        var k = kind switch { "m3u" => SourceKind.M3u, "stalker" => SourceKind.Stalker, _ => SourceKind.Xtream };
        ShowDialog(new SourceEditorViewModel(this, null, k));
    }

    public void EditSource(SourceConfig src)
    {
        if (!SettingsStore.CanEdit(src, Lib.Profile))
        {
            Toast(Loc.T("Source.ReadOnly"), Loc.T("Source.ReadOnlyMsg", OwnerName(src)), true);
            return;
        }
        ShowDialog(new SourceEditorViewModel(this, src, src.Kind));
    }

    public static string OwnerName(SourceConfig src) =>
        SettingsStore.Current.Profiles.FirstOrDefault(p => p.Id == src.OwnerProfileId)?.Name ?? "?";

    public async Task SaveSourceAsync(SourceConfig src, bool isNew)
    {
        var list = SettingsStore.Current.Sources;
        if (isNew || src.OwnerProfileId is null) src.OwnerProfileId ??= Lib.Profile?.Id;
        var idx = list.FindIndex(s => s.Id == src.Id);
        if (idx >= 0) list[idx] = src; else list.Add(src);
        SettingsStore.Save();
        RefreshSources();
        CloseDialog();

        if (isNew || src.Id == Lib.Source?.Id || Lib.Source is null)
        {
            Player.Close();
            DropPages();
            Navigate("home");
            await Lib.ActivateAsync(src, forceRefresh: true);
        }
        Toast(isNew ? Loc.T("Toast.SourceAdded") : Loc.T("Toast.SourceEdited"), src.Name);
    }

    public async Task DeleteSourceAsync(SourceConfig src)
    {
        if (!SettingsStore.CanEdit(src, Lib.Profile))
        {
            Toast(Loc.T("Source.ReadOnly"), Loc.T("Source.ReadOnlyMsg", OwnerName(src)), true);
            return;
        }
        if (!await ConfirmAsync(Loc.T("Source.DeleteTitle"), Loc.T("Source.DeleteMessage", src.Name), Loc.T("Common.Delete")))
            return;
        SettingsStore.Current.Sources.RemoveAll(s => s.Id == src.Id);
        SettingsStore.Save();
        Sources.Remove(src);
        Lib.ForgetSource(src);
        if (Lib.Source?.Id == src.Id)
        {
            Player.Close();
            _pages.Remove("home"); _pages.Remove("live"); _pages.Remove("guide");
            _pages.Remove("movies"); _pages.Remove("series"); _pages.Remove("favorites");
            var next = SettingsStore.SourcesFor(Lib.Profile).FirstOrDefault();
            await Lib.ActivateAsync(next);
            if (next is null) SettingsStore.Current.ActiveSourceId = null;
        }
        Toast(Loc.T("Toast.SourceDeleted"), src.Name);
    }

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (Lib.Source is { } src)
        {
            await Lib.ActivateAsync(src, forceRefresh: true);
            await Lib.RefreshEpgAsync(force: true);
        }
    }

    // ───────────── Thème ─────────────

    [RelayCommand]
    private void ToggleTheme()
    {
        var mode = ThemeService.IsDark ? "Light" : "Dark";
        SettingsStore.Current.ThemeMode = mode;
        if (Lib.Profile is { } p) p.Prefs.ThemeMode = mode;
        (CurrentPage as SettingsViewModel)?.OnNavigatedTo();
        SettingsStore.Save();
        ThemeService.Apply(mode, ThemeService.Current.Id);
    }

    // ───────────── Dialogues ─────────────

    public void ShowDialog(ViewModelBase vm) => Dialog = vm;

    [RelayCommand]
    public void CloseDialog()
    {
        // Création du premier profil : pas de bouton Annuler, donc pas d'abandon par Échap ou clic à côté.
        if (Dialog is ProfileEditorViewModel { CanCancel: false }) return;
        var closing = Dialog;
        Dialog = null;
        if (closing is PasswordDialogViewModel password) password.CancelCommand.Execute(null);
        // Fermer avant de prévenir l'appelant : sa suite peut rouvrir un dialogue (ex. l'éditeur de profil après un code annulé).
        if (closing is PinDialogViewModel) _pinTcs?.TrySetResult(false);
        if (closing is ConfirmDialogViewModel c) c.Complete(false);
    }

    /// <summary>Demande le code parental. Vrai si correct (déverrouille pour la session).
    /// force : le redemander même s'il a déjà été saisi pendant la session (actions sensibles sur les profils enfants).</summary>
    public async Task<bool> RequestPinAsync(bool forContent = true, bool force = false)
    {
        if (forContent && Lib.IsKidProfile)
        {
            Toast(Loc.T("Profile.KidBlocked"), Loc.T("Profile.KidBlockedMsg"), true);
            return false;
        }
        if (!SettingsStore.Current.HasPin || (Lib.ParentalUnlocked && !force)) return true;
        _pinTcs?.TrySetResult(false);
        var tcs = _pinTcs = new TaskCompletionSource<bool>();
        Dialog = new PinDialogViewModel(this, PinDialogMode.Verify, ok =>
        {
            if (ok) Lib.ParentalUnlocked = true;
            Dialog = null;
            tcs.TrySetResult(ok);
        });
        return await tcs.Task;
    }

    /// <summary>Fait choisir un code parental (saisi deux fois). Vrai s'il a été enregistré.</summary>
    public Task<bool> CreatePinAsync(string? prompt = null)
    {
        _pinTcs?.TrySetResult(false);
        var tcs = _pinTcs = new TaskCompletionSource<bool>();
        Dialog = new PinDialogViewModel(this, PinDialogMode.Create, ok =>
        {
            Dialog = null;
            tcs.TrySetResult(ok);
        }, prompt);
        return tcs.Task;
    }

    /// <summary>Gestion des profils (modifier, supprimer) depuis un profil enfant : code parental obligatoire.</summary>
    public async Task<bool> EnsureProfileAdminAsync() => !Lib.IsKidProfile || await RequestPinAsync(forContent: false);

    /// <summary>Créer un profil quand un profil enfant existe : code parental obligatoire,
    /// sinon un enfant pourrait se créer un profil adulte (même depuis l'écran « Qui regarde ? » au lancement).</summary>
    public async Task<bool> EnsureCanCreateProfileAsync() =>
        (!Lib.IsKidProfile && !SettingsStore.Current.Profiles.Any(p => p.IsKid)) || await RequestPinAsync(forContent: false);

    public Task<bool> ConfirmAsync(string title, string message, string? confirm = null)
    {
        var vm = new ConfirmDialogViewModel(title, message, confirm ?? Loc.T("Common.Confirm"));
        vm.Completed += _ => Dialog = null;
        Dialog = vm;
        return vm.Task;
    }

    public void Toast(string title, string? message = null, bool error = false)
    {
        var t = new ToastItem { Title = title, Message = message, IsError = error };
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Toasts.Remove(t), TimeSpan.FromSeconds(error ? 7 : 4));
    }

    /// <summary>Notification avec un bouton d'action ; elle reste plus longtemps pour laisser le temps de cliquer.</summary>
    public void Toast(string title, string? message, string actionText, Action action)
    {
        var t = new ToastItem { Title = title, Message = message, ActionText = actionText, Action = action };
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Toasts.Remove(t), TimeSpan.FromSeconds(12));
    }

    [RelayCommand]
    private void DismissToast(ToastItem t) => Toasts.Remove(t);

    [RelayCommand]
    private void RunToastAction(ToastItem t)
    {
        Toasts.Remove(t);
        t.Action?.Invoke();
    }

    /// <summary>Notes de version (Paramètres, numéro de version, annonce après une mise à jour).</summary>
    [RelayCommand]
    public void ShowChangelog() => ShowDialog(new ChangelogDialogViewModel(this));

    /// <summary>Premier lancement après une mise à jour : annonce discrète avec accès aux nouveautés.</summary>
    public void AnnounceUpdateIfNeeded()
    {
        if (!Changelog.TakeUpdateAnnouncement()) return;
        Toast(Loc.T("Changelog.UpdatedTitle", AppVersion.Display), Loc.T("Changelog.UpdatedMsg"), Loc.T("Changelog.ShowNew"), ShowChangelog);
    }

    public void Shutdown()
    {
        Player.Shutdown();
        Recorder.Shutdown();
        SettingsStore.SaveNow();
        // En dernier : le programme de mise à jour attend la fin de MilfTV pour installer la nouvelle version.
        Updates.ApplyOnExit();
    }

    /// <summary>« Redémarrer pour installer » : fermeture normale (avec la confirmation si un enregistrement est en
    /// cours), puis installation de la mise à jour et relance.</summary>
    /// <summary>Après la préparation d'une restauration : relance MilfTV (la nouvelle instance attend la fin de
    /// celle-ci, puis applique la sauvegarde avant d'ouvrir les données) et ferme normalement.</summary>
    public void RestartForRestore()
    {
        // Pas d'installation de mise à jour pendant ce redémarrage : elle remplacerait les fichiers de la nouvelle instance.
        Updates.SkipApplyOnExit = true;
        Backup.StartRestartedInstance();
        Dialog = null;
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close();
    }

    public void RestartForUpdate()
    {
        Updates.RestartRequested = true;
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close();
    }
}
