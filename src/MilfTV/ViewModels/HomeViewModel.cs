using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;

namespace MilfTV.ViewModels;

public sealed partial class WelcomeViewModel(MainViewModel main) : ViewModelBase
{
    public MainViewModel Main { get; } = main;
    public override bool KeepView => false;

    public IReadOnlyList<LanguageInfo> LanguageOptions => LanguageChoice.Options;
    public LanguageInfo SelectedLanguage
    {
        get => LanguageChoice.Current;
        set => LanguageChoice.Apply(value);
    }

    [RelayCommand] private void Add(string kind) => Main.AddSource(kind);
}

/// <summary>Tableau de bord du profil : reprises, séries en cours, « Ma liste », suggestions, chaînes (communes).</summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private int _reloadToken;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        Lib.CatalogChanged += Reload;
        Lib.FavoritesChanged += Reload;
        Lib.ParentalChanged += Reload;
        Lib.ProfileChanged += Reload;
        Lib.PrefsChanged += Reload;
        Lib.EpgChanged += RefreshNow;
        main.WatchChanged += Reload;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => RefreshNow());
        Reload();
    }

    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;

    public ObservableCollection<WatchEntry> ContinueWatching { get; } = new();
    public ObservableCollection<SeriesProgress> SeriesInProgress { get; } = new();
    public ObservableCollection<CatalogItem> MyList { get; } = new();
    public ObservableCollection<CatalogItem> Recommended { get; } = new();
    public ObservableCollection<CatalogItem> Because1 { get; } = new();
    public ObservableCollection<CatalogItem> Because2 { get; } = new();
    public ObservableCollection<LiveChannel> FavoriteChannels { get; } = new();
    public ObservableCollection<LiveChannel> RecentChannels { get; } = new();
    public ObservableCollection<Movie> RecentMovies { get; } = new();
    public ObservableCollection<Series> RecentSeries { get; } = new();

    [ObservableProperty] private string? _because1Title;
    [ObservableProperty] private string? _because2Title;
    [ObservableProperty] private CatalogItem? _hero;
    [ObservableProperty] private string? _heroTitle;
    [ObservableProperty] private string? _heroPlot;
    [ObservableProperty] private string? _heroMeta;
    [ObservableProperty] private string? _heroImage;
    [ObservableProperty] private string? _heroBackdrop;
    [ObservableProperty] private string? _heroBadge;
    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string? _stats;

    private ProfilePrefs Prefs => Lib.Prefs;
    private bool Shown(string row) => !Prefs.HiddenRows.Contains(row);

    public bool HasContinue => ContinueWatching.Count > 0 && Shown("continue");
    public bool HasSeriesInProgress => SeriesInProgress.Count > 0 && Shown("series");
    public bool HasMyList => MyList.Count > 0 && Shown("mylist");
    public bool HasRecommended => Recommended.Count > 0 && Shown("recommended") && Prefs.SuggestionsFromHistory;
    public bool HasBecause1 => Because1.Count > 0 && Shown("because") && Prefs.SuggestionsFromHistory;
    public bool HasBecause2 => Because2.Count > 0 && Shown("because") && Prefs.SuggestionsFromHistory;
    public bool HasFavoriteChannels => FavoriteChannels.Count > 0 && Shown("favchannels");
    public bool HasRecentChannels => RecentChannels.Count > 0 && Shown("recentchannels");
    public bool HasRecentMovies => RecentMovies.Count > 0 && Shown("recentmovies");
    public bool HasRecentSeries => RecentSeries.Count > 0 && Shown("recentseries");
    public bool HasHero => Hero is not null && Shown("hero");

    // Position (ligne de la grille) de chaque rangée, selon l'ordre choisi par le profil.
    [ObservableProperty] private int _rowHero = 1;
    [ObservableProperty] private int _rowContinue = 2;
    [ObservableProperty] private int _rowSeries = 3;
    [ObservableProperty] private int _rowMyList = 4;
    [ObservableProperty] private int _rowRecommended = 5;
    [ObservableProperty] private int _rowBecause = 6;
    [ObservableProperty] private int _rowBecause2 = 7;
    [ObservableProperty] private int _rowFavChannels = 8;
    [ObservableProperty] private int _rowRecentChannels = 9;
    [ObservableProperty] private int _rowRecentMovies = 10;
    [ObservableProperty] private int _rowRecentSeries = 11;
    [ObservableProperty] private int _rowQuick = 12;

    private void ComputeRows()
    {
        var order = Prefs.HomeRows.Concat(ProfilePrefs.AllRows.Except(Prefs.HomeRows)).ToList();
        var idx = 1;
        foreach (var r in order)
        {
            switch (r)
            {
                case "hero": RowHero = idx++; break;
                case "continue": RowContinue = idx++; break;
                case "series": RowSeries = idx++; break;
                case "mylist": RowMyList = idx++; break;
                case "recommended": RowRecommended = idx++; break;
                case "because": RowBecause = idx++; RowBecause2 = idx++; break;
                case "favchannels": RowFavChannels = idx++; break;
                case "recentchannels": RowRecentChannels = idx++; break;
                case "recentmovies": RowRecentMovies = idx++; break;
                case "recentseries": RowRecentSeries = idx++; break;
            }
        }
        RowQuick = idx;
    }

    public override void OnNavigatedTo()
    {
        UpdateGreeting();
        RefreshNow();
        _clock.Start();
    }

    public override void OnNavigatedFrom() => _clock.Stop();

    private void UpdateGreeting()
    {
        var g = DateTime.Now.Hour switch { < 5 => Loc.T("Home.Night"), < 12 => Loc.T("Home.Morning"), < 18 => Loc.T("Home.Afternoon"), _ => Loc.T("Home.Evening") };
        Greeting = Lib.Profile is { } p ? Loc.T("Home.GreetingName", g, p.Name) : g;
    }

    private async void Reload()
    {
        try { await ReloadCoreAsync(); }
        catch (Exception ex) { Log.Error("Accueil", ex); }
    }

    public override void Detach()
    {
        _clock.Stop();
        Lib.CatalogChanged -= Reload;
        Lib.FavoritesChanged -= Reload;
        Lib.ParentalChanged -= Reload;
        Lib.ProfileChanged -= Reload;
        Lib.PrefsChanged -= Reload;
        Lib.EpgChanged -= RefreshNow;
        _main.WatchChanged -= Reload;
    }

    private async Task ReloadCoreAsync()
    {
        var token = ++_reloadToken;
        ComputeRows();
        var c = Lib.Catalog;
        var src = Lib.Source;
        UpdateGreeting();
        if (c is null || src is null)
        {
            Clear();
            Hero = null;
            NotifyAll();
            return;
        }

        // Calculs (suggestions sur tout le catalogue) en arrière-plan.
        var data = await Task.Run(() =>
        {
            var useHistory = Lib.Prefs.SuggestionsFromHistory;
            var seeds = useHistory ? Lib.GetWatchedItems(10).Take(2).ToList() : new List<CatalogItem>();
            return new
            {
                Continue = Lib.GetContinueWatching(20).Where(w => !IsHiddenEntry(c, w)).ToList(),
                Series = Lib.GetSeriesInProgress(30).Where(sp => !sp.Series.IsWatched).Take(20).ToList(),
                MyList = Lib.GetWatchlistItems().Take(30).ToList(),
                Recommended = useHistory ? Lib.Recommend(24) : new List<CatalogItem>(),
                Seeds = seeds,
                Because = seeds.Select(s => Lib.Recommend(18, s)).ToList(),
                FavChannels = c.Channels.Where(x => x.IsFavorite && !Lib.IsHidden(x)).Take(40).ToList(),
                Recent = UserDataStore.GetRecentChannels(src.Id, 16)
                    .Select(w => c.ChannelById.GetValueOrDefault(w.ItemId)).Where(ch => ch is not null && !Lib.IsHidden(ch)).Cast<LiveChannel>().ToList(),
                RecentMovies = c.Movies.Where(x => !Lib.IsHidden(x)).OrderByDescending(x => x.Added).Take(30).ToList(),
                RecentSeries = c.Series.Where(x => !Lib.IsHidden(x)).OrderByDescending(x => x.Added).Take(30).ToList(),
            };
        });
        if (token != _reloadToken) return;

        Fill(ContinueWatching, data.Continue);
        Fill(SeriesInProgress, data.Series);
        Fill(MyList, data.MyList);
        Fill(Recommended, data.Recommended);
        Fill(FavoriteChannels, data.FavChannels);
        Fill(RecentChannels, data.Recent);
        Fill(RecentMovies, data.RecentMovies);
        Fill(RecentSeries, data.RecentSeries);
        Fill(Because1, data.Because.ElementAtOrDefault(0) ?? new());
        Fill(Because2, data.Because.ElementAtOrDefault(1) ?? new());
        Because1Title = data.Seeds.Count > 0 ? Loc.T("Home.BecauseYouWatched", TextNorm.SplitYear(data.Seeds[0].Name).Title) : null;
        Because2Title = data.Seeds.Count > 1 ? Loc.T("Home.BecauseYouWatched", TextNorm.SplitYear(data.Seeds[1].Name).Title) : null;
        Stats = Loc.T("Home.Stats", c.Channels.Count, c.Movies.Count, c.Series.Count);
        PickHero(c, data.Recommended);
        RefreshNow();
        NotifyAll();
    }

    private bool IsHiddenEntry(Catalog c, WatchEntry w) => w.Kind switch
    {
        ContentKind.Movie => c.MovieById.TryGetValue(w.ItemId, out var m) && Lib.IsHidden(m),
        ContentKind.Episode => w.ParentId is not null && c.SeriesById.TryGetValue(w.ParentId, out var s) && Lib.IsHidden(s),
        _ => false
    };

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void Clear()
    {
        ContinueWatching.Clear(); SeriesInProgress.Clear(); MyList.Clear(); Recommended.Clear();
        Because1.Clear(); Because2.Clear(); FavoriteChannels.Clear(); RecentChannels.Clear();
        RecentMovies.Clear(); RecentSeries.Clear();
    }

    private void RefreshNow()
    {
        var list = FavoriteChannels.Concat(RecentChannels).Distinct().ToList();
        if (list.Count > 0) Lib.UpdateNowNext(list);
    }

    /// <summary>Mise en avant : une suggestion pour le profil, sinon une nouveauté.</summary>
    private void PickHero(Catalog c, List<CatalogItem> recommended)
    {
        if (Hero is not null && (recommended.Contains(Hero) || c.MovieById.ContainsKey(Hero.Id) && !Lib.IsHidden(Hero))) return;
        var pick = recommended.Where(r => !string.IsNullOrEmpty(r.ImageUrl)).Take(6).ToList();
        CatalogItem? item;
        if (pick.Count > 0)
        {
            item = pick[Random.Shared.Next(pick.Count)];
            HeroBadge = Loc.T("Home.ForYou");
        }
        else
        {
            var pool = c.Movies.Where(m => !Lib.IsHidden(m) && !string.IsNullOrEmpty(m.ImageUrl)).OrderByDescending(m => m.Added).Take(40).ToList();
            item = pool.Count > 0 ? pool[Random.Shared.Next(pool.Count)] : null;
            HeroBadge = Loc.T("Ui.Nouveaute");
        }
        if (item is null) { Hero = null; return; }
        Hero = item;
        var (title, _) = TextNorm.SplitYear(item.Name);
        HeroTitle = title;
        var (year, genre, rating, plot) = item switch
        {
            Movie m => (m.YearText, m.Genre, m.RatingText, m.Plot),
            Series s => (s.YearText, s.Genre, s.RatingText, s.Plot),
            _ => (null, null, null, null)
        };
        HeroPlot = plot;
        HeroMeta = string.Join("  ·  ", new[] { year, genre, rating is { } r ? "★ " + r : null }.Where(x => !string.IsNullOrEmpty(x)));
        HeroImage = item.ImageUrl;
        HeroBackdrop = item is Series sr ? sr.Backdrop : null;
        _ = EnrichHeroAsync(item);
    }

    private async Task EnrichHeroAsync(CatalogItem item)
    {
        try
        {
            MediaDetails? details = null;
            if (Lib.Tmdb.IsConfigured)
            {
                details = item switch
                {
                    Movie m => await Lib.Tmdb.GetMovieAsync(m.TmdbId, m.Name, m.Year, CancellationToken.None),
                    Series s => await Lib.Tmdb.GetSeriesAsync(s.TmdbId, s.Name, s.Year, CancellationToken.None),
                    _ => null
                };
            }
            else if (item is Movie mv && Lib.Provider is { } p)
            {
                details = await p.GetMovieDetailsAsync(mv, CancellationToken.None);
            }
            if (Hero != item || details is null) return;
            HeroBackdrop = details.BackdropUrl ?? HeroBackdrop;
            HeroPlot = details.Plot ?? HeroPlot;
            if (Lib.Tmdb.IsConfigured && !string.IsNullOrEmpty(details.Title)) HeroTitle = details.Title;
            var meta = new List<string?>
            {
                details.Year?.ToString(),
                details.Genres.Count > 0 ? string.Join(", ", details.Genres.Take(2)) : null,
                details.RuntimeMinutes is > 0 ? TextNorm.Duration(details.RuntimeMinutes.Value * 60) : null,
                details.Rating is > 0 ? "★ " + details.Rating.Value.ToString("0.0", Loc.Culture) : null,
            };
            var joined = string.Join("  ·  ", meta.Where(x => !string.IsNullOrEmpty(x)));
            if (joined.Length > 0) HeroMeta = joined;
        }
        catch (Exception ex)
        {
            Log.Warn("Mise en avant : " + ex.Message);
        }
    }

    private void NotifyAll()
    {
        foreach (var n in new[] { nameof(HasContinue), nameof(HasSeriesInProgress), nameof(HasMyList), nameof(HasRecommended),
                     nameof(HasBecause1), nameof(HasBecause2), nameof(HasFavoriteChannels), nameof(HasRecentChannels),
                     nameof(HasRecentMovies), nameof(HasRecentSeries), nameof(HasHero) })
            OnPropertyChanged(n);
    }

    partial void OnHeroChanged(CatalogItem? value) => OnPropertyChanged(nameof(HasHero));

    [RelayCommand] private Task ResumeAsync(WatchEntry e) => _main.ResumeAsync(e);
    [RelayCommand] private Task PlayChannelAsync(LiveChannel ch) => _main.PlayChannelAsync(ch, FavoriteChannels.Contains(ch) ? FavoriteChannels : null);
    [RelayCommand] private void Open(CatalogItem item) => _main.OpenDetails(item);
    [RelayCommand] private void OpenSeries(SeriesProgress sp) => _main.OpenDetails(sp.Series);
    [RelayCommand] private void Go(string key) => _main.Navigate(key);

    [RelayCommand]
    private async Task PlayHeroAsync()
    {
        if (Hero is Movie m) await _main.PlayMovieAsync(m);
        else if (Hero is not null) _main.OpenDetails(Hero);
    }

    [RelayCommand]
    private void OpenHero()
    {
        if (Hero is not null) _main.OpenDetails(Hero);
    }

    [RelayCommand]
    private void RemoveContinue(WatchEntry e)
    {
        Lib.RemoveWatch(e.Key);
        ContinueWatching.Remove(e);
        OnPropertyChanged(nameof(HasContinue));
    }
}
