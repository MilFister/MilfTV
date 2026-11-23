using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.ViewModels;

namespace MilfTV.Views;

public partial class MainWindow : Window
{
    private WindowState _beforeFullscreen = WindowState.Normal;
    private PixelPoint _restorePosition;
    private Size _restoreSize;
    private bool _hasRestoreBounds;

    public MainWindow()
    {
        InitializeComponent();
        // Scénarios de test : ne pas voler le focus à l'utilisateur.
        if (DevScript.IsEnabled) ShowActivated = false;
        var s = SettingsStore.Current;
        if (s.WindowWidth is > 600 && s.WindowHeight is > 400)
        {
            Width = s.WindowWidth.Value;
            Height = s.WindowHeight.Value;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "milftv.ico");
            if (File.Exists(ico)) Icon = new WindowIcon(ico);
            else Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MilfTV/Assets/milftv.ico")));
        }
        catch { /* icône facultative */ }

        FitToScreen();
        // Fenêtre affichée et dessinée : le mode d'affichage fonctionne, le témoin de démarrage est retiré.
        Opened += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            try { File.Delete(Program.RenderGuardPath); } catch { /* facultatif */ }
            if (Program.RenderFallbackHappened && Vm is { } vm)
                vm.Toast(Localization.Loc.T("Settings.SmoothRenderingOffTitle"), Localization.Loc.T("Settings.SmoothRenderingOffMsg"), true);
            // Sauvegarde restaurée (ou échec) au démarrage.
            if (Vm is { } main)
            {
                if (Core.Backup.RestoredAtStartup is { } m)
                    main.Toast(Localization.Loc.T("Backup.RestoredTitle"), Localization.Loc.T("Backup.RestoredMsg", m.Created.LocalDateTime.ToString("g", Localization.Loc.Culture)));
                else if (Core.Backup.RestoreError is { } err)
                    main.Toast(Localization.Loc.T("Backup.RestoreFailed"), err, true);
                main.AnnounceUpdateIfNeeded();
            }
        }, TimeSpan.FromSeconds(3));
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        InitTrailerLayers();
        InitMiniPlayer();
        Library.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Library.IsLoading) or nameof(Library.Catalog) or nameof(Library.Error))
                UpdateOverlays();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Taille de départ adaptée à l'écran : jamais plus grande que la zone utile (barre des tâches exclue),
    /// et taille minimale réduite sur les petits écrans (portable 1366 × 768 en 125 %).</summary>
    private void FitToScreen()
    {
        try
        {
            if (Screens.Primary is not { } screen) return;
            var area = screen.WorkingArea;
            var maxW = area.Width / screen.Scaling;
            var maxH = area.Height / screen.Scaling;
            MinWidth = Math.Min(MinWidth, Math.Floor(maxW));
            MinHeight = Math.Min(MinHeight, Math.Floor(maxH));
            var tooBig = Width > maxW * 0.95 || Height > maxH * 0.95;
            Width = Math.Min(Width, Math.Floor(maxW * 0.95));
            Height = Math.Min(Height, Math.Floor(maxH * 0.95));
            // Écran plus petit que la taille de confort : on ouvre directement en plein écran de travail.
            if (tooBig && (maxW < 1280 || maxH < 760) && WindowState == WindowState.Normal) WindowState = WindowState.Maximized;
        }
        catch { /* taille par défaut */ }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsFullscreen)) ApplyFullscreen(vm.IsFullscreen);
            };
            WatchTrailerState(vm);
        }
        UpdateOverlays();
    }

    private void UpdateOverlays()
    {
        var lib = Library.Instance;
        FirstLoad.IsVisible = lib.IsLoading && lib.Catalog is null;
        LoadError.IsVisible = !lib.IsLoading && lib.Catalog is null && lib.Error is not null && lib.Source is not null;
    }

    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            if (WindowState != WindowState.FullScreen)
            {
                _beforeFullscreen = WindowState;
                // Mémorise la position et la taille exactes pour revenir à la fenêtre d'origine.
                _hasRestoreBounds = WindowState == WindowState.Normal;
                if (_hasRestoreBounds)
                {
                    _restorePosition = Position;
                    _restoreSize = ClientSize;
                }
            }
            ExtendClientAreaToDecorationsHint = false;
            WindowState = WindowState.FullScreen;
        }
        else if (WindowState == WindowState.FullScreen)
        {
            var target = _beforeFullscreen == WindowState.FullScreen ? WindowState.Normal : _beforeFullscreen;
            WindowState = target;
            ExtendClientAreaToDecorationsHint = true;
            if (target == WindowState.Normal && _hasRestoreBounds)
            {
                void Restore()
                {
                    if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                    Width = _restoreSize.Width;
                    Height = _restoreSize.Height;
                    Position = _restorePosition;
                }
                Restore();
                Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && Vm is { } vm)
        {
            var fs = WindowState == WindowState.FullScreen;
            if (!fs && !ExtendClientAreaToDecorationsHint) ExtendClientAreaToDecorationsHint = true;
            if (vm.IsFullscreen != fs) vm.IsFullscreen = fs;
        }
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsXButton1Pressed)
        {
            Vm?.GoBack();
            e.Handled = true;
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Haut de la fenêtre : on peut toujours la déplacer, même avec une fenêtre de dialogue ouverte.
        if (e.GetPosition(this).Y < 44 && WindowState != WindowState.FullScreen)
        {
            OnDragPressed(sender, e);
            return;
        }
        if (e.Source == sender && Vm?.Dialog is not SourceEditorViewModel) Vm?.CloseDialog();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        if (vm.Dialog is PinDialogViewModel pin)
        {
            if (e.Key is >= Key.D0 and <= Key.D9) { pin.Digit(((int)(e.Key - Key.D0)).ToString()); e.Handled = true; }
            else if (e.Key is >= Key.NumPad0 and <= Key.NumPad9) { pin.Digit(((int)(e.Key - Key.NumPad0)).ToString()); e.Handled = true; }
            else if (e.Key == Key.Back) { pin.Backspace(); e.Handled = true; }
            else if (e.Key == Key.Escape) { vm.CloseDialog(); e.Handled = true; }
            return;
        }
        if (vm.Dialog is not null)
        {
            if (e.Key == Key.Escape) { vm.CloseDialog(); e.Handled = true; }
            return;
        }

        // Aperçu au survol : Échap le ferme.
        if (e.Key == Key.Escape && PosterPreviewController.Current is { IsOpen: true } preview)
        {
            preview.Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            vm.IsFullscreen = !vm.IsFullscreen;
            e.Handled = true;
            return;
        }

        if (vm.IsTrailerOpen)
        {
            HandleTrailerKey(vm, e);
            return;
        }

        if (vm.Player.IsFullView)
        {
            if (PlayerView.HandleKey(vm, e)) e.Handled = true;
            return;
        }
        // Mini-lecteur : le clavier sert à l'application, sauf la touche multimédia lecture / pause.
        if (vm.Player.IsMini && e.Key == Key.MediaPlayPause)
        {
            vm.Player.TogglePauseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !(e.Source is TextBox tb && tb != SearchBox && !string.IsNullOrEmpty(tb.Text)))
        {
            if (vm.IsSearchOpen || !string.IsNullOrEmpty(vm.SearchText) && FocusManager?.GetFocusedElement() == SearchBox)
            {
                vm.ClearSearchCommand.Execute(null);
                Focus();
            }
            else vm.GoBack();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            vm.GoBack();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm)
        {
            vm.ShowAllResultsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSourceButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var src in vm.Sources)
        {
            var active = src.Id == Library.Instance.Source?.Id;
            menu.Items.Add(new MenuItem
            {
                Header = (active ? "✓  " : "     ") + src.Name + "  ·  " + src.KindLabel,
                Command = vm.SwitchSourceCommand,
                CommandParameter = src,
                FontWeight = active ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
            });
        }
        if (vm.Sources.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Localization.Loc.T("Source.MenuAddXtream"), Command = vm.AddSourceCommand, CommandParameter = "xtream" });
        menu.Items.Add(new MenuItem { Header = Localization.Loc.T("Source.MenuAddM3u"), Command = vm.AddSourceCommand, CommandParameter = "m3u" });
        menu.Items.Add(new MenuItem { Header = Localization.Loc.T("Source.MenuAddMac"), Command = vm.AddSourceCommand, CommandParameter = "stalker" });
        if (Library.Instance.Source is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = Localization.Loc.T("Source.MenuRefresh"), Command = vm.RefreshCatalogCommand });
        }
        menu.ShowAt(SourceButton);
    }

    /// <summary>Menu du profil actif : modifier le profil ou son avatar, ses réglages, changer de profil, se déconnecter.</summary>
    private void OnProfileButtonClick(object? sender, RoutedEventArgs e) => OpenProfileMenu();

    private Flyout? _profileMenu;

    internal void OpenProfileMenu()
    {
        if (Vm is not { } vm) return;
        var menu = new ProfileMenuViewModel(vm);
        var flyout = new Flyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            Content = new ProfileMenuView { DataContext = menu },
        };
        flyout.FlyoutPresenterClasses.Add("profileMenu");
        menu.CloseRequested += () => flyout.Hide();
        _profileMenu = flyout;
        flyout.ShowAt(ProfileButton);
    }

    private void OnEditSourceClick(object? sender, RoutedEventArgs e)
    {
        if (Library.Instance.Source is { } src) Vm?.EditSource(src);
    }

    private bool _closeConfirmed;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Enregistrement en cours ou imminent : on demande confirmation (il ne peut se faire que MilfTV ouvert).
        if (!_closeConfirmed && Vm is { } main)
        {
            bool interrupt;
            try { interrupt = main.Recorder.ClosingWouldInterrupt; } catch { interrupt = false; }
            if (interrupt)
            {
                e.Cancel = true;
                _ = ConfirmCloseAsync(main);
                return;
            }
        }
        var s = SettingsStore.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        Vm?.Shutdown();
        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync(MainViewModel main)
    {
        if (await main.ConfirmAsync(Localization.Loc.T("Rec.CloseTitle"), Localization.Loc.T("Rec.CloseMessage"), Localization.Loc.T("Rec.CloseConfirm")))
        {
            _closeConfirmed = true;
            Close();
        }
        // Fermeture annulée : un « Redémarrer pour installer » demandé avant ne doit pas relancer MilfTV plus tard.
        else Core.Updates.RestartRequested = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Fermeture normale : le mode d'affichage a fonctionné (même si la fenêtre a été fermée très vite).
        try { File.Delete(Program.RenderGuardPath); } catch { /* facultatif */ }
        base.OnClosed(e);
        // Le contexte de rendu est libéré avec la vue ; on peut détruire le moteur.
        Dispatcher.UIThread.Post(() =>
        {
            try { Vm?.Player.Engine.Dispose(); } catch { /* fermeture */ }
        });
    }
}
