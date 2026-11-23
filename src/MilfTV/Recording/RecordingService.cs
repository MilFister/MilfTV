using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;
using MilfTV.Player;
using MilfTV.Providers;
using MilfTV.ViewModels;

namespace MilfTV.Recording;

/// <summary>Enregistrements programmés depuis le guide TV. Utilise une connexion séparée :
/// copie brute du flux pour le MPEG-TS, moteur mpv sans affichage pour le HLS.
/// <para>Formats : « ts » garde le flux d'origine (copie brute avec reconnexions, le plus robuste).
/// « mkv » et « mp4 » passent toujours par l'enregistreur de mpv (stream-record vers un fichier .mkv/.mp4) :
/// changement de conteneur à la volée par libavformat, sans réencodage. Choix retenu plutôt qu'une conversion
/// après coup : le mode encodage de libmpv (o=, of=) réencode toujours (pas de copie de flux), ffmpeg n'est pas
/// fourni, et une conversion finale demanderait une seconde lecture complète et le double d'espace disque.
/// Si le conteneur choisi refuse les pistes de la chaîne (fichier toujours vide après 15 s), l'enregistrement
/// repasse en .ts.</para></summary>
public sealed partial class RecordingService
{
    /// <summary>Délai après lequel un enregistrement mkv/mp4 encore vide repasse en .ts.</summary>
    public static readonly TimeSpan RemuxProbeDelay = TimeSpan.FromSeconds(15);

    /// <summary>Débit de repli (bit/s) pour la qualité « medium » si la playlist HLS est illisible.</summary>
    private const string MediumFallbackBitrate = "3000000";

    /// <summary>Enregistrement programmé en cours.</summary>
    private sealed class ActiveRecording
    {
        public required CancellationTokenSource Cts { get; init; }
        public required RecordingEntry Entry { get; init; }
        /// <summary>Raison d'un arrêt anticipé (espace disque insuffisant) : devient le message d'erreur.</summary>
        public volatile string? StopReason;
    }

    private readonly MainViewModel _main;
    private readonly ConcurrentDictionary<long, ActiveRecording> _active = new();
    private DispatcherTimer? _timer;

    public RecordingService(MainViewModel main) => _main = main;

    public void Start()
    {
        // Les enregistrements interrompus (application fermée) sont marqués comme terminés.
        foreach (var r in UserDataStore.GetRecordings().Where(r => r.Status == RecordingStatus.Recording))
        {
            r.Status = Length(r.FilePath) > 0 ? RecordingStatus.Completed : RecordingStatus.Failed;
            if (r.Status == RecordingStatus.Failed) TryDelete(r.FilePath);
            r.Error ??= Loc.T("Rec.Interrupted");
            UserDataStore.UpdateRecording(r);
        }
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => Check());
        _timer.Start();
        Check();
    }

    public bool HasPending => UserDataStore.GetRecordings().Any(r => r.Status is RecordingStatus.Scheduled or RecordingStatus.Recording);

    /// <summary>Enregistrement en cours, ou programmé dans les 3 prochaines heures : fermer l'application l'empêcherait.</summary>
    public bool ClosingWouldInterrupt =>
        ActiveCount > 0
        || UserDataStore.GetRecordings().Any(r => r.Status == RecordingStatus.Scheduled && r.Start <= DateTimeOffset.Now.AddHours(3));

    // ───────────── Limite d'enregistrements simultanés ─────────────

    /// <summary>Enregistrements en cours : programmés + enregistrement immédiat du lecteur.</summary>
    public int ActiveCount => _active.Count + (_main.Player?.HasInstantRecording == true ? 1 : 0);

    /// <summary>Nombre maximal réglé (0 ou moins = sans limite).</summary>
    public static int MaxSimultaneous => SettingsStore.Current.MaxSimultaneousRecordings is var m && m > 0 ? m : int.MaxValue;

    /// <summary>Vrai si un nouvel enregistrement (programmé ou immédiat) dépasserait la limite.</summary>
    public bool LimitReached => ActiveCount >= MaxSimultaneous;

    public static string LimitMessage => Loc.T("Rec.TooMany", SettingsStore.Current.MaxSimultaneousRecordings);

    public RecordingEntry Schedule(LiveChannel ch, string title, DateTimeOffset start, DateTimeOffset end)
    {
        var s = SettingsStore.Current;
        var entry = new RecordingEntry
        {
            SourceId = Library.Instance.Source?.Id ?? "",
            ChannelId = ch.Id,
            ChannelName = ch.Name,
            Title = title,
            Start = start.AddMinutes(-s.RecordingMarginBeforeMin),
            End = end.AddMinutes(s.RecordingMarginAfterMin),
            Status = RecordingStatus.Scheduled,
        };
        if (entry.Start < DateTimeOffset.Now) entry.Start = DateTimeOffset.Now;
        UserDataStore.AddRecording(entry);
        _main.OnRecordingsChanged();
        Check();
        return entry;
    }

    public void Cancel(RecordingEntry e)
    {
        if (_active.TryRemove(e.Id, out var a)) a.Cts.Cancel();
        if (e.Status == RecordingStatus.Scheduled)
        {
            e.Status = RecordingStatus.Cancelled;
            UserDataStore.UpdateRecording(e);
        }
        _main.OnRecordingsChanged();
    }

    /// <summary>Tâches des enregistrements programmés en cours (attendues à la fermeture).</summary>
    private readonly ConcurrentDictionary<long, Task> _running = new();
    /// <summary>Enregistrements en attente d'un emplacement libre, déjà signalés.</summary>
    private readonly HashSet<long> _waitNotified = new();

    private void Check()
    {
        var now = DateTimeOffset.Now;
        // Heure de fin vérifiée sur l'horloge réelle : après une mise en veille, les minuteries ont pris du retard.
        foreach (var a in _active.Values)
            if (a.Entry.End <= now && !a.Cts.IsCancellationRequested) a.Cts.Cancel();
        KeepAwake(ActiveCount > 0);
        foreach (var r in UserDataStore.GetRecordings())
        {
            if (r.Status != RecordingStatus.Scheduled) continue;
            if (r.End <= now)
            {
                r.Status = RecordingStatus.Failed;
                r.Error = Loc.T("Rec.EndTimePassed");
                UserDataStore.UpdateRecording(r);
                continue;
            }
            if (r.Start > now || _active.ContainsKey(r.Id) || _running.ContainsKey(r.Id)) continue;

            // Limite atteinte (souvent deux programmes qui se suivent, marges comprises) : l'enregistrement
            // attend qu'un emplacement se libère, au lieu d'échouer ; il échoue seulement si son heure de fin passe.
            if (LimitReached)
            {
                if (_waitNotified.Add(r.Id))
                {
                    Log.Info($"Enregistrement en attente d'un emplacement libre : {r.Title}");
                    _main.Toast(Loc.T("Rec.Waiting"), Loc.T("Rec.WaitingBody", r.Title, SettingsStore.Current.MaxSimultaneousRecordings));
                }
                continue;
            }
            if (DiskSpace.IsLow(SettingsStore.Current.RecordingsFolder, out var freeGb))
            {
                Reject(r, DiskSpace.LowMessage(freeGb));
                continue;
            }
            _waitNotified.Remove(r.Id);
            // Tout l'enregistrement (réseau, écriture, moteur mpv) tourne hors du fil de l'interface.
            var task = Task.Run(() => RunAsync(r));
            _running[r.Id] = task;
            _ = task.ContinueWith(_ => _running.TryRemove(r.Id, out Task? _), TaskScheduler.Default);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    private bool _keepingAwake;

    /// <summary>Empêche la mise en veille automatique du PC pendant un enregistrement (l'écran peut s'éteindre).</summary>
    private void KeepAwake(bool on)
    {
        if (on == _keepingAwake) return;
        _keepingAwake = on;
        const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
        try { SetThreadExecutionState(on ? Continuous | SystemRequired : Continuous); }
        catch { /* facultatif */ }
    }

    /// <summary>Enregistrement qui ne peut pas démarrer : marqué en échec, avec une notification.</summary>
    private void Reject(RecordingEntry r, string error)
    {
        Log.Warn($"Enregistrement refusé : {r.Title} ({error})");
        r.Status = RecordingStatus.Failed;
        r.Error = error;
        UserDataStore.UpdateRecording(r);
        _main.OnRecordingsChanged();
        _main.Toast(Loc.T("Rec.Failed"), Loc.T("Rec.FailedBody", r.Title, error), true);
    }

    private async Task RunAsync(RecordingEntry r)
    {
        var cts = new CancellationTokenSource();
        var active = new ActiveRecording { Cts = cts, Entry = r };
        if (!_active.TryAdd(r.Id, active)) return;
        var remaining = r.End - DateTimeOffset.Now;
        cts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));

        var s = SettingsStore.Current;
        var format = RecordingNames.NormalizeFormat(s.RecordingFormat);
        string file = "";

        try
        {
            var folder = AppPaths.Ensure(s.RecordingsFolder);
            file = RecordingNames.BuildPath(folder, s.RecordingNameTemplate, r.Title, r.ChannelName, r.Start, format);
            r.FilePath = file;
            r.Status = RecordingStatus.Recording;
            UserDataStore.UpdateRecording(r);
            Dispatcher.UIThread.Post(() =>
            {
                _main.OnRecordingsChanged();
                _main.Toast(Loc.T("Rec.Started"), $"{r.Title} ({r.ChannelName})");
            });
            Log.Info($"Enregistrement démarré : {r.Title} → {file}");
            _ = WatchDiskAsync(r, active, folder);

            var info = await ResolveAsync(r, cts.Token);
            var hls = LooksHls(info.Url);
            var bitrate = hls ? await HlsBitrateAsync(info, s.RecordingQuality, cts.Token) : null;

            if (format != "ts")
            {
                // mkv / mp4 : toujours par l'enregistreur de mpv, même pour un flux MPEG-TS.
                var wrote = await RecordWithMpvAsync(info, file, bitrate, probeEmpty: true, cts.Token);
                if (!wrote && !cts.IsCancellationRequested)
                {
                    Log.Warn($"Enregistrement {r.Title} : aucune donnée en {format} après {RemuxProbeDelay.TotalSeconds:0} s, repli sur le format TS");
                    TryDelete(file);
                    var failedFormat = format.ToUpperInvariant();
                    format = "ts";
                    file = RecordingNames.UniquePath(Path.ChangeExtension(file, ".ts"));
                    r.FilePath = file;
                    UserDataStore.UpdateRecording(r);
                    Dispatcher.UIThread.Post(() =>
                        _main.Toast(Loc.T("Rec.FormatFallbackTitle"), Loc.T("Rec.FormatFallback", r.Title, failedFormat)));
                }
            }
            if (format == "ts")
            {
                if (hls) await RecordWithMpvAsync(info, file, bitrate, probeEmpty: false, cts.Token);
                else
                {
                    try { await RecordHttpAsync(info, file, r, cts.Token); }
                    catch (HlsDetectedException)
                    {
                        // Adresse sans « .m3u8 » mais réponse HLS (playlist texte) : enregistrement par mpv.
                        Log.Info($"Enregistrement {r.Title} : flux HLS détecté, enregistrement par mpv");
                        TryDelete(file);
                        await RecordWithMpvAsync(info, file, await HlsBitrateAsync(info, s.RecordingQuality, cts.Token), probeEmpty: false, cts.Token);
                    }
                }
            }

            r.Status = Length(file) > 0 ? RecordingStatus.Completed : RecordingStatus.Failed;
            if (active.StopReason is { } reason) r.Error = reason;
            if (r.Status == RecordingStatus.Failed) r.Error ??= Loc.T("Rec.NoData");
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement", ex);
            r.Status = Length(file) > 0 ? RecordingStatus.Completed : RecordingStatus.Failed;
            r.Error = active.StopReason ?? ex.Message;
        }
        finally
        {
            _active.TryRemove(r.Id, out _);
            // Arrête aussi la surveillance de l'espace disque.
            try { cts.Cancel(); } catch { /* déjà libéré */ }
            r.SizeBytes = Length(file);
            if (r.Status == RecordingStatus.Failed) TryDelete(file);
            if (r.Status == RecordingStatus.Completed && DateTimeOffset.Now < r.End.AddSeconds(-30) && r.Error is null)
                r.Error = Loc.T("Rec.StoppedEarly");
            UserDataStore.UpdateRecording(r);
            var lowDisk = active.StopReason is not null;
            Dispatcher.UIThread.Post(() =>
            {
                _main.OnRecordingsChanged();
                if (lowDisk)
                    _main.Toast(Loc.T("Rec.LowDiskStopped"), Loc.T("Rec.FailedBody", r.Title, r.Error), true);
                else
                    _main.Toast(r.Status == RecordingStatus.Completed ? Loc.T("Rec.Completed") : Loc.T("Rec.Failed"),
                        r.Status == RecordingStatus.Completed ? r.Title : Loc.T("Rec.FailedBody", r.Title, r.Error), r.Status != RecordingStatus.Completed);
            });
        }
    }

    /// <summary>Contrôle l'espace libre toutes les 30 s ; en dessous du seuil, arrête proprement l'enregistrement.</summary>
    private static async Task WatchDiskAsync(RecordingEntry r, ActiveRecording active, string folder)
    {
        var ct = active.Cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(DiskSpace.CheckInterval, ct);
                // Hors du fil de l'interface : un lecteur réseau peut répondre lentement.
                var low = await Task.Run(() => DiskSpace.IsLow(folder, out var free) ? free : (double?)null, ct);
                if (low is { } gb)
                {
                    active.StopReason = DiskSpace.LowMessage(gb);
                    Log.Warn($"Enregistrement {r.Title} arrêté : espace disque insuffisant ({gb:0.0} Go libres)");
                    active.Cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<PlaybackInfo> ResolveAsync(RecordingEntry r, CancellationToken ct)
    {
        var lib = Library.Instance;
        if (lib.Source?.Id == r.SourceId && lib.Catalog?.ChannelById.GetValueOrDefault(r.ChannelId) is { } ch)
            return await lib.ResolveAsync(ch, null, ct);

        var src = SettingsStore.Current.Sources.FirstOrDefault(s => s.Id == r.SourceId)
                  ?? throw new InvalidOperationException(Loc.T("Rec.SourceNotFound"));
        var catalog = await CatalogCache.LoadAsync(src.Id) ?? throw new InvalidOperationException(Loc.T("Rec.CatalogUnavailable"));
        var channel = catalog.Channels.FirstOrDefault(c => c.Id == r.ChannelId) ?? throw new InvalidOperationException(Loc.T("Rec.ChannelNotFound"));
        using var provider = ProviderFactory.Create(src);
        return await provider.ResolveAsync(channel, null, ct);
    }

    // ───────────── Qualité HLS ─────────────

    [GeneratedRegex(@"[:,]\s*BANDWIDTH=(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BandwidthRegex();

    /// <summary>Valeur de l'option mpv hls-bitrate pour la qualité réglée : max (meilleure), min (plus basse).
    /// Pour « medium », débit exact de la variante intermédiaire lue dans la playlist maîtresse : mpv choisit la
    /// variante de débit le plus élevé inférieur ou égal, en gardant les pistes audio séparées éventuelles.</summary>
    private static async Task<string> HlsBitrateAsync(PlaybackInfo info, string? quality, CancellationToken ct)
    {
        switch (quality?.Trim().ToLowerInvariant())
        {
            case "low": return "min";
            case "medium": break;
            default: return "max";
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = Http.Request(info.Url, info.UserAgent ?? SettingsStore.Current.DefaultUserAgent);
            if (!string.IsNullOrEmpty(info.Referrer)) req.Headers.TryAddWithoutValidation("Referer", info.Referrer);
            using var resp = await Http.Iptv.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(timeout.Token);
            var rates = new SortedSet<long>();
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
                var m = BandwidthRegex().Match(line);
                if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bw) && bw > 0)
                    rates.Add(bw);
            }
            if (rates.Count > 0)
            {
                var list = rates.ToList();
                // 1 ou 2 variantes : la plus basse ; sinon celle du milieu (supérieure si nombre pair).
                var pick = list.Count <= 2 ? list[0] : list[list.Count / 2];
                return pick.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Qualité moyenne : playlist HLS illisible (" + ex.Message + "), débit par défaut");
        }
        return MediumFallbackBitrate;
    }

    // ───────────── Moteurs d'enregistrement ─────────────

    private static async Task RecordHttpAsync(PlaybackInfo info, string file, RecordingEntry r, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16, true);
        var buffer = new byte[1 << 16];
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = Http.Request(info.Url, info.UserAgent ?? SettingsStore.Current.DefaultUserAgent);
                if (!string.IsNullOrEmpty(info.Referrer)) req.Headers.TryAddWithoutValidation("Referer", info.Referrer);
                using var resp = await Http.Iptv.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentType?.MediaType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true)
                    throw new HlsDetectedException();
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                // Aucune donnée pendant 20 s : connexion considérée comme morte, on se reconnecte.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var first = true;
                int n;
                while (true)
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(20));
                    n = await s.ReadAsync(buffer, readCts.Token);
                    if (n <= 0) break;
                    if (first && fs.Length == 0 && n >= 7 && System.Text.Encoding.ASCII.GetString(buffer, 0, 7) == "#EXTM3U")
                        throw new HlsDetectedException();
                    first = false;
                    try { await fs.WriteAsync(buffer.AsMemory(0, n), ct); }
                    catch (IOException ex) { throw new RecordingWriteException(Loc.T("Rec.WriteFailed", ex.Message), ex); }
                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HlsDetectedException) { throw; }
            catch (RecordingWriteException) { throw; }
            catch (Exception ex)
            {
                failures++;
                Log.Warn($"Enregistrement {r.Title} : reconnexion ({ex.Message})");
                if (failures > 30) throw new IOException(Loc.T("Rec.StreamLost", ex.Message), ex);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + failures)), ct); }
            catch (OperationCanceledException) { break; }
        }
        await fs.FlushAsync(CancellationToken.None);
    }

    /// <summary>Enregistrement par une instance mpv sans affichage (stream-record : copie des paquets,
    /// conteneur choisi d'après l'extension du fichier). Renvoie faux si <paramref name="probeEmpty"/> est
    /// demandé et que rien n'a été écrit au bout de <see cref="RemuxProbeDelay"/> (conteneur refusé).</summary>
    private static async Task<bool> RecordWithMpvAsync(PlaybackInfo info, string file, string? hlsBitrate, bool probeEmpty, CancellationToken ct)
    {
        var h = MpvApi.mpv_create();
        if (h == IntPtr.Zero) throw new InvalidOperationException(Loc.T("Rec.MpvUnavailable"));
        try
        {
            void Opt(string n, string v) => MpvApi.mpv_set_option_string(h, MpvApi.Utf8(n), MpvApi.Utf8(v));
            Opt("vo", "null");
            Opt("ao", "null");
            Opt("terminal", "no");
            Opt("config", "no");
            Opt("load-scripts", "no");
            Opt("ytdl", "no");
            Opt("hwdec", "no");
            Opt("vd-lavc-skipframe", "all");
            Opt("network-timeout", "20");
            Opt("stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=5");
            Opt("user-agent", info.UserAgent ?? SettingsStore.Current.DefaultUserAgent ?? Http.DefaultUserAgent);
            if (!string.IsNullOrEmpty(info.Referrer)) Opt("referrer", info.Referrer!);
            // Choix de la variante HLS (sans effet sur un flux MPEG-TS simple).
            if (!string.IsNullOrEmpty(hlsBitrate)) Opt("hls-bitrate", hlsBitrate!);
            Opt("stream-record", file);
            if (MpvApi.mpv_initialize(h) < 0) throw new InvalidOperationException(Loc.T("Rec.MpvInitFailed"));
            var argv = MpvApi.AllocArgs(new[] { "loadfile", info.Url });
            try { MpvApi.mpv_command(h, argv); }
            finally { MpvApi.FreeArgs(argv); }

            return await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var probing = probeEmpty;
                while (!ct.IsCancellationRequested)
                {
                    if (probing && clock.Elapsed >= RemuxProbeDelay)
                    {
                        probing = false;
                        if (Length(file) == 0) return false;
                    }
                    var evp = MpvApi.mpv_wait_event(h, 0.5);
                    if (evp == IntPtr.Zero) continue;
                    var ev = Marshal.PtrToStructure<MpvEvent>(evp);
                    if (ev.EventId == MpvEventId.Shutdown) break;
                    if (ev.EventId == MpvEventId.EndFile)
                    {
                        // Une redirection (playlist développée) n'est pas une fin de flux.
                        var ef = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        if (ef.Reason != (int)MpvEndFileReason.Redirect) break;
                    }
                }
                // Flux terminé avant la vérification sans rien écrire : même repli.
                return !(probeEmpty && !ct.IsCancellationRequested && Length(file) == 0);
            }, CancellationToken.None);
        }
        finally
        {
            MpvApi.mpv_terminate_destroy(h);
        }
    }

    // ───────────── Outils ─────────────

    /// <summary>Taille du fichier (0 s'il n'existe pas ou est illisible).</summary>
    public static long Length(string? file)
    {
        try { return !string.IsNullOrEmpty(file) && File.Exists(file) ? new FileInfo(file).Length : 0; }
        catch { return 0; }
    }

    /// <summary>Supprime un fichier vide laissé par un essai de conteneur (réessaie si mpv le tient encore ouvert).</summary>
    public static void TryDelete(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        try { if (File.Exists(file) && new FileInfo(file).Length == 0) File.Delete(file); }
        catch
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                try { if (File.Exists(file) && new FileInfo(file).Length == 0) File.Delete(file); }
                catch { /* laissé sur le disque */ }
            });
        }
    }

    public void Shutdown()
    {
        _timer?.Stop();
        foreach (var a in _active.Values) a.Cts.Cancel();
        // Laisse le temps d'écrire la fin des fichiers (index MKV, en-tête MP4) avant que le processus s'arrête.
        var tasks = _running.Values.ToArray();
        if (tasks.Length > 0)
        {
            Log.Info($"Fermeture : fin de {tasks.Length} enregistrement(s) en cours");
            try { Task.WaitAll(tasks, TimeSpan.FromSeconds(10)); } catch { /* erreurs déjà journalisées */ }
        }
        KeepAwake(false);
    }

    /// <summary>Adresse HLS : « .m3u8 » dans le chemin ou un paramètre (extension=m3u8, type=m3u8…).</summary>
    private static bool LooksHls(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
        || url.Contains("=m3u8", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/hls/", StringComparison.OrdinalIgnoreCase);

    private sealed class HlsDetectedException : Exception { }

    private sealed class RecordingWriteException(string message, Exception inner) : IOException(message, inner) { }
}
