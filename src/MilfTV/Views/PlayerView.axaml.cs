using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MilfTV.Core;
using MilfTV.ViewModels;

namespace MilfTV.Views;

public partial class PlayerView : UserControl
{
    private DispatcherTimer? _clickTimer;
    private static readonly Cursor Hidden = new(StandardCursorType.None);

    public PlayerView()
    {
        InitializeComponent();
        Seek.SeekRequested += v => Vm?.SeekTo(v);
        Seek.Interacted += () => Vm?.NotifyActivity();
        Root.PointerMoved += (_, _) => Vm?.NotifyActivity();
        // L'image et le fond des barres du haut et du bas réagissent pareil : clic, double-clic, glisser.
        foreach (var surface in new Control[] { ClickArea, TopBar, BottomBar })
        {
            surface.PointerPressed += OnSurfacePressed;
            surface.PointerMoved += OnSurfaceMoved;
            surface.PointerReleased += OnSurfaceReleased;
            surface.PointerCaptureLost += (_, _) => _press = null;
            surface.ContextRequested += OnSurfaceContextRequested;
        }
        ClickArea.PointerWheelChanged += (_, e) =>
        {
            Vm?.ChangeVolume(e.Delta.Y > 0 ? 5 : -5);
            e.Handled = true;
        };
        FullscreenButton.Click += (_, _) =>
        {
            if (Vm is { } vm) vm.Main.IsFullscreen = !vm.Main.IsFullscreen;
        };
    }

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayerViewModel.ControlsVisible))
                Root.Cursor = vm.ControlsVisible ? Cursor.Default : Hidden;
            else if (args.PropertyName == nameof(PlayerViewModel.IsSidePanelOpen) && vm.IsSidePanelOpen)
                Dispatcher.UIThread.Post(() =>
                {
                    if (vm.PanelSelection is { } sel) ZapList.ScrollIntoView(sel);
                    ZapList.Focus();
                }, DispatcherPriority.Background);
        };
        vm.Main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsFullscreen))
                FullscreenGlyph.Text = vm.Main.IsFullscreen ? Icons.ExitFullScreen : Icons.FullScreen;
        };
    }

    // ── Souris sur l'image ──
    //  - bouton gauche maintenu puis déplacement : la fenêtre suit la souris, la lecture continue ;
    //  - clic simple (relâché sans bouger) : ferme le panneau ouvert, sinon pause / lecture ;
    //  - double-clic : plein écran ;
    //  - appui long sans bouger : rien.
    // L'action du clic est décidée au relâchement : un appui suivi d'un glisser ne met plus en pause.

    /// <summary>Distance (px) à partir de laquelle un appui devient un déplacement de fenêtre.</summary>
    private const double DragThreshold = 5;
    /// <summary>Au-delà de cette durée (ms), un appui sans déplacement n'est plus un clic.</summary>
    private const ulong LongPressMs = 600;

    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private ulong _pressTime;
    private bool _devGesture;

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (Vm is not { } vm || sender is not Control surface) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInteractive(e.Source, surface)) return;
        vm.NotifyActivity();
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            _clickTimer?.Stop();
            vm.Main.IsFullscreen = !vm.Main.IsFullscreen;
            return;
        }
        _press = e;
        _pressPoint = e.GetPosition(this);
        _pressTime = e.Timestamp;
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press) return;
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
        _press = null; // ce n'est plus un clic : pas de pause au relâchement
        if (TopLevel.GetTopLevel(this) is not Window { WindowState: not WindowState.FullScreen } w) return;
        if (_devGesture) Log.Info("DevScript geste : déplacement de la fenêtre demandé");
        else w.BeginMoveDrag(press); // Windows déplace la fenêtre jusqu'au relâchement du bouton
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is null || Vm is not { } vm) return;
        var held = e.Timestamp - _pressTime;
        _press = null;
        if (e.InitialPressMouseButton != MouseButton.Left || held > LongPressMs) return;
        e.Handled = true;
        if (vm.IsSidePanelOpen || vm.IsTracksPanelOpen)
        {
            vm.IsSidePanelOpen = vm.IsTracksPanelOpen = false;
            return;
        }
        // Attendre un éventuel second clic (double-clic = plein écran) avant de mettre en pause.
        _clickTimer?.Stop();
        _clickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(260), DispatcherPriority.Input, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            vm.TogglePauseCommand.Execute(null);
        });
        _clickTimer.Start();
    }

    /// <summary>Clic droit sur l'image : menu des pistes, sorties, formats, informations…</summary>
    private void OnSurfaceContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control surface) return;
        _press = null;
        _clickTimer?.Stop();
        PlayerContextMenu.Show(vm, surface);
        e.Handled = true;
    }

    /// <summary>Ouvre le menu du clic droit au centre de l'image (tests MILFTV_DEV).</summary>
    internal void DevOpenContextMenu()
    {
        if (Vm is { } vm) PlayerContextMenu.Show(vm, ClickArea, atPointer: false);
    }

    /// <summary>Vrai si l'appui vise un contrôle (bouton, barre de progression, curseur, liste…) et non le fond.</summary>
    private static bool IsInteractive(object? source, Control surface)
    {
        for (var v = source as Visual; v is not null && v != surface; v = v.GetVisualParent())
            if (v is Button or MilfTV.Controls.SeekBar or Avalonia.Controls.Primitives.RangeBase or TextBox or ComboBox or ListBox)
                return true;
        return false;
    }

    /// <summary>Tests (MILFTV_DEV) : rejoue un geste sur l'image sans toucher à la vraie souris.
    /// click, drag, hold (appui long), dbl (double-clic). Le déplacement de fenêtre est seulement journalisé.</summary>
    internal async Task DevGestureAsync(string kind)
    {
        if (TopLevel.GetTopLevel(this) is not { } root) return;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var at = ClickArea.TranslatePoint(new Point(ClickArea.Bounds.Width / 2, ClickArea.Bounds.Height / 2), root) ?? default;
        var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var moved = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var released = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        var t = (ulong)Environment.TickCount64;
        void Down(int count) => ClickArea.RaiseEvent(new PointerPressedEventArgs(ClickArea, pointer, root, at, t, pressed, KeyModifiers.None, count));
        void Move(double dx) => ClickArea.RaiseEvent(new PointerEventArgs(PointerMovedEvent, ClickArea, pointer, root, at + new Point(dx, dx / 2), t, moved, KeyModifiers.None));
        void Up() => ClickArea.RaiseEvent(new PointerReleasedEventArgs(ClickArea, pointer, root, at, t, released, KeyModifiers.None, MouseButton.Left));
        _devGesture = true;
        try
        {
            switch (kind)
            {
                case "click": Down(1); t += 90; Up(); break;
                case "jitter": Down(1); Move(3); t += 120; Up(); break;
                case "hold": Down(1); t += 1500; Up(); break;
                case "drag":
                    Down(1);
                    for (var dx = 2; dx <= 40; dx += 6) { t += 16; Move(dx); await Task.Delay(16); }
                    t += 300; Up();
                    break;
                case "dbl": Down(1); t += 80; Up(); t += 90; Down(2); t += 80; Up(); break;
            }
        }
        finally { _devGesture = false; }
    }

    private void OnZapTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LiveChannel ch } && Vm is { } vm)
            vm.PlayFromPanelCommand.Execute(ch);
    }

    private void OnZapDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsSidePanelOpen = false;
    }

    private void OnZapListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            vm.PlayFromPanelCommand.Execute(vm.PanelSelection);
            e.Handled = true;
        }
    }

    private void OnEpisodeDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Episode ep } && Vm is { } vm)
            vm.PlayEpisodeFromPanelCommand.Execute(ep);
    }

    /// <summary>Raccourcis clavier du lecteur.</summary>
    public static bool HandleKey(MainViewModel main, KeyEventArgs e)
    {
        var p = main.Player;
        // laisser les champs de saisie tranquilles
        if (e.Source is TextBox) return false;
        p.NotifyActivity();
        if (p.IsSidePanelOpen && e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Enter)
            return false; // la liste des chaînes gère la navigation
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
            case Key.MediaPlayPause:
                p.TogglePauseCommand.Execute(null);
                return true;
            case Key.Left:
                p.SeekByCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "-60" : "-10");
                return true;
            case Key.Right:
                p.SeekByCommand.Execute(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "60" : "30");
                return true;
            case Key.Up:
                if (p.IsLive && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) p.ZapRelative(-1);
                else p.ChangeVolume(5);
                return true;
            case Key.Down:
                if (p.IsLive && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) p.ZapRelative(1);
                else p.ChangeVolume(-5);
                return true;
            case Key.PageUp:
                p.ZapRelative(-1);
                return true;
            case Key.PageDown:
                p.ZapRelative(1);
                return true;
            case Key.Add:
            case Key.OemPlus:
                p.ChangeVolume(5);
                return true;
            case Key.Subtract:
            case Key.OemMinus:
                p.ChangeVolume(-5);
                return true;
            case Key.M:
            case Key.VolumeMute:
                p.ToggleMuteCommand.Execute(null);
                return true;
            case Key.F:
            case Key.Enter when !p.IsSidePanelOpen:
                main.IsFullscreen = !main.IsFullscreen;
                return true;
            case Key.L:
                p.GoLiveCommand.Execute(null);
                return true;
            case Key.R:
                p.ToggleRecordCommand.Execute(null);
                return true;
            case Key.C:
                p.ToggleSidePanelCommand.Execute(null);
                return true;
            case Key.A:
            case Key.S:
                p.ToggleTracksPanelCommand.Execute(null);
                return true;
            case Key.Escape:
            case Key.Back:
                if (p.IsSidePanelOpen || p.IsTracksPanelOpen) { p.IsSidePanelOpen = p.IsTracksPanelOpen = false; return true; }
                if (main.IsFullscreen) { main.IsFullscreen = false; return true; }
                p.Leave(); // mini-lecteur ou arrêt, selon le réglage
                return true;
            case Key.I:
            case Key.P:
                p.EnterMini();
                return true;
        }
        return false;
    }
}
