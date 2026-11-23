using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Fenêtre principale : calque de la bande-annonce et calque de l'aperçu au survol.</summary>
public partial class MainWindow
{
    /// <summary>Le lecteur YouTube est passé en plein écran : la vidéo occupe toute la fenêtre.</summary>
    private bool _trailerExpanded;
    /// <summary>La fenêtre a été mise en plein écran par le lecteur YouTube (à rétablir en sortant).</summary>
    private bool _trailerMadeFullscreen;

    private void InitTrailerLayers()
    {
        PosterPreviewController.Attach(this, HoverLayer, HoverPreview);
        // Défilement (molette, barre, clavier) : l'aperçu se ferme et attend la fin du défilement.
        AddHandler(PointerWheelChangedEvent, (_, _) => PosterPreviewController.Current?.OnScroll(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.OffsetDelta != default) PosterPreviewController.Current?.OnScroll();
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        TrailerPlayer.EscapePressed += (_, _) => Vm?.CloseTrailer();
        TrailerPlayer.PlaybackEnded += (_, _) => Vm?.CloseTrailer();
        TrailerPlayer.Unavailable += (_, _) => Vm?.OnTrailerUnavailable();
        TrailerPlayer.FullScreenChanged += (_, fullscreen) => SetTrailerExpanded(fullscreen);
    }

    /// <summary>Ferme aperçu et bande-annonce quand la page change, qu'un dialogue ou le lecteur s'ouvre.</summary>
    private void WatchTrailerState(MainViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.CurrentPage):
                case nameof(MainViewModel.Dialog):
                case nameof(MainViewModel.ProfileOverlay):
                    PosterPreviewController.Current?.Close();
                    if (vm.IsTrailerOpen && (e.PropertyName != nameof(MainViewModel.Dialog) || vm.Dialog is not null)
                                         && (e.PropertyName != nameof(MainViewModel.ProfileOverlay) || vm.ProfileOverlay is not null))
                        vm.CloseTrailer();
                    break;
                case nameof(MainViewModel.IsTrailerOpen):
                    PosterPreviewController.Current?.Close();
                    if (vm.IsTrailerOpen)
                    {
                        Dispatcher.UIThread.Post(() => TrailerCloseButton.Focus(), DispatcherPriority.Loaded);
                    }
                    else
                    {
                        SetTrailerExpanded(false);
                        Focus();
                    }
                    break;
            }
        };
        vm.Player.PropertyChanged += OnPlayerStateChanged;
    }

    private void OnPlayerStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsFullView) || Vm is not { } vm || !vm.Player.IsFullView) return;
        PosterPreviewController.Current?.Close();
        if (vm.IsTrailerOpen) vm.CloseTrailer();
    }

    private void HandleTrailerKey(MainViewModel vm, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseTrailer();
                e.Handled = true;
                break;
            case Key.Space:
                TrailerPlayer.TogglePause();
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                // Boutons du calque (fermer, ouvrir sur YouTube).
                break;
            default:
                // La page en dessous ne reçoit pas le clavier.
                e.Handled = true;
                break;
        }
    }

    private void OnTrailerScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Haut de la fenêtre : déplacement, comme pour les dialogues.
        if (e.GetPosition(this).Y < 44 && WindowState != WindowState.FullScreen)
        {
            OnDragPressed(sender, e);
            return;
        }
        if (e.Source == sender) Vm?.CloseTrailer();
    }

    private void OnTrailerOverlaySizeChanged(object? sender, SizeChangedEventArgs e) => LayoutTrailer();

    /// <summary>Vidéo 16:9 centrée, 1280 px de large au plus, sous la barre de titre de la fenêtre.</summary>
    private void LayoutTrailer()
    {
        var size = TrailerOverlay.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        double w, h;
        if (_trailerExpanded)
        {
            TrailerHeader.IsVisible = false;
            TrailerFrame.Margin = default;
            w = size.Width;
            h = size.Height;
        }
        else
        {
            TrailerHeader.IsVisible = true;
            var top = WindowState == WindowState.FullScreen ? 24 : 56;
            TrailerFrame.Margin = new Thickness(0, top, 0, 32);
            var availW = size.Width - 96;
            var availH = size.Height - top - 32 - 66;
            w = Math.Min(1280, availW);
            h = w * 9 / 16;
            if (h > availH)
            {
                h = availH;
                w = h * 16 / 9;
            }
        }
        TrailerBox.Width = Math.Max(160, Math.Floor(w));
        TrailerBox.Height = Math.Max(90, Math.Floor(h));
    }

    /// <summary>Plein écran demandé par le lecteur YouTube : la vidéo prend toute la fenêtre, elle-même en plein écran.</summary>
    private void SetTrailerExpanded(bool expanded)
    {
        if (_trailerExpanded == expanded) return;
        _trailerExpanded = expanded;
        if (Vm is { } vm)
        {
            if (expanded && !vm.IsFullscreen)
            {
                _trailerMadeFullscreen = true;
                vm.IsFullscreen = true;
            }
            else if (!expanded && _trailerMadeFullscreen)
            {
                _trailerMadeFullscreen = false;
                vm.IsFullscreen = false;
            }
        }
        LayoutTrailer();
    }
}
