using System.Collections.ObjectModel;
using MilfTV.Localization;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Theming;

namespace MilfTV.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Vérifie qu'un code parental existe, sinon invite à en créer un.</summary>
    public bool EnsurePinConfigured()
    {
        if (SettingsStore.Current.HasPin) return true;
        Toast(Loc.T("Toast.PinRequired"), Loc.T("Toast.PinRequiredMsg"), true);
        return false;
    }
}

/// <summary>« Ma liste » du profil : à voir, en cours, déjà vu ; plus les chaînes favorites (communes).</summary>
public sealed partial class FavoritesViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    private void OnEpgChanged() => Lib.UpdateNowNext(Channels);

    public override void Detach()
    {
        Lib.CatalogChanged -= Reload;
        Lib.FavoritesChanged -= Reload;
        Lib.ParentalChanged -= Reload;
        Lib.ProfileChanged -= Reload;
        _main.WatchChanged -= Reload;
        Lib.EpgChanged -= OnEpgChanged;
    }

    public FavoritesViewModel(MainViewModel main)
    {
        _main = main;
        Lib.CatalogChanged += Reload;
        Lib.FavoritesChanged += Reload;
        Lib.ParentalChanged += Reload;
        Lib.ProfileChanged += Reload;
        main.WatchChanged += Reload;
        Lib.EpgChanged += OnEpgChanged;
        Reload();
    }

    public MainViewModel Main => _main;
    public Library Lib => Library.Instance;
    public ObservableCollection<CatalogItem> ToWatch { get; } = new();
    public ObservableCollection<CatalogItem> InProgress { get; } = new();
    public ObservableCollection<CatalogItem> Watched { get; } = new();
    public ObservableCollection<LiveChannel> Channels { get; } = new();

    /// <summary>0 = à voir, 1 = en cours, 2 = déjà vu, 3 = chaînes.</summary>
    [ObservableProperty] private int _tab;

    public bool IsEmptyTab => Tab switch
    {
        0 => ToWatch.Count == 0,
        1 => InProgress.Count == 0,
        2 => Watched.Count == 0,
        _ => Channels.Count == 0
    };

    public string EmptyText => Tab switch
    {
        0 => Loc.T("MyList.EmptyToWatch"),
        1 => Loc.T("MyList.EmptyInProgress"),
        2 => Loc.T("MyList.EmptyWatched"),
        _ => Loc.T("MyList.EmptyChannels")
    };

    public string ProfileName => Lib.Profile?.Name ?? "";

    partial void OnTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsEmptyTab));
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand] private void SetTab(string tab) => Tab = int.Parse(tab);

    public override void OnNavigatedTo() => Lib.UpdateNowNext(Channels);

    private void Reload()
    {
        ToWatch.Clear(); InProgress.Clear(); Watched.Clear(); Channels.Clear();
        var c = Lib.Catalog;
        var src = Lib.Source;
        if (c is not null && src is not null)
        {
            foreach (var it in Lib.GetWatchlistItems()) ToWatch.Add(it);

            var seriesProgress = Lib.GetSeriesInProgress(500);
            foreach (var m in Lib.GetMoviesInProgress()) InProgress.Add(m);
            foreach (var sp in seriesProgress.Where(x => !x.LastEpisode.Watched || x.WatchedCount == 0)) InProgress.Add(sp.Series);

            foreach (var m in Lib.GetWatchedMovies()) Watched.Add(m);
            foreach (var sp in seriesProgress.Where(x => x.LastEpisode.Watched && x.WatchedCount > 0)) Watched.Add(sp.Series);

            foreach (var key in UserDataStore.GetFavoritesOrdered(src.Id))
            {
                var parts = key.Split(':', 2);
                if (parts.Length == 2 && parts[0] == "Live" && c.ChannelById.TryGetValue(parts[1], out var ch) && !Lib.IsHidden(ch))
                    Channels.Add(ch);
            }
            Lib.UpdateNowNext(Channels);
        }
        OnPropertyChanged(nameof(IsEmptyTab));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ProfileName));
    }

    [RelayCommand] private Task PlayChannelAsync(LiveChannel ch) => _main.PlayChannelAsync(ch, Channels);
    [RelayCommand] private void Open(CatalogItem item) => _main.OpenDetails(item);
    [RelayCommand] private void Remove(CatalogItem item) => Lib.ToggleFavorite(item);

    [RelayCommand]
    private void RemoveFromHistory(CatalogItem item)
    {
        if (item is Movie m) Lib.RemoveWatch(m.Key);
        else if (item is Series s) Lib.ClearSeriesHistory(s);
        _main.OnWatchChanged();
    }

    /// <summary>Menu de l'affiche : film vu, ou tous les épisodes d'une série.</summary>
    [RelayCommand]
    private async Task MarkWatchedAsync(CatalogItem item)
    {
        if (item is Series s) { await _main.MarkSeriesWatchedAsync(s); return; }
        if (item is not Movie m) return;
        var w = Lib.GetWatch(m.Key);
        Lib.SaveWatch(new WatchEntry
        {
            Key = m.Key, Kind = ContentKind.Movie, ItemId = m.Id, Title = TextNorm.StripPrefix(m.Name),
            ImageUrl = m.ImageUrl, Position = w?.Duration ?? 1, Duration = w?.Duration ?? 1, Watched = true,
        });
        _main.OnWatchChanged();
    }
}

public sealed partial class RecordingItem : ObservableObject
{
    public required RecordingEntry Entry { get; init; }
    public string Title => Entry.Title;
    public string Channel => TextNorm.StripPrefix(Entry.ChannelName);
    public string When => Loc.T("Rec.When", TextNorm.DayLabel(Entry.Start), Entry.Start.ToLocalTime(), Entry.End.ToLocalTime());
    public string StatusText => Entry.Status switch
    {
        RecordingStatus.Scheduled => Loc.T("Rec.Scheduled"),
        RecordingStatus.Recording => Loc.T("Rec.InProgress"),
        RecordingStatus.Completed => (Entry.SizeBytes > 0 ? Loc.T("Rec.DoneSize", Entry.SizeBytes / 1048576.0) : Loc.T("Rec.Done")) + (Entry.Error is { } err ? " · " + err : ""),
        RecordingStatus.Failed => Loc.T("Rec.StatusFailed") + (Entry.Error is { } e ? " · " + e : ""),
        RecordingStatus.Cancelled => Loc.T("Rec.Cancelled"),
        _ => ""
    };
    public bool IsActive => Entry.Status is RecordingStatus.Scheduled or RecordingStatus.Recording;
    public bool IsRecording => Entry.Status == RecordingStatus.Recording;
    public bool CanPlay => Entry.FilePath is not null && File.Exists(Entry.FilePath) && Entry.Status != RecordingStatus.Scheduled;
    public bool IsFailed => Entry.Status == RecordingStatus.Failed;
}

public sealed partial class RecordingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public RecordingsViewModel(MainViewModel main)
    {
        _main = main;
        main.RecordingsChanged += Reload;
        Reload();
    }

    public override void Detach() => _main.RecordingsChanged -= Reload;

    public ObservableCollection<RecordingItem> Upcoming { get; } = new();
    public ObservableCollection<RecordingItem> Done { get; } = new();
    public bool HasUpcoming => Upcoming.Count > 0;
    public bool HasDone => Done.Count > 0;
    public bool IsEmpty => !HasUpcoming && !HasDone;
    public string Folder => SettingsStore.Current.RecordingsFolder;

    public override void OnNavigatedTo() => Reload();

    private void Reload()
    {
        Upcoming.Clear();
        Done.Clear();
        foreach (var r in UserDataStore.GetRecordings())
        {
            var item = new RecordingItem { Entry = r };
            if (item.IsActive) Upcoming.Insert(0, item);
            else Done.Add(item);
        }
        OnPropertyChanged(nameof(HasUpcoming));
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Folder));
    }

    [RelayCommand]
    private void Play(RecordingItem r)
    {
        if (r.Entry.FilePath is { } f && File.Exists(f)) _main.Player.PlayFile(f, r.Title);
    }

    [RelayCommand]
    private void Cancel(RecordingItem r)
    {
        _main.Recorder.Cancel(r.Entry);
        Reload();
    }

    [RelayCommand]
    private async Task DeleteAsync(RecordingItem r)
    {
        var hasFile = r.Entry.FilePath is { } f && File.Exists(f);
        if (hasFile && !await _main.ConfirmAsync(Loc.T("Rec.DeleteTitle"), Loc.T("Rec.DeleteMessage", r.Title), Loc.T("Common.Delete")))
            return;
        try { if (hasFile) File.Delete(r.Entry.FilePath!); }
        catch (Exception ex) { _main.Toast(Loc.T("Toast.DeleteFailed"), ex.Message, true); return; }
        UserDataStore.DeleteRecording(r.Entry.Id);
        Reload();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var dir = AppPaths.Ensure(SettingsStore.Current.RecordingsFolder);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { _main.Toast(Loc.T("Common.Folder"), ex.Message, true); }
    }
}

public sealed partial class SearchViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SearchViewModel(MainViewModel main, string query)
    {
        _main = main;
        Query = query;
        _ = RunAsync();
    }

    public override bool KeepView => false;
    public MainViewModel Main => _main;
    public string Query { get; }
    [ObservableProperty] private SearchResults? _results;
    [ObservableProperty] private bool _isBusy = true;
    public string Summary => Results is null ? Loc.T("Search.Searching") : Results.IsEmpty ? Loc.T("Search.NoResult", Query) : Loc.T("Search.Results", Results.Total, Query);

    private async Task RunAsync()
    {
        try
        {
            Results = await Task.Run(() => Library.Instance.Search(Query, 120));
            Library.Instance.UpdateNowNext(Results.Channels);
        }
        catch (Exception ex)
        {
            Log.Error("Recherche", ex);
            Results ??= new SearchResults();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Summary));
        }
    }

    [RelayCommand] private void OpenItem(object? item) => _main.OpenSearchResultCommand.Execute(item);
    [RelayCommand] private void Back() => _main.GoBack();
}

/// <summary>Liste des langues proposées et changement de langue.</summary>
public static class LanguageChoice
{
    public static IReadOnlyList<LanguageInfo> Options { get; } =
        new[] { new LanguageInfo("auto", Loc.T("Lang.Auto")) }.Concat(Loc.Languages).ToList();

    private static string CurrentCode => Library.Instance.Profile?.Prefs.Language ?? SettingsStore.Current.Language ?? "auto";

    public static LanguageInfo Current =>
        Options.FirstOrDefault(o => o.Code == CurrentCode) ?? Options[0];

    public static void Apply(LanguageInfo? choice)
    {
        if (choice is null || choice.Code == CurrentCode) return;
        var s = SettingsStore.Current;
        var oldLang = Loc.Lang;
        s.Language = choice.Code;
        if (Library.Instance.Profile is { } profile) profile.Prefs.Language = choice.Code;
        var newLang = choice.Code == "auto" ? Loc.SystemLanguage() : choice.Code;
        // Langues audio / sous-titres : on suit la nouvelle langue si l'utilisateur n'a rien personnalisé.
        if (s.AudioLanguages == Loc.DefaultAudioLanguages(oldLang)) s.AudioLanguages = Loc.DefaultAudioLanguages(newLang);
        if (s.SubtitleLanguages == Loc.DefaultSubtitleLanguages(oldLang)) s.SubtitleLanguages = Loc.DefaultSubtitleLanguages(newLang);
        SettingsStore.Save();
        // Le libellé « Automatique » dépend de la langue : on le met à jour.
        if (Options[0] is { } auto && Options is List<LanguageInfo> list) list[0] = auto with { NativeName = Loc.T("Lang.Auto") };
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Loc.SetLanguage(choice.Code);
            if (Options is List<LanguageInfo> l) l[0] = l[0] with { NativeName = Loc.T("Lang.Auto") };
        });
    }
}

public sealed partial class ThemeChoice : ObservableObject
{
    public required ColorTheme Theme { get; init; }
    [ObservableProperty] private bool _isSelected;
}

