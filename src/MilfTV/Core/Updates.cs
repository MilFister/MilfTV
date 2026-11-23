using System.Reflection;
using Avalonia.Threading;
using MilfTV.Data;
using Velopack;
using Velopack.Sources;

namespace MilfTV.Core;

/// <summary>Mises à jour automatiques (Velopack) : recherche et téléchargement en arrière-plan, installation à la
/// fermeture de MilfTV (ou tout de suite avec « Redémarrer pour installer »).
/// Deux canaux : stable (versions publiées) et bêta (pré-versions x.y.z-beta.n, nouveautés en avance). En bêta, une
/// version stable plus récente que la dernière bêta est proposée aussi ; revenir au stable depuis une bêta réinstalle
/// la dernière version stable, même si elle est plus ancienne.
/// Actives seulement pour une copie installée par le programme d'installation, et si un dépôt de versions a été
/// inscrit à la compilation (propriété UpdateRepo du .csproj, ex. https://github.com/compte/MilfTV).</summary>
public static class Updates
{
    public enum UpdateState { Off, Idle, Checking, Downloading, Ready, UpToDate, Failed }

    /// <summary>Canal Velopack des versions stables (canal par défaut sous Windows).</summary>
    public const string StableChannel = "win";
    public const string BetaChannel = "beta";

    /// <summary>Source des versions inscrite à la compilation (vide = mises à jour désactivées).</summary>
    public static string Feed { get; } = ReadFeed();

    private static readonly object Gate = new();
    /// <summary>Canal choisi (stable ou bêta).</summary>
    private static UpdateManager? _primary;
    /// <summary>En bêta : le canal stable, pour une version stable plus récente que la dernière bêta.</summary>
    private static UpdateManager? _stableAlso;
    private static UpdateManager? _readyFrom;
    private static UpdateInfo? _ready;
    private static SemanticVersion? _installed;
    private static int _busy;
    private static int _applied;
    private static int _generation;
    private static Timer? _timer;

    public static UpdateState State { get; private set; } = UpdateState.Off;
    public static int Progress { get; private set; }
    public static string? Error { get; private set; }

    /// <summary>Changement d'état (toujours signalé sur le fil de l'interface).</summary>
    public static event Action? Changed;
    /// <summary>Une nouvelle version vient d'être téléchargée (numéro de version, fil de l'interface).</summary>
    public static event Action<string>? Downloaded;

    /// <summary>« Redémarrer pour installer » : MilfTV se relance une fois la mise à jour installée.</summary>
    public static bool RestartRequested { get; set; }

    /// <summary>Redémarrage pour restaurer une sauvegarde : la mise à jour prête attend la prochaine fermeture.</summary>
    public static bool SkipApplyOnExit { get; set; }

    /// <summary>Un dépôt de versions est configuré (sinon la rubrique Mises à jour est masquée).</summary>
    public static bool HasFeed => Feed.Length > 0;
    /// <summary>Copie installée avec une source : les mises à jour peuvent être recherchées et installées.</summary>
    public static bool IsAvailable => _primary is not null;
    /// <summary>Canal bêta : choisi explicitement, ou par défaut quand la version installée est une bêta.</summary>
    public static bool IsBeta => SettingsStore.Current.ReleaseChannel is { } c
        ? string.Equals(c, "beta", StringComparison.OrdinalIgnoreCase)
        : BetaInstalled;
    /// <summary>La version installée est une bêta (pré-version).</summary>
    public static bool BetaInstalled => _installed?.IsPrerelease ?? CurrentVersion.Contains('-');
    public static string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();
    /// <summary>La mise à jour prête remplace une bêta par une version stable plus ancienne.</summary>
    public static bool ReadyIsDowngrade => _ready?.IsDowngrade == true;
    public static string CurrentVersion => _installed?.ToString() ?? AppVersion.Display;

    private static string ReadFeed()
    {
        // Test uniquement (données isolées) : dossier local de versions.
        if (Environment.GetEnvironmentVariable("MILFTV_DATA") is { Length: > 0 }
            && Environment.GetEnvironmentVariable("MILFTV_UPDATE_FEED") is { Length: > 0 } test)
            return test;
        return typeof(Updates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value?.Trim() ?? "";
    }

    private static UpdateManager? Create(string channel, bool allowDowngrade)
    {
        var options = new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = allowDowngrade };
        var mgr = Feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            // Les bêtas sont publiées comme pré-versions GitHub : seul le canal bêta les lit.
            ? new UpdateManager(new GithubSource(Feed, null, channel == BetaChannel), options)
            : new UpdateManager(Feed, options);
        // Exécutable lancé depuis un dossier (version portable, développement) : pas de mise à jour.
        return mgr.IsInstalled ? mgr : null;
    }

    /// <summary>Prépare les gestionnaires selon le canal choisi.</summary>
    private static void Configure()
    {
        lock (Gate)
        {
            _primary = _stableAlso = null;
            if (Feed.Length == 0) return;
            try
            {
                var stable = Create(StableChannel, false);
                if (stable is null) return;
                _installed = stable.CurrentVersion;
                if (IsBeta)
                {
                    _primary = Create(BetaChannel, false);
                    _stableAlso = stable;
                }
                else
                {
                    // Retour au stable depuis une bêta : la dernière stable peut être plus ancienne, on l'autorise.
                    _primary = BetaInstalled ? Create(StableChannel, true) : stable;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Mises à jour indisponibles : " + ex.Message);
                _primary = _stableAlso = null;
            }
        }
    }

    /// <summary>Première recherche peu après le lancement, puis toutes les 12 heures (MilfTV peut rester ouvert
    /// longtemps pour enregistrer).</summary>
    public static void Start()
    {
        Configure();
        if (_primary is null)
        {
            if (HasFeed) Log.Info("Mises à jour : copie non installée (portable ou développement), recherche désactivée");
            return;
        }
        State = UpdateState.Idle;
        Log.Info($"Mises à jour : version installée {CurrentVersion}, canal {(IsBeta ? "bêta" : "stable")}");
        _timer = new Timer(_ =>
        {
            if (SettingsStore.Current.AutoUpdate) _ = CheckAsync();
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(12));
    }

    /// <summary>Changement de canal : une mise à jour téléchargée pour l'autre canal est abandonnée, puis on
    /// cherche tout de suite dans le nouveau.</summary>
    public static void SetChannel(bool beta)
    {
        SettingsStore.Current.ReleaseChannel = beta ? "beta" : "stable";
        SettingsStore.Save();
        Interlocked.Increment(ref _generation);
        lock (Gate)
        {
            _ready = null;
            _readyFrom = null;
        }
        Configure();
        Log.Info($"Mises à jour : canal {(beta ? "bêta" : "stable")}" + (BetaInstalled && !beta ? " (retour à la version stable)" : ""));
        Set(_primary is null ? UpdateState.Off : UpdateState.Idle);
        if (_primary is not null) _ = CheckAsync();
    }

    /// <summary>Recherche une nouvelle version et la télécharge (sans l'installer).</summary>
    public static async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var generation = Volatile.Read(ref _generation);
        try
        {
            UpdateManager? primary, stableAlso;
            lock (Gate) { primary = _primary; stableAlso = _stableAlso; }
            if (primary is null) return;
            Error = null;
            Set(UpdateState.Checking);
            var info = await primary.CheckForUpdatesAsync().ConfigureAwait(false);
            var from = primary;
            if (stableAlso is not null)
            {
                // Vérification secondaire : tant qu'aucune version stable n'est publiée, son absence n'est pas une erreur.
                UpdateInfo? stable = null;
                try { stable = await stableAlso.CheckForUpdatesAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Info("Canal stable non consulté : " + ex.GetBaseException().Message); }
                if (stable is not null && (info is null || stable.TargetFullRelease.Version > info.TargetFullRelease.Version))
                {
                    info = stable;
                    from = stableAlso;
                }
            }
            if (generation != Volatile.Read(ref _generation)) return; // canal changé pendant la recherche
            if (info is null)
            {
                Set(_ready is null ? UpdateState.UpToDate : UpdateState.Ready);
                return;
            }
            if (_ready?.TargetFullRelease.Version == info.TargetFullRelease.Version)
            {
                Set(UpdateState.Ready);
                return;
            }
            Progress = 0;
            Set(UpdateState.Downloading);
            await from.DownloadUpdatesAsync(info, p =>
            {
                if (p == Progress) return;
                Progress = p;
                Raise();
            }).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation)) return;
            lock (Gate)
            {
                _ready = info;
                _readyFrom = from;
            }
            var version = info.TargetFullRelease.Version.ToString();
            Log.Info($"Mise à jour {version}{(info.IsDowngrade ? " (retour à la version stable)" : "")} téléchargée : installée à la fermeture de MilfTV");
            Set(UpdateState.Ready);
            Dispatcher.UIThread.Post(() => Downloaded?.Invoke(version));
        }
        catch (Exception ex)
        {
            Error = ex.GetBaseException().Message;
            Log.Warn("Recherche de mise à jour impossible : " + Error);
            Set(_ready is null ? UpdateState.Failed : UpdateState.Ready);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>À la fermeture : confie la mise à jour téléchargée au programme de mise à jour, qui attend la fin
    /// de MilfTV pour l'installer (enregistrements terminés, réglages enregistrés). Seule une version téléchargée
    /// pendant cette session, pour le canal choisi, est installée.</summary>
    public static void ApplyOnExit()
    {
        if (SkipApplyOnExit || Interlocked.Exchange(ref _applied, 1) == 1) return;
        _timer?.Dispose();
        UpdateManager? from;
        UpdateInfo? ready;
        lock (Gate) { from = _readyFrom; ready = _ready; }
        if (from is null || ready is null) return;
        try
        {
            var asset = ready.TargetFullRelease;
            Log.Info($"Installation de la mise à jour {asset.Version} à la fermeture" + (RestartRequested ? ", puis redémarrage" : ""));
            // Sur demande de l'utilisateur, une petite fenêtre montre l'avancement ; sinon tout se fait sans bruit.
            from.WaitExitThenApplyUpdates(asset, silent: !RestartRequested, restart: RestartRequested);
        }
        catch (Exception ex)
        {
            Log.Warn("Mise à jour non installée : " + ex.Message);
        }
    }

    private static void Set(UpdateState state)
    {
        State = state;
        Raise();
    }

    private static void Raise() => Dispatcher.UIThread.Post(() => Changed?.Invoke());
}

/// <summary>Version de l'application telle qu'affichée (avec le suffixe des bêtas, sans l'identifiant de commit).</summary>
public static class AppVersion
{
    public static string Display { get; } = Read();

    private static string Read()
    {
        var asm = typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "?";
    }
}
