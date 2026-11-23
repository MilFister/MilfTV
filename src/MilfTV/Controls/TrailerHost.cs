using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using MilfTV.Core;

namespace MilfTV.Controls;

/// <summary>
/// Lecteur de bande-annonce YouTube intégré : lecteur officiel « embed » de YouTube affiché dans un WebView2.
/// <para>La page est servie par un hôte virtuel local (https://milftv.trailer/trailer.html) : depuis about:blank ou
/// NavigateToString, YouTube refuse souvent l'intégration (erreurs 152/153, référent absent).</para>
/// <para>Une surface native (HWND) est toujours dessinée par-dessus le contenu Avalonia (« airspace ») : elle n'est montrée
/// qu'une fois la page prête. Les enfants déclarés en XAML (image, indicateur de chargement…) restent visibles en attendant,
/// ou si la vidéo ne peut pas être lue. Rien ne doit être dessiné par-dessus la vidéo.</para>
/// <para>Le moteur WebView2 n'est créé qu'à la première lecture, puis libéré après un moment d'inactivité.</para>
/// </summary>
public sealed partial class TrailerHost : Panel
{
    /// <summary>Nom d'hôte virtuel qui sert la page du lecteur.</summary>
    public const string VirtualHost = "milftv.trailer";

    /// <summary>Clé YouTube (identifiant de 11 caractères, une URL complète est acceptée). Null = arrêt.</summary>
    public static readonly StyledProperty<string?> VideoKeyProperty =
        AvaloniaProperty.Register<TrailerHost, string?>(nameof(VideoKey));

    /// <summary>Son coupé (modifiable pendant la lecture).</summary>
    public static readonly StyledProperty<bool> MutedProperty =
        AvaloniaProperty.Register<TrailerHost, bool>(nameof(Muted));

    /// <summary>Volume de 0 à 100 (modifiable pendant la lecture).</summary>
    public static readonly StyledProperty<int> VolumeProperty =
        AvaloniaProperty.Register<TrailerHost, int>(nameof(Volume), 100);

    /// <summary>Lecture automatique dès que la clé est définie (sinon YouTube affiche sa vignette et son bouton lecture).</summary>
    public static readonly StyledProperty<bool> AutoplayProperty =
        AvaloniaProperty.Register<TrailerHost, bool>(nameof(Autoplay), true);

    /// <summary>Commandes YouTube et interaction avec la vidéo. Faux pour un aperçu : les clics sont renvoyés par <see cref="SurfaceClicked"/>.</summary>
    public static readonly StyledProperty<bool> ShowControlsProperty =
        AvaloniaProperty.Register<TrailerHost, bool>(nameof(ShowControls), true);

    /// <summary>Image affichée par la page tant que la vidéo ne joue pas (fond ou affiche, http/https uniquement).</summary>
    public static readonly StyledProperty<string?> PosterUrlProperty =
        AvaloniaProperty.Register<TrailerHost, string?>(nameof(PosterUrl));

    /// <summary>Langue de l'interface du lecteur et des sous-titres (fr, en…). Null = langue de l'application.</summary>
    public static readonly StyledProperty<string?> TrailerLanguageProperty =
        AvaloniaProperty.Register<TrailerHost, string?>(nameof(TrailerLanguage));

    public static readonly DirectProperty<TrailerHost, bool> IsPlayingProperty =
        AvaloniaProperty.RegisterDirect<TrailerHost, bool>(nameof(IsPlaying), o => o.IsPlaying);

    public static readonly DirectProperty<TrailerHost, bool> HasErrorProperty =
        AvaloniaProperty.RegisterDirect<TrailerHost, bool>(nameof(HasError), o => o.HasError);

    /// <summary>Délai après lequel le moteur WebView2 inutilisé est libéré.</summary>
    private static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(90);

    private readonly TrailerSurface _surface;
    private readonly DispatcherTimer _idleTimer;
    private string? _currentKey;
    private int _token;
    private bool _isPlaying;
    private bool _hasError;

    public TrailerHost()
    {
        _surface = new TrailerSurface(this) { IsVisible = false };
        Children.Add(_surface);
        _idleTimer = new DispatcherTimer { Interval = IdleRelease };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (_currentKey is null) _surface.ReleaseEngine();
        };
    }

    public string? VideoKey { get => GetValue(VideoKeyProperty); set => SetValue(VideoKeyProperty, value); }
    public bool Muted { get => GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public int Volume { get => GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }
    public bool Autoplay { get => GetValue(AutoplayProperty); set => SetValue(AutoplayProperty, value); }
    public bool ShowControls { get => GetValue(ShowControlsProperty); set => SetValue(ShowControlsProperty, value); }
    public string? PosterUrl { get => GetValue(PosterUrlProperty); set => SetValue(PosterUrlProperty, value); }
    public string? TrailerLanguage { get => GetValue(TrailerLanguageProperty); set => SetValue(TrailerLanguageProperty, value); }

    /// <summary>La vidéo est en cours de lecture (signalé par le lecteur YouTube).</summary>
    public bool IsPlaying { get => _isPlaying; private set => SetAndRaise(IsPlayingProperty, ref _isPlaying, value); }

    /// <summary>YouTube a refusé la lecture (vidéo supprimée, intégration interdite…) : la surface est masquée.</summary>
    public bool HasError { get => _hasError; private set => SetAndRaise(HasErrorProperty, ref _hasError, value); }

    /// <summary>La lecture a commencé.</summary>
    public event EventHandler? PlaybackStarted;
    /// <summary>La vidéo est terminée.</summary>
    public event EventHandler? PlaybackEnded;
    /// <summary>YouTube a refusé la lecture (code d'erreur du lecteur, 0 si inconnu).</summary>
    public event EventHandler<int>? PlaybackFailed;
    /// <summary>WebView2 absent ou impossible à créer : l'appelant peut ouvrir la vidéo dans le navigateur.</summary>
    public event EventHandler? Unavailable;
    /// <summary>Échap pressée alors que la vidéo a le focus clavier.</summary>
    public event EventHandler? EscapePressed;
    /// <summary>Clic sur la vidéo quand <see cref="ShowControls"/> est faux.</summary>
    public event EventHandler? SurfaceClicked;
    /// <summary>Molette au-dessus de la vidéo (la page Avalonia ne la reçoit pas).</summary>
    public event EventHandler? WheelScrolled;
    /// <summary>Le lecteur YouTube passe en plein écran (vrai) ou en sort (faux).</summary>
    public event EventHandler<bool>? FullScreenChanged;

    /// <summary>Vrai si le runtime WebView2 est installé (vérifié une fois).</summary>
    public static bool IsRuntimeAvailable => TrailerRuntime.IsAvailable;

    /// <summary>Lance une bande-annonce.</summary>
    public void Play(string youtubeKey, bool muted)
    {
        SetCurrentValue(MutedProperty, muted);
        SetCurrentValue(VideoKeyProperty, youtubeKey);
    }

    /// <summary>Arrête la lecture (le son s'arrête immédiatement).</summary>
    public void Stop() => SetCurrentValue(VideoKeyProperty, null);

    /// <summary>Pause / reprise de la vidéo en cours.</summary>
    public void TogglePause() => _surface.Execute("window.milftv && window.milftv.togglePause()");

    /// <summary>Extrait l'identifiant YouTube d'une clé ou d'une URL (watch?v=, youtu.be/, embed/, shorts/…). Null si invalide.</summary>
    public static string? ParseYoutubeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (YoutubeIdRegex().IsMatch(s)) return s;
        var m = YoutubeUrlRegex().Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YoutubeIdRegex();

    [GeneratedRegex(@"(?:youtu\.be/|youtube(?:-nocookie)?\.com/(?:watch\?(?:[^#]*&)?v=|embed/|v/|shorts/|live/))([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YoutubeUrlRegex();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VideoKeyProperty || change.Property == AutoplayProperty || change.Property == ShowControlsProperty)
            Reload();
        else if (change.Property == MutedProperty && _currentKey is not null)
            _surface.Execute($"window.milftv && window.milftv.setMuted({(Muted ? "true" : "false")})");
        else if (change.Property == VolumeProperty && _currentKey is not null)
            _surface.Execute($"window.milftv && window.milftv.setVolume({ClampedVolume})");
    }

    private int ClampedVolume => Math.Clamp(Volume, 0, 100);

    /// <summary>Tests : la page renvoie le volume appliqué par le lecteur YouTube (message « volume:… »).</summary>
    internal void DevReportVolume() => _surface.Execute("window.milftv && window.milftv.report && window.milftv.report()");

    /// <summary>(Re)charge la page du lecteur pour la clé courante, ou arrête la lecture.</summary>
    private void Reload()
    {
        var key = ParseYoutubeId(VideoKey);
        _token++;
        IsPlaying = false;
        HasError = false;
        _surface.IsVisible = false;
        _currentKey = key;
        if (key is null)
        {
            _surface.ReleaseFocus();
            _surface.Navigate("about:blank");
            _idleTimer.Stop();
            _idleTimer.Start();
            return;
        }
        _idleTimer.Stop();
        _surface.Navigate(BuildUrl(key));
    }

    private string BuildUrl(string key)
    {
        var lang = NormalizeLanguage(TrailerLanguage);
        var url = $"https://{VirtualHost}/{TrailerRuntime.PageName}?v={Uri.EscapeDataString(key)}"
                  + $"&mute={(Muted ? 1 : 0)}&vol={ClampedVolume}&autoplay={(Autoplay ? 1 : 0)}&ui={(ShowControls ? 1 : 0)}"
                  + $"&hl={lang}&t={_token.ToString(CultureInfo.InvariantCulture)}";
        if (PosterUrl is { } img && Uri.TryCreate(img, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            url += "&img=" + Uri.EscapeDataString(SmallerTmdbImage(img));
        return url;
    }

    /// <summary>Les fonds TMDB « w1280 » sont inutilement lourds pour une attente de quelques secondes.</summary>
    private static string SmallerTmdbImage(string url) =>
        url.Contains("image.tmdb.org/t/p/w1280/", StringComparison.OrdinalIgnoreCase) || url.Contains("image.tmdb.org/t/p/original/", StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(url, @"/t/p/(w1280|original)/", "/t/p/w780/")
            : url;

    internal static string NormalizeLanguage(string? lang)
    {
        var l = (lang ?? "").Trim().ToLowerInvariant();
        if (l.Length >= 2 && char.IsAsciiLetterLower(l[0]) && char.IsAsciiLetterLower(l[1])) return l[..2];
        return Localization.Loc.Lang;
    }

    // ───────────── Messages de la page ─────────────

    /// <summary>Message « jeton|type » envoyé par la page ; ceux d'une page précédente sont ignorés.</summary>
    internal void OnPageMessage(string message)
    {
        var sep = message.IndexOf('|');
        if (sep <= 0 || !int.TryParse(message.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out var token) || token != _token)
            return;
        var kind = message[(sep + 1)..];
        switch (kind)
        {
            case "ready":
                if (_currentKey is not null && !HasError) _surface.IsVisible = true;
                break;
            case "playing":
                if (_currentKey is null) break;
                _surface.IsVisible = true;
                if (!IsPlaying)
                {
                    IsPlaying = true;
                    // Le son a pu être changé avant que le lecteur YouTube soit prêt.
                    _surface.Execute($"window.milftv && (window.milftv.setVolume({ClampedVolume}), window.milftv.setMuted({(Muted ? "true" : "false")}))");
                    PlaybackStarted?.Invoke(this, EventArgs.Empty);
                }
                break;
            case "ended":
                IsPlaying = false;
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
                break;
            case "click":
                SurfaceClicked?.Invoke(this, EventArgs.Empty);
                break;
            case "wheel":
                WheelScrolled?.Invoke(this, EventArgs.Empty);
                break;
            case var k when k.StartsWith("volume:", StringComparison.Ordinal):
                if (ViewModels.DevScript.IsEnabled) Log.Info("Bande-annonce : volume appliqué par YouTube = " + k[7..]);
                break;
            default:
                if (kind.StartsWith("error:", StringComparison.Ordinal))
                {
                    int.TryParse(kind.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
                    Log.Warn($"Bande-annonce {_currentKey} : lecture refusée par YouTube (code {code})");
                    IsPlaying = false;
                    HasError = true;
                    _surface.IsVisible = false;
                    _surface.ReleaseFocus();
                    PlaybackFailed?.Invoke(this, code);
                }
                break;
        }
    }

    internal void OnEscape() => EscapePressed?.Invoke(this, EventArgs.Empty);

    internal void OnFullScreen(bool fullscreen) => FullScreenChanged?.Invoke(this, fullscreen);

    internal void OnEngineUnavailable()
    {
        _surface.IsVisible = false;
        if (_currentKey is not null) Unavailable?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Le moteur s'est arrêté (plantage) : la vidéo en cours est perdue, on la relance une fois.</summary>
    internal void OnEngineCrashed()
    {
        if (_currentKey is null) return;
        _surface.IsVisible = false;
        IsPlaying = false;
        if (_crashedKey == _currentKey)
        {
            // Deuxième plantage pour la même vidéo : on abandonne (l'appelant peut proposer YouTube).
            HasError = true;
            PlaybackFailed?.Invoke(this, -1);
            return;
        }
        _crashedKey = _currentKey;
        Dispatcher.UIThread.Post(Reload, DispatcherPriority.Background);
    }

    private string? _crashedKey;

    /// <summary>Ouvre un lien du lecteur (logo YouTube, « Regarder sur YouTube »…) dans le navigateur par défaut.</summary>
    internal void OpenExternal(string uri)
    {
        if (!ShowControls) return;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try
        {
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Ouverture du lien de la bande-annonce impossible : " + ex.Message);
        }
    }
}

/// <summary>Surface native (fenêtre enfant) qui accueille le contrôleur WebView2.</summary>
internal sealed class TrailerSurface : NativeControlHost
{
    private static readonly Dictionary<IntPtr, TrailerSurface> ByHandle = new();

    private readonly TrailerHost _owner;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _controller;
    private bool _initializing;
    private string? _pendingUrl;
    private Window? _window;

    public TrailerSurface(TrailerHost owner) => _owner = owner;

    // ───────────── Fenêtre native ─────────────

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hwnd = TrailerWin32.CreateChild(parent.Handle);
        if (_hwnd == IntPtr.Zero) return base.CreateNativeControlCore(parent);
        ByHandle[_hwnd] = this;
        if (_controller is not null)
        {
            // Contrôle rattaché à nouveau : le moteur suit la nouvelle fenêtre.
            try { _controller.ParentWindow = _hwnd; } catch (Exception ex) { Log.Warn("WebView2 : changement de fenêtre impossible : " + ex.Message); }
            UpdateBounds();
        }
        else if (_pendingUrl is not null && _pendingUrl != "about:blank")
        {
            _ = InitAsync();
        }
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.Handle != _hwnd)
        {
            base.DestroyNativeControlCore(control);
            return;
        }
        ReleaseEngine();
        ByHandle.Remove(_hwnd);
        TrailerWin32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = e.Root as Window;
        if (_window is not null) _window.PositionChanged += OnWindowMoved;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PositionChanged -= OnWindowMoved;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Indispensable pour que les menus du lecteur s'ouvrent au bon endroit après un déplacement de la fenêtre.</summary>
    private void OnWindowMoved(object? sender, PixelPointEventArgs e)
    {
        try { _controller?.NotifyParentWindowPositionChanged(); } catch { /* moteur en cours de fermeture */ }
    }

    /// <summary>Appelé par la procédure de fenêtre (WM_SIZE) : taille en pixels physiques, donc indépendante du facteur d'échelle.</summary>
    internal static void OnResized(IntPtr hwnd, int width, int height)
    {
        if (ByHandle.TryGetValue(hwnd, out var s)) s.SetBounds(width, height);
    }

    private void UpdateBounds()
    {
        if (_hwnd == IntPtr.Zero || !TrailerWin32.GetClientRect(_hwnd, out var r)) return;
        SetBounds(r.Right - r.Left, r.Bottom - r.Top);
    }

    private void SetBounds(int width, int height)
    {
        if (_controller is null) return;
        try { _controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(1, width), Math.Max(1, height)); }
        catch (Exception ex) { Log.Warn("WebView2 : redimensionnement impossible : " + ex.Message); }
    }

    // ───────────── Moteur WebView2 ─────────────

    public void Navigate(string url)
    {
        _pendingUrl = url;
        if (_controller is { } c)
        {
            try { c.CoreWebView2.Navigate(url); }
            catch (Exception ex)
            {
                Log.Warn("WebView2 : navigation impossible : " + ex.Message);
                ReleaseEngine();
                if (url != "about:blank") _ = InitAsync();
            }
            return;
        }
        if (url == "about:blank") return;
        if (!TrailerRuntime.IsAvailable)
        {
            _owner.OnEngineUnavailable();
            return;
        }
        if (_hwnd != IntPtr.Zero) _ = InitAsync();
    }

    public void Execute(string script)
    {
        try { _ = _controller?.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { Log.Warn("WebView2 : script impossible : " + ex.Message); }
    }

    private async Task InitAsync()
    {
        if (_initializing || _controller is not null) return;
        _initializing = true;
        var hwnd = _hwnd;
        try
        {
            var env = await TrailerRuntime.GetEnvironmentAsync(_owner.TrailerLanguage);
            if (hwnd == IntPtr.Zero || hwnd != _hwnd) return;
            CoreWebView2Controller controller;
            try
            {
                controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            }
            catch (Exception first) when (first is not WebView2RuntimeNotFoundException)
            {
                // Environnement périmé (processus du navigateur arrêté entre-temps) : un nouvel essai avec un environnement neuf.
                Log.Warn("WebView2 : nouvel essai après un échec : " + first.Message);
                TrailerRuntime.ResetEnvironment();
                env = await TrailerRuntime.GetEnvironmentAsync(_owner.TrailerLanguage);
                if (hwnd != _hwnd) return;
                controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            }
            if (hwnd != _hwnd)
            {
                controller.Close();
                return;
            }
            Configure(controller);
            _controller = controller;
            UpdateBounds();
            if (_pendingUrl is { } url && url != "about:blank") controller.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Log.Warn("Lecteur de bandes-annonces (WebView2) indisponible : " + ex.Message);
            TrailerRuntime.MarkUnavailable();
            _owner.OnEngineUnavailable();
        }
        finally
        {
            _initializing = false;
        }
    }

    private void Configure(CoreWebView2Controller controller)
    {
        controller.DefaultBackgroundColor = System.Drawing.Color.Black;
        controller.IsVisible = true;
        var wv = controller.CoreWebView2;
        var s = wv.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        // Réglages récents : absents des runtimes WebView2 anciens.
        TrySet(() => s.AreBrowserAcceleratorKeysEnabled = false);
        TrySet(() => s.IsPinchZoomEnabled = false);
        TrySet(() => s.IsSwipeNavigationEnabled = false);
        TrySet(() => s.IsGeneralAutofillEnabled = false);
        TrySet(() => s.IsPasswordAutosaveEnabled = false);
        wv.SetVirtualHostNameToFolderMapping(TrailerHost.VirtualHost, TrailerRuntime.PageFolder, CoreWebView2HostResourceAccessKind.Allow);

        wv.WebMessageReceived += (_, e) =>
        {
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch { /* message non textuel */ }
            if (msg is not null) _owner.OnPageMessage(msg);
        };
        wv.NavigationStarting += (_, e) =>
        {
            // Seules la page locale et la page vide sont chargées dans le cadre principal.
            var uri = e.Uri ?? "";
            if (uri.StartsWith($"https://{TrailerHost.VirtualHost}/", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;
            e.Cancel = true;
            _owner.OpenExternal(uri);
        };
        wv.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _owner.OpenExternal(e.Uri);
        };
        wv.ContainsFullScreenElementChanged += (_, _) => _owner.OnFullScreen(wv.ContainsFullScreenElement);
        wv.ProcessFailed += (_, e) =>
        {
            Log.Warn($"WebView2 : processus arrêté ({e.ProcessFailedKind}, {e.Reason})");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ReleaseEngine();
                    _owner.OnEngineCrashed();
                });
            }
        };
        controller.AcceleratorKeyPressed += (_, e) =>
        {
            // Échap : fermeture, sauf si le lecteur est en plein écran (Échap en sort d'abord).
            if (e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && e.VirtualKey == 0x1B && !wv.ContainsFullScreenElement)
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(_owner.OnEscape);
            }
        };
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { /* non pris en charge par ce runtime */ }
    }

    /// <summary>Ferme le contrôleur (les processus WebView2 s'arrêtent quand plus aucun lecteur n'est ouvert).</summary>
    public void ReleaseEngine()
    {
        var c = _controller;
        _controller = null;
        if (c is null) return;
        ReleaseFocus();
        try { c.Close(); } catch (Exception ex) { Log.Warn("WebView2 : fermeture : " + ex.Message); }
    }

    /// <summary>Rend le focus clavier à la fenêtre principale s'il est resté dans le lecteur (sinon le clavier ne répond plus).</summary>
    public void ReleaseFocus()
    {
        if (_hwnd == IntPtr.Zero) return;
        var focus = TrailerWin32.GetFocus();
        if (focus == IntPtr.Zero || (focus != _hwnd && !TrailerWin32.IsChild(_hwnd, focus))) return;
        if (TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle is { } top && top != IntPtr.Zero)
            TrailerWin32.SetFocus(top);
    }
}

/// <summary>Environnement WebView2 partagé (un seul dossier de données) et page HTML du lecteur.</summary>
internal static class TrailerRuntime
{
    public const string PageName = "trailer.html";

    private static Task<CoreWebView2Environment>? _environment;
    private static bool? _available;

    /// <summary>Dossier servi par l'hôte virtuel (distinct du dossier de données du navigateur).</summary>
    public static string PageFolder { get; } = Path.Combine(AppPaths.Local, "trailer");

    public static bool IsAvailable
    {
        get
        {
            if (_available is { } known) return known;
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
                _available = !string.IsNullOrEmpty(version);
                Log.Info("WebView2 : runtime " + version);
            }
            catch (Exception ex)
            {
                _available = false;
                Log.Warn("WebView2 : runtime introuvable, bandes-annonces ouvertes dans le navigateur (" + ex.Message + ")");
            }
            return _available.Value;
        }
    }

    public static void MarkUnavailable()
    {
        _available = false;
        _environment = null;
    }

    /// <summary>Oublie l'environnement : le prochain lecteur en crée un nouveau (même dossier de données).</summary>
    public static void ResetEnvironment() => _environment = null;

    public static Task<CoreWebView2Environment> GetEnvironmentAsync(string? language) =>
        _environment ??= CreateEnvironmentAsync(language);

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string? language)
    {
        WritePage();
        var dataFolder = AppPaths.Ensure(Path.Combine(AppPaths.Local, "webview2"));
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            // Langue préférée transmise à YouTube (choix de la piste audio des vidéos multilingues).
            Language = Localization.Loc.CultureName(TrailerHost.NormalizeLanguage(language)),
        };
        CoreWebView2Environment env;
        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, dataFolder, options);
        }
        catch
        {
            _environment = null;
            throw;
        }
        try
        {
            // Quand tous les lecteurs sont fermés, le navigateur s'arrête : l'environnement n'est plus utilisable.
            env.BrowserProcessExited += (_, _) => Dispatcher.UIThread.Post(ResetEnvironment);
        }
        catch { /* runtime ancien : l'échec de création est rattrapé par un nouvel essai */ }
        return env;
    }

    /// <summary>Écrit la page du lecteur (réécrite à chaque démarrage du moteur pour suivre les mises à jour de l'application).</summary>
    private static void WritePage()
    {
        Directory.CreateDirectory(PageFolder);
        File.WriteAllText(Path.Combine(PageFolder, PageName), PageHtml);
    }

    /// <summary>
    /// Page du lecteur. Paramètres : v (clé), mute, vol (0 à 100), autoplay, ui (commandes), hl (langue), t (jeton), img (image d'attente).
    /// Messages envoyés à l'application : « t|ready », « t|playing », « t|ended », « t|error:code », « t|click », « t|wheel ».
    /// </summary>
    private const string PageHtml = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="strict-origin-when-cross-origin">
<title>MilfTV</title>
<style>
  html, body { margin: 0; width: 100%; height: 100%; background: #000; overflow: hidden; user-select: none; }
  #bg { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; }
  #bg.on { opacity: 1; }
  #player { position: absolute; inset: 0; width: 100%; height: 100%; border: 0; opacity: 0; transition: opacity .45s ease; }
  body.playing #player { opacity: 1; }
  body.passive #player { pointer-events: none; }
  #spin { position: absolute; left: 50%; top: 50%; width: 38px; height: 38px; margin: -19px 0 0 -19px; border-radius: 50%;
          border: 3px solid rgba(255,255,255,.18); border-top-color: rgba(255,255,255,.85); animation: spin .9s linear infinite; display: none; }
  body.waiting #spin { display: block; }
  body.playing #spin { display: none; }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
</head>
<body>
<img id="bg" alt="">
<div id="spin"></div>
<script>
(function () {
  var q = new URLSearchParams(location.search);
  var v = (q.get('v') || '').replace(/[^A-Za-z0-9_-]/g, '');
  var mute = q.get('mute') === '1';
  var vol = Math.max(0, Math.min(100, parseInt(q.get('vol') || '100', 10) || 0));
  var auto = q.get('autoplay') !== '0';
  var ui = q.get('ui') !== '0';
  var hl = (q.get('hl') || 'en').replace(/[^A-Za-z-]/g, '');
  var tok = (q.get('t') || '0').replace(/[^0-9]/g, '');
  var img = q.get('img');
  var player = null, shown = false, failed = false, readySent = false;

  function post(m) { try { window.chrome.webview.postMessage(tok + '|' + m); } catch (e) { } }
  function ready() { if (!readySent) { readySent = true; post('ready'); } }
  function show() { if (shown || failed) return; shown = true; document.body.classList.add('playing'); post('playing'); }
  function fail(code) { if (failed) return; failed = true; document.body.classList.remove('playing', 'waiting'); post('error:' + code); }

  // Aperçu : pas d'interaction avec la vidéo. Lecteur complet : indicateur de chargement.
  document.body.classList.add(ui ? 'waiting' : 'passive');
  document.addEventListener('click', function () { post('click'); });
  document.addEventListener('wheel', function () { post('wheel'); }, { passive: true });

  // Image d'attente : la surface n'est montrée qu'une fois l'image chargée, pour éviter un écran noir.
  // Image introuvable : l'application garde la sienne jusqu'au début de la vidéo.
  var bg = document.getElementById('bg'), bgFailed = false;
  if (img) {
    bg.onload = function () { bg.className = 'on'; ready(); };
    bg.onerror = function () { bgFailed = true; };
    bg.src = img;
    setTimeout(function () { if (!bgFailed) ready(); }, 2500);
  } else {
    ready();
  }
  if (!v) { fail(0); return; }

  var src = 'https://www.youtube-nocookie.com/embed/' + v
    + '?autoplay=' + (auto ? 1 : 0) + '&mute=' + (mute ? 1 : 0)
    + '&rel=0&modestbranding=1&playsinline=1&iv_load_policy=3&enablejsapi=1'
    + '&hl=' + hl + '&cc_lang_pref=' + hl
    + '&origin=' + encodeURIComponent(location.origin)
    + (ui ? '' : '&controls=0&disablekb=1&fs=0');
  var f = document.createElement('iframe');
  f.id = 'player';
  f.src = src;
  f.allow = 'autoplay; encrypted-media; picture-in-picture' + (ui ? '; fullscreen' : '');
  if (ui) f.allowFullscreen = true;
  f.referrerPolicy = 'strict-origin-when-cross-origin';
  document.body.appendChild(f);
  // Sans lecture automatique, la vignette YouTube et son bouton lecture sont affichés tout de suite.
  if (!auto) f.addEventListener('load', show);
  // Secours si l'API YouTube ne répond pas : le lecteur est montré quand même.
  f.addEventListener('load', function () { setTimeout(show, 6000); });

  window.onYouTubeIframeAPIReady = function () {
    player = new YT.Player('player', {
      events: {
        onReady: function () { if (player && player.setVolume) player.setVolume(vol); },
        onStateChange: function (e) {
          if (e.data === 1) show();
          else if (e.data === 0) post('ended');
        },
        onError: function (e) { fail(e.data); }
      }
    });
  };
  var api = document.createElement('script');
  api.src = 'https://www.youtube.com/iframe_api';
  api.onerror = function () { setTimeout(show, 2500); };
  document.head.appendChild(api);

  window.milftv = {
    setMuted: function (m) {
      if (!player || !player.mute) return;
      if (m) player.mute(); else { player.unMute(); if (player.setVolume) player.setVolume(vol); }
    },
    setVolume: function (n) {
      vol = Math.max(0, Math.min(100, n | 0));
      if (player && player.setVolume) player.setVolume(vol);
    },
    report: function () {
      if (player && player.getVolume) post('volume:' + player.getVolume() + (player.isMuted && player.isMuted() ? ' (muet)' : ''));
    },
    togglePause: function () {
      if (!player || !player.getPlayerState) return;
      if (player.getPlayerState() === 1) player.pauseVideo(); else player.playVideo();
    }
  };
})();
</script>
</body>
</html>
""";
}

/// <summary>Fonctions Win32 de la surface native.</summary>
internal static class TrailerWin32
{
    private const string ClassName = "MilfTV.TrailerSurface";
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000;
    private const uint WmSize = 0x0005;
    private const int BlackBrush = 4;
    private const int IdcArrow = 32512;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Gardé en vie pendant toute l'exécution : la classe de fenêtre y fait référence.
    private static readonly WndProcDelegate Proc = WndProc;
    private static bool _registered;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr parent, IntPtr child);

    /// <summary>Fenêtre enfant noire (pas de flash blanc avant le premier rendu de la page).</summary>
    public static IntPtr CreateChild(IntPtr parent)
    {
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, IdcArrow),
                hbrBackground = GetStockObject(BlackBrush),
                lpszClassName = ClassName,
            };
            // Échec toléré si la classe existe déjà (ERROR_CLASS_ALREADY_EXISTS).
            RegisterClassEx(ref wc);
            _registered = true;
        }
        var hwnd = CreateWindowEx(0, ClassName, null, WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, 1, 1, parent, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            Log.Warn("Lecteur de bandes-annonces : création de la fenêtre impossible (erreur " + Marshal.GetLastWin32Error() + ")");
        return hwnd;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmSize)
        {
            var size = lParam.ToInt64();
            TrailerSurface.OnResized(hWnd, (int)(size & 0xFFFF), (int)((size >> 16) & 0xFFFF));
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
