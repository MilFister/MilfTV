using Microsoft.Data.Sqlite;

namespace MilfTV.Data;

/// <summary>Accès à la base SQLite locale (%LOCALAPPDATA%\MilfTV\milftv.db).
/// Chaque appel à <see cref="Open"/> renvoie une connexion ouverte (pool activé) : à disposer après usage.</summary>
public static class Db
{
    private static readonly string ConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Core.AppPaths.Database,
        Cache = SqliteCacheMode.Default,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    private static int _initialized;
    private static readonly object InitGate = new();

    public static SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        if (Volatile.Read(ref _initialized) == 0)
        {
            lock (InitGate)
            {
                if (_initialized == 0)
                {
                    // Base neuve : vidage incrémental pour récupérer la place libérée par le guide TV.
                    if (Convert.ToInt64(Scalar(c, "PRAGMA auto_vacuum;") ?? 0L) == 0 && Convert.ToInt64(Scalar(c, "PRAGMA page_count;") ?? 0L) < 8)
                        Exec(c, "PRAGMA auto_vacuum=INCREMENTAL; VACUUM;");
                    Exec(c, "PRAGMA journal_mode=WAL;");
                    Volatile.Write(ref _initialized, 1);
                }
            }
        }
        Exec(c, "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        return c;
    }

    public static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Libère une partie de l'espace inutilisé (bases en auto_vacuum incrémental).</summary>
    public static void Compact()
    {
        try
        {
            using var c = Open();
            Exec(c, "PRAGMA incremental_vacuum(20000);");
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Compactage de la base : " + ex.Message);
        }
    }

    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
