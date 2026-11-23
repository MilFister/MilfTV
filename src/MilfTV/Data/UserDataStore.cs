using Microsoft.Data.Sqlite;
using MilfTV.Core;
using MilfTV.Localization;

namespace MilfTV.Data;

/// <summary>Entrée d'historique / reprise de lecture.</summary>
public sealed class WatchEntry
{
    public string ProfileId { get; set; } = "";    // vide pour les chaînes (communes à tous les profils)
    public string SourceId { get; set; } = "";
    public string Key { get; set; } = "";          // "Movie:123", "Episode:456", "Live:789"
    public ContentKind Kind { get; set; }
    public string ItemId { get; set; } = "";
    public string? ParentId { get; set; }          // série d'un épisode
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }          // "S02E05 · Titre"
    public string? ImageUrl { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public bool Watched { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public double Progress => Duration > 0 ? Math.Clamp(Position / Duration * 100, 0, 100) : 0;
    public string RemainingText
    {
        get
        {
            if (Duration <= 0) return "";
            var rem = Math.Max(0, Duration - Position);
            return rem < 60 ? Loc.T("Watch.AlmostDone") : Loc.T("Watch.Remaining", TextNorm.Duration((int)rem));
        }
    }
    public string When => TextNorm.RelativeAgo(UpdatedAt);
}

public enum RecordingStatus { Scheduled, Recording, Completed, Failed, Cancelled }

public sealed class RecordingEntry
{
    public long Id { get; set; }
    public string SourceId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public RecordingStatus Status { get; set; }
    public string? FilePath { get; set; }
    public string? Error { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>Favoris des chaînes (communs), historique et « Ma liste » par profil, enregistrements (SQLite).</summary>
public static class UserDataStore
{
    private static int _schema;

    /// <summary>Profil qui reçoit l'historique existant lors de la migration vers les profils.</summary>
    public static string? MigrationProfileId { get; set; }

    private const string CreateWatch = """
        CREATE TABLE IF NOT EXISTS watch(
            profile TEXT NOT NULL DEFAULT '', source TEXT NOT NULL, key TEXT NOT NULL, kind TEXT NOT NULL, item_id TEXT NOT NULL,
            parent_id TEXT, title TEXT NOT NULL, subtitle TEXT, image TEXT,
            position REAL NOT NULL, duration REAL NOT NULL, watched INTEGER NOT NULL, updated INTEGER NOT NULL,
            PRIMARY KEY(profile, source, key));
        """;

    private static readonly object SchemaGate = new();

    private static SqliteConnection Open()
    {
        var c = Db.Open();
        if (Volatile.Read(ref _schema) == 0)
        {
            lock (SchemaGate)
            {
                if (_schema == 0)
                {
                    try
                    {
                        EnsureSchema(c);
                        Volatile.Write(ref _schema, 1);
                    }
                    catch (Exception ex) { Log.Error("Schéma des données utilisateur (nouvel essai au prochain accès)", ex); }
                }
            }
        }
        return c;
    }

    private static void EnsureSchema(SqliteConnection c)
    {
        Db.Exec(c, """
            CREATE TABLE IF NOT EXISTS favorites(
                source TEXT NOT NULL, key TEXT NOT NULL, added INTEGER NOT NULL,
                PRIMARY KEY(source, key));
            CREATE TABLE IF NOT EXISTS watchlist(
                profile TEXT NOT NULL, source TEXT NOT NULL, key TEXT NOT NULL, added INTEGER NOT NULL,
                PRIMARY KEY(profile, source, key));
            CREATE TABLE IF NOT EXISTS recordings(
                id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, channel_id TEXT NOT NULL,
                channel_name TEXT NOT NULL, title TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL,
                status TEXT NOT NULL, file TEXT, error TEXT, size INTEGER NOT NULL DEFAULT 0);
            """);

        var watchExists = Convert.ToInt64(Db.Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='watch'") ?? 0L) > 0;
        var hasProfile = watchExists && Convert.ToInt64(Db.Scalar(c, "SELECT count(*) FROM pragma_table_info('watch') WHERE name='profile'") ?? 0L) > 0;
        if (watchExists && !hasProfile)
        {
            // Migration vers les profils : films et épisodes vont au premier profil, les chaînes restent communes.
            var p = MigrationProfileId ?? "";
            Db.Exec(c, "BEGIN IMMEDIATE;" + """
                ALTER TABLE watch RENAME TO watch_v1;
                """ + CreateWatch + """
                INSERT INTO watch(profile, source, key, kind, item_id, parent_id, title, subtitle, image, position, duration, watched, updated)
                    SELECT CASE WHEN kind IN ('Movie','Episode') THEN $p ELSE '' END,
                           source, key, kind, item_id, parent_id, title, subtitle, image, position, duration, watched, updated
                    FROM watch_v1;
                DROP TABLE watch_v1;
                INSERT OR IGNORE INTO watchlist(profile, source, key, added)
                    SELECT $p, source, key, added FROM favorites WHERE key LIKE 'Movie:%' OR key LIKE 'Series:%';
                DELETE FROM favorites WHERE key LIKE 'Movie:%' OR key LIKE 'Series:%';
                COMMIT;
                """, ("$p", p));
            Log.Info("Données utilisateur migrées vers les profils");
        }
        else
        {
            Db.Exec(c, CreateWatch);
        }
        Db.Exec(c, "CREATE INDEX IF NOT EXISTS ix_watch_profile_updated ON watch(profile, source, updated DESC);");
    }

    // ───────────── Favoris des chaînes (communs à tous les profils) ─────────────

    public static HashSet<string> GetFavorites(string sourceId)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT key FROM favorites WHERE source=$s ORDER BY added", ("$s", sourceId));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public static List<string> GetFavoritesOrdered(string sourceId)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT key FROM favorites WHERE source=$s ORDER BY added", ("$s", sourceId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public static void SetFavorite(string sourceId, string key, bool fav)
    {
        using var c = Open();
        if (fav)
            Db.Exec(c, "INSERT OR IGNORE INTO favorites(source,key,added) VALUES($s,$k,$a)",
                ("$s", sourceId), ("$k", key), ("$a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        else
            Db.Exec(c, "DELETE FROM favorites WHERE source=$s AND key=$k", ("$s", sourceId), ("$k", key));
    }

    // ───────────── « Ma liste » (par profil) ─────────────

    /// <summary>Clés de « Ma liste », les plus récentes d'abord.</summary>
    public static List<string> GetWatchlist(string sourceId, string profileId)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT key FROM watchlist WHERE source=$s AND profile=$p ORDER BY added DESC",
            ("$s", sourceId), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public static void SetWatchlist(string sourceId, string profileId, string key, bool inList)
    {
        using var c = Open();
        if (inList)
            Db.Exec(c, "INSERT OR IGNORE INTO watchlist(profile,source,key,added) VALUES($p,$s,$k,$a)",
                ("$p", profileId), ("$s", sourceId), ("$k", key), ("$a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        else
            Db.Exec(c, "DELETE FROM watchlist WHERE profile=$p AND source=$s AND key=$k", ("$p", profileId), ("$s", sourceId), ("$k", key));
    }

    // ───────────── Historique / reprise ─────────────

    public static void SaveWatch(WatchEntry e)
    {
        using var c = Open();
        Db.Exec(c, """
            INSERT INTO watch(profile,source,key,kind,item_id,parent_id,title,subtitle,image,position,duration,watched,updated)
            VALUES($pr,$s,$k,$kind,$id,$p,$t,$st,$img,$pos,$dur,$w,$u)
            ON CONFLICT(profile,source,key) DO UPDATE SET kind=$kind,item_id=$id,parent_id=$p,title=$t,subtitle=$st,image=$img,
                position=$pos,duration=$dur,watched=$w,updated=$u
            """,
            ("$pr", e.ProfileId), ("$s", e.SourceId), ("$k", e.Key), ("$kind", e.Kind.ToString()), ("$id", e.ItemId), ("$p", e.ParentId),
            ("$t", e.Title), ("$st", e.Subtitle), ("$img", e.ImageUrl), ("$pos", e.Position), ("$dur", e.Duration),
            ("$w", e.Watched ? 1 : 0), ("$u", e.UpdatedAt.ToUnixTimeSeconds()));
    }

    public static WatchEntry? GetWatch(string sourceId, string profileId, string key)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT * FROM watch WHERE source=$s AND profile=$p AND key=$k",
            ("$s", sourceId), ("$p", profileId), ("$k", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadWatch(r) : null;
    }

    /// <summary>Films et épisodes d'un profil, indexés par clé (progressions, déjà vu).</summary>
    public static Dictionary<string, WatchEntry> GetAllWatch(string sourceId, string profileId)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT * FROM watch WHERE source=$s AND profile=$p", ("$s", sourceId), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, WatchEntry>();
        while (r.Read()) { var e = ReadWatch(r); d[e.Key] = e; }
        return d;
    }

    /// <summary>« Reprendre la lecture » : films et épisodes entamés, le plus récent par série.</summary>
    public static List<WatchEntry> GetContinueWatching(string sourceId, string profileId, int limit = 30)
    {
        using var c = Open();
        using var cmd = Db.Command(c, """
            SELECT * FROM watch WHERE source=$s AND profile=$p AND kind IN ('Movie','Episode') AND watched=0 AND position > 30
            ORDER BY updated DESC LIMIT 300
            """, ("$s", sourceId), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var list = new List<WatchEntry>();
        var seenSeries = new HashSet<string>();
        while (r.Read() && list.Count < limit)
        {
            var e = ReadWatch(r);
            if (e.Kind == ContentKind.Episode && e.ParentId is not null && !seenSeries.Add(e.ParentId)) continue;
            list.Add(e);
        }
        return list;
    }

    /// <summary>Historique complet (films et épisodes) d'un profil, le plus récent d'abord.</summary>
    public static List<WatchEntry> GetHistory(string sourceId, string profileId)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT * FROM watch WHERE source=$s AND profile=$p AND kind IN ('Movie','Episode') ORDER BY updated DESC",
            ("$s", sourceId), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var list = new List<WatchEntry>();
        while (r.Read()) list.Add(ReadWatch(r));
        return list;
    }

    /// <summary>Chaînes récemment regardées (communes à tous les profils).</summary>
    public static List<WatchEntry> GetRecentChannels(string sourceId, int limit = 20)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT * FROM watch WHERE source=$s AND profile='' AND kind='Live' ORDER BY updated DESC LIMIT $l",
            ("$s", sourceId), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<WatchEntry>();
        while (r.Read()) list.Add(ReadWatch(r));
        return list;
    }

    /// <summary>Dernier épisode regardé d'une série (pour « Reprendre S02E05 »).</summary>
    public static WatchEntry? GetLastEpisode(string sourceId, string profileId, string seriesId)
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT * FROM watch WHERE source=$s AND profile=$pr AND kind='Episode' AND parent_id=$p ORDER BY updated DESC LIMIT 1",
            ("$s", sourceId), ("$pr", profileId), ("$p", seriesId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadWatch(r) : null;
    }

    public static void RemoveWatch(string sourceId, string profileId, string key)
    {
        using var c = Open();
        Db.Exec(c, "DELETE FROM watch WHERE source=$s AND profile=$p AND key=$k", ("$s", sourceId), ("$p", profileId), ("$k", key));
    }

    /// <summary>Efface l'historique de tous les épisodes d'une série pour un profil.</summary>
    public static void RemoveSeriesHistory(string sourceId, string profileId, string seriesId)
    {
        using var c = Open();
        Db.Exec(c, "DELETE FROM watch WHERE source=$s AND profile=$p AND kind='Episode' AND parent_id=$id",
            ("$s", sourceId), ("$p", profileId), ("$id", seriesId));
    }

    /// <summary>Rattache à un profil l'historique et la liste qui n'appartiennent à aucun profil (ancienne version).</summary>
    public static void AdoptOrphanData(string profileId)
    {
        using var c = Open();
        Db.Exec(c, """
            UPDATE OR IGNORE watch SET profile=$p WHERE profile='' AND kind IN ('Movie','Episode');
            UPDATE OR IGNORE watchlist SET profile=$p WHERE profile='';
            """, ("$p", profileId));
    }

    /// <summary>Efface l'historique (films et épisodes) d'un profil, toutes sources confondues.</summary>
    public static void ClearHistory(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return;
        using var c = Open();
        Db.Exec(c, "DELETE FROM watch WHERE profile=$p AND kind IN ('Movie','Episode')", ("$p", profileId));
    }

    /// <summary>Vide « Ma liste » d'un profil, toutes sources confondues.</summary>
    public static void ClearWatchlist(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return;
        using var c = Open();
        Db.Exec(c, "DELETE FROM watchlist WHERE profile=$p", ("$p", profileId));
    }

    public static void DeleteProfile(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return;
        using var c = Open();
        Db.Exec(c, "DELETE FROM watch WHERE profile=$p; DELETE FROM watchlist WHERE profile=$p;", ("$p", profileId));
    }

    public static void DeleteSource(string sourceId)
    {
        using var c = Open();
        Db.Exec(c, "DELETE FROM favorites WHERE source=$s; DELETE FROM watch WHERE source=$s; DELETE FROM watchlist WHERE source=$s;", ("$s", sourceId));
    }

    private static WatchEntry ReadWatch(SqliteDataReader r) => new()
    {
        ProfileId = r.GetString(r.GetOrdinal("profile")),
        SourceId = r.GetString(r.GetOrdinal("source")),
        Key = r.GetString(r.GetOrdinal("key")),
        Kind = Enum.TryParse<ContentKind>(r.GetString(r.GetOrdinal("kind")), out var k) ? k : ContentKind.Movie,
        ItemId = r.GetString(r.GetOrdinal("item_id")),
        ParentId = r.GetStringOrNull(r.GetOrdinal("parent_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        Subtitle = r.GetStringOrNull(r.GetOrdinal("subtitle")),
        ImageUrl = r.GetStringOrNull(r.GetOrdinal("image")),
        Position = r.GetDouble(r.GetOrdinal("position")),
        Duration = r.GetDouble(r.GetOrdinal("duration")),
        Watched = r.GetInt32(r.GetOrdinal("watched")) != 0,
        UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(r.GetOrdinal("updated"))),
    };

    // ───────────── Enregistrements ─────────────

    public static long AddRecording(RecordingEntry e)
    {
        using var c = Open();
        Db.Exec(c, """
            INSERT INTO recordings(source,channel_id,channel_name,title,start,end,status,file,error,size)
            VALUES($s,$ch,$cn,$t,$b,$e,$st,$f,$err,$sz)
            """,
            ("$s", e.SourceId), ("$ch", e.ChannelId), ("$cn", e.ChannelName), ("$t", e.Title),
            ("$b", e.Start.ToUnixTimeSeconds()), ("$e", e.End.ToUnixTimeSeconds()), ("$st", e.Status.ToString()),
            ("$f", e.FilePath), ("$err", e.Error), ("$sz", e.SizeBytes));
        e.Id = (long)(Db.Scalar(c, "SELECT last_insert_rowid()") ?? 0L);
        return e.Id;
    }

    public static void UpdateRecording(RecordingEntry e)
    {
        using var c = Open();
        Db.Exec(c, "UPDATE recordings SET status=$st, file=$f, error=$err, size=$sz, start=$b, end=$e WHERE id=$id",
            ("$st", e.Status.ToString()), ("$f", e.FilePath), ("$err", e.Error), ("$sz", e.SizeBytes),
            ("$b", e.Start.ToUnixTimeSeconds()), ("$e", e.End.ToUnixTimeSeconds()), ("$id", e.Id));
    }

    public static void DeleteRecording(long id)
    {
        using var c = Open();
        Db.Exec(c, "DELETE FROM recordings WHERE id=$id", ("$id", id));
    }

    public static List<RecordingEntry> GetRecordings()
    {
        using var c = Open();
        using var cmd = Db.Command(c, "SELECT id,source,channel_id,channel_name,title,start,end,status,file,error,size FROM recordings ORDER BY start DESC");
        using var r = cmd.ExecuteReader();
        var list = new List<RecordingEntry>();
        while (r.Read())
        {
            list.Add(new RecordingEntry
            {
                Id = r.GetInt64(0),
                SourceId = r.GetString(1),
                ChannelId = r.GetString(2),
                ChannelName = r.GetString(3),
                Title = r.GetString(4),
                Start = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)),
                End = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)),
                Status = Enum.TryParse<RecordingStatus>(r.GetString(7), out var st) ? st : RecordingStatus.Failed,
                FilePath = r.GetStringOrNull(8),
                Error = r.GetStringOrNull(9),
                SizeBytes = r.GetInt64(10),
            });
        }
        return list;
    }

    // ───────────── Sauvegarde et restauration ─────────────

    /// <summary>Tables des données de l'utilisateur (le guide TV et les caches sont exclus des sauvegardes).</summary>
    private static readonly string[] UserTables = { "watch", "watchlist", "favorites", "recordings" };

    /// <summary>Copie les tables utilisateur dans une base séparée ; renvoie le nombre de lignes par table.</summary>
    public static Dictionary<string, int> ExportUserData(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
        var counts = new Dictionary<string, int>();
        using var c = Open();
        Db.Exec(c, "ATTACH DATABASE $p AS bk", ("$p", destPath));
        try
        {
            foreach (var t in UserTables)
            {
                Db.Exec(c, $"CREATE TABLE bk.{t} AS SELECT * FROM main.{t}");
                counts[t] = Convert.ToInt32(Db.Scalar(c, $"SELECT COUNT(*) FROM bk.{t}") ?? 0);
            }
        }
        finally
        {
            Db.Exec(c, "DETACH DATABASE bk");
        }
        return counts;
    }

    /// <summary>Remplace les tables utilisateur par celles d'une sauvegarde (colonnes communes seulement :
    /// une sauvegarde d'une autre version reste lisible ; les colonnes absentes prennent leur valeur par défaut).</summary>
    public static void ReplaceUserData(string srcPath)
    {
        using var c = Open();
        Db.Exec(c, "ATTACH DATABASE $p AS bk", ("$p", srcPath));
        try
        {
            Db.Exec(c, "BEGIN");
            try
            {
                foreach (var t in UserTables)
                {
                    Db.Exec(c, $"DELETE FROM main.{t}");
                    var backupCols = Columns(c, "bk", t);
                    if (backupCols.Count == 0) continue;
                    var cols = string.Join(",", Columns(c, "main", t).Where(backupCols.Contains).Select(x => "\"" + x + "\""));
                    if (cols.Length > 0) Db.Exec(c, $"INSERT INTO main.{t} ({cols}) SELECT {cols} FROM bk.{t}");
                }
                Db.Exec(c, "COMMIT");
            }
            catch
            {
                Db.Exec(c, "ROLLBACK");
                throw;
            }
        }
        finally
        {
            Db.Exec(c, "DETACH DATABASE bk");
        }
    }

    /// <summary>Nombre de lignes des tables utilisateur (contrôle, tests).</summary>
    public static Dictionary<string, int> CountUserData()
    {
        using var c = Open();
        return UserTables.ToDictionary(t => t, t => Convert.ToInt32(Db.Scalar(c, $"SELECT COUNT(*) FROM {t}") ?? 0));
    }

    private static List<string> Columns(SqliteConnection c, string schema, string table)
    {
        using var cmd = Db.Command(c, $"PRAGMA {schema}.table_info({table})");
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }
}
