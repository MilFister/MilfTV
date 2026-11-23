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

    private static void Write(string level, string msg)
    {
        try
        {
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

    public static string HashPin(string pin)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("milftv-pin:" + pin));
        return Convert.ToHexString(bytes);
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

    /// <summary>Client pour les serveurs IPTV (certificats souvent invalides chez les fournisseurs).</summary>
    public static HttpClient Iptv { get; } = Create(acceptAnyCertificate: true);

    /// <summary>Client strict pour les services tiers (TMDB, images…).</summary>
    public static HttpClient Web { get; } = Create(acceptAnyCertificate: false);

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
        return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, string? userAgent, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = Request(url, userAgent);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true }, cts.Token).ConfigureAwait(false);
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
