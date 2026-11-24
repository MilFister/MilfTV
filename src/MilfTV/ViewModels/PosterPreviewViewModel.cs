using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.ViewModels;

/// <summary>
/// Aperçu au survol d'une affiche (film ou série) : bande-annonce en lecture (ou image), titre, informations, résumé et actions.
/// Les détails sont chargés en arrière-plan et abandonnés dès que l'aperçu se ferme (<see cref="Cancel"/>).
/// </summary>
public sealed partial class PosterPreviewViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly CancellationTokenSource _cts = new();

    public PosterPreviewViewModel(MainViewModel main, CatalogItem item)
    {
        _main = main;
        Item = item;
        // Pas de son par-dessus une vidéo qui joue dans le mini-lecteur (le bouton son reste disponible).
        IsMuted = !Library.Instance.Prefs.PreviewSound || main.Player is { IsOpen: true, IsPaused: false };
        _volume = Math.Clamp(Library.Instance.Prefs.PreviewVolume, 0, 100);
        TrailerLanguage = TrailerResolver.Language();
        Title = TextNorm.SplitYear(item.Name).Title;
        switch (item)
        {
            case Movie m:
                Plot = m.Plot;
                ImageUrl = m.ImageUrl;
                MetaLine = BuildMeta(m.Year, null, null, m.Rating, SplitGenres(m.Genre));
                // Déjà lu : les pastilles s'affichent tout de suite, sans attendre le fournisseur.
                MediaChips = MediaInfoCache.Get(m.Key)?.Chips ?? Array.Empty<string>();
                break;
            case Series s:
                Plot = s.Plot;
                ImageUrl = FirstNonEmpty(s.Backdrop, s.ImageUrl);
                var seasons = s.Episodes is { Count: > 0 } eps ? eps.Select(e => e.Season).Distinct().Count() : (int?)null;
                MetaLine = BuildMeta(s.Year, null, seasons, s.Rating, SplitGenres(s.Genre));
                break;
        }
        _ = LoadAsync();
    }

    public CatalogItem Item { get; }
    public bool IsMovie => Item is Movie;
    public string TrailerLanguage { get; }

    [ObservableProperty] private string _title = "";
    /// <summary>« 2023 · 1 h 52 · ★ 7,4 · Action, Aventure ».</summary>
    [ObservableProperty] private string? _metaLine;
    [ObservableProperty] private string? _plot;
    /// <summary>Fond (ou affiche) montré tant que la vidéo ne joue pas.</summary>
    [ObservableProperty] private string? _imageUrl;
    [ObservableProperty] private string? _trailerKey;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isLoading = true;
    /// <summary>Aucune bande-annonce trouvée : l'image reste affichée.</summary>
    [ObservableProperty] private bool _noTrailer;

    public bool HasPlot => !string.IsNullOrWhiteSpace(Plot);
    partial void OnPlotChanged(string? value) => OnPropertyChanged(nameof(HasPlot));

    /// <summary>Pastilles techniques (4K, HDR, HEVC, 5.1, langues) : fournisseur et relevé de la dernière lecture.</summary>
    [ObservableProperty] private IReadOnlyList<string> _mediaChips = Array.Empty<string>();
    public bool HasMediaChips => MediaChips.Count > 0;
    partial void OnMediaChipsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasMediaChips));

    /// <summary>L'aperçu doit se fermer (action lancée depuis l'aperçu).</summary>
    public event Action? CloseRequested;

    /// <summary>Abandonne les chargements en cours (aperçu fermé).</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task LoadAsync()
    {
        var ct = _cts.Token;
        var lib = Library.Instance;
        try
        {
            // Laisse l'aperçu s'afficher avant les requêtes réseau.
            await Task.Yield();
            MediaDetails? provider = null, tmdb = null;
            int? seasons = null, episodeSecs = null;
            MediaInfo? media = null;
            string? itemTmdbId = null;
            switch (Item)
            {
                case Movie m:
                {
                    itemTmdbId = m.TmdbId;
                    var providerTask = Safe(lib.Provider is { } p ? p.GetMovieDetailsAsync(m, ct) : null);
                    var tmdbTask = lib.Tmdb.IsConfigured ? lib.Tmdb.GetMovieAsync(m.TmdbId, m.Name, m.Year, ct) : Task.FromResult<MediaDetails?>(null);
                    provider = await providerTask;
                    tmdb = await tmdbTask;
                    // Le fournisseur connaît parfois l'identifiant TMDB alors que le catalogue ne l'a pas.
                    if (tmdb is null && provider?.TmdbId is { } pid && pid != m.TmdbId && lib.Tmdb.IsConfigured)
                        tmdb = await lib.Tmdb.GetMovieAsync(pid, m.Name, m.Year ?? provider.Year, ct);
                    media = MediaInfo.Merge(MediaInfoCache.Get(m.Key), provider?.Media);
                    break;
                }
                case Series s:
                {
                    itemTmdbId = s.TmdbId;
                    var detailsTask = SafeSeries(lib.Provider is { } p ? p.GetSeriesDetailsAsync(s, ct) : null);
                    var tmdbTask = lib.Tmdb.IsConfigured ? lib.Tmdb.GetSeriesAsync(s.TmdbId, s.Name, s.Year, ct) : Task.FromResult<MediaDetails?>(null);
                    var details = await detailsTask;
                    tmdb = await tmdbTask;
                    provider = details?.Info;
                    if (details?.Seasons is { Count: > 0 } list)
                    {
                        seasons = list.Count;
                        var episodes = list.SelectMany(x => x.Episodes).ToList();
                        media = MediaInfo.Summarize(episodes.Select(e => MediaInfo.Merge(MediaInfoCache.Get(e.Key), e.ProviderMedia)));
                        // Durée typique d'un épisode (médiane : un pilote ou un final plus long ne la fausse pas).
                        var secs = episodes.Select(e => e.DurationSecs ?? e.ProviderMedia?.DurationSecs ?? 0).Where(x => x > 60).OrderBy(x => x).ToList();
                        if (secs.Count > 0) episodeSecs = secs[secs.Count / 2];
                    }
                    if (tmdb is null && provider?.TmdbId is { } pid && pid != s.TmdbId && lib.Tmdb.IsConfigured)
                        tmdb = await lib.Tmdb.GetSeriesAsync(pid, s.Name, s.Year ?? provider.Year, ct);
                    break;
                }
            }
            ct.ThrowIfCancellationRequested();
            Apply(provider, tmdb, seasons, media, episodeSecs);

            var kind = Item is Series ? ContentKind.Series : ContentKind.Movie;
            var key = await TrailerResolver.ResolveAsync(kind, tmdb?.TmdbId ?? provider?.TmdbId ?? itemTmdbId,
                FirstNonEmpty(tmdb?.TrailerYoutubeKey, provider?.TrailerYoutubeKey), ct);
            ct.ThrowIfCancellationRequested();
            TrailerKey = key;
            NoTrailer = key is null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Aperçu au survol : " + ex.Message);
            NoTrailer = true;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    private void Apply(MediaDetails? provider, MediaDetails? tmdb, int? seasons, MediaInfo? media, int? episodeSecs)
    {
        if (media is not null) MediaChips = media.Chips;
        if (!string.IsNullOrWhiteSpace(tmdb?.Title)) Title = tmdb!.Title;
        Plot = FirstNonEmpty(tmdb?.Plot, provider?.Plot, Plot);
        ImageUrl = FirstNonEmpty(tmdb?.BackdropUrl, provider?.BackdropUrl, ImageUrl, tmdb?.PosterUrl, provider?.PosterUrl);
        var genres = tmdb?.Genres is { Count: > 0 } g ? g : provider?.Genres is { Count: > 0 } pg ? pg : null;
        var (year, rating, itemGenres) = Item switch
        {
            Movie m => (m.Year, m.Rating, m.Genre),
            Series s => (s.Year, s.Rating, s.Genre),
            _ => ((int?)null, (double?)null, (string?)null),
        };
        var runtime = Item is Movie ? tmdb?.RuntimeMinutes ?? provider?.RuntimeMinutes ?? (media?.DurationSecs is > 60 and var ms ? ms / 60 : null) : null;
        MetaLine = BuildMeta(tmdb?.Year ?? provider?.Year ?? year, runtime, seasons, tmdb?.Rating ?? provider?.Rating ?? rating,
            genres ?? SplitGenres(itemGenres), episodeSecs);
    }

    private static string? BuildMeta(int? year, int? runtimeMinutes, int? seasons, double? rating, IReadOnlyList<string>? genres,
        int? episodeSecs = null)
    {
        var parts = new List<string>();
        if (year is > 0) parts.Add(year.Value.ToString(CultureInfo.InvariantCulture));
        if (runtimeMinutes is > 0) parts.Add(TextNorm.Duration(runtimeMinutes.Value * 60));
        if (seasons is > 0) parts.Add(Loc.T(seasons > 1 ? "Preview.Seasons.Many" : "Preview.Seasons.One", seasons.Value));
        if (episodeSecs is > 0) parts.Add(Loc.T("Preview.PerEpisode", TextNorm.Duration(episodeSecs.Value)));
        if (rating is > 0) parts.Add("★ " + rating.Value.ToString("0.0", Loc.Culture));
        if (genres is { Count: > 0 }) parts.Add(string.Join(", ", genres.Take(3)));
        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }

    private static IReadOnlyList<string>? SplitGenres(string? genre) =>
        string.IsNullOrWhiteSpace(genre)
            ? null
            : genre.Split(new[] { ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Fiche du fournisseur facultative : une erreur réseau ne doit pas empêcher l'aperçu.</summary>
    private static async Task<MediaDetails?> Safe(Task<MediaDetails?>? task)
    {
        if (task is null) return null;
        try { return await task; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Aperçu au survol : fiche du fournisseur : " + ex.Message);
            return null;
        }
    }

    private static async Task<SeriesDetails?> SafeSeries(Task<SeriesDetails?>? task)
    {
        if (task is null) return null;
        try { return await task; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Aperçu au survol : fiche du fournisseur : " + ex.Message);
            return null;
        }
    }

    // ───────────── Actions ─────────────

    /// <summary>Film : lecture immédiate. Série : ouverture de la fiche (choix de l'épisode).</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        CloseRequested?.Invoke();
        if (Item is Movie m) await _main.PlayMovieAsync(m);
        else _main.OpenDetails(Item);
    }

    [RelayCommand]
    private void ToggleFavorite() => Library.Instance.ToggleFavorite(Item);

    [RelayCommand]
    private void MoreInfo()
    {
        CloseRequested?.Invoke();
        _main.OpenDetails(Item);
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Volume des bandes-annonces d'aperçu (0 à 100), mémorisé pour le profil.</summary>
    [ObservableProperty] private int _volume;
    partial void OnVolumeChanged(int value)
    {
        // Monter le volume rend le son, comme sur un lecteur classique.
        if (value > 0 && IsMuted) IsMuted = false;
        Library.Instance.Prefs.PreviewVolume = value;
        Data.SettingsStore.Save();
    }
}
