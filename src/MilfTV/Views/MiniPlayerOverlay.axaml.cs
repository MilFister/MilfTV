using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MilfTV.ViewModels;

namespace MilfTV.Views;

/// <summary>Commandes du mini-lecteur. Le glisser et le clic sont gérés par la fenêtre (MainWindow.MiniPlayer.cs),
/// qui déplace tout le lecteur ; la zone du volume les arrête pour qu'on puisse régler le son.</summary>
public partial class MiniPlayerOverlay : UserControl
{
    public MiniPlayerOverlay()
    {
        InitializeComponent();
        // Appui et relâchement dans la zone du volume : ni déplacement ni retour au lecteur complet.
        VolumeArea.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, handledEventsToo: true);
        VolumeArea.AddHandler(PointerReleasedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, handledEventsToo: true);
        // Molette sur le mini-lecteur : volume, comme dans le lecteur complet.
        PointerWheelChanged += (_, e) =>
        {
            if (DataContext is not PlayerViewModel vm) return;
            vm.ChangeVolume(e.Delta.Y > 0 ? 5 : -5);
            e.Handled = true;
        };
        // Mini-lecteur étroit : curseur plus court et pourcentage masqué, pour laisser la place au temps écoulé.
        SizeChanged += (_, e) =>
        {
            VolumeSlider.Width = e.NewSize.Width >= 360 ? 90 : e.NewSize.Width >= 290 ? 70 : 54;
            VolumeText.IsVisible = e.NewSize.Width >= 330;
        };
    }
}
