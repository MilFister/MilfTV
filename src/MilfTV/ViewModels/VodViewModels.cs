using System.Collections.ObjectModel;
using MilfTV.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;

namespace MilfTV.ViewModels;

public sealed class SortOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

/// <summary>Grille d'affiches des films ou des séries.</summary>
public sealed partial class VodBrowserViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _posterCts;

    private readonly Category _all;
    private readonly Category _fav;
    private readonly Category _recent;

    public VodBrowserViewModel(MainViewModel main, ContentKind kind)
    {
        _main = main;
        Kind = kind;
        _all = new Category { Id = "__all", Name = kind == ContentKind.Movie ? Loc.T("Vod.AllMovies") : Loc.T("Vod.AllSeries"), Kind = kind, IsVirtual = true };
        _fav = new Category { Id = "__fav", Name = Loc.T("Common.Favorites"), Kind = kind, IsVirtual = true };
        _recent = new Category { Id = "__recent", Name = Loc.T("Vod.Recent"), Kind = kind, IsVirtual = true };
        SortOptions = new[]
        {
            new SortOption { Key = "added", Label = Loc.T("Vod.SortRecent") },
            new SortOption { Key = "name", Label = Loc.T("Vod.SortName") },
            new SortOption { Key = "rating", Label = Loc.T("Vod.SortRating") },
            new SortOption { Key = "year", Label = Loc.T("Vod.SortYear") },
        };
        _selectedSort = SortOptions[0];
        // Films : « vus » ; séries : « vues » = au moins un épisode regardé ou commencé.
        var movie = kind == ContentKind.Movie;
        WatchFilterOptions = new[]
        {
            new SortOption { Key = "all", Label = Loc.T("Vod.WatchAll") },
            new SortOption { Key = "unwatched", Label = Loc.T(movie ? "Vod.WatchUnseen.Movie" : "Vod.WatchUnseen.Series") },
            new SortOption { Key = "watched", Label = Loc.T(movie ? "Vod.WatchSeen.Movie" : "Vod.WatchSeen.Series") },
        };
        _selectedWatchFilter = WatchFilterOptions.FirstOrDefault(o => o.Key == Lib.Prefs.WatchFilter) ?? WatchFilterOptions[0];
        // Un film marqué vu (ou une série commencée) change de catégorie : la liste filtrée se met à jour.
        main.WatchChanged += OnWatchChanged;
        Lib.CatalogChanged += Reload;
        Lib.ParentalChanged += Reload;
        Lib.FavoritesChanged += OnFavoritesChanged;
        Reload();
    }

    public ContentKind Kind { get; }
    public override string Title => Kind == ContentKind.Movie ? Loc.T("Nav.Movies") : Loc.T("Nav.Series");
    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;
    public bool IsMovies => Kind == ContentKind.Movie;

    public ObservableCollection<Category> Categories { get; } = new();
    public IReadOnlyList<SortOption> SortOptions { get; }
    public IReadOnlyList<SortOption> WatchFilterOptions { get; }
    [ObservableProperty] private SortOption _selectedWatchFilter;
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _categoryFilter = "";
    [ObservableProperty] private IReadOnlyList<CatalogItem> _items = Array.Empty<CatalogItem>();

    public double CardWidth => SettingsStore.Current.PosterSize switch { 0 => 140, 2 => 210, _ => 172 };
    public double CardHeight => CardWidth * 1.5;
    public string CountText => Loc.T((Kind == ContentKind.Movie ? "Vod.MovieCount." : "Vod.SeriesCount.") + (Items.Count > 1 ? "Many" : "One"), Items.Count);
    public bool IsEmpty => Items.Count == 0 && Lib.Catalog is not null;

    private List<Category> _allCategories = new();

    private IEnumerable<CatalogItem> Source() => Lib.Catalog is not { } c
        ? Enumerable.Empty<CatalogItem>()
        : Kind == ContentKind.Movie ? c.Movies : c.Series;

    public override void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(CardHeight));
    }

    private void Reload()
    {
        var c = Lib.Catalog;
        var previous = SelectedCategory?.Id;
        _allCategories = new List<Category> { _all, _recent, _fav };
        _all.Count = Source().Count(x => !Lib.IsHidden(x));
        _fav.Count = Source().Count(x => x.IsFavorite && !Lib.IsHidden(x));
        _recent.Count = Math.Min(200, _all.Count);
        if (c is not null)
            _allCategories.AddRange((Kind == ContentKind.Movie ? c.MovieCategories : c.SeriesCategories).Where(x => x.Count > 0));
        ApplyCategoryFilter();
        SelectedCategory = Categories.FirstOrDefault(x => x.Id == previous && !Lib.IsHidden(x)) ?? _all;
        ApplyFilter();
    }

    private void OnWatchChanged()
    {
        if (SelectedWatchFilter.Key != "all") ApplyFilter();
    }

    private void OnFavoritesChanged()
    {
        _fav.Count = Source().Count(x => x.IsFavorite && !Lib.IsHidden(x));
        if (SelectedCategory == _fav) ApplyFilter();
    }

    public override void Detach()
    {
        _posterCts?.Cancel();
        _main.WatchChanged -= OnWatchChanged;
        Lib.CatalogChanged -= Reload;
        Lib.ParentalChanged -= Reload;
        Lib.FavoritesChanged -= OnFavoritesChanged;
    }

    partial void OnCategoryFilterChanged(string value) => ApplyCategoryFilter();

    private bool _rebuildingCategories;

    private void ApplyCategoryFilter()
    {
        var tokens = TextNorm.Tokens(CategoryFilter);
        var keep = SelectedCategory;
        _rebuildingCategories = true;
        try
        {
            Categories.Clear();
            foreach (var cat in _allCategories)
            {
                if (tokens.Length > 0 && !cat.IsVirtual && TextNorm.Score(TextNorm.Normalize(cat.Name), tokens, TextNorm.Normalize(CategoryFilter)) == 0) continue;
                Categories.Add(cat);
            }
            if (keep is not null && Categories.Contains(keep)) SelectedCategory = keep;
        }
        finally { _rebuildingCategories = false; }
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (_rebuildingCategories) return;
        if (value is not null && Lib.IsHidden(value))
        {
            _ = UnlockAsync();
            return;
        }
        ApplyFilter();
    }

    private async Task UnlockAsync()
    {
        if (await _main.RequestPinAsync()) ApplyFilter();
        else SelectedCategory = _all;
    }

    partial void OnSelectedSortChanged(SortOption value) => ApplyFilter();

    partial void OnSelectedWatchFilterChanged(SortOption value)
    {
        // Choix mémorisé pour le profil (commun aux pages Films et Séries).
        if (Lib.Profile is { } p && p.Prefs.WatchFilter != value.Key)
        {
            p.Prefs.WatchFilter = value.Key;
            SettingsStore.Save();
        }
        ApplyFilter();
    }

    /// <summary>Déjà vu : film terminé, ou série vue en entier ou dont au moins un épisode a été regardé ou commencé.</summary>
    private static bool Seen(CatalogItem x) => x.IsWatched || (x is Series && x.IsInProgress);
    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var cat = SelectedCategory ?? _all;
        IEnumerable<CatalogItem> q = cat.Id switch
        {
            "__all" => Source(),
            "__fav" => Source().Where(x => x.IsFavorite),
            "__recent" => Source().OrderByDescending(x => x.Added).Take(200),
            _ => Source().Where(x => x.CategoryId == cat.Id)
        };
        q = q.Where(x => !Lib.IsHidden(x));
        q = SelectedWatchFilter.Key switch
        {
            "watched" => q.Where(Seen),
            "unwatched" => q.Where(x => !Seen(x)),
            _ => q,
        };
        var tokens = TextNorm.Tokens(Filter);
        if (tokens.Length > 0)
        {
            var norm = TextNorm.Normalize(Filter);
            q = q.Select(x => (x, s: TextNorm.Score(x.SearchKey, tokens, norm))).Where(t => t.s > 0).OrderByDescending(t => t.s).Select(t => t.x);
        }
        else if (cat.Id != "__recent")
        {
            q = SelectedSort.Key switch
            {
                "name" => q.OrderBy(x => TextNorm.StripPrefix(x.Name), StringComparer.Create(Loc.Culture, true)),
                "rating" => q.OrderByDescending(x => x is Movie m ? m.Rating ?? 0 : ((Series)x).Rating ?? 0),
                "year" => q.OrderByDescending(x => x is Movie m ? m.Year ?? 0 : ((Series)x).Year ?? 0).ThenByDescending(x => x.Added),
                _ => q.OrderByDescending(x => x.Added)
            };
        }
        Items = q.ToList();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        FillMissingPosters();
    }

    /// <summary>Complète les affiches manquantes avec TMDB (en arrière-plan, premiers éléments).</summary>
    private void FillMissingPosters()
    {
        _posterCts?.Cancel();
        if (!Lib.Tmdb.IsConfigured) return;
        var cts = _posterCts = new CancellationTokenSource();
        var missing = Items.Where(x => string.IsNullOrEmpty(x.ImageUrl)).Take(150).ToList();
        if (missing.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var item in missing)
            {
                if (cts.IsCancellationRequested) return;
                var (title, year) = TextNorm.SplitYear(item.Name);
                var url = await Lib.Tmdb.FindPosterAsync(Kind, title, year, cts.Token);
                if (url is not null) Avalonia.Threading.Dispatcher.UIThread.Post(() => item.ImageUrl ??= url);
            }
        }, cts.Token);
    }

    [RelayCommand] private void Open(CatalogItem item) => _main.OpenDetails(item);
    [RelayCommand] private void ToggleFavorite(CatalogItem item) => Lib.ToggleFavorite(item);

    /// <summary>Menu de l'affiche : marquer un film comme vu, ou tous les épisodes d'une série.</summary>
    [RelayCommand]
    private async Task MarkWatchedAsync(CatalogItem item)
    {
        if (item is Series s) { await _main.MarkSeriesWatchedAsync(s); return; }
        if (item is not Movie m) return;
        var w = Lib.GetWatch(m.Key);
        Lib.SaveWatch(new WatchEntry
        {
            Key = m.Key, Kind = ContentKind.Movie, ItemId = m.Id, Title = TextNorm.StripPrefix(m.Name),
            ImageUrl = m.ImageUrl, Position = w?.Duration ?? 1, Duration = w?.Duration ?? 1, Watched = true,
        });
        _main.OnWatchChanged();
    }

    /// <summary>Menu de l'affiche : film « non vu », ou historique d'une série effacé.</summary>
    [RelayCommand]
    private void MarkUnwatched(CatalogItem item)
    {
        if (item is Movie m) Lib.RemoveWatch(m.Key);
        else if (item is Series s) Lib.ClearSeriesHistory(s);
        _main.OnWatchChanged();
    }

    [RelayCommand]
    private async Task PlayAsync(CatalogItem item)
    {
        if (item is Movie m) await _main.PlayMovieAsync(m);
        else _main.OpenDetails(item);
    }

    [RelayCommand]
    private async Task ToggleCategoryLockAsync(Category cat)
    {
        if (cat.IsVirtual || !_main.EnsurePinConfigured()) return;
        if (!await _main.RequestPinAsync()) return;
        Lib.SetCategoryLock(cat, !cat.IsLocked);
    }

    [RelayCommand]
    private void SetPosterSize(string size)
    {
        SettingsStore.Current.PosterSize = int.Parse(size);
        SettingsStore.Save();
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(CardHeight));
    }
}

/// <summary>Base commune des fiches film / série.</summary>
/// <summary>Une case de la section « Informations sur le média » d'une fiche.</summary>
public sealed record MediaRow(string Label, string Value);

public abstract partial class DetailViewModelBase : ViewModelBase
{
    protected readonly MainViewModel MainVm;
    protected readonly CancellationTokenSource Cts = new();

    protected DetailViewModelBase(MainViewModel main) => MainVm = main;

    public override bool KeepView => false;
    public Library Lib => Library.Instance;
    public MainViewModel Main => MainVm;

    [ObservableProperty] private string _displayTitle = "";
    [ObservableProperty] private string? _originalTitle;
    [ObservableProperty] private string? _plot;
    [ObservableProperty] private string? _tagline;
    [ObservableProperty] private string? _poster;
    [ObservableProperty] private string? _backdrop;
    [ObservableProperty] private string? _metaLine;
    [ObservableProperty] private string? _genres;
    [ObservableProperty] private string? _director;
    [ObservableProperty] private string? _ratingText;
    [ObservableProperty] private string? _certification;
    [ObservableProperty] private string? _trailerKey;
    [ObservableProperty] private bool _isLoadingDetails = true;
    public ObservableCollection<CastMember> Cast { get; } = new();
    public bool HasCast => Cast.Count > 0;
    public bool HasTrailer => !string.IsNullOrEmpty(TrailerKey);

    partial void OnTrailerKeyChanged(string? value) => OnPropertyChanged(nameof(HasTrailer));

    // ───────────── Informations sur le média ─────────────

    [ObservableProperty] private IReadOnlyList<string> _mediaChips = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<MediaRow> _mediaRows = Array.Empty<MediaRow>();
    [ObservableProperty] private string? _mediaNote;
    [ObservableProperty] private string _mediaTitle = Loc.T("Media.Title");
    public bool HasMediaChips => MediaChips.Count > 0;
    public bool HasMediaRows => MediaRows.Count > 0;
    partial void OnMediaChipsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasMediaChips));
    partial void OnMediaRowsChanged(IReadOnlyList<MediaRow> value) => OnPropertyChanged(nameof(HasMediaRows));

    /// <summary>Langue originale (TMDB).</summary>
    protected string? OriginalLanguage { get; private set; }

    /// <summary>Recalcule les informations sur le média (fournisseur, relevé de lecture, TMDB).</summary>
    protected virtual void RefreshMedia() { }

    protected void ShowMedia(MediaInfo? m, string? title = null, bool withDuration = true)
    {
        MediaTitle = title ?? Loc.T("Media.Title");
        MediaChips = m?.Chips ?? Array.Empty<string>();
        var rows = new List<MediaRow>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new MediaRow(Loc.T(key), value!));
        }
        Add("Media.Resolution", m?.ResolutionText);
        Add("Media.Video", m?.VideoText);
        Add("Media.Audio", m?.AudioText);
        Add("Media.AudioLanguages", m?.AudioLanguagesText);
        Add("Media.Subtitles", m?.SubtitlesText);
        if (withDuration && m?.DurationSecs is > 0) Add("Media.Duration", TextNorm.Duration(m.DurationSecs.Value));
        Add("Media.Bitrate", m?.BitrateText);
        Add("Media.Container", m?.Container);
        Add("Media.OriginalLanguage", Player.TrackInfo.LanguageName(OriginalLanguage));
        MediaRows = rows;
        MediaNote = m is null ? null : Loc.T(m.FromPlayback ? "Media.FromPlayback" : "Media.FromProvider");
    }

    protected void ApplyDetails(MediaDetails? provider, MediaDetails? tmdb, string fallbackTitle, int? fallbackYear, double? fallbackRating)
    {
        var d = tmdb;
        DisplayTitle = !string.IsNullOrWhiteSpace(d?.Title) ? d!.Title : TextNorm.SplitYear(fallbackTitle).Title;
        OriginalTitle = d?.OriginalTitle is { } ot && !string.Equals(ot, DisplayTitle, StringComparison.OrdinalIgnoreCase) ? ot : null;
        Plot = FirstNonEmpty(d?.Plot, provider?.Plot, Plot);
        Tagline = d?.Tagline is { Length: > 0 } tg ? tg : null;
        Poster = FirstNonEmpty(d?.PosterUrl, provider?.PosterUrl, Poster);
        Backdrop = FirstNonEmpty(d?.BackdropUrl, provider?.BackdropUrl, Backdrop);
        var genres = d?.Genres is { Count: > 0 } g ? g : provider?.Genres ?? new List<string>();
        Genres = genres.Count > 0 ? string.Join(" · ", genres.Take(4)) : null;
        Director = FirstNonEmpty(d?.Director, provider?.Director, null);
        var rating = d?.Rating ?? provider?.Rating ?? fallbackRating;
        RatingText = rating is > 0 ? rating.Value.ToString("0.0") : null;
        Certification = FirstNonEmpty(d?.Certification, provider?.Certification, null);
        // Bande-annonce : identifiant TMDB gardé pour chercher la version dans la langue du profil à l'ouverture ;
        // la clé du fournisseur peut être une URL complète.
        _trailerTmdbId = d?.TmdbId ?? provider?.TmdbId ?? _trailerTmdbId;
        TrailerKey = FirstNonEmpty(d?.TrailerYoutubeKey, Controls.TrailerHost.ParseYoutubeId(provider?.TrailerYoutubeKey), null);
        var year = d?.Year ?? provider?.Year ?? fallbackYear;
        var runtime = d?.RuntimeMinutes ?? provider?.RuntimeMinutes;
        MetaLine = string.Join("  ·  ", new[]
        {
            year?.ToString(),
            runtime is > 0 ? TextNorm.Duration(runtime.Value * 60) : null,
            d?.ReleaseDate is { Length: > 0 } rd && year is null ? rd : null,
        }.Where(x => !string.IsNullOrEmpty(x)));
        var cast = d?.Cast is { Count: > 0 } c1 ? c1 : provider?.Cast ?? new List<CastMember>();
        Cast.Clear();
        foreach (var m in cast.Take(15)) Cast.Add(m);
        OnPropertyChanged(nameof(HasCast));
        OriginalLanguage = d?.OriginalLanguage ?? OriginalLanguage;
        RefreshMedia();
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private string? _trailerTmdbId;

    /// <summary>Bande-annonce lue dans l'application (langue des bandes-annonces du profil), repli sur la clé de la fiche.</summary>
    [RelayCommand]
    private Task OpenTrailerAsync()
    {
        if (string.IsNullOrEmpty(TrailerKey) && _trailerTmdbId is null) return Task.CompletedTask;
        var kind = this is SeriesDetailViewModel ? ContentKind.Series : ContentKind.Movie;
        return MainVm.OpenTrailerAsync(kind, _trailerTmdbId, TrailerKey, DisplayTitle);
    }

    [RelayCommand] private void Back() => MainVm.GoBack();

    public override void OnNavigatedFrom() => Cts.Cancel();
}

public sealed partial class MovieDetailViewModel : DetailViewModelBase
{
    public MovieDetailViewModel(MainViewModel main, Movie movie) : base(main)
    {
        Movie = movie;
        DisplayTitle = TextNorm.SplitYear(movie.Name).Title;
        Poster = movie.ImageUrl;
        Plot = movie.Plot;
        MetaLine = movie.YearText;
        RatingText = movie.RatingText;
        if (Lib.Catalog is { } c)
            foreach (var m in c.Movies.Where(x => x.CategoryId == movie.CategoryId && x != movie && !Lib.IsHidden(x)).OrderByDescending(x => x.Added).Take(20))
                Similar.Add(m);
        RefreshResume();
        _ = LoadAsync();
    }

    public Movie Movie { get; }
    public ObservableCollection<Movie> Similar { get; } = new();
    [ObservableProperty] private string _playLabel = Loc.T("Vod.Play");
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private double _resumeProgress;
    public bool HasSimilar => Similar.Count > 0;

    private void RefreshResume()
    {
        var w = Lib.GetWatch(Movie.Key);
        if (w is { Watched: false, Position: > 30 })
        {
            PlayLabel = Loc.T("Vod.ResumeAt", TextNorm.Clock(w.Position));
            CanRestart = true;
            ResumeProgress = w.Progress;
        }
        else
        {
            PlayLabel = w?.Watched == true ? Loc.T("Vod.Rewatch") : Loc.T("Vod.Play");
            CanRestart = false;
            ResumeProgress = w?.Watched == true ? 100 : 0;
        }
    }

    public override void OnNavigatedTo()
    {
        MainVm.WatchChanged += RefreshResume;
        RefreshResume();
        RefreshMedia(); // retour du lecteur : le relevé de la lecture complète la fiche
    }

    private MediaInfo? _providerMedia;

    protected override void RefreshMedia() => ShowMedia(MediaInfo.Merge(MediaInfoCache.Get(Movie.Key), _providerMedia));

    public override void OnNavigatedFrom()
    {
        MainVm.WatchChanged -= RefreshResume;
    }

    private async Task LoadAsync()
    {
        try
        {
            var providerTask = Lib.Provider?.GetMovieDetailsAsync(Movie, Cts.Token) ?? Task.FromResult<MediaDetails?>(null);
            var provider = await providerTask;
            _providerMedia = provider?.Media;
            ApplyDetails(provider, null, Movie.Name, Movie.Year, Movie.Rating);
            if (Lib.Tmdb.IsConfigured)
            {
                var tmdb = await Lib.Tmdb.GetMovieAsync(provider?.TmdbId ?? Movie.TmdbId, Movie.Name, Movie.Year ?? provider?.Year, Cts.Token);
                if (tmdb is not null) ApplyDetails(provider, tmdb, Movie.Name, Movie.Year, Movie.Rating);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Fiche film : " + ex.Message);
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    [RelayCommand] private Task PlayAsync() => MainVm.PlayMovieAsync(Movie);
    [RelayCommand] private Task RestartAsync() => MainVm.PlayMovieAsync(Movie, fromStart: true);
    [RelayCommand] private void ToggleFavorite() => Lib.ToggleFavorite(Movie);
    [RelayCommand] private void OpenSimilar(Movie m) => MainVm.OpenDetails(m);

    [RelayCommand]
    private void MarkWatched()
    {
        if (Movie.IsWatched)
        {
            Lib.RemoveWatch(Movie.Key);
            RefreshResume();
            MainVm.OnWatchChanged();
            return;
        }
        var w = Lib.GetWatch(Movie.Key);
        Lib.SaveWatch(new WatchEntry
        {
            Key = Movie.Key, Kind = ContentKind.Movie, ItemId = Movie.Id, Title = TextNorm.StripPrefix(Movie.Name),
            ImageUrl = Movie.ImageUrl, Position = w?.Duration ?? 1, Duration = w?.Duration ?? 1, Watched = true,
        });
        RefreshResume();
        MainVm.OnWatchChanged();
    }
}

public sealed partial class SeriesDetailViewModel : DetailViewModelBase
{
    private Dictionary<string, WatchEntry> _watch = new();

    public SeriesDetailViewModel(MainViewModel main, Series series) : base(main)
    {
        Series = series;
        DisplayTitle = TextNorm.SplitYear(series.Name).Title;
        Poster = series.ImageUrl;
        Backdrop = series.Backdrop;
        Plot = series.Plot;
        MetaLine = series.YearText;
        RatingText = series.RatingText;
        Genres = series.Genre;
        _ = LoadAsync();
    }

    public Series Series { get; }
    public ObservableCollection<SeasonInfo> Seasons { get; } = new();
    [ObservableProperty] private SeasonInfo? _selectedSeason;
    [ObservableProperty] private IReadOnlyList<Episode> _episodes = Array.Empty<Episode>();
    [ObservableProperty] private string _playLabel = Loc.T("Vod.Play");
    [ObservableProperty] private string? _error;
    /// <summary>Tous les épisodes de la saison affichée (ou de la série) sont vus : le bouton propose l'inverse.</summary>
    [ObservableProperty] private bool _seasonWatched;
    [ObservableProperty] private bool _seriesWatched;
    private Episode? _resumeEpisode;
    private string? _tmdbId;

    public bool HasSeasons => Seasons.Count > 0;
    public string? SeasonSummary => Seasons.Count == 0 ? null : Loc.T(Seasons.Count > 1 ? "Vod.Seasons.Many" : "Vod.Seasons.One", Seasons.Count, Seasons.Sum(s => s.Episodes.Count));

    public override void OnNavigatedTo()
    {
        MainVm.WatchChanged += RefreshProgress;
        RefreshProgress();
        RefreshMedia();
    }

    /// <summary>Chaque épisode : relevé de lecture complété par le fournisseur ; l'en-tête résume la saison affichée.</summary>
    protected override void RefreshMedia()
    {
        foreach (var s in Seasons)
            foreach (var e in s.Episodes)
                e.Media = MediaInfo.Merge(MediaInfoCache.Get(e.Key), e.ProviderMedia);
        var season = SelectedSeason;
        ShowMedia(MediaInfo.Summarize((season?.Episodes ?? new List<Episode>()).Select(e => e.Media)),
            season is null ? null : Loc.T("Media.SeasonTitle", season.Name), withDuration: false);
    }

    public override void OnNavigatedFrom()
    {
        MainVm.WatchChanged -= RefreshProgress;
    }

    private async Task LoadAsync()
    {
        try
        {
            var provider = Lib.Provider;
            if (provider is null) return;
            var detailsTask = provider.GetSeriesDetailsAsync(Series, Cts.Token);
            var tmdbTask = Lib.Tmdb.IsConfigured
                ? Lib.Tmdb.GetSeriesAsync(Series.TmdbId, Series.Name, Series.Year, Cts.Token)
                : Task.FromResult<MediaDetails?>(null);
            var details = await detailsTask;
            ApplyDetails(details?.Info, null, Series.Name, Series.Year, Series.Rating);
            if (details is not null)
            {
                Seasons.Clear();
                foreach (var s in details.Seasons) Seasons.Add(s);
                OnPropertyChanged(nameof(HasSeasons));
                OnPropertyChanged(nameof(SeasonSummary));
                RefreshProgress();
                SelectedSeason = _resumeEpisode is { } re ? Seasons.FirstOrDefault(s => s.Episodes.Contains(re)) ?? Seasons.FirstOrDefault() : Seasons.FirstOrDefault();
                RefreshMedia();
            }
            if (Seasons.Count == 0) Error = Loc.T("Vod.NoEpisodes");
            var tmdb = await tmdbTask;
            if (tmdb is not null)
            {
                ApplyDetails(details?.Info, tmdb, Series.Name, Series.Year, Series.Rating);
                _tmdbId = tmdb.TmdbId;
                if (SelectedSeason is { } ss) _ = EnrichSeasonAsync(ss);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Fiche série : " + ex.Message);
            Error = Loc.T("Vod.EpisodesError", ex.Message);
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    partial void OnSelectedSeasonChanged(SeasonInfo? value)
    {
        Episodes = value?.Episodes ?? (IReadOnlyList<Episode>)Array.Empty<Episode>();
        UpdateWatchedFlags();
        RefreshMedia();
        if (value is not null && _tmdbId is not null) _ = EnrichSeasonAsync(value);
    }

    /// <summary>Complète vignettes et résumés des épisodes avec TMDB.</summary>
    private async Task EnrichSeasonAsync(SeasonInfo season)
    {
        if (_tmdbId is null || season.Episodes.All(e => e.ImageUrl is not null && e.Plot is not null)) return;
        try
        {
            var eps = await Lib.Tmdb.GetSeasonAsync(_tmdbId, season.Number, Cts.Token);
            var changed = false;
            foreach (var e in season.Episodes)
            {
                var t = eps.FirstOrDefault(x => x.Number == e.Number);
                if (t is null) continue;
                if (string.IsNullOrEmpty(e.ImageUrl) && t.StillUrl is not null) { e.ImageUrl = t.StillUrl; changed = true; }
                if (string.IsNullOrWhiteSpace(e.Plot) && t.Overview is not null) { e.Plot = t.Overview; changed = true; }
                if (string.IsNullOrWhiteSpace(e.Title) && t.Name is not null) { e.Title = t.Name; changed = true; }
                if (e.DurationSecs is null or 0 && t.RuntimeMinutes is > 0) { e.DurationSecs = t.RuntimeMinutes * 60; changed = true; }
            }
            if (changed && SelectedSeason == season)
            {
                Episodes = Array.Empty<Episode>();
                Episodes = season.Episodes;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Épisodes TMDB : " + ex.Message);
        }
    }

    private void RefreshProgress()
    {
        if (Lib.Source is null) return;
        _watch = Lib.GetAllWatchEntries();
        foreach (var s in Seasons)
            foreach (var e in s.Episodes)
            {
                var w = _watch.GetValueOrDefault(e.Key);
                e.IsWatched = w?.Watched == true;
                e.Progress = w is null ? 0 : w.Watched ? 100 : w.Progress;
            }
        var last = Lib.GetLastEpisode(Series.Id);
        _resumeEpisode = null;
        if (last is not null)
        {
            var all = Seasons.SelectMany(s => s.Episodes).ToList();
            var ep = all.FirstOrDefault(e => e.Id == last.ItemId);
            if (ep is not null && last.Watched)
            {
                // Suite : premier épisode non vu après le dernier regardé (une saison marquée vue est sautée).
                var idx = all.IndexOf(ep);
                ep = all.Skip(idx + 1).FirstOrDefault(e => !e.IsWatched);
            }
            _resumeEpisode = ep;
        }
        PlayLabel = _resumeEpisode is { } re
            ? (re.Progress > 0 && !re.IsWatched ? Loc.T("Vod.ResumeEpisode", re.Code) : Loc.T("Vod.PlayEpisode", re.Code))
            : Seasons.FirstOrDefault()?.Episodes.FirstOrDefault() is { } first ? Loc.T("Vod.PlayEpisode", first.Code) : Loc.T("Vod.Play");
        UpdateWatchedFlags();
        if (Seasons.Count > 0) Lib.SetSeriesCompleted(Series, SeriesWatched);
    }

    private void UpdateWatchedFlags()
    {
        SeasonWatched = SelectedSeason is { Episodes.Count: > 0 } ss && ss.Episodes.All(e => e.IsWatched);
        var all = Seasons.SelectMany(x => x.Episodes).ToList();
        SeriesWatched = all.Count > 0 && all.All(e => e.IsWatched);
    }

    /// <summary>Saison affichée marquée vue (ou non vue si elle l'est déjà entièrement).</summary>
    [RelayCommand]
    private void ToggleSeasonWatched()
    {
        if (SelectedSeason is not { } season) return;
        MarkEpisodes(season.Episodes, !SeasonWatched);
    }

    /// <summary>Toute la série marquée vue (ou non vue si elle l'est déjà entièrement).</summary>
    [RelayCommand]
    private void ToggleSeriesWatched() => MarkEpisodes(Seasons.SelectMany(x => x.Episodes).ToList(), !SeriesWatched);

    private void MarkEpisodes(IReadOnlyList<Episode> episodes, bool watched)
    {
        var count = Lib.SetEpisodesWatched(Series, episodes, watched);
        RefreshProgress();
        MainVm.ReportEpisodesMarked(Series, count, watched);
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        var ep = _resumeEpisode ?? Seasons.FirstOrDefault()?.Episodes.FirstOrDefault();
        if (ep is null) return;
        var season = Seasons.First(s => s.Episodes.Contains(ep));
        await MainVm.PlayEpisodeAsync(Series, ep, season.Episodes);
    }

    [RelayCommand]
    private async Task PlayEpisodeAsync(Episode ep)
    {
        var season = Seasons.FirstOrDefault(s => s.Episodes.Contains(ep));
        if (season is null) return;
        await MainVm.PlayEpisodeAsync(Series, ep, season.Episodes);
    }

    [RelayCommand] private void ToggleFavorite() => Lib.ToggleFavorite(Series);

    [RelayCommand]
    private void ToggleWatched(Episode ep)
    {
        if (ep.IsWatched)
        {
            Lib.RemoveWatch(ep.Key);
        }
        else
        {
            Lib.SaveWatch(new WatchEntry
            {
                Key = ep.Key, Kind = ContentKind.Episode, ItemId = ep.Id, ParentId = Series.Id,
                Title = TextNorm.StripPrefix(Series.Name), Subtitle = ep.Code, ImageUrl = ep.ImageUrl ?? Series.ImageUrl,
                Position = 1, Duration = 1, Watched = true,
            });
        }
        RefreshProgress();
        MainVm.OnWatchChanged();
    }
}
