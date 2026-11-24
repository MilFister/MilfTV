using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MilfTV.Core;

public static class AppPaths
{
    /// <summary>Dossier de données isolé pour les tests (variable MILFTV_DATA) ; vide en utilisation normale.</summary>
    public static string? DevRoot { get; } = Environment.GetEnvironmentVariable("MILFTV_DATA") is { Length: > 0 } d ? d : null;

    public static string Roaming { get; } = Ensure(DevRoot is not null
        ? Path.Combine(DevRoot, "Roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MilfTV"));
    public static string Local { get; } = Ensure(DevRoot is not null
        ? Path.Combine(DevRoot, "Local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MilfTV"));
    public static string ImageCache { get; } = Ensure(Path.Combine(Local, "cache", "images"));
    public static string CatalogCache { get; } = Ensure(Path.Combine(Local, "cache", "catalog"));
    public static string PlayerCache { get; } = Ensure(Path.Combine(Local, "cache", "timeshift"));
    public static string Logs { get; } = Ensure(Path.Combine(Local, "logs"));
    public static string Database => Path.Combine(Local, "milftv.db");
    public static string Settings => Path.Combine(Roaming, "settings.json");
    /// <summary>Images d'avatar importées pour les profils (recadrées en 256 × 256).</summary>
    public static string Avatars => Ensure(Path.Combine(Roaming, "avatars"));

    public static string DefaultRecordings => DevRoot is not null
        ? Path.Combine(DevRoot, "Recordings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MilfTV");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>Chemins venant d'un fournisseur ou d'une playlist : seuls les fichiers locaux sont acceptés.
/// Un chemin réseau (\\serveur\partage, file://serveur/…, \\?\UNC\…) ferait envoyer par Windows l'identité
/// de l'utilisateur (empreinte NTLM) à ce serveur dès la première vérification d'existence.</summary>
public static class PathSafety
{
    public static bool IsLocalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return false;
        if (Uri.TryCreate(p, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) return false;
            p = uri.LocalPath;
        }
        // Lettre de lecteur locale (C:\…) ; les lecteurs réseau mappés restent acceptés (choix de l'utilisateur).
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
    }
}

/// <summary>Adresses qu'on accepte d'ouvrir dans le moteur vidéo. Les playlists et portails sont des données non
/// fiables : un partage réseau (\\serveur\partage) ferait envoyer par Windows l'empreinte du mot de passe du compte au
/// serveur, et les protocoles internes de mpv (edl://, av://, memory://…) n'ont rien à faire dans une source IPTV.
/// Acceptés : protocoles de diffusion réseau et fichiers sur un lecteur local.</summary>
public static class StreamUrlPolicy
{
    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "rtmp", "rtmps", "rtmpe", "rtmpt", "rtmpte", "rtmpts", "rtsp", "rtsps", "rtp", "udp", "srt", "mms", "mmsh", "mmst",
    };

    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();
        // Partage réseau sous toutes ses formes (\\hôte, //hôte, file://hôte) : refusé.
        if (u.StartsWith(@"\\", StringComparison.Ordinal) || u.StartsWith("//", StringComparison.Ordinal)) return false;
        if (PathSafety.IsLocalFile(u)) return true;
        var colon = u.IndexOf(':');
        if (colon <= 1) return false;
        var scheme = u[..colon];
        return NetworkSchemes.Contains(scheme) && Uri.TryCreate(u, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host);
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.Logs, "milftv.log");

    static Log()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                File.Move(FilePath, FilePath + ".old", true);
        }
        catch { /* ignoré */ }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex is null ? msg : $"{msg} : {ex}");

    private static int _writes;

    // Identifiants qui peuvent apparaître dans une adresse (messages du moteur vidéo, erreurs réseau) :
    // chemins Xtream /live|movie|series|timeshift/<utilisateur>/<mot de passe>/, paramètres username, password,
    // token, mac…, et identifiants dans l'adresse (http://utilisateur:motdepasse@serveur).
    private static readonly System.Text.RegularExpressions.Regex XtreamPath = new(
        @"/(live|movie|series|timeshift|timeshifts)/[^/\s?#]+/[^/\s?#]+/", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SecretQuery = new(
        @"\b(username|user|password|pass|pwd|token|play_token|mac|api_key|auth|key)=([^&\s""'#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex UserInfo = new(
        @"([a-z][a-z0-9+.-]*://)[^/\s@:]+:[^/\s@]+@", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Masque les identifiants : le journal peut être joint à un signalement public.</summary>
    public static string Redact(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg;
        msg = XtreamPath.Replace(msg, "/$1/***/***/");
        msg = SecretQuery.Replace(msg, "$1=***");
        return UserInfo.Replace(msg, "$1***:***@");
    }

    private static void Write(string level, string msg)
    {
        try
        {
            msg = Redact(msg);
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
                // Rotation aussi pendant la session (toutes les 500 lignes, au-delà de 8 Mo).
                if (++_writes % 500 == 0 && new FileInfo(FilePath).Length > 8 * 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", true);
            }
        }
        catch { /* ignoré */ }
    }
}

/// <summary>Chiffrement DPAPI lié au compte Windows (mots de passe, clés API).</summary>
public static class Secret
{
    private static readonly byte[] Entropy = "MilfTV.v1"u8.ToArray();

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            Log.Warn("Impossible de déchiffrer une valeur protégée : " + ex.Message);
            return null;
        }
    }

    /// <summary>Empreinte d'un mot de passe de profil : PBKDF2-SHA256, sel aléatoire.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return true;
        var parts = stored.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iter)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Empreinte du code parental : PBKDF2 salé, comme les mots de passe de profil. (Un code de 4 chiffres
    /// reste vite retrouvé par qui peut lire les réglages : le sel et le coût évitent au moins une table toute faite.)</summary>
    public static string HashPin(string pin) => HashPassword("milftv-pin:" + pin);

    /// <summary>Vérifie un code parental ; legacy = ancienne empreinte (SHA-256 sans sel), à remplacer après succès.</summary>
    public static bool VerifyPin(string pin, string? stored, out bool legacy)
    {
        legacy = false;
        if (string.IsNullOrEmpty(stored)) return false;
        if (stored.StartsWith("pbkdf2$", StringComparison.Ordinal)) return VerifyPassword("milftv-pin:" + pin, stored);
        legacy = true;
        var old = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("milftv-pin:" + pin)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(old), Encoding.ASCII.GetBytes(stored));
    }
}

/// <summary>Limite les essais de code (code parental, mots de passe de profil) : après 3 erreurs, une attente
/// qui double à chaque nouvelle erreur (5 s, 10 s, 20 s… jusqu'à 5 min). Remis à zéro par un code correct.</summary>
public static class AttemptGuard
{
    private const int FreeAttempts = 3;
    private static readonly Dictionary<string, (int Failures, DateTime Until)> State = new();

    /// <summary>Temps d'attente restant avant de pouvoir réessayer (zéro si aucun).</summary>
    public static TimeSpan Remaining(string key)
    {
        lock (State)
            return State.TryGetValue(key, out var s) && s.Until > DateTime.UtcNow ? s.Until - DateTime.UtcNow : TimeSpan.Zero;
    }

    public static void Failed(string key)
    {
        lock (State)
        {
            var failures = (State.TryGetValue(key, out var s) ? s.Failures : 0) + 1;
            var wait = failures <= FreeAttempts ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, failures - FreeAttempts - 1)));
            State[key] = (failures, DateTime.UtcNow + wait);
        }
    }

    public static void Succeeded(string key)
    {
        lock (State) State.Remove(key);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

/// <summary>Clients HTTP partagés.</summary>
public static class Http
{
    public const string DefaultUserAgent = "MilfTV/1.0 (Windows)";

    /// <summary>Client strict (certificats vérifiés) : services tiers et, par défaut, les serveurs IPTV.</summary>
    public static HttpClient Web { get; } = Create(acceptAnyCertificate: false);

    /// <summary>Client tolérant : réservé aux serveurs des sources où l'utilisateur a accepté un certificat non valide.
    /// Sans cela, quelqu'un sur le même réseau (Wi-Fi public…) pourrait intercepter une connexion HTTPS.</summary>
    private static HttpClient Lenient { get; } = Create(acceptAnyCertificate: true);

    /// <summary>Client pour une source : tolérant seulement si l'utilisateur l'a choisi pour elle.</summary>
    public static HttpClient ForSource(SourceConfig? source) => For(source?.AllowInvalidCertificate == true);

    /// <summary>Client strict, ou tolérant si l'utilisateur a accepté un certificat non valide pour ce serveur.</summary>
    public static HttpClient For(bool allowInvalidCertificate) => allowInvalidCertificate ? Lenient : Web;

    /// <summary>Client pour une adresse : tolérant seulement si elle vise le serveur (ou le guide TV) d'une source
    /// pour laquelle l'utilisateur a accepté un certificat non valide.</summary>
    public static HttpClient ForUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return Web;
        try
        {
            foreach (var s in Data.SettingsStore.Current.Sources.ToArray())
            {
                if (!s.AllowInvalidCertificate) continue;
                if (SameHost(s.Url, u.Host) || SameHost(s.EpgUrl, u.Host)) return Lenient;
            }
        }
        catch { /* liste modifiée pendant la lecture : client strict */ }
        return Web;
    }

    private static bool SameHost(string? url, string host) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Contains("://") ? url : "http://" + url, UriKind.Absolute, out var u)
        && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Échec dû au certificat du serveur (non valide, expiré, autosigné…).</summary>
    public static bool IsCertificateError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException) return true;
        return false;
    }

    private static HttpClient Create(bool acceptAnyCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
            MaxConnectionsPerServer = 8,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            UseCookies = false,
        };
        if (acceptAnyCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    public static HttpRequestMessage Request(string url, string? userAgent = null, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            req.Headers.UserAgent.Clear();
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        return req;
    }

    public static async Task<string> GetStringAsync(HttpClient client, string url, string? userAgent, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = Request(url, userAgent);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var limited = new LimitedStream(stream, MaxDocumentBytes);
        using var reader = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Taille maximale d'une playlist ou d'une réponse JSON (les plus gros catalogues font quelques dizaines
    /// de Mo) : au-delà, un serveur malveillant ou défaillant ne doit pas pouvoir saturer la mémoire.</summary>
    public const long MaxDocumentBytes = 256L * 1024 * 1024;

    public static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, string? userAgent, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = Request(url, userAgent);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var limited = new LimitedStream(stream, MaxDocumentBytes);
        return await JsonDocument.ParseAsync(limited, new JsonDocumentOptions { AllowTrailingCommas = true }, cts.Token).ConfigureAwait(false);
    }
}

/// <summary>Flux en lecture qui s'arrête (IOException) au-delà d'une taille donnée : protège la mémoire et le disque
/// contre une réponse démesurée (playlist, guide TV, archive compressée « bombe »).</summary>
public sealed class LimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    private int Count(int n)
    {
        _read += n;
        if (_read > maxBytes) throw new IOException(Localization.Loc.T("Err.TooLarge", maxBytes / (1024 * 1024)));
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Accès tolérant aux JSON des fournisseurs (types souvent incohérents : nombres en texte, null…).</summary>
public static class JsonX
{
    public static string? Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null
        };
    }

    public static string? Str(this JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var v = e.Str(n);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public static int? Int(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return (int)d;
        return null;
    }

    public static long? Long(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;
    }

    public static double? Dbl(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? p : null;

    public static IEnumerable<JsonElement> Items(this JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) yield return x;
        else if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject()) yield return p.Value;
    }
}
