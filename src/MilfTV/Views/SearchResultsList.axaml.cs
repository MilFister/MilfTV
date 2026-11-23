using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MilfTV.Core;
using MilfTV.ViewModels;

namespace MilfTV.Views;

public partial class SearchResultsList : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<SearchResultsList, bool>(nameof(Compact));

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public SearchResultsList()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Fill();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty) Fill();
    }

    private void Fill()
    {
        var r = DataContext as SearchResults;
        var n = Compact ? 5 : int.MaxValue;
        Set(ChannelsList, ChannelsHeader, r?.Channels.Take(n).ToList());
        Set(MoviesList, MoviesHeader, r?.Movies.Take(n).ToList());
        Set(SeriesList, SeriesHeader, r?.Series.Take(n).ToList());
        Set(ProgramsList, ProgramsHeader, r?.Programs.Take(n).ToList());
        if (r is not null && r.Channels.Count > 0)
            Library.Instance.UpdateNowNext(r.Channels.Take(n).ToList());
    }

    private static void Set<T>(ItemsControl list, Control header, List<T>? items)
    {
        list.ItemsSource = items;
        var visible = items is { Count: > 0 };
        list.IsVisible = visible;
        header.IsVisible = visible;
    }

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c) MainViewModel.Current?.OpenSearchResultCommand.Execute(c.DataContext);
    }
}
