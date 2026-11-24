using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MilfTV.Controls;

/// <summary>Rangée horizontale (tableau de bord, fiches) : ascenseur épais toujours affiché, flèches au survol pour
/// défiler d'un écran, et défilement en faisant glisser la souris (bouton gauche maintenu, même sur une affiche),
/// avec un peu d'élan au lâcher. Un clic sans glisser reste un clic ; le glisser n'ouvre rien.
/// Usage XAML : <c>&lt;ctl:Rail&gt;&lt;ItemsControl … /&gt;&lt;/ctl:Rail&gt;</c> (le contenu passe dans le défileur).</summary>
public class Rail : Grid
{
    public static readonly StyledProperty<Thickness> ScrollPaddingProperty =
        AvaloniaProperty.Register<Rail, Thickness>(nameof(ScrollPadding));

    /// <summary>Marge intérieure du défileur (l'ascenseur reste sous les affiches).</summary>
    public Thickness ScrollPadding
    {
        get => GetValue(ScrollPaddingProperty);
        set => SetValue(ScrollPaddingProperty, value);
    }

    /// <summary>Un glisser est en cours : l'aperçu au survol des affiches attend.</summary>
    public static bool IsDragging { get; private set; }

    private const double DragThreshold = 8;      // px avant de considérer que c'est un glisser et non un clic
    private const double ArrowStep = 0.8;        // part de la largeur visible parcourue par une flèche
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeWestEast);

    private readonly ScrollViewer _scroller;
    private readonly Button _left;
    private readonly Button _right;

    // Glisser
    private IPointer? _pressed;
    private Point _pressPos;
    private double _pressOffset;
    private bool _dragging;
    private readonly List<(long Ticks, double X)> _samples = new();

    // Animation (flèches) et élan (après un glisser)
    private enum Motion { None, Ease, Glide }
    private Motion _motion;
    private double _from, _to, _velocity;      // velocity en px/ms
    private long _motionStart, _lastFrame;
    private const double EaseMs = 320;

    public Rail()
    {
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false, // ascenseur toujours déployé : plus facile à attraper
        };
        // Ascenseur : thème commun AppScrollBar (Theming/Styles.axaml), toujours affiché ici.
        _scroller.Classes.Add("rail");
        _left = MakeArrow("", HorizontalAlignment.Left, -1);
        _right = MakeArrow("", HorizontalAlignment.Right, 1);
        Children.Add(_scroller);
        Children.Add(_left);
        Children.Add(_right);

        _scroller.ScrollChanged += (_, _) => UpdateArrows();
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => { if (_dragging) EndDrag(glide: false); }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Le contenu déclaré en XAML (ajouté aux enfants de la grille) passe dans le défileur.</summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        var content = Children.FirstOrDefault(c => c != _scroller && c != _left && c != _right);
        if (content is null) return;
        Children.Remove(content);
        _scroller.Content = content;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScrollPaddingProperty) _scroller.Padding = ScrollPadding;
        // État de survol tenu par Avalonia (les événements Entered/Exited se décalent autour d'une capture du pointeur).
        else if (change.Property == IsPointerOverProperty) UpdateArrows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _motion = Motion.None;
        if (_dragging) EndDrag(glide: false);
        _pressed = null;
        base.OnDetachedFromVisualTree(e);
    }

    private double MaxOffset => Math.Max(0, _scroller.Extent.Width - _scroller.Viewport.Width);

    private void SetX(double x) => _scroller.Offset = new Vector(Math.Clamp(x, 0, MaxOffset), _scroller.Offset.Y);

    // ───────────── Flèches ─────────────

    private Button MakeArrow(string glyph, HorizontalAlignment side, int direction)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = 16 },
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            // Centrée sur les affiches (le titre et l'ascenseur sont en dessous).
            Margin = new Thickness(side == HorizontalAlignment.Left ? 6 : 0, 0, side == HorizontalAlignment.Right ? 6 : 0, 56),
            Focusable = false,
            Opacity = 0,
            IsHitTestVisible = false,
            Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) } },
        };
        b.Classes.Add("railArrow");
        b.Click += (_, e) =>
        {
            e.Handled = true;
            var start = _motion == Motion.Ease ? _to : _scroller.Offset.X;
            EaseTo(start + direction * Math.Max(200, _scroller.Viewport.Width * ArrowStep));
        };
        return b;
    }

    private void UpdateArrows()
    {
        var max = MaxOffset;
        var x = _scroller.Offset.X;
        var hover = IsPointerOver;
        Show(_left, hover && !_dragging && x > 1);
        Show(_right, hover && !_dragging && x < max - 1);
    }

    private static void Show(Button b, bool show)
    {
        b.Opacity = show ? 1 : 0;
        b.IsHitTestVisible = show;
    }

    // ───────────── Glisser ─────────────

    private bool IsOwnChrome(object? source)
    {
        for (var v = source as Visual; v is not null && v != this; v = v.GetVisualParent())
            if (v == _left || v == _right || v is ScrollBar) return true;
        return false;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsOwnChrome(e.Source)) return;
        _motion = Motion.None;
        _pressed = e.Pointer;
        _pressPos = e.GetPosition(this);
        _pressOffset = _scroller.Offset.X;
        _dragging = false;
        _samples.Clear();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed != e.Pointer) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (_dragging) EndDrag(glide: false);
            else _pressed = null;
            return;
        }
        var p = e.GetPosition(this);
        var dx = p.X - _pressPos.X;
        if (!_dragging)
        {
            // Glisser franchement horizontal, et seulement s'il y a de quoi défiler.
            if (Math.Abs(dx) < DragThreshold || Math.Abs(dx) < Math.Abs(p.Y - _pressPos.Y) || MaxOffset <= 0) return;
            _dragging = true;
            IsDragging = true;
            // Capture par la rangée : l'affiche perd l'appui, le lâcher ne l'ouvrira pas.
            e.Pointer.Capture(this);
            Cursor = DragCursor;
            _pressPos = p;
            _pressOffset = _scroller.Offset.X;
            dx = 0;
            UpdateArrows();
        }
        SetX(_pressOffset - dx);
        var now = Stopwatch.GetTimestamp();
        _samples.Add((now, p.X));
        while (_samples.Count > 0 && Stopwatch.GetElapsedTime(_samples[0].Ticks, now).TotalMilliseconds > 90) _samples.RemoveAt(0);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed != e.Pointer) return;
        if (!_dragging)
        {
            _pressed = null;
            return;
        }
        e.Handled = true;
        EndDrag(glide: true);
    }

    private void EndDrag(bool glide)
    {
        var wasDragging = _dragging;
        _dragging = false;
        IsDragging = false;
        Cursor = null;
        var pointer = _pressed;
        _pressed = null;
        // Après la remise à zéro : la perte de capture qui suit n'est plus prise pour une interruption.
        if (pointer?.Captured == this) pointer.Capture(null);
        if (wasDragging && glide && _samples.Count >= 2)
        {
            var (t0, x0) = _samples[0];
            var (t1, x1) = _samples[^1];
            var ms = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            // Vitesse du doigt → élan dans le sens inverse (on tire le contenu).
            if (ms > 5 && Stopwatch.GetElapsedTime(t1).TotalMilliseconds < 80) StartGlide(-(x1 - x0) / ms);
        }
        _samples.Clear();
        UpdateArrows();
    }

    // ───────────── Mouvements animés ─────────────

    private void EaseTo(double target)
    {
        _from = _scroller.Offset.X;
        _to = Math.Clamp(target, 0, MaxOffset);
        if (Math.Abs(_to - _from) < 1) return;
        _motionStart = Stopwatch.GetTimestamp();
        StartMotion(Motion.Ease);
    }

    private void StartGlide(double velocity)
    {
        if (Math.Abs(velocity) < 0.25) return;
        _velocity = Math.Clamp(velocity, -6, 6);
        _lastFrame = Stopwatch.GetTimestamp();
        StartMotion(Motion.Glide);
    }

    private void StartMotion(Motion motion)
    {
        var wasRunning = _motion != Motion.None;
        _motion = motion;
        if (!wasRunning) RequestFrame();
    }

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(_ => OnFrame());
        else _motion = Motion.None;
    }

    private void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        switch (_motion)
        {
            case Motion.Ease:
                var t = Math.Min(1, Stopwatch.GetElapsedTime(_motionStart, now).TotalMilliseconds / EaseMs);
                var eased = 1 - Math.Pow(1 - t, 3);
                SetX(_from + (_to - _from) * eased);
                if (t >= 1) _motion = Motion.None;
                break;
            case Motion.Glide:
                var dt = Math.Min(40, Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
                _lastFrame = now;
                var before = _scroller.Offset.X;
                SetX(before + _velocity * dt);
                _velocity *= Math.Pow(0.93, dt / 16.0);
                // Arrêt : vitesse négligeable, ou bord atteint.
                if (Math.Abs(_velocity) < 0.02 || Math.Abs(_scroller.Offset.X - before) < 0.01) _motion = Motion.None;
                break;
            default:
                return;
        }
        if (_motion != Motion.None) RequestFrame();
    }
}
