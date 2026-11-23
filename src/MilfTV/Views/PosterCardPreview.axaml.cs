using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Carte flottante de l'aperçu au survol (placée par <see cref="PosterPreviewController"/>).</summary>
public partial class PosterCardPreview : UserControl
{
    public PosterCardPreview()
    {
        InitializeComponent();
        // La vidéo est une fenêtre native : clics et molette n'atteignent pas Avalonia, la page les signale.
        Trailer.SurfaceClicked += (_, _) => (DataContext as PosterPreviewViewModel)?.MoreInfoCommand.Execute(null);
        Trailer.WheelScrolled += (_, _) => PosterPreviewController.Current?.OnScroll();
    }

    /// <summary>Coupe immédiatement la vidéo (avant le changement de contexte, pour ne pas réactiver le son).</summary>
    internal void StopTrailer() => Trailer.Stop();
}

/// <summary>
/// Aperçu au survol des affiches : après un survol immobile de <see cref="ProfilePrefs.HoverDelayMs"/>, une carte flottante
/// s'ouvre à côté de l'affiche (à droite de préférence, sinon à gauche, remontée pour rester dans la fenêtre).
/// Elle se ferme quand le pointeur quitte l'affiche et la carte (après un court délai de grâce), au défilement,
/// à la navigation, à l'ouverture du lecteur ou d'un dialogue. Un seul aperçu à la fois.
/// <para>La position du pointeur est relevée par Win32 : au-dessus de la vidéo (fenêtre native), Avalonia ne reçoit plus d'événements.</para>
/// </summary>
public sealed class PosterPreviewController
{
    private const double Gap = 12;
    private const double Margin = 10;
    /// <summary>Hauteur de la barre supérieure (boutons de la fenêtre) que l'aperçu ne recouvre pas.</summary>
    private const double TopReserved = 56;
    private const double MoveTolerance = 6;
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    public static PosterPreviewController? Current { get; private set; }

    private readonly Window _window;
    private readonly Canvas _layer;
    private readonly PosterCardPreview _view;
    private readonly DispatcherTimer _delay;
    private readonly DispatcherTimer _watch;

    private PosterCard? _candidate;
    private Point _candidatePoint;
    private PosterCard? _anchor;
    private PosterPreviewViewModel? _vm;
    private DateTime _lastScroll = DateTime.MinValue;
    private DateTime? _outsideSince;

    /// <summary>Tests (DevScript) : aperçu ouvert et volume réellement appliqué par YouTube.</summary>
    internal PosterPreviewViewModel? DevVm => _vm;
    internal void DevReportVolume() => _view.Trailer.DevReportVolume();

    private PosterPreviewController(Window window, Canvas layer, PosterCardPreview view)
    {
        _window = window;
        _layer = layer;
        _view = view;
        _view.IsVisible = false;
        _delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _delay.Tick += (_, _) => OnDelayElapsed();
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _watch.Tick += (_, _) => Watch();
        _view.SizeChanged += (_, _) => { if (_anchor is not null) Position(); };
        _layer.SizeChanged += (_, _) => { if (_anchor is not null) Position(); };
        _window.Deactivated += (_, _) =>
        {
            CancelPending();
            Close();
        };
    }

    /// <summary>Installe le calque de l'aperçu dans la fenêtre principale.</summary>
    public static void Attach(Window window, Canvas layer, PosterCardPreview view) => Current = new PosterPreviewController(window, layer, view);

    public bool IsOpen => _anchor is not null;

    /// <summary>Tests (MILFTV_DEV) : aperçu ouvert sans survol réel, gardé ouvert jusqu'à Close().</summary>
    private bool _devPinned;
    internal void DevOpen(PosterCard card)
    {
        Open(card);
        _devPinned = true;
    }

    private MainViewModel? Main => _window.DataContext as MainViewModel;
    private static ProfilePrefs Prefs => Library.Instance.Prefs;

    // ───────────── Événements des affiches ─────────────

    internal void OnPosterEntered(PosterCard card, Point windowPoint)
    {
        if (!CanPreview(card))
        {
            if (_candidate == card) CancelPending();
            return;
        }
        _candidate = card;
        _candidatePoint = windowPoint;
        if (_anchor != card) Restart();
    }

    internal void OnPosterMoved(PosterCard card, Point windowPoint)
    {
        if (card != _candidate)
        {
            OnPosterEntered(card, windowPoint);
            return;
        }
        // Survol « immobile » : le délai repart si le pointeur bouge franchement.
        var d = windowPoint - _candidatePoint;
        if (Math.Abs(d.X) <= MoveTolerance && Math.Abs(d.Y) <= MoveTolerance) return;
        _candidatePoint = windowPoint;
        if (_anchor != card) Restart();
    }

    internal void OnPosterExited(PosterCard card)
    {
        // L'aperçu ouvert est surveillé par la position réelle du pointeur (Watch) ; ici, seul l'aperçu en attente est annulé.
        if (_candidate == card) CancelPending();
    }

    internal void OnPosterDetached(PosterCard card)
    {
        if (_candidate == card) CancelPending();
        if (_anchor == card) Close();
    }

    /// <summary>Défilement (molette ou barre) : l'aperçu se ferme et ne se rouvre qu'une fois le défilement terminé.</summary>
    public void OnScroll()
    {
        _lastScroll = DateTime.UtcNow;
        Close();
        if (_candidate is not null) Restart();
    }

    // ───────────── Ouverture ─────────────

    private TimeSpan Delay => TimeSpan.FromMilliseconds(Math.Clamp(Prefs.HoverDelayMs, 150, 10_000));

    private void Restart()
    {
        _delay.Stop();
        _delay.Interval = Delay;
        _delay.Start();
    }

    private void CancelPending()
    {
        _delay.Stop();
        _candidate = null;
    }

    private void OnDelayElapsed()
    {
        _delay.Stop();
        var card = _candidate;
        if (card is null) return;
        if (DateTime.UtcNow - _lastScroll < Delay)
        {
            Restart();
            return;
        }
        if (!card.IsPointerOver || !CanPreview(card))
        {
            CancelPending();
            return;
        }
        if (_anchor != card) Open(card);
    }

    private bool CanPreview(PosterCard card)
    {
        var main = Main;
        return main is not null
               && Prefs.HoverPreview
               && card.DataContext is Movie or Series
               && !Library.Instance.IsHidden((CatalogItem)card.DataContext)
               && card.GetVisualRoot() == _window
               && card.IsEffectivelyVisible
               && _window.IsActive
               && !main.Player.IsFullView
               && main.Dialog is null
               && main.ProfileOverlay is null
               && !main.IsTrailerOpen;
    }

    private void Open(PosterCard card)
    {
        Close();
        if (Main is not { } main || card.DataContext is not CatalogItem item) return;
        var vm = new PosterPreviewViewModel(main, item);
        vm.CloseRequested += Close;
        _vm = vm;
        _anchor = card;
        _outsideSince = null;
        _view.DataContext = vm;
        _view.IsVisible = true;
        Position();
        _watch.Start();
    }

    /// <summary>Place la carte à droite de l'affiche, sinon à gauche, sinon centrée ; remontée pour rester dans la fenêtre.</summary>
    private void Position()
    {
        if (_anchor is null) return;
        var area = _anchor.PosterArea;
        if (area.TranslatePoint(default, _layer) is not { } topLeft)
        {
            Close();
            return;
        }
        var poster = new Rect(topLeft, area.Bounds.Size);
        var w = _view.Width;
        _view.Measure(new Size(w, double.PositiveInfinity));
        var h = _view.DesiredSize.Height;
        var bounds = _layer.Bounds.Size;
        var top = _window.WindowState == WindowState.FullScreen ? Margin : TopReserved;

        var x = poster.Right + Gap;
        if (x + w > bounds.Width - Margin)
        {
            x = poster.Left - Gap - w;
            if (x < Margin) x = Math.Clamp(poster.Center.X - w / 2, Margin, Math.Max(Margin, bounds.Width - Margin - w));
        }
        var y = poster.Top;
        if (y + h > bounds.Height - Margin) y = bounds.Height - Margin - h;
        if (y < top) y = top;
        Canvas.SetLeft(_view, Math.Round(x));
        Canvas.SetTop(_view, Math.Round(y));
    }

    // ───────────── Surveillance et fermeture ─────────────

    private void Watch()
    {
        var main = Main;
        if (_anchor is not { } anchor || _vm is not { } vm)
        {
            _watch.Stop();
            return;
        }
        if (main is null || !Prefs.HoverPreview || main.Player.IsFullView || main.Dialog is not null || main.ProfileOverlay is not null || main.IsTrailerOpen
            || anchor.GetVisualRoot() != _window || !anchor.IsEffectivelyVisible || !ReferenceEquals(anchor.DataContext, vm.Item))
        {
            Close();
            return;
        }

        if (_devPinned) return;
        var inside = CursorInLayer() is { } p
                     && (AnchorRect() is { } r && r.Inflate(6).Contains(p)
                         || new Rect(Canvas.GetLeft(_view), Canvas.GetTop(_view), _view.Bounds.Width, _view.Bounds.Height).Inflate(2).Contains(p));
        if (inside)
        {
            _outsideSince = null;
            return;
        }
        var now = DateTime.UtcNow;
        _outsideSince ??= now;
        if (now - _outsideSince.Value >= Grace) Close();
    }

    private Rect? AnchorRect()
    {
        var area = _anchor?.PosterArea;
        return area?.TranslatePoint(default, _layer) is { } tl ? new Rect(tl, area.Bounds.Size) : null;
    }

    /// <summary>Position réelle du pointeur dans le calque (y compris au-dessus de la vidéo native ou hors de la fenêtre).</summary>
    private Point? CursorInLayer()
    {
        if (!GetCursorPos(out var pt)) return null;
        var client = _window.PointToClient(new PixelPoint(pt.X, pt.Y));
        return _window.TranslatePoint(client, _layer);
    }

    /// <summary>Ferme l'aperçu (vidéo arrêtée, chargements abandonnés).</summary>
    public void Close()
    {
        _watch.Stop();
        _outsideSince = null;
        _devPinned = false;
        if (_vm is not { } vm) return;
        vm.CloseRequested -= Close;
        vm.Cancel();
        _vm = null;
        _anchor = null;
        _view.StopTrailer();
        _view.IsVisible = false;
        _view.DataContext = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
