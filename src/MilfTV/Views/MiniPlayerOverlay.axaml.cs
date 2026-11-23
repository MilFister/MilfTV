using Avalonia.Controls;

namespace MilfTV.Views;

/// <summary>Commandes du mini-lecteur. Le glisser et le clic sont gérés par la fenêtre (MainWindow.MiniPlayer.cs),
/// qui déplace tout le lecteur.</summary>
public partial class MiniPlayerOverlay : UserControl
{
    public MiniPlayerOverlay() => InitializeComponent();
}
