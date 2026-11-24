using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MilfTV.Core;

namespace MilfTV.Images;

/// <summary>Chargement asynchrone des images distantes (affiches, logos) avec cache mémoire + disque.
/// Utilisation : &lt;Image img:ImageLoader.Source="{Binding ImageUrl}" img:ImageLoader.DecodeWidth="240"/&gt;</summary>
public sealed class ImageLoader
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, string?>("Source");

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, int>("DecodeWidth", 0);

    /// <summary>Vrai une fois l'image affichée (permet de masquer un texte de remplacement).</summary>
    public static readonly AttachedProperty<bool> IsLoadedProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, bool>("IsLoaded");

    public static string? GetSource(Image img) => img.GetValue(SourceProperty);
    public static void SetSource(Image img, string? v) => img.SetValue(SourceProperty, v);
    public static int GetDecodeWidth(Image img) => img.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image img, int v) => img.SetValue(DecodeWidthProperty, v);
    public static bool GetIsLoaded(Image img) => img.GetValue(IsLoadedProperty);
    public static void SetIsLoaded(Image img, bool v) => img.SetValue(IsLoadedProperty, v);

    private static readonly SemaphoreSlim Downloads = new(8);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new();
    /// <summary>Images en échec et date à partir de laquelle on peut réessayer (MaxValue : échec définitif).</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Failed = new();
    /// <summary>Cache mémoire limité en nombre ET en octets (les grandes images de fond pèsent plusieurs Mo).</summary>
    private static readonly LruCache Memory = new(450, 256L * 1024 * 1024);
    /// <summary>Panne réseau, serveur saturé… : on réessaie au bout de quelques minutes plutôt que jamais.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(3);
    private const long MaxImageBytes = 15 * 1024 * 1024;

    private static void MarkFailed(string url, bool permanent)
    {
        Failed[url] = permanent ? DateTime.MaxValue : DateTime.UtcNow + RetryAfter;
        // Garde-fou : la liste ne grossit pas indéfiniment pendant une longue session.
        if (Failed.Count > 20_000) Failed.Clear();
    }

    private static bool IsFailed(string url) => Failed.TryGetValue(url, out var until) && until > DateTime.UtcNow;

    static ImageLoader()
    {
        SourceProperty.Changed.AddClassHandler<Image>((img, e) => OnSourceChanged(img, e.NewValue as string));
    }

    private static void OnSourceChanged(Image img, string? url)
    {
        img.Source = null;
        SetIsLoaded(img, false);
        if (string.IsNullOrWhiteSpace(url)) return;
        var width = GetDecodeWidth(img);
        var key = width + "|" + url;
        if (Memory.TryGet(key, out var cached))
        {
            img.Source = cached;
            SetIsLoaded(img, true);
            return;
        }
        if (IsFailed(url)) return;
        _ = LoadIntoAsync(img, url, width, key);
    }

    private static async Task LoadIntoAsync(Image img, string url, int width, string key)
    {
        var bmp = await GetAsync(url, width, key).ConfigureAwait(true);
        if (bmp is not null && GetSource(img) == url)
        {
            img.Source = bmp;
            SetIsLoaded(img, true);
        }
    }

    /// <summary>Charge une image (utilisable hors contrôle Image, ex. fond d'écran).</summary>
    public static Task<Bitmap?> GetAsync(string url, int width) => GetAsync(url, width, width + "|" + url);

    private static Task<Bitmap?> GetAsync(string url, int width, string key)
    {
        if (Memory.TryGet(key, out var cached)) return Task.FromResult<Bitmap?>(cached);
        return InFlight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try
            {
                var (bytes, permanent) = await GetBytesAsync(url).ConfigureAwait(false);
                if (bytes is null || bytes.Length < 16) { MarkFailed(url, permanent); return null; }
                using var ms = new MemoryStream(bytes);
                Bitmap bmp;
                try
                {
                    bmp = width > 0 ? Bitmap.DecodeToWidth(ms, width, BitmapInterpolationMode.MediumQuality) : new Bitmap(ms);
                }
                catch
                {
                    MarkFailed(url, permanent: true);
                    TryDeleteDisk(url);
                    return null;
                }
                Memory.Add(key, bmp);
                return (Bitmap?)bmp;
            }
            catch (Exception ex)
            {
                MarkFailed(url, permanent: false);
                // Pas d'adresse dans le journal : elle peut contenir des identifiants du fournisseur.
                Log.Warn("Image indisponible : " + ex.Message);
                return null;
            }
            finally
            {
                InFlight.TryRemove(key, out Task<Bitmap?>? _);
            }
        }));
    }

    private static string DiskPath(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(AppPaths.ImageCache, hash[..2], hash + ".img");
    }

    private static void TryDeleteDisk(string url)
    {
        try { File.Delete(DiskPath(url)); } catch { /* ignoré */ }
    }

    /// <summary>Octets de l'image. Permanent = inutile de réessayer (introuvable, adresse refusée).</summary>
    private static async Task<(byte[]? Bytes, bool Permanent)> GetBytesAsync(string url)
    {
        if (url.StartsWith("avares://", StringComparison.Ordinal))
        {
            using var s = Avalonia.Platform.AssetLoader.Open(new Uri(url));
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms).ConfigureAwait(false);
            return (ms.ToArray(), true);
        }
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // Fichier local (playlist M3U locale). Jamais de chemin réseau (\\serveur\partage, file://serveur) :
            // une simple vérification d'existence enverrait l'identité Windows de l'utilisateur à ce serveur.
            if (!PathSafety.IsLocalFile(url) || !File.Exists(url)) return (null, true);
            if (new FileInfo(url).Length > MaxImageBytes) return (null, true);
            return (await File.ReadAllBytesAsync(url).ConfigureAwait(false), true);
        }

        var path = DiskPath(url);
        if (File.Exists(path))
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return (await File.ReadAllBytesAsync(path).ConfigureAwait(false), true);
            }
            catch { /* on retélécharge */ }
        }

        await Downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var req = Http.Request(url);
            // Certificats vérifiés, sauf pour le serveur d'une source où l'utilisateur a accepté un certificat non valide.
            var client = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("tmdb.org", StringComparison.OrdinalIgnoreCase) ? Http.Web : Http.ForUrl(url);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.Forbidden);
            // Un « logo » qui serait en fait un flux vidéo : on ne le télécharge pas.
            if (resp.Content.Headers.ContentLength is > MaxImageBytes) return (null, true);
            var media = resp.Content.Headers.ContentType?.MediaType;
            if (media is not null && (media.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || media.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)))
                return (null, true);
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxImageBytes) return (null, true);
            }
            var bytes = buffer.ToArray();
            if (bytes.Length > 16)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                }
                catch { /* cache disque facultatif */ }
            }
            return (bytes, false);
        }
        finally
        {
            Downloads.Release();
        }
    }

    /// <summary>Limite la taille du cache disque (appelé au démarrage).</summary>
    public static void TrimDiskCache(long maxBytes = 1_200_000_000, long targetBytes = 900_000_000)
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.ImageCache).EnumerateFiles("*.img", SearchOption.AllDirectories).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= targetBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { /* ignoré */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Nettoyage du cache d'images : " + ex.Message);
        }
    }

    private sealed class LruCache(int capacity, long maxBytes)
    {
        private readonly object _gate = new();
        private long _bytes;
        private static long SizeOf(Bitmap b) => (long)b.PixelSize.Width * b.PixelSize.Height * 4;
        private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp)>> _map = new();
        private readonly LinkedList<(string Key, Bitmap Bmp)> _list = new();

        public bool TryGet(string key, out Bitmap bmp)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    bmp = node.Value.Bmp;
                    return true;
                }
            }
            bmp = null!;
            return false;
        }

        public void Add(string key, Bitmap bmp)
        {
            lock (_gate)
            {
                if (_map.ContainsKey(key)) return;
                var node = _list.AddFirst((key, bmp));
                _map[key] = node;
                _bytes += SizeOf(bmp);
                while (_map.Count > 1 && (_map.Count > capacity || _bytes > maxBytes))
                {
                    var last = _list.Last!;
                    _list.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _bytes -= SizeOf(last.Value.Bmp);
                    // Pas de Dispose : le bitmap peut encore être affiché ; le GC s'en chargera.
                }
            }
        }
    }
}
