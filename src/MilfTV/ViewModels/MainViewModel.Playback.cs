using MilfTV.Core;
using MilfTV.Localization;
using MilfTV.Data;

namespace MilfTV.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Reprend un élément de « Reprendre la lecture ».</summary>
    public async Task ResumeAsync(WatchEntry e)
    {
        var c = Lib.Catalog;
        if (c is null) return;
        try
        {
            switch (e.Kind)
            {
                case ContentKind.Movie when c.MovieById.TryGetValue(e.ItemId, out var m):
                    await PlayMovieAsync(m);
                    break;
                case ContentKind.Episode when e.ParentId is not null && c.SeriesById.TryGetValue(e.ParentId, out var s):
                    if (Lib.IsHidden(s) && !await RequestPinAsync()) return;
                    var details = await Lib.Provider!.GetSeriesDetailsAsync(s, CancellationToken.None);
                    var season = details?.Seasons.FirstOrDefault(x => x.Episodes.Any(ep => ep.Id == e.ItemId));
                    var episode = season?.Episodes.First(ep => ep.Id == e.ItemId);
                    if (season is null || episode is null)
                    {
                        Push(new SeriesDetailViewModel(this, s));
                        return;
                    }
                    Player.IsMini = false;
                    await Player.PlayEpisodeAsync(s, episode, season.Episodes);
                    break;
                case ContentKind.Live when c.ChannelById.TryGetValue(e.ItemId, out var ch):
                    await PlayChannelAsync(ch, null);
                    break;
                default:
                    Toast(Loc.T("Toast.ItemNotFound"), Loc.T("Toast.ItemNotFoundMsg"), true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Toast(Loc.T("Toast.PlaybackFailed"), ex.Message, true);
        }
    }

    public async Task PlayEpisodeAsync(Series s, Episode e, IEnumerable<Episode> season, bool fromStart = false)
    {
        if (Lib.IsHidden(s) && !await RequestPinAsync()) return;
        Player.IsMini = false;
        await Player.PlayEpisodeAsync(s, e, season, fromStart);
    }

    /// <summary>Jour inséré dans une phrase : « aujourd'hui », « lundi 23 novembre »… En anglais et en allemand,
    /// les noms de jours et de mois gardent leur majuscule ; seuls « aujourd'hui / demain / hier » passent en minuscule.</summary>
    private static string DayInSentence(string label)
    {
        var relative = label == Loc.T("Date.Today") || label == Loc.T("Date.Tomorrow") || label == Loc.T("Date.Yesterday");
        if (relative || Loc.Lang is not ("en" or "de")) return label.ToLower(Loc.Culture);
        return label;
    }

    /// <summary>Programme un enregistrement depuis le guide.</summary>
    public void ScheduleRecording(LiveChannel ch, EpgProgram p)
    {
        if (p.End <= DateTimeOffset.Now)
        {
            Toast(Loc.T("Toast.ProgramEnded"), Loc.T("Toast.ProgramEndedMsg"), true);
            return;
        }
        var existing = UserDataStore.GetRecordings().FirstOrDefault(r =>
            r.ChannelId == ch.Id && r.Status is RecordingStatus.Scheduled or RecordingStatus.Recording &&
            Math.Abs((r.Start - p.Start).TotalMinutes) < 15);
        if (existing is not null)
        {
            Toast(Loc.T("Toast.AlreadyScheduled"), existing.Title);
            return;
        }
        Recorder.Schedule(ch, p.Title, p.Start, p.End);
        var when = p.Start <= DateTimeOffset.Now ? Loc.T("Rec.StartsNow") : Loc.T("Rec.StartsAt", DayInSentence(p.DayLabel), p.Start.ToLocalTime());
        Toast(Loc.T("Toast.RecScheduled"), Loc.T("Toast.RecScheduledMsg", p.Title, when));
    }
}
