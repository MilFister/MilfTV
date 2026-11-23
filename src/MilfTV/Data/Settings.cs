using System.Text.Json;
using System.Text.Json.Serialization;
using MilfTV.Core;

namespace MilfTV.Data;

/// <summary>Profil utilisateur : historique, « Ma liste » et tableau de bord propres (les chaînes sont communes).</summary>
public sealed class UserProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C5CFF";
    /// <summary>Avatar : null = initiale, « emoji:🦊 » = émoji de la galerie, « file:xxx.png » = image importée (dossier avatars).</summary>
    public string? Avatar { get; set; }
    /// <summary>Profil enfant : contenus verrouillés et catégories adultes toujours masqués.</summary>
    public bool IsKid { get; set; }

    /// <summary>Mot de passe facultatif (empreinte PBKDF2, jamais le mot de passe en clair).</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Source IPTV active de ce profil.</summary>
    public string? ActiveSourceId { get; set; }

    /// <summary>Préférences propres au profil.</summary>
    public ProfilePrefs Prefs { get; set; } = new();

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();
}

/// <summary>Préférences d'un profil : tableau de bord, historique, lecture automatique, aperçus, apparence.</summary>
public sealed class ProfilePrefs
{
    /// <summary>Rangées possibles du tableau de bord, dans l'ordre par défaut.</summary>
    public static readonly string[] AllRows =
    {
        "hero", "continue", "series", "mylist", "recommended", "because",
        "favchannels", "recentchannels", "recentmovies", "recentseries",
    };

    /// <summary>Filtre des listes Films / Séries : all (tout), unwatched (pas encore vus), watched (déjà vus).</summary>
    public string WatchFilter { get; set; } = "all";

    // Tableau de bord
    public List<string> HomeRows { get; set; } = AllRows.ToList();
    public List<string> HiddenRows { get; set; } = new();

    // Historique
    /// <summary>Enregistrer ce que le profil regarde (films et séries).</summary>
    public bool TrackHistory { get; set; } = true;
    /// <summary>Reprendre automatiquement là où on s'était arrêté.</summary>
    public bool ResumePlayback { get; set; } = true;
    /// <summary>Pourcentage à partir duquel un film ou un épisode est considéré comme vu.</summary>
    public int WatchedThresholdPercent { get; set; } = 93;
    /// <summary>Suggestions basées sur l'historique.</summary>
    public bool SuggestionsFromHistory { get; set; } = true;

    // Lecture automatique
    public bool AutoPlayNext { get; set; } = true;
    public int AutoPlayCountdown { get; set; } = 8;
    /// <summary>Nombre d'épisodes enchaînés avant de demander « Toujours là ? » (0 = illimité).</summary>
    public int AutoPlayMaxEpisodes { get; set; }

    // Aperçus et bandes-annonces
    public bool HoverPreview { get; set; } = true;
    public int HoverDelayMs { get; set; } = 1000;
    public bool PreviewSound { get; set; } = true;
    /// <summary>Volume des aperçus au survol (0 à 100), réglé depuis la fenêtre d'aperçu.</summary>
    public int PreviewVolume { get; set; } = 60;
    /// <summary>Langue des bandes-annonces : auto (langue de l'application) ou code fr, en, es, de, it, pt.</summary>
    public string TrailerLanguage { get; set; } = "auto";

    // Apparence propre au profil (null = réglage général)
    public string? ThemeMode { get; set; }
    public string? ColorTheme { get; set; }
    public string? Language { get; set; }

    public IEnumerable<string> VisibleRows() => HomeRows.Concat(AllRows.Except(HomeRows)).Where(r => !HiddenRows.Contains(r));
}

public sealed class AppSettings
{
    // Profils
    public List<UserProfile> Profiles { get; set; } = new();
    public string? LastProfileId { get; set; }
    public bool AskProfileAtStartup { get; set; } = true;

    public List<SourceConfig> Sources { get; set; } = new();
    public string? ActiveSourceId { get; set; }

    // Langue de l'interface : auto (Windows), fr, en, es, de, it, pt
    public string Language { get; set; } = "auto";

    // Apparence
    public string ThemeMode { get; set; } = "Dark"; // System | Light | Dark
    public string ColorTheme { get; set; } = "minuit";
    public int PosterSize { get; set; } = 1; // 0 petit, 1 moyen, 2 grand
    public bool CleanNames { get; set; } = true;

    // TMDB
    public string? TmdbKeyProtected { get; set; }

    [JsonIgnore]
    public string? TmdbKey
    {
        get => Secret.Unprotect(TmdbKeyProtected);
        set => TmdbKeyProtected = Secret.Protect(value?.Trim());
    }

    // Contrôle parental
    public string? ParentalPinHash { get; set; }
    public bool AutoLockAdult { get; set; } = true;
    public bool ProtectSettings { get; set; }
    /// <summary>Catégories verrouillées : "{sourceId}|{Kind}:{categoryId}".</summary>
    public List<string> LockedCategories { get; set; } = new();
    /// <summary>Catégories adultes déverrouillées explicitement (exception au verrouillage automatique).</summary>
    public List<string> UnlockedCategories { get; set; } = new();

    // Lecture
    public int TimeshiftMinutes { get; set; } = 60;
    public string HwDecode { get; set; } = "auto-copy";
    public bool HighQuality { get; set; } = true;
    /// <summary>Affichage fluide : composition DXGI à la fréquence de l'écran (pris en compte au démarrage).</summary>
    public bool SmoothRendering { get; set; } = true;
    /// <summary>Recherche et téléchargement automatiques des nouvelles versions (copie installée seulement).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Dernière version lancée : sert à annoncer les nouveautés après une mise à jour.</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>Canal des mises à jour : « stable » (versions publiées) ou « beta » (nouveautés en avance).</summary>
    /// <summary>Canal choisi par l'utilisateur ; null = celui de la version installée (une bêta reste en bêta).
    /// Nouveau nom : l'ancien réglage « UpdateChannel », toujours écrit à « stable », est ignoré.</summary>
    public string? ReleaseChannel { get; set; }
    public string AudioLanguages { get; set; } = "fr,fre,fra,fr-FR,en,eng";
    public string SubtitleLanguages { get; set; } = "fr,fre,fra";
    public bool SubtitlesByDefault { get; set; }
    public double Volume { get; set; } = 100;
    public bool AutoPlayNextEpisode { get; set; } = true;
    /// <summary>Retour / Échap depuis le lecteur : réduire la vidéo en mini-lecteur au lieu d'arrêter la lecture.</summary>
    public bool MiniPlayerOnBack { get; set; } = true;
    /// <summary>Taille du mini-lecteur : 0 petit, 1 moyen, 2 grand.</summary>
    public int MiniPlayerSize { get; set; } = 1;
    /// <summary>Coin du mini-lecteur : tl, tr, bl, br.</summary>
    public string MiniPlayerCorner { get; set; } = "br";
    /// <summary>Largeur libre du mini-lecteur (px) ; null = taille prédéfinie MiniPlayerSize.</summary>
    public double? MiniPlayerWidth { get; set; }
    /// <summary>Position libre du mini-lecteur, en proportion de la place disponible (0 = gauche / haut, 1 = droite / bas).
    /// Elle suit ainsi les changements de taille de la fenêtre. null = coin MiniPlayerCorner.</summary>
    public double? MiniPlayerX { get; set; }
    public double? MiniPlayerY { get; set; }

    /// <summary>Largeurs des tailles prédéfinies du mini-lecteur : petit, moyen, grand.</summary>
    public static readonly double[] MiniPresetWidths = { 320, 420, 560 };
    /// <summary>Périphérique de sortie audio mpv ("auto" = périphérique par défaut de Windows).</summary>
    public string AudioDevice { get; set; } = "auto";
    public string? DefaultUserAgent { get; set; }

    // Enregistrements
    public string RecordingsFolder { get; set; } = AppPaths.DefaultRecordings;
    public int RecordingMarginBeforeMin { get; set; } = 2;
    public int RecordingMarginAfterMin { get; set; } = 5;
    /// <summary>Format des fichiers : ts (flux d'origine), mkv ou mp4.</summary>
    public string RecordingFormat { get; set; } = "ts";
    /// <summary>Qualité pour les flux à débits multiples (HLS) : best, medium, low.</summary>
    public string RecordingQuality { get; set; } = "best";
    /// <summary>Modèle de nom : {titre} {chaine} {date} {heure}.</summary>
    public string RecordingNameTemplate { get; set; } = "{titre} - {chaine} {date} {heure}";
    /// <summary>Arrêt automatique de l'enregistrement immédiat : 0 = jamais, -1 = fin du programme, sinon minutes.</summary>
    public int InstantRecordingStopMinutes { get; set; } = -1;
    /// <summary>Espace libre minimal sur le disque (Go) ; l'enregistrement s'arrête en dessous.</summary>
    public int RecordingMinFreeGb { get; set; } = 2;
    /// <summary>Nombre maximal d'enregistrements programmés simultanés.</summary>
    public int MaxSimultaneousRecordings { get; set; } = 2;

    // Données
    public int CatalogRefreshHours { get; set; } = 12;
    public int EpgRefreshHours { get; set; } = 12;

    // Fenêtre
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    [JsonIgnore] public bool HasPin => !string.IsNullOrEmpty(ParentalPinHash);

    /// <summary>Calculée : ne pas l'écrire dans settings.json (elle y recopiait la source et son mot de passe chiffré).</summary>
    [JsonIgnore] public SourceConfig? ActiveSource => Sources.FirstOrDefault(s => s.Id == ActiveSourceId) ?? Sources.FirstOrDefault();
}

/// <summary>Chargement / enregistrement des réglages (JSON dans %APPDATA%\MilfTV).</summary>
public static class SettingsStore
{
    private static readonly object Gate = new();
    private static Timer? _saveTimer;

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Vrai si aucun fichier de réglages n'existait au démarrage (première utilisation).</summary>
    public static bool IsFirstRun => !File.Exists(AppPaths.Settings);

    public static bool IsLoaded { get; private set; }

    public static void Load()
    {
        IsLoaded = true;
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                var json = File.ReadAllText(AppPaths.Settings);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Json.Options) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Réglages illisibles, valeurs par défaut utilisées", ex);
            try { File.Copy(AppPaths.Settings, AppPaths.Settings + ".bak", true); } catch { /* ignoré */ }
            Current = new AppSettings();
        }
    }

    private static long _serial;
    private static long _written;

    /// <summary>Enregistrement différé (regroupe les modifications rapprochées).</summary>
    public static void Save()
    {
        lock (Gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => SaveDeferred(), null, 400, Timeout.Infinite);
        }
    }

    /// <summary>Les réglages sont modifiés sur le fil de l'interface : on les sérialise là-bas, jamais pendant
    /// une modification (sinon « collection modifiée »), puis on écrit le fichier en arrière-plan.</summary>
    private static void SaveDeferred()
    {
        if (Avalonia.Application.Current is null)
        {
            SaveNow();
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!TrySerialize(out var json, out var serial)) return;
            _ = Task.Run(() => Write(json, serial));
        });
    }

    private static bool TrySerialize(out string json, out long serial)
    {
        serial = Interlocked.Increment(ref _serial);
        try
        {
            json = JsonSerializer.Serialize(Current, Json.Indented);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement des réglages impossible", ex);
            json = "";
            return false;
        }
    }

    private static void Write(string json, long serial)
    {
        lock (Gate)
        {
            // Une version plus récente a déjà été écrite : on n'écrase pas avec une plus ancienne.
            if (serial < _written) return;
            try
            {
                var tmp = AppPaths.Settings + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, AppPaths.Settings, true);
                _written = serial;
            }
            catch (Exception ex)
            {
                Log.Error("Enregistrement des réglages impossible", ex);
            }
        }
    }

    /// <summary>Crée un premier profil si besoin et le désigne pour la migration de l'historique existant.</summary>
    /// <summary>Aucun profil n'est créé automatiquement : la création se fait au premier lancement.</summary>
    public static void EnsureProfiles()
    {
        UserDataStore.MigrationProfileId = Current.Profiles.FirstOrDefault()?.Id;
    }

    /// <summary>Sources visibles par un profil : les siennes, les sources partagées, et les anciennes sources sans propriétaire.</summary>
    public static IEnumerable<SourceConfig> SourcesFor(UserProfile? p) =>
        Current.Sources.Where(s => s.OwnerProfileId is null || s.IsShared || s.OwnerProfileId == p?.Id);

    /// <summary>Seul le profil propriétaire peut modifier ou supprimer une source.</summary>
    public static bool CanEdit(SourceConfig s, UserProfile? p) => s.OwnerProfileId is null || s.OwnerProfileId == p?.Id;

    public static readonly string[] ProfileColors =
    {
        "#7C5CFF", "#2F8CFF", "#14B8A6", "#22C55E", "#F59E0B", "#FF6B4A", "#E5484D", "#EC4899", "#A78BFA", "#64748B",
    };

    /// <summary>Enregistrement immédiat (fermeture de l'application), sur le fil appelant.</summary>
    public static void SaveNow()
    {
        lock (Gate) _saveTimer?.Dispose();
        if (TrySerialize(out var json, out var serial)) Write(json, serial);
    }
}
