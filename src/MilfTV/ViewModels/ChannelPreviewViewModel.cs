using CommunityToolkit.Mvvm.ComponentModel;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.ViewModels;

/// <summary>Programme à venir dans la fiche d'une chaîne (heure de début et titre).</summary>
public sealed class ChannelPreviewNext
{
    public required string Time { get; init; }
    public required string Title { get; init; }
}

/// <summary>Fiche d'une chaîne affichée au survol de la liste des chaînes du lecteur : programme en cours (horaire,
/// progression, temps restant, genre, épisode, résumé, image) et programmes suivants. Les programmes sont chargés à
/// l'ouverture de la fiche et le chargement s'arrête si elle se referme avant.</summary>
public sealed partial class ChannelPreviewViewModel : ObservableObject
{
    private const int NextCount = 4;
    private readonly CancellationTokenSource _cts = new();

    public ChannelPreviewViewModel(LiveChannel channel)
    {
        Channel = channel;
        _ = LoadAsync();
    }

    public LiveChannel Channel { get; }
    public string Name => Data.SettingsStore.Current.CleanNames ? TextNorm.StripPrefix(Channel.Name) : Channel.Name;
    public string NumberText => Channel.Number > 0 ? Channel.Number.ToString(Loc.Culture) : "";
    public bool IsPlaying => Channel.IsPlaying;
    public bool IsRecording => Channel.IsRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNow), nameof(HasNoEpg))]
    private EpgProgram? _now;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEpg))]
    private bool _isLoading = true;
    [ObservableProperty] private string? _nowMeta;
    [ObservableProperty] private double _nowProgress;
    [ObservableProperty] private string? _nowImage;

    public List<ChannelPreviewNext> Next { get; } = new();
    public bool HasNow => Now is not null;
    public bool HasNext => Next.Count > 0;
    public bool HasNoEpg => !IsLoading && Now is null && Next.Count == 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Now?.Description);

    public void Cancel() => _cts.Cancel();

    private async Task LoadAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var programs = await Library.Instance.GetChannelProgramsAsync(Channel, now.AddHours(-2), now.AddHours(14), _cts.Token);
            if (_cts.IsCancellationRequested) return;
            EpgProgram? current = null;
            foreach (var p in programs.OrderBy(p => p.Start))
            {
                if (p.End <= now) continue;
                if (current is null && p.IsLiveAt(now)) { current = p; continue; }
                if (Next.Count < NextCount && p.Start >= now)
                    Next.Add(new ChannelPreviewNext { Time = p.Start.ToLocalTime().ToString("HH:mm", Loc.Culture), Title = p.Title });
            }
            if (current is not null)
            {
                var remaining = (int)Math.Max(60, (current.End - now).TotalSeconds);
                var meta = new List<string> { current.TimeRange, Loc.T("ChannelPreview.Remaining", TextNorm.Duration(remaining)) };
                if (!string.IsNullOrWhiteSpace(current.Category)) meta.Add(current.Category!);
                if (!string.IsNullOrWhiteSpace(current.EpisodeNum)) meta.Add(current.EpisodeNum!);
                NowMeta = string.Join("  ·  ", meta);
                NowProgress = current.ProgressAt(now);
                // Image du programme seulement : beaucoup de guides mettent le logo de la chaîne à la place.
                NowImage = current.IconUrl is { Length: > 0 } icon && !string.Equals(icon, Channel.ImageUrl, StringComparison.OrdinalIgnoreCase) ? icon : null;
            }
            Now = current;
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(HasNext));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Fiche de la chaîne : " + ex.Message);
        }
        finally
        {
            if (!_cts.IsCancellationRequested) IsLoading = false;
        }
    }
}
