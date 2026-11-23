using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Controls;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.ViewModels;

/// <summary>Bande-annonce plein cadre, lue dans l'application (WebView2 + lecteur YouTube officiel).</summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _trailerCts;

    /// <summary>Calque de la bande-annonce affiché.</summary>
    [ObservableProperty] private bool _isTrailerOpen;

    /// <summary>Le mini-lecteur a été mis en pause à l'ouverture de la bande-annonce (reprise à sa fermeture).</summary>
    private bool _pausedForTrailer;

    partial void OnIsTrailerOpenChanged(bool value)
    {
        if (value && Player is { IsMini: true, IsPaused: false })
        {
            Player.TogglePauseCommand.Execute(null);
            _pausedForTrailer = true;
        }
        else if (!value && _pausedForTrailer)
        {
            _pausedForTrailer = false;
            if (Player is { IsOpen: true, IsPaused: true }) Player.TogglePauseCommand.Execute(null);
        }
    }
    /// <summary>Clé YouTube en cours (null pendant la recherche de la bande-annonce).</summary>
    [ObservableProperty] private string? _trailerKey;
    [ObservableProperty] private string _trailerTitle = "";
    /// <summary>Langue du lecteur et des sous-titres (préférence du profil).</summary>
    [ObservableProperty] private string _trailerLanguage = "fr";
    /// <summary>Recherche de la bande-annonce en cours.</summary>
    [ObservableProperty] private bool _isTrailerLoading;

    /// <summary>Bande-annonce d'un film ou d'une série du catalogue, dans la langue choisie pour le profil.</summary>
    public async Task OpenTrailerAsync(CatalogItem item)
    {
        var (kind, tmdbId, name, year) = item switch
        {
            Movie m => (ContentKind.Movie, m.TmdbId, m.Name, m.Year),
            Series s => (ContentKind.Series, s.TmdbId, s.Name, s.Year),
            _ => (ContentKind.Live, (string?)null, item.Name, (int?)null),
        };
        if (kind == ContentKind.Live) return;
        var title = TextNorm.SplitYear(name).Title;
        await OpenTrailerAsync(kind, tmdbId, null, title, async ct =>
        {
            // Identifiant TMDB inconnu du catalogue : fiche du fournisseur, puis recherche TMDB par titre.
            string? id = tmdbId, providerKey = null;
            try
            {
                if (item is Movie mv && Lib.Provider is { } p && await p.GetMovieDetailsAsync(mv, ct) is { } d)
                {
                    id ??= d.TmdbId;
                    providerKey = d.TrailerYoutubeKey;
                }
                else if (item is Series sr && Lib.Provider is { } p2 && await p2.GetSeriesDetailsAsync(sr, ct) is { } sd)
                {
                    id ??= sd.Info.TmdbId;
                    providerKey = sd.Info.TrailerYoutubeKey;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Bande-annonce : fiche du fournisseur indisponible : " + ex.Message);
            }
            if (id is null && Lib.Tmdb.IsConfigured)
            {
                var details = kind == ContentKind.Series
                    ? await Lib.Tmdb.GetSeriesAsync(null, name, year, ct)
                    : await Lib.Tmdb.GetMovieAsync(null, name, year, ct);
                id = details?.TmdbId;
                providerKey ??= details?.TrailerYoutubeKey;
            }
            return (id, providerKey);
        });
    }

    /// <summary>
    /// Bande-annonce à partir d'un identifiant TMDB (langue du profil) et/ou d'une clé de repli (fournisseur ou fiche TMDB).
    /// Le calque s'ouvre tout de suite (indicateur de chargement) ; sans WebView2, la vidéo s'ouvre dans le navigateur.
    /// </summary>
    public Task OpenTrailerAsync(ContentKind kind, string? tmdbId, string? fallbackKey, string title) =>
        OpenTrailerAsync(kind, tmdbId, fallbackKey, title, null);

    private async Task OpenTrailerAsync(ContentKind kind, string? tmdbId, string? fallbackKey, string title,
        Func<CancellationToken, Task<(string? TmdbId, string? ProviderKey)>>? complete)
    {
        _trailerCts?.Cancel();
        var cts = _trailerCts = new CancellationTokenSource();
        var inApp = TrailerHost.IsRuntimeAvailable;
        if (inApp)
        {
            IsSearchOpen = false;
            TrailerKey = null;
            TrailerTitle = title;
            TrailerLanguage = TrailerResolver.Language();
            IsTrailerLoading = true;
            IsTrailerOpen = true;
        }

        string? key;
        try
        {
            key = await TrailerResolver.ResolveAsync(kind, tmdbId, fallbackKey, cts.Token);
            if (key is null && complete is not null)
            {
                var (id, providerKey) = await complete(cts.Token);
                key = await TrailerResolver.ResolveAsync(kind, id, providerKey ?? fallbackKey, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("Bande-annonce : " + ex.Message);
            key = null;
        }
        if (cts.IsCancellationRequested) return;
        IsTrailerLoading = false;

        if (key is null)
        {
            if (inApp) CloseTrailer();
            Toast(Loc.T("Vod.Trailer"), Loc.T("Trailer.NotFound"), true);
            return;
        }
        if (inApp && IsTrailerOpen) TrailerKey = key;
        else if (!inApp) OpenTrailerInBrowser(key);
    }

    /// <summary>Affiche directement une bande-annonce connue.</summary>
    public void ShowTrailer(string youtubeKey, string title)
    {
        var key = TrailerHost.ParseYoutubeId(youtubeKey);
        if (key is null) return;
        _trailerCts?.Cancel();
        if (!TrailerHost.IsRuntimeAvailable)
        {
            OpenTrailerInBrowser(key);
            return;
        }
        IsSearchOpen = false;
        IsTrailerLoading = false;
        TrailerTitle = title;
        TrailerLanguage = TrailerResolver.Language();
        TrailerKey = key;
        IsTrailerOpen = true;
    }

    [RelayCommand]
    public void CloseTrailer()
    {
        _trailerCts?.Cancel();
        IsTrailerLoading = false;
        TrailerKey = null;
        IsTrailerOpen = false;
    }

    /// <summary>Le lecteur intégré n'a pas pu démarrer (WebView2 absent ou en échec) : repli sur le navigateur.</summary>
    public void OnTrailerUnavailable()
    {
        var key = TrailerKey;
        CloseTrailer();
        if (key is not null) OpenTrailerInBrowser(key);
    }

    /// <summary>Ouvre la bande-annonce sur YouTube dans le navigateur par défaut.</summary>
    [RelayCommand]
    public void OpenTrailerInBrowser(string? key)
    {
        key = TrailerHost.ParseYoutubeId(key ?? TrailerKey);
        if (key is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("https://www.youtube.com/watch?v=" + Uri.EscapeDataString(key)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast(Loc.T("Vod.Trailer"), ex.Message, true);
        }
    }
}

/// <summary>Choix de la clé YouTube d'une bande-annonce selon la langue du profil.</summary>
public static class TrailerResolver
{
    /// <summary>Langue des bandes-annonces du profil actif : « auto » = langue de l'application.</summary>
    public static string Language()
    {
        var pref = Library.Instance.Prefs.TrailerLanguage?.Trim().ToLowerInvariant();
        return pref is { Length: 2 } && char.IsAsciiLetterLower(pref[0]) && char.IsAsciiLetterLower(pref[1]) ? pref : Loc.Lang;
    }

    /// <summary>
    /// Clé YouTube : bande-annonce TMDB dans la langue du profil (si une clé TMDB et un identifiant sont connus),
    /// sinon la clé de repli (fournisseur Xtream « youtube_trailer », éventuellement une URL complète).
    /// </summary>
    public static async Task<string?> ResolveAsync(ContentKind kind, string? tmdbId, string? fallbackKey, CancellationToken ct)
    {
        var tmdb = Library.Instance.Tmdb;
        if (!string.IsNullOrWhiteSpace(tmdbId) && tmdb.IsConfigured)
        {
            var key = await tmdb.GetTrailerKeyAsync(kind, tmdbId, Language(), ct);
            if (TrailerHost.ParseYoutubeId(key) is { } k) return k;
        }
        return TrailerHost.ParseYoutubeId(fallbackKey);
    }
}
