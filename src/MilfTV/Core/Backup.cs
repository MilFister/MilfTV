using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MilfTV.Data;

namespace MilfTV.Core;

/// <summary>Sauvegarde et restauration de la configuration dans un fichier .milftv-backup (archive ZIP).
/// Inclus : réglages (profils, avatars, mots de passe des profils et code parental sous forme d'empreintes, sources
/// IPTV, clé TMDB, préférences), historique et reprise de lecture, Ma liste, chaînes favorites, liste des enregistrements.
/// Exclus : fichiers vidéo enregistrés, guide TV, caches (affiches, catalogues, fiches TMDB), journaux.
/// Les mots de passe des sources et la clé TMDB sont chiffrés pour le compte Windows (DPAPI) : sans mot de passe de
/// sauvegarde, ils ne se restaurent que sur ce PC et ce compte ; avec, ils sont rechiffrés (AES-256-GCM) et
/// se restaurent partout.
/// La restauration se fait au démarrage suivant, avant l'ouverture de la base : MilfTV redémarre pour l'appliquer.</summary>
public static class Backup
{
    public const string Extension = ".milftv-backup";
    private const int CurrentFormat = 1;
    private static readonly byte[] SecretsMagic = "MTVS1"u8.ToArray();
    private const int KdfIterations = 200_000;

    /// <summary>Sauvegardes automatiques (avant le passage en bêta, avant une restauration).</summary>
    public static string AutoFolder => AppPaths.Ensure(Path.Combine(AppPaths.Local, "backups"));
    private static string PendingFolder => Path.Combine(AppPaths.Local, "restore-pending");

    /// <summary>Restauration appliquée à ce démarrage (pour l'annoncer une fois la fenêtre ouverte).</summary>
    public static Manifest? RestoredAtStartup { get; private set; }
    public static string? RestoreError { get; private set; }

    public sealed class Manifest
    {
        public int Format { get; set; }
        public string App { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        /// <summary>« windows » : secrets chiffrés pour le compte Windows d'origine ; « password » : rechiffrés avec le
        /// mot de passe de la sauvegarde ; « none » : aucun secret.</summary>
        public string Secrets { get; set; } = "none";
        public string? Note { get; set; }
        public int Profiles { get; set; }
        public int Sources { get; set; }
        public int Avatars { get; set; }
        public int History { get; set; }
        public int MyList { get; set; }
        public int Favorites { get; set; }
        public int Recordings { get; set; }
    }

    private sealed class SecretBundle
    {
        public Dictionary<string, string> Sources { get; set; } = new();
        public string? Tmdb { get; set; }
    }

    // ───────────── Création ─────────────

    /// <summary>Crée une sauvegarde. À appeler sur le fil de l'interface (les réglages y sont lus), l'écriture se
    /// fait en arrière-plan. password : facultatif, rend les secrets transférables vers un autre PC.</summary>
    public static async Task<Manifest> CreateAsync(string path, string? password, string? note = null)
    {
        var settings = SettingsStore.Current;
        var json = JsonSerializer.Serialize(settings, Json.Indented);
        var hasSecrets = settings.Sources.Any(s => !string.IsNullOrEmpty(s.PasswordProtected)) || !string.IsNullOrEmpty(settings.TmdbKeyProtected);
        SecretBundle? bundle = null;
        if (hasSecrets && !string.IsNullOrEmpty(password))
        {
            bundle = new SecretBundle { Tmdb = settings.TmdbKey };
            foreach (var s in settings.Sources)
                if (s.Password is { Length: > 0 } pw) bundle.Sources[s.Id] = pw;
        }
        var manifest = new Manifest
        {
            Format = CurrentFormat,
            App = AppVersion.Display,
            Created = DateTimeOffset.Now,
            Secrets = !hasSecrets ? "none" : bundle is not null ? "password" : "windows",
            Note = note,
            Profiles = settings.Profiles.Count,
            Sources = settings.Sources.Count,
        };

        await Task.Run(() =>
        {
            var tmp = path + ".tmp";
            var tmpDb = Path.Combine(Path.GetTempPath(), "milftv-backup-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    // Réglages : avec un mot de passe de sauvegarde, les secrets voyagent à part, chiffrés.
                    if (bundle is not null)
                    {
                        var node = JsonNode.Parse(json)!.AsObject();
                        node["TmdbKeyProtected"] = null;
                        // Partout où un mot de passe chiffré apparaît (liste des sources ou ailleurs).
                        foreach (var obj in ObjectsWithPassword(node)) obj["PasswordProtected"] = null;
                        json = node.ToJsonString(Json.Indented);
                        WriteEntry(zip, "secrets.bin", EncryptSecrets(bundle, password!));
                    }
                    WriteEntry(zip, "settings.json", Encoding.UTF8.GetBytes(json));

                    foreach (var file in Directory.GetFiles(AppPaths.Avatars))
                    {
                        zip.CreateEntryFromFile(file, "avatars/" + Path.GetFileName(file), CompressionLevel.Optimal);
                        manifest.Avatars++;
                    }

                    var counts = UserDataStore.ExportUserData(tmpDb);
                    manifest.History = counts.GetValueOrDefault("watch");
                    manifest.MyList = counts.GetValueOrDefault("watchlist");
                    manifest.Favorites = counts.GetValueOrDefault("favorites");
                    manifest.Recordings = counts.GetValueOrDefault("recordings");
                    zip.CreateEntryFromFile(tmpDb, "userdata.db", CompressionLevel.Optimal);

                    WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json.Indented));
                }
                File.Move(tmp, path, true);
            }
            finally
            {
                TryDelete(tmp);
                TryDelete(tmpDb);
            }
        });
        Log.Info($"Sauvegarde créée : {Path.GetFileName(path)} ({manifest.Profiles} profil(s), {manifest.Sources} source(s), secrets : {manifest.Secrets})");
        return manifest;
    }

    /// <summary>Sauvegarde automatique dans le dossier des sauvegardes (les 10 plus récentes sont gardées).</summary>
    public static async Task<string> CreateAutomaticAsync(string note)
    {
        var name = $"MilfTV-{note}-{DateTime.Now:yyyy-MM-dd-HHmmss}{Extension}";
        var path = Path.Combine(AutoFolder, name);
        await CreateAsync(path, null, note);
        foreach (var old in new DirectoryInfo(AutoFolder).GetFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
            TryDelete(old.FullName);
        return path;
    }

    public static string SuggestedFileName => $"MilfTV-sauvegarde-{DateTime.Now:yyyy-MM-dd}{Extension}";

    // ───────────── Lecture ─────────────

    /// <summary>Lit le descriptif d'une sauvegarde (erreur explicite si le fichier n'en est pas une).</summary>
    public static Manifest ReadManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException(Localization.Loc.T("Backup.NotABackup"));
        using var s = entry.Open();
        var m = JsonSerializer.Deserialize<Manifest>(s, Json.Options) ?? throw new InvalidDataException(Localization.Loc.T("Backup.NotABackup"));
        if (m.Format > CurrentFormat) throw new InvalidDataException(Localization.Loc.T("Backup.TooNew", m.App));
        if (zip.GetEntry("settings.json") is null) throw new InvalidDataException(Localization.Loc.T("Backup.NotABackup"));
        return m;
    }

    /// <summary>Ce que la restauration va mettre en place, à montrer avant de confirmer (une sauvegarde peut venir
    /// d'ailleurs) : serveurs des sources, et dossier des enregistrements s'il s'agit d'un partage réseau.</summary>
    public static (IReadOnlyList<string> Servers, string? NetworkRecordingsFolder) ReadRestoreDetails(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var node = JsonNode.Parse(ReadEntry(zip.GetEntry("settings.json")!))!.AsObject();
            var servers = (node["Sources"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(s => s["Url"]?.GetValue<string>() ?? "")
                .Select(u => Uri.TryCreate(u.Contains("://") ? u : "http://" + u, UriKind.Absolute, out var x) && !x.IsFile ? x.Host : Path.GetFileName(u))
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var folder = node["RecordingsFolder"]?.GetValue<string>();
            var network = folder is not null && (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) ? folder : null;
            return (servers, network);
        }
        catch
        {
            return (Array.Empty<string>(), null);
        }
    }

    /// <summary>Le mot de passe de la sauvegarde est-il le bon ?</summary>
    public static bool CheckPassword(string path, string password)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("secrets.bin") is { } e && DecryptSecrets(ReadEntry(e), password) is not null;
        }
        catch { return false; }
    }

    /// <summary>Sauvegarde « windows » : ses secrets se déchiffrent-ils sur ce PC et ce compte ?</summary>
    public static bool WindowsSecretsReadable(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var node = JsonNode.Parse(ReadEntry(zip.GetEntry("settings.json")!))!.AsObject();
            var values = (node["Sources"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(s => s["PasswordProtected"]?.GetValue<string>())
                .Append(node["TmdbKeyProtected"]?.GetValue<string>())
                .Where(v => !string.IsNullOrEmpty(v)).ToList();
            return values.Count == 0 || values.All(v => Secret.Unprotect(v) is not null);
        }
        catch { return false; }
    }

    // ───────────── Restauration ─────────────

    /// <summary>Prépare la restauration (appliquée au prochain démarrage, avant l'ouverture des données).</summary>
    public static void PrepareRestore(string path, string? password)
    {
        var manifest = ReadManifest(path);
        var dir = PendingFolder;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var node = JsonNode.Parse(ReadEntry(zip.GetEntry("settings.json")!))!.AsObject();
            if (manifest.Secrets == "password")
            {
                var bundle = (zip.GetEntry("secrets.bin") is { } e ? DecryptSecrets(ReadEntry(e), password ?? "") : null)
                             ?? throw new CryptographicException(Localization.Loc.T("Backup.WrongPassword"));
                // Rechiffrés pour ce compte Windows.
                node["TmdbKeyProtected"] = Secret.Protect(bundle.Tmdb);
                foreach (var src in ObjectsWithId(node))
                    if (src["Id"]?.GetValue<string>() is { } id && bundle.Sources.TryGetValue(id, out var pw))
                        src["PasswordProtected"] = Secret.Protect(pw);
            }
            var json = node.ToJsonString(Json.Indented);
            // Contrôle : les réglages doivent se relire.
            _ = JsonSerializer.Deserialize<AppSettings>(json, Json.Options) ?? throw new InvalidDataException(Localization.Loc.T("Backup.NotABackup"));
            File.WriteAllText(Path.Combine(dir, "settings.json"), json);

            var avatars = Directory.CreateDirectory(Path.Combine(dir, "avatars")).FullName;
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("avatars/", StringComparison.Ordinal) && e.Name.Length > 0))
                e.ExtractToFile(Path.Combine(avatars, Path.GetFileName(e.Name)), true);
            zip.GetEntry("userdata.db")?.ExtractToFile(Path.Combine(dir, "userdata.db"), true);

            File.WriteAllBytes(Path.Combine(dir, "restore.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json.Indented));
            Log.Info($"Restauration préparée : sauvegarde du {manifest.Created:yyyy-MM-dd HH:mm} (MilfTV {manifest.App})");
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { /* ignoré */ }
            throw;
        }
    }

    /// <summary>Au démarrage, avant la lecture des réglages : applique une restauration préparée.</summary>
    public static void ApplyPendingRestore()
    {
        var dir = PendingFolder;
        var marker = Path.Combine(dir, "restore.json");
        if (!File.Exists(marker)) return;
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(marker), Json.Options);
            File.Copy(Path.Combine(dir, "settings.json"), AppPaths.Settings, true);

            foreach (var f in Directory.GetFiles(AppPaths.Avatars)) TryDelete(f);
            var avatars = Path.Combine(dir, "avatars");
            if (Directory.Exists(avatars))
                foreach (var f in Directory.GetFiles(avatars)) File.Copy(f, Path.Combine(AppPaths.Avatars, Path.GetFileName(f)), true);

            var db = Path.Combine(dir, "userdata.db");
            if (File.Exists(db)) UserDataStore.ReplaceUserData(db);

            Directory.Delete(dir, true);
            RestoredAtStartup = manifest;
            Log.Info("Sauvegarde restaurée");
        }
        catch (Exception ex)
        {
            RestoreError = ex.Message;
            Log.Error("Restauration impossible", ex);
            // On ne réessaie pas à chaque démarrage : le dossier est mis de côté pour diagnostic.
            try { Directory.Move(dir, dir + "-echec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { /* ignoré */ }
        }
    }

    /// <summary>Relance MilfTV : le nouveau processus attend la fin de celui-ci (instance unique), puis applique
    /// la restauration préparée.</summary>
    public static void StartRestartedInstance()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Tests : le scénario en cours ne doit pas se rejouer dans la nouvelle instance (il relancerait la restauration).
        psi.Environment.Remove("MILFTV_DEV");
        if (Environment.GetEnvironmentVariable("MILFTV_DEV_AFTER_RESTART") is { Length: > 0 } next) psi.Environment["MILFTV_DEV"] = next;
        Process.Start(psi);
    }

    /// <summary>Argument --wait-pid : attend la fin de l'instance précédente (15 s au plus).</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, "--wait-pid");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(15_000)) Log.Warn("L'instance précédente ne s'est pas fermée à temps");
        }
        catch (ArgumentException) { /* déjà terminée */ }
    }

    // ───────────── Chiffrement des secrets (mot de passe de sauvegarde) ─────────────

    private static byte[] EncryptSecrets(SecretBundle bundle, string password)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(bundle);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. SecretsMagic, .. salt, .. nonce, .. tag, .. cipher];
    }

    private static SecretBundle? DecryptSecrets(byte[] data, string password)
    {
        var head = SecretsMagic.Length;
        if (data.Length < head + 44 || !data.AsSpan(0, head).SequenceEqual(SecretsMagic)) return null;
        var salt = data.AsSpan(head, 16).ToArray();
        var nonce = data.AsSpan(head + 16, 12);
        var tag = data.AsSpan(head + 28, 16);
        var cipher = data.AsSpan(head + 44);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException) { return null; }
        return JsonSerializer.Deserialize<SecretBundle>(plain);
    }

    // ───────────── Utilitaires ─────────────

    /// <summary>Tous les objets JSON de l'arbre (parcours en profondeur).</summary>
    private static IEnumerable<JsonObject> AllObjects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var (_, child) in o.ToList())
                    foreach (var x in AllObjects(child)) yield return x;
                break;
            case JsonArray a:
                foreach (var child in a.ToList())
                    foreach (var x in AllObjects(child)) yield return x;
                break;
        }
    }

    private static List<JsonObject> ObjectsWithPassword(JsonNode root) =>
        AllObjects(root).Where(o => o.ContainsKey("PasswordProtected")).ToList();

    private static List<JsonObject> ObjectsWithId(JsonNode root) =>
        AllObjects(root).Where(o => o.ContainsKey("Id") && o.ContainsKey("PasswordProtected")).ToList();

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        s.Write(data);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignoré */ }
    }
}
