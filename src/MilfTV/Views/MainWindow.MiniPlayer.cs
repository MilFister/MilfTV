using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Mini-lecteur : la vidéo en cours continue dans une fenêtre flottante pendant la navigation.
/// C'est le même lecteur (même surface vidéo, sans coupure), simplement réduit.
/// Glisser l'image le déplace librement dans la fenêtre ; tirer un bord ou un coin le redimensionne (format 16:9 conservé).
/// Un simple clic rouvre le lecteur complet. Taille et position sont mémorisées.</summary>
public partial class MainWindow
{
    private const double MiniMinWidth = 240;
    /// <summary>Marge entre le mini-lecteur et les bords de la fenêtre.</summary>
    private const double MiniEdge = 12;
    /// <summary>Le mini-lecteur reste sous la rangée des boutons réduire / agrandir / fermer.</summary>
    private const double MiniTopLimit = 48;
    /// <summary>Bande sensible le long des bords pour redimensionner.</summary>
    private const double MiniResizeBand = 9;
    /// <summary>Distance à laquelle le mini-lecteur se colle aux bords de la fenêtre.</summary>
    private const double MiniMagnet = 14;
    private const double MiniDragThreshold = 4;

    [Flags]
    private enum MiniGrip { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private Point? _miniPressPoint;
    private bool _miniDragging;
    private MiniGrip _miniGrip;
    private Rect _miniStart;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Dictionary<MiniGrip, Cursor> GripCursors = new()
    {
        [MiniGrip.Left] = new(StandardCursorType.LeftSide),
        [MiniGrip.Right] = new(StandardCursorType.RightSide),
        [MiniGrip.Top] = new(StandardCursorType.TopSide),
        [MiniGrip.Bottom] = new(StandardCursorType.BottomSide),
        [MiniGrip.Left | MiniGrip.Top] = new(StandardCursorType.TopLeftCorner),
        [MiniGrip.Right | MiniGrip.Top] = new(StandardCursorType.TopRightCorner),
        [MiniGrip.Left | MiniGrip.Bottom] = new(StandardCursorType.BottomLeftCorner),
        [MiniGrip.Right | MiniGrip.Bottom] = new(StandardCursorType.BottomRightCorner),
    };

    private void InitMiniPlayer()
    {
        MiniOverlay.PointerPressed += OnMiniPressed;
        MiniOverlay.PointerMoved += OnMiniMoved;
        MiniOverlay.PointerReleased += OnMiniReleased;
        MiniOverlay.PointerCaptureLost += (_, _) =>
        {
            if (_miniPressPoint is null) return;
            _miniPressPoint = null;
            if (_miniDragging) EndMiniGesture();
        };
        // Clic droit sur le mini-lecteur : même menu que sur le lecteur complet.
        MiniOverlay.ContextRequested += (_, e) =>
        {
            if (Vm?.Player is not { } p) return;
            _miniPressPoint = null;
            PlayerContextMenu.Show(p, MiniOverlay);
            e.Handled = true;
        };
        // La position est mémorisée en proportion : le mini-lecteur garde sa place relative quand la fenêtre change de taille.
        SizeChanged += (_, _) =>
        {
            if (_miniPressPoint is null && Vm?.Player is { IsOpen: true, IsMini: true }) ApplyPlayerLayout();
        };
        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm) vm.Player.PropertyChanged += OnMiniStateChanged;
        };
    }

    private void OnMiniStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.IsMini) or nameof(PlayerViewModel.IsOpen) or PlayerViewModel.MiniLayoutProperty)
            ApplyPlayerLayout();
    }

    // ───────────── Taille et position ─────────────

    /// <summary>Largeur maximale : le mini-lecteur (16:9) doit tenir dans la fenêtre.</summary>
    private double MiniMaxWidth() =>
        Math.Max(MiniMinWidth, Math.Min(Bounds.Width - 2 * MiniEdge, (Bounds.Height - MiniTopLimit - MiniEdge) * 16 / 9));

    private static double HeightFor(double w) => Math.Round(w * 9 / 16);

    /// <summary>Positions extrêmes possibles pour un mini-lecteur de cette taille.</summary>
    private (double MinX, double MaxX, double MinY, double MaxY) MiniRange(double w, double h) =>
        (MiniEdge, Math.Max(MiniEdge, Bounds.Width - MiniEdge - w), MiniTopLimit, Math.Max(MiniTopLimit, Bounds.Height - MiniEdge - h));

    /// <summary>Lecteur plein cadre, ou mini-lecteur à sa taille et sa place mémorisées, avec ombre et coins arrondis.</summary>
    private void ApplyPlayerLayout()
    {
        if (Vm?.Player is not { IsOpen: true, IsMini: true })
        {
            PlayerHost.Width = PlayerHost.Height = double.NaN;
            PlayerHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            PlayerHost.VerticalAlignment = VerticalAlignment.Stretch;
            PlayerHost.Margin = default;
            PlayerHost.CornerRadius = default;
            PlayerHost.BoxShadow = default;
            PlayerClip.CornerRadius = default;
            PlayerClip.ClipToBounds = false;
            ToastHost.Margin = new Thickness(0, 0, 24, 24);
            return;
        }

        var s = SettingsStore.Current;
        var w = Math.Clamp(s.MiniPlayerWidth ?? AppSettings.MiniPresetWidths[Math.Clamp(s.MiniPlayerSize, 0, 2)], MiniMinWidth, MiniMaxWidth());
        var h = HeightFor(w);
        // Anciennes versions : coin mémorisé au lieu d'une position libre.
        var corner = s.MiniPlayerCorner is "tl" or "tr" or "bl" or "br" ? s.MiniPlayerCorner : "br";
        var fx = Math.Clamp(s.MiniPlayerX ?? (corner[1] == 'r' ? 1 : 0), 0, 1);
        var fy = Math.Clamp(s.MiniPlayerY ?? (corner[0] == 'b' ? 1 : 0), 0, 1);
        var (minX, maxX, minY, maxY) = MiniRange(w, h);
        SetMiniRect(minX + fx * (maxX - minX), minY + fy * (maxY - minY), w, h);
        PlayerHost.CornerRadius = new CornerRadius(14);
        PlayerHost.BoxShadow = BoxShadows.Parse("0 18 44 0 #80000000");
        PlayerClip.CornerRadius = new CornerRadius(14);
        PlayerClip.ClipToBounds = true;
    }

    private void SetMiniRect(double x, double y, double w, double h)
    {
        PlayerHost.HorizontalAlignment = HorizontalAlignment.Left;
        PlayerHost.VerticalAlignment = VerticalAlignment.Top;
        PlayerHost.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
        PlayerHost.Width = Math.Round(w);
        PlayerHost.Height = Math.Round(h);
        // Les notifications (en bas à droite) passent au-dessus du mini-lecteur quand il est dans ce coin.
        var toastLeft = Bounds.Width - 24 - 380;
        var overlaps = x + w > toastLeft && y + h > Bounds.Height - 220;
        ToastHost.Margin = new Thickness(0, 0, 24, overlaps ? Math.Max(24, Bounds.Height - y + 12) : 24);
    }

    private Rect MiniRect() => new(PlayerHost.Margin.Left, PlayerHost.Margin.Top, PlayerHost.Width, PlayerHost.Height);

    /// <summary>Mémorise la taille et la position (en proportion de la place disponible).</summary>
    private void SaveMiniRect()
    {
        var r = MiniRect();
        var (minX, maxX, minY, maxY) = MiniRange(r.Width, r.Height);
        var s = SettingsStore.Current;
        s.MiniPlayerWidth = Math.Round(r.Width);
        s.MiniPlayerX = maxX > minX ? Math.Round((r.X - minX) / (maxX - minX), 4) : 0.5;
        s.MiniPlayerY = maxY > minY ? Math.Round((r.Y - minY) / (maxY - minY), 4) : 0.5;
        var preset = Array.FindIndex(AppSettings.MiniPresetWidths, p => Math.Abs(p - r.Width) < 1);
        if (preset >= 0) s.MiniPlayerSize = preset;
        SettingsStore.Save();
        Vm?.Player.NotifyMiniLayoutChanged();
        Log.Info($"Mini-lecteur : {r.Width:0}×{r.Height:0} en ({r.X:0}, {r.Y:0})");
    }

    // ───────────── Souris : déplacer, redimensionner, cliquer ─────────────

    /// <summary>Bord ou coin sous la souris (bande de quelques pixels le long du cadre).</summary>
    private MiniGrip GripAt(Point p)
    {
        var w = PlayerHost.Bounds.Width;
        var h = PlayerHost.Bounds.Height;
        var g = MiniGrip.None;
        if (p.X <= MiniResizeBand) g |= MiniGrip.Left;
        else if (p.X >= w - MiniResizeBand) g |= MiniGrip.Right;
        if (p.Y <= MiniResizeBand) g |= MiniGrip.Top;
        else if (p.Y >= h - MiniResizeBand) g |= MiniGrip.Bottom;
        return g;
    }

    private void OnMiniPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _miniPressPoint = e.GetPosition(this);
        _miniGrip = GripAt(e.GetPosition(PlayerHost));
        _miniStart = MiniRect();
        _miniDragging = false;
        e.Handled = true;
    }

    private void OnMiniMoved(object? sender, PointerEventArgs e)
    {
        if (_miniPressPoint is not { } start)
        {
            // Survol : curseur de redimensionnement sur les bords, main ailleurs.
            var g = GripAt(e.GetPosition(PlayerHost));
            MiniOverlay.Cursor = g == MiniGrip.None ? HandCursor : GripCursors[g];
            return;
        }
        var d = e.GetPosition(this) - start;
        if (!_miniDragging && Math.Abs(d.X) < MiniDragThreshold && Math.Abs(d.Y) < MiniDragThreshold) return;
        _miniDragging = true;
        if (_miniGrip == MiniGrip.None) MoveMini(d.X, d.Y);
        else ResizeMini(d.X, d.Y);
    }

    private void OnMiniReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_miniPressPoint is null) return;
        _miniPressPoint = null;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        if (_miniDragging) EndMiniGesture();
        else Vm?.Player.ExpandMini(); // simple clic : retour au lecteur complet
    }

    private void EndMiniGesture()
    {
        _miniDragging = false;
        SaveMiniRect();
    }

    /// <summary>Déplacement libre, sans sortir de la fenêtre ; léger aimant le long des bords.</summary>
    private void MoveMini(double dx, double dy)
    {
        var r = _miniStart;
        var (minX, maxX, minY, maxY) = MiniRange(r.Width, r.Height);
        var x = Magnet(Math.Clamp(r.X + dx, minX, maxX), minX, maxX);
        var y = Magnet(Math.Clamp(r.Y + dy, minY, maxY), minY, maxY);
        SetMiniRect(x, y, r.Width, r.Height);
    }

    private static double Magnet(double v, double min, double max) =>
        v - min < MiniMagnet ? min : max - v < MiniMagnet ? max : v;

    /// <summary>Redimensionnement par un bord ou un coin : le bord opposé reste fixe et l'image garde le format 16:9.</summary>
    private void ResizeMini(double dx, double dy)
    {
        var r = _miniStart;
        var g = _miniGrip;
        bool left = g.HasFlag(MiniGrip.Left), right = g.HasFlag(MiniGrip.Right);
        bool top = g.HasFlag(MiniGrip.Top), bottom = g.HasFlag(MiniGrip.Bottom);
        var byX = left ? r.Width - dx : right ? r.Width + dx : double.NaN;
        var byY = top ? (r.Height - dy) * 16 / 9 : bottom ? (r.Height + dy) * 16 / 9 : double.NaN;
        // Coin : c'est le mouvement le plus marqué qui décide.
        var w = double.IsNaN(byX) ? byY
            : double.IsNaN(byY) ? byX
            : Math.Abs(dx) >= Math.Abs(dy) * 16 / 9 ? byX : byY;

        // Place disponible du côté qui s'agrandit (le bord opposé ne bouge pas).
        var max = MiniMaxWidth();
        max = Math.Min(max, left ? r.Right - MiniEdge : Bounds.Width - MiniEdge - r.X);
        max = Math.Min(max, (top ? r.Bottom - MiniTopLimit : Bounds.Height - MiniEdge - r.Y) * 16 / 9);
        w = Math.Clamp(w, MiniMinWidth, Math.Max(MiniMinWidth, max));
        var h = HeightFor(w);
        SetMiniRect(left ? r.Right - w : r.X, top ? r.Bottom - h : r.Y, w, h);
    }

    // ───────────── Tests (MILFTV_DEV) ─────────────

    /// <summary>Rejoue un geste sur le mini-lecteur sans toucher à la vraie souris : glisser depuis le centre (grip null),
    /// depuis un bord ou un coin (« l », « r », « t », « b », « tl », « br »…), ou simple clic si dx = dy = 0.</summary>
    internal async Task DevMiniGestureAsync(double dx, double dy, string? grip = null)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var b = PlayerHost.Bounds;
        var at = new Point(
            grip?.Contains('l') == true ? b.X + 3 : grip?.Contains('r') == true ? b.Right - 3 : b.X + b.Width / 2,
            grip?.Contains('t') == true ? b.Y + 3 : grip?.Contains('b') == true ? b.Bottom - 3 : b.Y + b.Height / 2);
        var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var moved = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var released = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        var t = (ulong)Environment.TickCount64;
        MiniOverlay.RaiseEvent(new PointerPressedEventArgs(MiniOverlay, pointer, this, at, t, pressed, KeyModifiers.None));
        const int steps = 12;
        for (var i = 1; i <= steps && (dx != 0 || dy != 0); i++)
        {
            t += 16;
            MiniOverlay.RaiseEvent(new PointerEventArgs(PointerMovedEvent, MiniOverlay, pointer, this, at + new Point(dx * i / steps, dy * i / steps), t, moved, KeyModifiers.None));
            await Task.Delay(16);
        }
        t += 60;
        MiniOverlay.RaiseEvent(new PointerReleasedEventArgs(MiniOverlay, pointer, this, at + new Point(dx, dy), t, released, KeyModifiers.None, MouseButton.Left));
    }
}
