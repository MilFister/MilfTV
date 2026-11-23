using System.Collections.ObjectModel;
using MilfTV.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Controls;
using MilfTV.Core;

namespace MilfTV.ViewModels;

public sealed class DayOption
{
    public int Offset { get; init; }
    public string Label { get; init; } = "";
    public DateTime Date { get; init; }
}

/// <summary>Guide TV en grille.</summary>
public sealed partial class GuideViewModel : ViewModelBase, IEpgGridSource
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, List<EpgProgram>> _cache = new();
    private readonly HashSet<string> _loaded = new();

    public GuideViewModel(MainViewModel main)
    {
        _main = main;
        LiveViewModel.AllCategory.Name = Loc.T("Live.AllChannels");
        LiveViewModel.FavCategory.Name = Loc.T("Common.Favorites");
        Lib.CatalogChanged += Reload;
        Lib.ParentalChanged += Reload;
        Lib.EpgChanged += OnEpgChanged;
        for (int i = -1; i < 7; i++)
        {
            var d = DateTime.Today.AddDays(i);
            Days.Add(new DayOption { Offset = i, Date = d, Label = TextNorm.DayLabel(new DateTimeOffset(d)) });
        }
        _selectedDay = Days[1];
        Reload();
    }

    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<DayOption> Days { get; } = new();
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private DayOption _selectedDay;
    [ObservableProperty] private IReadOnlyList<LiveChannel> _channels = Array.Empty<LiveChannel>();
    [ObservableProperty] private EpgProgram? _selectedProgram;
    [ObservableProperty] private LiveChannel? _selectedProgramChannel;
    [ObservableProperty] private int _scrollToNowRequest;

    public DateTimeOffset DayStart => new(SelectedDay.Date);
    public bool HasSelection => SelectedProgram is not null;
    public bool SelectedIsLive => SelectedProgram?.IsLiveAt(DateTimeOffset.Now) == true;
    public bool SelectedCanRecord => SelectedProgram is { } p && p.End > DateTimeOffset.Now;
    public string? SelectedWhen => SelectedProgram is { } p ? $"{p.DayLabel} · {p.TimeRange} · {p.DurationText}" : null;
    public bool HasEpgSource => Lib.Provider?.GetXmltvUrl(Lib.Catalog ?? new Catalog()) is not null || Lib.Source?.Kind != SourceKind.M3u;

    public event Action? Changed;

    IReadOnlyList<LiveChannel> IEpgGridSource.Channels => Channels;

    private void Reload()
    {
        var c = Lib.Catalog;
        var previous = SelectedCategory?.Id;
        Categories.Clear();
        LiveViewModel.FavCategory.Count = c?.Channels.Count(x => x.IsFavorite && !Lib.IsHidden(x)) ?? 0;
        if (LiveViewModel.FavCategory.Count > 0) Categories.Add(LiveViewModel.FavCategory);
        if (c is not null)
            foreach (var cat in c.LiveCategories.Where(x => x.Count > 0 && !Lib.IsHidden(x))) Categories.Add(cat);
        Categories.Add(LiveViewModel.AllCategory);
        SelectedCategory = Categories.FirstOrDefault(x => x.Id == previous) ?? Categories.FirstOrDefault();
        OnPropertyChanged(nameof(HasEpgSource));
    }

    partial void OnSelectedCategoryChanged(Category? value) => ApplyCategory();

    private void OnEpgChanged()
    {
        ClearCache();
        Changed?.Invoke();
    }

    public override void Detach()
    {
        Lib.CatalogChanged -= Reload;
        Lib.ParentalChanged -= Reload;
        Lib.EpgChanged -= OnEpgChanged;
    }

    /// <summary>Guide resté ouvert après minuit : « Aujourd'hui » redevient le bon jour.</summary>
    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        if (Days.Count > 1 && Days[1].Date.Date != DateTime.Today)
        {
            var offset = SelectedDay?.Offset ?? 0;
            Days.Clear();
            for (int i = -1; i < 7; i++)
            {
                var d = DateTime.Today.AddDays(i);
                Days.Add(new DayOption { Offset = i, Date = d, Label = TextNorm.DayLabel(new DateTimeOffset(d)) });
            }
            SelectedDay = Days.FirstOrDefault(d => d.Offset == offset) ?? Days[1];
        }
    }

    partial void OnSelectedDayChanged(DayOption value)
    {
        // Ctrl+clic sur le jour sélectionné le désélectionne : on garde un jour (la grille en a besoin).
        if (value is null)
        {
            if (Days.Count > 1) SelectedDay = Days[1];
            return;
        }
        ClearCache();
        OnPropertyChanged(nameof(DayStart));
        Changed?.Invoke();
    }

    private void ApplyCategory()
    {
        var c = Lib.Catalog;
        var cat = SelectedCategory;
        if (c is null || cat is null) { Channels = Array.Empty<LiveChannel>(); Changed?.Invoke(); return; }
        IEnumerable<LiveChannel> q = cat.Id switch
        {
            "__all" => c.Channels,
            "__fav" => c.Channels.Where(x => x.IsFavorite),
            _ => c.Channels.Where(x => x.CategoryId == cat.Id)
        };
        Channels = q.Where(x => !Lib.IsHidden(x)).ToList();
        Changed?.Invoke();
    }

    private void ClearCache()
    {
        _cache.Clear();
        _loaded.Clear();
    }

    public IReadOnlyList<EpgProgram>? GetPrograms(LiveChannel ch) =>
        ch.EpgKey is { } k && _cache.TryGetValue(k, out var l) ? l : (ch.EpgKey is { } k2 && _loaded.Contains(k2) ? Array.Empty<EpgProgram>() : null);

    public void EnsureLoaded(int firstRow, int lastRow)
    {
        var src = Lib.Source;
        if (src is null || Channels.Count == 0) return;
        firstRow = Math.Max(0, firstRow - 10);
        lastRow = Math.Min(Channels.Count - 1, lastRow + 10);
        var keys = new List<string>();
        for (int i = firstRow; i <= lastRow; i++)
            if (Channels[i].EpgKey is { } k && _loaded.Add(k)) keys.Add(k);
        if (keys.Count == 0) return;
        try
        {
            var from = DayStart.AddHours(-3);
            var to = DayStart.AddHours(27);
            foreach (var (k, list) in Lib.Epg.GetPrograms(src.Id, keys, from, to)) _cache[k] = list;
        }
        catch (Exception ex)
        {
            Log.Warn("Guide : " + ex.Message);
            foreach (var k in keys) _loaded.Remove(k);
        }
    }

    public void Select(LiveChannel ch, EpgProgram p)
    {
        SelectedProgramChannel = ch;
        SelectedProgram = p;
    }

    partial void OnSelectedProgramChanged(EpgProgram? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedIsLive));
        OnPropertyChanged(nameof(SelectedCanRecord));
        OnPropertyChanged(nameof(SelectedWhen));
    }

    public Task ActivateAsync(LiveChannel ch, EpgProgram? p)
    {
        if (p is null || p.IsLiveAt(DateTimeOffset.Now))
            return _main.PlayChannelAsync(ch, Channels);
        Select(ch, p);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Now()
    {
        SelectedDay = Days[1];
        ScrollToNowRequest++;
    }

    [RelayCommand]
    private Task WatchAsync() => SelectedProgramChannel is { } ch ? _main.PlayChannelAsync(ch, Channels) : Task.CompletedTask;

    [RelayCommand]
    private void Record()
    {
        if (SelectedProgramChannel is { } ch && SelectedProgram is { } p) _main.ScheduleRecording(ch, p);
    }

    [RelayCommand]
    private void RefreshEpg() => _ = Lib.RefreshEpgAsync(force: true);
}
