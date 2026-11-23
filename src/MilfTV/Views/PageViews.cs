using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MilfTV.Core;
using MilfTV.ViewModels;

namespace MilfTV.Views;

public partial class PosterCard : UserControl
{
    public PosterCard() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width > 0) PosterBorder.Height = Math.Round(e.NewSize.Width * 1.5);
    }
}

public partial class ChannelTile : UserControl
{
    public ChannelTile() => InitializeComponent();
}

public static class WheelHelper
{
    /// <summary>Molette verticale toujours transmise au défilement de la page (Maj+molette = horizontal).</summary>
    public static void ForwardWheel(ScrollViewer main)
    {
        main.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            if (e.Source is Visual v && FindRowScroller(v, main) is null) return;
            main.Offset = new Vector(main.Offset.X, Math.Clamp(main.Offset.Y - e.Delta.Y * 90, 0, Math.Max(0, main.Extent.Height - main.Viewport.Height)));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private static ScrollViewer? FindRowScroller(Visual v, ScrollViewer main)
    {
        for (var p = v; p is not null && p != main; p = p.GetVisualParent())
            if (p is ScrollViewer sv && sv.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                return sv;
        return null;
    }
}

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        WheelHelper.ForwardWheel(MainScroll);
    }
}

public partial class LiveView : UserControl
{
    public LiveView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            // Fenêtre étroite (petit portable) : on cache le panneau d'informations et on resserre les catégories.
            var narrow = e.NewSize.Width < 1060;
            InfoPanel.IsVisible = !narrow;
            LayoutRoot.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(400);
            LayoutRoot.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 900 ? 220 : 270);
        };
    }

    private LiveViewModel? Vm => DataContext as LiveViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = Vm;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnVmPropertyChanged;
    }

    private LiveViewModel? _subscribed;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs a)
    {
        if (a.PropertyName == nameof(LiveViewModel.SelectedChannel) && _subscribed?.SelectedChannel is { } ch)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ChannelList.ScrollIntoView(ch));
    }

    private void OnChannelDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        Vm?.PlayCommand.Execute(null);
    }

    private void OnChannelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm?.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }
}

public partial class VodBrowserView : UserControl
{
    public VodBrowserView() => InitializeComponent();

    private VodBrowserViewModel? Vm => DataContext as VodBrowserViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmPropertyChanged;
        _subscribed = Vm;
        if (_subscribed is null) return;
        ApplyLayout(_subscribed);
        _subscribed.PropertyChanged += OnVmPropertyChanged;
    }

    private VodBrowserViewModel? _subscribed;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs a)
    {
        if (_subscribed is not { } vm) return;
        if (a.PropertyName is nameof(VodBrowserViewModel.CardWidth)) ApplyLayout(vm);
        else if (a.PropertyName is nameof(VodBrowserViewModel.Items)) GridScroll.Offset = default;
    }

    private void ApplyLayout(VodBrowserViewModel vm)
    {
        if (Repeater.Layout is UniformGridLayout l)
        {
            l.MinItemWidth = vm.CardWidth;
            l.MinItemHeight = vm.CardHeight + 56;
        }
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CatalogItem item }) Vm?.OpenCommand.Execute(item);
    }
}

internal static class VisualExt
{
    public static Visual? GetVisualParent(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);

    public static T? FindAncestorOfType<T>(this Visual v) where T : Visual
    {
        for (var p = v.GetVisualParent(); p is not null; p = p.GetVisualParent())
            if (p is T t) return t;
        return null;
    }
}
