using System.Collections.ObjectModel;
using MilfTV.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;

namespace MilfTV.ViewModels;

public sealed partial class ProgramRow : ObservableObject
{
    public required EpgProgram Program { get; init; }
    public bool IsNow { get; init; }
    public bool IsPast { get; init; }
    public string Time => Program.Start.ToLocalTime().ToString("HH:mm");
    public string Title => Program.Title;
    public string? Description => Program.Description;
    public string Duration => Program.DurationText;
    public double Progress => Program.ProgressAt(DateTimeOffset.Now);
    public bool CanRecord => Program.End > DateTimeOffset.Now;
}

/// <summary>TV en direct : catégories, liste des chaînes avec programme en cours, fiche de la chaîne.</summary>
public sealed partial class LiveViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly LiveChannel? _initial;
    private CancellationTokenSource? _detailsCts;

    public static readonly Category AllCategory = new() { Id = "__all", Name = "Toutes les chaînes", Kind = ContentKind.Live, IsVirtual = true };
    public static readonly Category FavCategory = new() { Id = "__fav", Name = "Favoris", Kind = ContentKind.Live, IsVirtual = true };

    public LiveViewModel(MainViewModel main, LiveChannel? initial = null)
    {
        _main = main;
        _initial = initial;
        AllCategory.Name = Loc.T("Live.AllChannels");
        FavCategory.Name = Loc.T("Common.Favorites");
        Lib.CatalogChanged += Reload;
        Lib.ParentalChanged += Reload;
        Lib.FavoritesChanged += OnFavoritesChanged;
        Lib.EpgChanged += OnEpgChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (_, _) => UpdateNow());
        Reload();
    }

    public override bool KeepView => _initial is null;
    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;

    public ObservableCollection<Category> Categories { get; } = new();
    [ObservableProperty] private Category? _selectedCategory;
    [ObservableProperty] private IReadOnlyList<LiveChannel> _channels = Array.Empty<LiveChannel>();
    [ObservableProperty] private LiveChannel? _selectedChannel;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _categoryFilter = "";
    [ObservableProperty] private ProgramRow? _nowProgram;
    [ObservableProperty] private bool _detailsLoading;
    public ObservableCollection<ProgramRow> Upcoming { get; } = new();

    public bool HasNoEpg => SelectedChannel is not null && !DetailsLoading && NowProgram is null && Upcoming.Count == 0;
    public string ChannelCountText => Loc.T(Channels.Count > 1 ? "Live.ChannelCount.Many" : "Live.ChannelCount.One", Channels.Count);

    public override void OnNavigatedTo()
    {
        _timer.Start();
        UpdateNow();
    }

    public override void OnNavigatedFrom() => _timer.Stop();

    private List<Category> _allCategories = new();

    private void Reload()
    {
        var c = Lib.Catalog;
        var previous = SelectedCategory?.Id;
        _allCategories = new List<Category> { AllCategory, FavCategory };
        if (c is not null) _allCategories.AddRange(c.LiveCategories.Where(x => x.Count > 0));
        AllCategory.Count = c?.Channels.Count(x => !Lib.IsHidden(x)) ?? 0;
        FavCategory.Count = c?.Channels.Count(x => x.IsFavorite && !Lib.IsHidden(x)) ?? 0;
        ApplyCategoryFilter();

        if (_initial is not null && c is not null)
        {
            SelectedCategory = Categories.FirstOrDefault(x => x.Id == _initial.CategoryId) ?? AllCategory;
            SelectedChannel = Channels.FirstOrDefault(x => x.Id == _initial.Id);
            return;
        }
        SelectedCategory = Categories.FirstOrDefault(x => x.Id == previous && !Lib.IsHidden(x))
                           ?? (FavCategory.Count > 0 ? FavCategory : Categories.FirstOrDefault(x => !x.IsVirtual && !Lib.IsHidden(x)) ?? AllCategory);
    }

    private void OnEpgChanged()
    {
        UpdateNow();
        _ = LoadDetailsAsync();
    }

    public override void Detach()
    {
        Lib.CatalogChanged -= Reload;
        Lib.ParentalChanged -= Reload;
        Lib.FavoritesChanged -= OnFavoritesChanged;
        Lib.EpgChanged -= OnEpgChanged;
    }

    partial void OnCategoryFilterChanged(string value) => ApplyCategoryFilter();

    /// <summary>Reconstruction de la liste des catégories : la sélection vidée au passage par la liste n'est pas un choix.</summary>
    private bool _rebuildingCategories;

    private void ApplyCategoryFilter()
    {
        var tokens = TextNorm.Tokens(CategoryFilter);
        var keep = SelectedCategory;
        _rebuildingCategories = true;
        try
        {
            Categories.Clear();
            foreach (var cat in _allCategories)
            {
                if (tokens.Length > 0 && !cat.IsVirtual && TextNorm.Score(TextNorm.Normalize(cat.Name), tokens, TextNorm.Normalize(CategoryFilter)) == 0) continue;
                Categories.Add(cat);
            }
            if (keep is not null && Categories.Contains(keep)) SelectedCategory = keep;
        }
        finally { _rebuildingCategories = false; }
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        if (_rebuildingCategories) return;
        if (value is not null && Lib.IsHidden(value))
        {
            _ = UnlockAndSelectAsync(value);
            return;
        }
        ApplyFilter();
    }

    private async Task UnlockAndSelectAsync(Category cat)
    {
        if (await _main.RequestPinAsync()) ApplyFilter();
        else SelectedCategory = AllCategory;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var c = Lib.Catalog;
        var cat = SelectedCategory;
        if (c is null || cat is null)
        {
            Channels = Array.Empty<LiveChannel>();
            OnPropertyChanged(nameof(ChannelCountText));
            return;
        }
        IEnumerable<LiveChannel> q = cat.Id switch
        {
            "__all" => c.Channels,
            "__fav" => c.Channels.Where(x => x.IsFavorite),
            _ => c.Channels.Where(x => x.CategoryId == cat.Id)
        };
        q = q.Where(x => !Lib.IsHidden(x));
        var tokens = TextNorm.Tokens(Filter);
        if (tokens.Length > 0)
        {
            var norm = TextNorm.Normalize(Filter);
            q = q.Where(x => TextNorm.Score(x.SearchKey, tokens, norm) > 0);
        }
        var list = q.ToList();
        var keep = SelectedChannel;
        Channels = list;
        OnPropertyChanged(nameof(ChannelCountText));
        UpdateNow();
        SelectedChannel = keep is not null && list.Contains(keep) ? keep : list.FirstOrDefault();
    }

    private void UpdateNow()
    {
        if (Channels.Count == 0) return;
        Lib.UpdateNowNext(Channels as IReadOnlyCollection<LiveChannel> ?? Channels.ToList());
        if (NowProgram is not null) OnPropertyChanged(nameof(NowProgram));
    }

    private void OnFavoritesChanged()
    {
        FavCategory.Count = Lib.Catalog?.Channels.Count(x => x.IsFavorite && !Lib.IsHidden(x)) ?? 0;
        if (SelectedCategory == FavCategory) ApplyFilter();
    }

    partial void OnSelectedChannelChanged(LiveChannel? value) => _ = LoadDetailsAsync();

    private async Task LoadDetailsAsync()
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        var ch = SelectedChannel;
        Upcoming.Clear();
        NowProgram = null;
        if (ch is null) { OnPropertyChanged(nameof(HasNoEpg)); return; }
        DetailsLoading = true;
        OnPropertyChanged(nameof(HasNoEpg));
        try
        {
            var now = DateTimeOffset.Now;
            var list = await Lib.GetChannelProgramsAsync(ch, now.AddHours(-1), now.AddHours(30), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var p in list)
            {
                if (p.End <= now) continue;
                var row = new ProgramRow { Program = p, IsNow = p.IsLiveAt(now) };
                if (row.IsNow) NowProgram = row;
                else if (Upcoming.Count < 12) Upcoming.Add(row);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Programmes de la chaîne : " + ex.Message);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                DetailsLoading = false;
                OnPropertyChanged(nameof(HasNoEpg));
            }
        }
    }

    [RelayCommand]
    private Task PlayAsync(LiveChannel? ch)
    {
        ch ??= SelectedChannel;
        return ch is null ? Task.CompletedTask : _main.PlayChannelAsync(ch, Channels);
    }

    [RelayCommand]
    private void ToggleFavorite(LiveChannel? ch)
    {
        ch ??= SelectedChannel;
        if (ch is not null) Lib.ToggleFavorite(ch);
    }

    [RelayCommand]
    private void Record(ProgramRow row)
    {
        if (SelectedChannel is { } ch) _main.ScheduleRecording(ch, row.Program);
    }

    [RelayCommand]
    private async Task ToggleCategoryLockAsync(Category cat)
    {
        if (cat.IsVirtual) return;
        if (!_main.EnsurePinConfigured()) return;
        if (!await _main.RequestPinAsync()) return;
        Lib.SetCategoryLock(cat, !cat.IsLocked);
    }
}
