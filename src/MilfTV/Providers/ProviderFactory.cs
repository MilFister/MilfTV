using System.IO.Compression;
using System.Text.Json;
using MilfTV.Core;

namespace MilfTV.Providers;

public static class ProviderFactory
{
    public static IProvider Create(SourceConfig source) => source.Kind switch
    {
        SourceKind.Xtream => new XtreamProvider(source),
        SourceKind.M3u => new M3uProvider(source),
        SourceKind.Stalker => new StalkerProvider(source),
        _ => throw new NotSupportedException(source.Kind.ToString())
    };
}

/// <summary>Cache disque du catalogue (JSON compressé) pour un démarrage instantané.</summary>
public static class CatalogCache
{
    private static string PathFor(string sourceId) => Path.Combine(AppPaths.CatalogCache, sourceId + ".json.gz");

    public static async Task SaveAsync(string sourceId, Catalog catalog)
    {
        var path = PathFor(sourceId);
        var tmp = path + ".tmp";
        try
        {
            await using (var fs = File.Create(tmp))
            await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gz, catalog, Json.Options).ConfigureAwait(false);
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Cache du catalogue non enregistré : " + ex.Message);
            try { File.Delete(tmp); } catch { /* ignoré */ }
        }
    }

    public static async Task<Catalog?> LoadAsync(string sourceId)
    {
        var path = PathFor(sourceId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<Catalog>(gz, Json.Options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Cache du catalogue illisible : " + ex.Message);
            return null;
        }
    }

    public static void Delete(string sourceId)
    {
        try { File.Delete(PathFor(sourceId)); } catch { /* ignoré */ }
    }
}
