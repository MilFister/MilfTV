using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Localization;
using MilfTV.Providers;

namespace MilfTV.Epg;

/// <summary>
/// Guide TV (EPG) stocké dans SQLite.
/// <para>
/// Organisation : les programmes sont rangés par « flux » (table epg_feeds). Chaque import XMLTV d'une source
/// remplit un nouveau flux par petits lots, puis le rend actif dans une seule transaction très courte :
/// les lecteurs voient l'ancien guide jusqu'à cette bascule, sans que la base reste verrouillée en écriture
/// pendant tout le téléchargement. L'ancien flux est ensuite supprimé par lots.
/// </para>
/// <para>
/// Les programmes obtenus directement auprès du fournisseur (<see cref="Upsert"/>) vivent dans un flux
/// d'origine « fournisseur » qui n'est jamais vidé par un import XMLTV (seules les plages qu'un nouveau XMLTV
/// couvre pour la même chaîne sont remplacées).
/// </para>
/// Toutes les heures sont stockées en secondes Unix (UTC), les clés de chaîne en minuscules (invariant).
/// Chaque méthode ouvre sa propre connexion : la classe est utilisable depuis plusieurs threads.
/// </summary>
public sealed class EpgStore
{
    private const int SchemaVersion = 1;

    private const int OriginXmltv = 0;
    private const int OriginProvider = 1;

    private const int StateBuilding = 0;
    private const int StateActive = 1;
    private const int StateObsolete = 2;

    private const long KeepPastSeconds = 24 * 3600;          // fin > maintenant - 24 h
    private const long KeepFutureSeconds = 8 * 24 * 3600;    // début < maintenant + 8 jours
    private const long PurgeAgeSeconds = 2 * 24 * 3600;      // PurgeOld : fin < maintenant - 2 jours
    private const long OrphanFeedSeconds = 6 * 3600;         // flux « en cours » abandonné (plantage)
    private const int BatchSize = 10_000;                    // programmes par transaction d'import
    private const int DeleteChunk = 5_000;                   // lignes par transaction de suppression
    private const int KeySetThreshold = 64;                  // au-delà, on charge la liste des chaînes renseignées
    private const int ProgressInterval = 5000;
    private const int MaxSearchTokens = 8;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static int _schemaReady;
    private static readonly object SchemaGate = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ImportGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<long, byte> BuildingFeeds = new();

    private const string DropSql = """
        DROP TABLE IF EXISTS epg_programs;
        DROP TABLE IF EXISTS epg_channels;
        DROP TABLE IF EXISTS epg_channel_names;
        DROP TABLE IF EXISTS epg_feeds;
        """;

    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS epg_feeds(
            id INTEGER PRIMARY KEY,
            source TEXT NOT NULL,
            origin INTEGER NOT NULL,            -- 0 = XMLTV, 1 = fournisseur (Upsert)
            state INTEGER NOT NULL,             -- 0 = import en cours, 1 = actif, 2 = obsolète (à supprimer)
            created INTEGER NOT NULL,
            imported INTEGER,
            programs INTEGER NOT NULL DEFAULT 0,
            maxdur INTEGER NOT NULL DEFAULT 0   -- durée maximale d'un programme du flux (bornes des requêtes)
        );
        CREATE INDEX IF NOT EXISTS ix_epg_feeds_source ON epg_feeds(source, origin, state);

        CREATE TABLE IF NOT EXISTS epg_programs(
            feed INTEGER NOT NULL,
            channel TEXT NOT NULL,
            start INTEGER NOT NULL,
            stop INTEGER NOT NULL,
            title TEXT NOT NULL,
            title_norm TEXT NOT NULL,
            descr TEXT,
            category TEXT,
            icon TEXT,
            episode TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_epg_programs ON epg_programs(feed, channel, start, stop);

        CREATE TABLE IF NOT EXISTS epg_channels(
            feed INTEGER NOT NULL,
            id TEXT NOT NULL,
            ord INTEGER NOT NULL,
            icon TEXT,
            programs INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(feed, id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS epg_channel_names(
            feed INTEGER NOT NULL,
            ord INTEGER NOT NULL,
            channel TEXT NOT NULL,
            name TEXT NOT NULL,
            name_norm TEXT NOT NULL,
            PRIMARY KEY(feed, ord)
        ) WITHOUT ROWID;
        """;

    private const string ProgramColumns = "start, stop, title, descr, category, icon, episode";

    public EpgStore()
    {
        try { EnsureSchema(); }
        catch (Exception ex) { Log.Error("Guide TV : création des tables impossible", ex); }
    }

    // =====================================================================================
    // Import XMLTV
    // =====================================================================================

    /// <summary>
    /// Télécharge (http/https, chemin local ou URI file://) et importe un fichier XMLTV pour une source.
    /// Remplace atomiquement les programmes XMLTV précédents de la source. Lève une
    /// <see cref="ProviderException"/> (message dans la langue de l'interface) en cas d'échec.
    /// </summary>
    /// <returns>Nombre de programmes importés.</returns>
    public Task<int> ImportXmltvAsync(string sourceId, string url, string? userAgent, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => ImportGuardedAsync(sourceId, url, userAgent, progress, ct), ct);

    private static async Task<int> ImportGuardedAsync(string sourceId, string url, string? userAgent, IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ProviderException(Loc.T("Epg.NoUrl"));
        try { EnsureSchema(); }
        catch (Exception ex) { throw new ProviderException(Loc.T("Epg.DbUnavailable", ex.Message), ex); }

        // Un seul import à la fois par source.
        var gate = ImportGates.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ImportLockedAsync(sourceId, url.Trim(), userAgent, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<int> ImportLockedAsync(string sourceId, string url, string? userAgent, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);
        var token = timeoutCts.Token;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = Db.Open();
        var throttle = new WriteThrottle();

        long feed;
        try
        {
            // Restes d'un import interrompu (plantage, arrêt) ou ancien guide pas encore supprimé.
            DeleteFeeds(conn, throttle, "SELECT id FROM epg_feeds WHERE source = $s AND origin = 0 AND state <> 1", ("$s", sourceId));
            feed = (long)Scalar(conn, null,
                "INSERT INTO epg_feeds(source, origin, state, created) VALUES ($s, 0, 0, $now); SELECT last_insert_rowid();",
                ("$s", sourceId), ("$now", now))!;
        }
        catch (Exception ex)
        {
            throw ToUserError(ex, false);
        }
        BuildingFeeds.TryAdd(feed, 0);

        int count;
        XmltvParser parser;
        bool partial = false;
        try
        {
            using var writer = new FeedWriter(conn, feed, throttle);
            parser = new XmltvParser(now - KeepPastSeconds, now + KeepFutureSeconds, token)
            {
                ChannelFound = writer.AddChannel,
                ProgrammeFound = writer.AddProgramme,
                ProgressInterval = ProgressInterval,
                Progress = progress is null ? null : n => progress.Report(Loc.T("Epg.ProgramsProgress", n)),
            };

            progress?.Report(Loc.T("Epg.Downloading"));
            try
            {
                using var source = await OpenSourceAsync(url, userAgent, token).ConfigureAwait(false);
                using var content = await XmltvParser.DecodeAsync(source.Stream, token).ConfigureAwait(false);
                await parser.ParseAsync(content).ConfigureAwait(false);
            }
            catch (XmlException ex) when (writer.Count > 0 && !token.IsCancellationRequested)
            {
                // Fichier mal formé (ou tronqué) après des programmes valides : on garde ce qui a été lu,
                // sauf si c'est nettement moins que le guide actuel (téléchargement coupé : mieux vaut garder l'ancien).
                var previous = Scalar(conn, null,
                    "SELECT programs FROM epg_feeds WHERE source = $s AND origin = 0 AND state = 1 ORDER BY id DESC LIMIT 1",
                    ("$s", sourceId)) as long?;
                if (previous is > 0 && writer.Count < previous.Value / 2)
                    throw new ProviderException(Loc.T("Epg.IncompleteKeptPrevious", ex.LineNumber), ex);
                partial = true;
                Log.Warn($"Guide TV ({sourceId}) : XMLTV invalide ligne {ex.LineNumber} ({ex.Message}) ; " +
                         $"{writer.Count} programmes lus avant l'erreur sont conservés.");
            }

            token.ThrowIfCancellationRequested();
            writer.Flush();
            if (!writer.Activate(sourceId, now))
            {
                // La source a été supprimée pendant l'import.
                TryDeleteFeed(conn, feed, throttle);
                return 0;
            }
            count = writer.Count;
        }
        catch (Exception ex)
        {
            TryDeleteFeed(conn, feed, throttle);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(Loc.T("Epg.ImportCancelled"), ex, ct);
            var error = ToUserError(ex, timeoutCts.IsCancellationRequested);
            Log.Warn($"Guide TV ({sourceId}) : {error.Message} [{ex.GetType().Name} : {ex.Message}]");
            throw error;
        }
        finally
        {
            BuildingFeeds.TryRemove(feed, out _);
        }

        // Suppression de l'ancien guide, par lots (le nouveau est déjà visible).
        try
        {
            DeleteFeeds(conn, throttle, "SELECT id FROM epg_feeds WHERE source = $s AND origin = 0 AND state = 2", ("$s", sourceId));
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : suppression de l'ancien guide différée : {ex.Message}");
        }

        Log.Info($"Guide TV ({sourceId}) : {count} programmes importés en {sw.Elapsed.TotalSeconds:0.0} s " +
                 $"({parser.Seen} lus, {parser.Channels} chaînes, {parser.OutOfWindow} hors période, {parser.Invalid} invalides" +
                 (partial ? ", fichier incomplet" : "") + ").");
        progress?.Report(partial
            ? Loc.T("Epg.ImportedPartial", count)
            : Loc.T("Epg.Imported", count));
        return count;
    }

    /// <summary>Flux source ouvert (et réponse HTTP associée à libérer).</summary>
    private sealed class OpenedSource(Stream stream, HttpRequestMessage? request, HttpResponseMessage? response) : IDisposable
    {
        public Stream Stream { get; } = stream;

        public void Dispose()
        {
            Stream.Dispose();
            response?.Dispose();
            request?.Dispose();
        }
    }

    private static async Task<OpenedSource> OpenSourceAsync(string url, string? userAgent, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new ProviderException(Loc.T("Epg.UnsupportedScheme", uri.Scheme));

            var request = Http.Request(url, userAgent);
            HttpResponseMessage? response = null;
            try
            {
                response = await Http.Iptv.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new ProviderException(Loc.T("Epg.DownloadFailed", (int)response.StatusCode));
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new OpenedSource(new BufferedStream(stream, 1 << 16), request, response);
            }
            catch
            {
                response?.Dispose();
                request.Dispose();
                throw;
            }
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : url;
        // Fichier local seulement : un chemin réseau enverrait l'identité Windows de l'utilisateur au serveur.
        if (!PathSafety.IsLocalFile(path)) throw new ProviderException(Loc.T("Epg.UnsupportedScheme", "file"));
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new OpenedSource(file, null, null);
    }

    private static ProviderException ToUserError(Exception ex, bool timedOut)
    {
        if (ex is ProviderException pe) return pe;
        if (timedOut)
            return new ProviderException(Loc.T("Epg.Timeout"), ex);
        return ex switch
        {
            HttpRequestException { StatusCode: { } code } => new ProviderException(Loc.T("Epg.DownloadFailed", (int)code), ex),
            HttpRequestException h => new ProviderException(Loc.T("Epg.DownloadFailed", h.Message), ex),
            XmltvFormatException => new ProviderException(ex.Message, ex),
            XmlException { LineNumber: 0 } => new ProviderException(Loc.T("Epg.EmptyOrUnreadable"), ex),
            XmlException x => new ProviderException(Loc.T("Epg.InvalidAtLine", x.LineNumber), ex),
            InvalidDataException => new ProviderException(Loc.T("Epg.BadCompressed"), ex),
            FileNotFoundException or DirectoryNotFoundException => new ProviderException(Loc.T("Epg.FileNotFound"), ex),
            UnauthorizedAccessException => new ProviderException(Loc.T("Epg.AccessDenied"), ex),
            IOException => new ProviderException(Loc.T("Epg.InterruptedDetail", ex.Message), ex),
            SqliteException s => new ProviderException(Loc.T("Epg.DbError", s.Message), ex),
            OperationCanceledException => new ProviderException(Loc.T("Epg.Interrupted"), ex),
            _ => new ProviderException(Loc.T("Epg.ImportFailed", ex.Message), ex),
        };
    }

    /// <summary>Écrit un flux en cours d'import par lots (une transaction courte par lot, commande préparée réutilisée).</summary>
    private sealed class FeedWriter : IDisposable
    {
        private struct ChannelStat
        {
            public long Min;
            public long Max;
            public int Count;
        }

        private readonly SqliteConnection _c;
        private readonly long _feed;
        private readonly WriteThrottle _throttle;

        private readonly SqliteCommand _insProgram;
        private readonly SqliteParameter _pChannel, _pStart, _pStop, _pTitle, _pNorm, _pDesc, _pCategory, _pIcon, _pEpisode;
        private readonly SqliteCommand _insChannel;
        private readonly SqliteParameter _cId, _cOrd, _cIcon;
        private readonly SqliteCommand _insName;
        private readonly SqliteParameter _nOrd, _nChannel, _nName, _nNorm;

        private readonly List<XmltvProgramme> _programs = new(BatchSize);
        private readonly List<XmltvChannel> _channels = new();
        private readonly HashSet<(string, string)> _names = new();
        private readonly Dictionary<string, string> _normCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ChannelStat> _stats = new(StringComparer.Ordinal);
        private int _channelOrd;
        private int _nameOrd;

        public int Count { get; private set; }
        public long MaxDuration { get; private set; }

        public FeedWriter(SqliteConnection c, long feed, WriteThrottle throttle)
        {
            _c = c;
            _feed = feed;
            _throttle = throttle;

            _insProgram = c.CreateCommand();
            _insProgram.CommandText =
                "INSERT INTO epg_programs(feed, channel, start, stop, title, title_norm, descr, category, icon, episode) " +
                "VALUES ($f, $c, $s, $e, $t, $n, $d, $g, $i, $p)";
            _insProgram.Parameters.AddWithValue("$f", feed);
            _pChannel = _insProgram.Parameters.Add("$c", SqliteType.Text);
            _pStart = _insProgram.Parameters.Add("$s", SqliteType.Integer);
            _pStop = _insProgram.Parameters.Add("$e", SqliteType.Integer);
            _pTitle = _insProgram.Parameters.Add("$t", SqliteType.Text);
            _pNorm = _insProgram.Parameters.Add("$n", SqliteType.Text);
            _pDesc = _insProgram.Parameters.Add("$d", SqliteType.Text);
            _pCategory = _insProgram.Parameters.Add("$g", SqliteType.Text);
            _pIcon = _insProgram.Parameters.Add("$i", SqliteType.Text);
            _pEpisode = _insProgram.Parameters.Add("$p", SqliteType.Text);

            _insChannel = c.CreateCommand();
            _insChannel.CommandText = "INSERT OR IGNORE INTO epg_channels(feed, id, ord, icon, programs) VALUES ($f, $id, $o, $i, 0)";
            _insChannel.Parameters.AddWithValue("$f", feed);
            _cId = _insChannel.Parameters.Add("$id", SqliteType.Text);
            _cOrd = _insChannel.Parameters.Add("$o", SqliteType.Integer);
            _cIcon = _insChannel.Parameters.Add("$i", SqliteType.Text);

            _insName = c.CreateCommand();
            _insName.CommandText = "INSERT OR IGNORE INTO epg_channel_names(feed, ord, channel, name, name_norm) VALUES ($f, $o, $c, $n, $k)";
            _insName.Parameters.AddWithValue("$f", feed);
            _nOrd = _insName.Parameters.Add("$o", SqliteType.Integer);
            _nChannel = _insName.Parameters.Add("$c", SqliteType.Text);
            _nName = _insName.Parameters.Add("$n", SqliteType.Text);
            _nNorm = _insName.Parameters.Add("$k", SqliteType.Text);
        }

        public void AddChannel(XmltvChannel channel)
        {
            _channels.Add(channel);
            if (_channels.Count >= 2000) Flush();
        }

        public void AddProgramme(XmltvProgramme p)
        {
            _programs.Add(p);
            Count++;
            var duration = p.Stop - p.Start;
            if (duration > MaxDuration) MaxDuration = duration;

            ref var st = ref CollectionsMarshal.GetValueRefOrAddDefault(_stats, p.Channel, out var exists);
            if (!exists) { st.Min = p.Start; st.Max = p.Stop; }
            else
            {
                if (p.Start < st.Min) st.Min = p.Start;
                if (p.Stop > st.Max) st.Max = p.Stop;
            }
            st.Count++;

            if (_programs.Count >= BatchSize) Flush();
        }

        /// <summary>Écrit le lot en attente (aucune transaction n'est ouverte entre deux lots, même si le réseau est lent).</summary>
        public void Flush()
        {
            if (_programs.Count == 0 && _channels.Count == 0) return;

            using var tx = _c.BeginTransaction();
            long started = Stopwatch.GetTimestamp();
            _insProgram.Transaction = tx;
            _insChannel.Transaction = tx;
            _insName.Transaction = tx;

            foreach (var ch in _channels)
            {
                _cId.Value = ch.Id;
                _cOrd.Value = _channelOrd++;
                _cIcon.Value = (object?)ch.Icon ?? DBNull.Value;
                _insChannel.ExecuteNonQuery();

                foreach (var name in ch.Names)
                {
                    var norm = NameKey(name);
                    if (norm.Length == 0 || !_names.Add((ch.Id, norm))) continue;
                    _nOrd.Value = _nameOrd++;
                    _nChannel.Value = ch.Id;
                    _nName.Value = name;
                    _nNorm.Value = norm;
                    _insName.ExecuteNonQuery();
                }
            }

            foreach (var p in _programs)
            {
                _pChannel.Value = p.Channel;
                _pStart.Value = p.Start;
                _pStop.Value = p.Stop;
                _pTitle.Value = p.Title;
                _pNorm.Value = Normalized(p.Title);
                _pDesc.Value = (object?)p.Desc ?? DBNull.Value;
                _pCategory.Value = (object?)p.Category ?? DBNull.Value;
                _pIcon.Value = (object?)p.Icon ?? DBNull.Value;
                _pEpisode.Value = (object?)p.EpisodeNum ?? DBNull.Value;
                _insProgram.ExecuteNonQuery();
            }

            tx.Commit();
            _programs.Clear();
            _channels.Clear();
            _throttle.Track(started);
        }

        private string Normalized(string title)
        {
            if (_normCache.TryGetValue(title, out var n)) return n;
            n = TextNorm.Normalize(title);
            if (_normCache.Count >= 100_000) _normCache.Clear();
            _normCache[title] = n;
            return n;
        }

        /// <summary>
        /// Bascule : rend ce flux actif et marque l'ancien obsolète, dans une seule transaction courte.
        /// Renvoie false si le flux a disparu entre-temps (source supprimée).
        /// </summary>
        public bool Activate(string sourceId, long now)
        {
            using var tx = _c.BeginTransaction();
            if (Scalar(_c, tx, "SELECT state FROM epg_feeds WHERE id = $f", ("$f", _feed)) is null)
            {
                tx.Rollback();
                return false;
            }

            // Nombre de programmes par chaîne (sert à préférer les chaînes renseignées lors de la correspondance par nom),
            // y compris pour les chaînes qui n'apparaissent que dans les programmes.
            using (var up = Command(_c, tx,
                       "INSERT INTO epg_channels(feed, id, ord, icon, programs) VALUES ($f, $id, $o, NULL, $n) " +
                       "ON CONFLICT(feed, id) DO UPDATE SET programs = excluded.programs", ("$f", _feed)))
            {
                var pId = up.Parameters.Add("$id", SqliteType.Text);
                var pOrd = up.Parameters.Add("$o", SqliteType.Integer);
                var pN = up.Parameters.Add("$n", SqliteType.Integer);
                int extra = 0;
                foreach (var (id, st) in _stats)
                {
                    pId.Value = id;
                    pOrd.Value = 1_000_000 + extra++;
                    pN.Value = st.Count;
                    up.ExecuteNonQuery();
                }
            }

            // Programmes « fournisseur » désormais couverts par le XMLTV pour la même chaîne : le XMLTV, plus récent, les remplace.
            if (Scalar(_c, tx, "SELECT id FROM epg_feeds WHERE source = $s AND origin = 1 AND state = 1 ORDER BY id DESC LIMIT 1",
                    ("$s", sourceId)) is long provider)
            {
                var providerKeys = LoadChannelSet(_c, tx, provider);
                if (providerKeys.Count > 0)
                {
                    using var del = Command(_c, tx,
                        "DELETE FROM epg_programs WHERE feed = $f AND channel = $c AND start < $hi AND stop > $lo", ("$f", provider));
                    var pc = del.Parameters.Add("$c", SqliteType.Text);
                    var phi = del.Parameters.Add("$hi", SqliteType.Integer);
                    var plo = del.Parameters.Add("$lo", SqliteType.Integer);
                    foreach (var key in providerKeys)
                    {
                        if (!_stats.TryGetValue(key, out var st)) continue;
                        pc.Value = key;
                        phi.Value = st.Max;
                        plo.Value = st.Min;
                        del.ExecuteNonQuery();
                    }
                }
            }

            Exec(_c, tx, "UPDATE epg_feeds SET state = 2 WHERE source = $s AND origin = 0 AND state = 1", ("$s", sourceId));
            Exec(_c, tx, "UPDATE epg_feeds SET state = 1, imported = $now, programs = $n, maxdur = $d WHERE id = $f",
                ("$now", now), ("$n", Count), ("$d", MaxDuration), ("$f", _feed));
            tx.Commit();
            return true;
        }

        public void Dispose()
        {
            _insProgram.Dispose();
            _insChannel.Dispose();
            _insName.Dispose();
        }
    }

    // =====================================================================================
    // Correspondance chaînes ↔ guide
    // =====================================================================================

    /// <summary>
    /// Renseigne <see cref="LiveChannel.EpgKey"/> (toujours en minuscules) : identifiant EPG présent dans le XMLTV,
    /// sinon correspondance par nom, sinon identifiant EPG, sinon « ch:&lt;id&gt; ».
    /// </summary>
    public void ResolveKeys(string sourceId, IEnumerable<LiveChannel> channels)
    {
        HashSet<string>? ids = null;
        Dictionary<string, string>? exact = null, loose = null, compact = null;
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            using var tx = c.BeginTransaction(deferred: true);
            var feeds = LoadFeeds(c, tx, sourceId);
            if (feeds.Xmltv != 0)
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                using (var cmd = Command(c, tx, "SELECT id FROM epg_channels WHERE feed = $f", ("$f", feeds.Xmltv)))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) ids.Add(r.GetString(0));

                exact = new Dictionary<string, string>(StringComparer.Ordinal);
                loose = new Dictionary<string, string>(StringComparer.Ordinal);
                compact = new Dictionary<string, string>(StringComparer.Ordinal);
                // Chaînes qui ont des programmes d'abord, puis ordre du fichier : le premier nom rencontré l'emporte.
                using (var cmd = Command(c, tx,
                           "SELECT n.channel, n.name_norm FROM epg_channel_names n " +
                           "LEFT JOIN epg_channels ch ON ch.feed = n.feed AND ch.id = n.channel " +
                           "WHERE n.feed = $f ORDER BY (IFNULL(ch.programs, 0) > 0) DESC, n.ord", ("$f", feeds.Xmltv)))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var id = r.GetString(0);
                        var k1 = r.GetString(1);
                        if (k1.Length == 0) continue;
                        exact.TryAdd(k1, id);
                        var k2 = StripQuality(k1);
                        loose.TryAdd(k2, id);
                        var k3 = k2.Replace(" ", "", StringComparison.Ordinal);
                        if (k3.Length > 0) compact.TryAdd(k3, id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : chargement des chaînes XMLTV impossible : {ex.Message}");
        }

        foreach (var ch in channels)
        {
            var epgId = ch.EpgId?.Trim();
            var epgLower = string.IsNullOrEmpty(epgId) ? null : epgId.ToLowerInvariant();
            string? key = null;

            // 1) identifiant EPG connu du XMLTV
            if (epgLower is not null && ids is not null && ids.Contains(epgLower)) key = epgLower;

            // 2) nom de la chaîne : exact, sans marqueurs de qualité, puis sans espaces
            if (key is null && exact is { Count: > 0 } && !string.IsNullOrWhiteSpace(ch.Name))
            {
                var k1 = NameKey(ch.Name);
                if (k1.Length > 0 && !exact.TryGetValue(k1, out key))
                {
                    var k2 = StripQuality(k1);
                    if (!loose!.TryGetValue(k2, out key))
                    {
                        var k3 = k2.Replace(" ", "", StringComparison.Ordinal);
                        if (k3.Length > 0) compact!.TryGetValue(k3, out key);
                    }
                }
            }

            // 3) identifiant EPG tel quel, 4) identifiant de la chaîne (programmes obtenus auprès du fournisseur)
            ch.EpgKey = key ?? epgLower ?? "ch:" + ch.Id.Trim().ToLowerInvariant();
        }
    }

    private static readonly Regex PlusRegex = new(@"\+\s*(\d{1,2})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> QualityTokens = new(StringComparer.Ordinal)
    {
        "hd", "fhd", "uhd", "qhd", "4k", "8k", "sd", "hq", "lq", "hdr", "hevc", "h265", "h264", "x265", "x264",
        "25fps", "50fps", "60fps", "1080p", "1080i", "720p", "2160p", "576p", "576i", "backup",
    };

    /// <summary>
    /// Clé de correspondance d'un nom de chaîne : préfixe pays retiré, formes de compatibilité Unicode
    /// (« ᴴᴰ » → « HD »), « +1 » conservé distinctement (« plus1 »), puis <see cref="TextNorm.Normalize"/>.
    /// </summary>
    internal static string NameKey(string name)
    {
        var s = TextNorm.StripPrefix(name);
        try { s = s.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { /* chaîne Unicode invalide : laissée telle quelle */ }
        if (s.Contains('+')) s = PlusRegex.Replace(s, " plus$1 ");
        return TextNorm.Normalize(s);
    }

    /// <summary>Retire les mots de qualité (hd, fhd, 4k, hevc, 50fps, backup…) d'une clé normalisée.</summary>
    internal static string StripQuality(string key)
    {
        if (key.Length == 0) return key;
        var parts = key.Split(' ');
        var sb = new StringBuilder(key.Length);
        bool removed = false;
        foreach (var p in parts)
        {
            if (QualityTokens.Contains(p) || (p.StartsWith("backup", StringComparison.Ordinal) && p.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0))
            {
                removed = true;
                continue;
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p);
        }
        return !removed || sb.Length == 0 ? key : sb.ToString();
    }

    // =====================================================================================
    // Lecture
    // =====================================================================================

    /// <summary>Programme en cours et suivant pour chaque clé à l'instant <paramref name="at"/>. Les clés sans programme sont absentes.</summary>
    public Dictionary<string, (EpgProgram? Now, EpgProgram? Next)> GetNowNext(string sourceId, IReadOnlyCollection<string> keys, DateTimeOffset at)
    {
        var result = new Dictionary<string, (EpgProgram? Now, EpgProgram? Next)>(StringComparer.OrdinalIgnoreCase);
        if (keys is null || keys.Count == 0) return result;
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            using var tx = c.BeginTransaction(deferred: true);
            var feeds = LoadFeeds(c, tx, sourceId);
            if (feeds.IsEmpty) return result;

            var filter = keys.Count > KeySetThreshold ? KeyFilter.Load(c, tx, feeds) : null;
            long t = at.ToUnixTimeSeconds();
            using var q = new ProgramQuery(c, tx);
            var buffer = new List<EpgProgram>(4);
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key)) continue;
                buffer.Clear();
                Fetch(q, feeds, filter, key.ToLowerInvariant(), t, long.MaxValue, 2, buffer);
                if (buffer.Count == 0) continue;
                var first = buffer[0];
                result[key] = first.Start.ToUnixTimeSeconds() <= t
                    ? (first, buffer.Count > 1 ? buffer[1] : null)
                    : (null, first);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : lecture en cours/à suivre impossible : {ex.Message}");
        }
        return result;
    }

    /// <summary>Programmes d'une chaîne qui chevauchent [from ; to), triés par début.</summary>
    public List<EpgProgram> GetPrograms(string sourceId, string key, DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<EpgProgram>();
        if (string.IsNullOrWhiteSpace(key) || to <= from) return list;
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            using var tx = c.BeginTransaction(deferred: true);
            var feeds = LoadFeeds(c, tx, sourceId);
            if (feeds.IsEmpty) return list;
            using var q = new ProgramQuery(c, tx);
            Fetch(q, feeds, null, key.ToLowerInvariant(), from.ToUnixTimeSeconds(), to.ToUnixTimeSeconds(), -1, list);
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : lecture des programmes impossible : {ex.Message}");
        }
        return list;
    }

    /// <summary>Programmes de plusieurs chaînes qui chevauchent [from ; to), triés par début. Les clés sans programme sont absentes.</summary>
    public Dictionary<string, List<EpgProgram>> GetPrograms(string sourceId, IReadOnlyCollection<string> keys, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
        if (keys is null || keys.Count == 0 || to <= from) return result;
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            using var tx = c.BeginTransaction(deferred: true);
            var feeds = LoadFeeds(c, tx, sourceId);
            if (feeds.IsEmpty) return result;

            var filter = keys.Count > KeySetThreshold ? KeyFilter.Load(c, tx, feeds) : null;
            long f = from.ToUnixTimeSeconds(), t = to.ToUnixTimeSeconds();
            using var q = new ProgramQuery(c, tx);
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key)) continue;
                var list = new List<EpgProgram>();
                Fetch(q, feeds, filter, key.ToLowerInvariant(), f, t, -1, list);
                if (list.Count > 0) result[key] = list;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : lecture des programmes impossible : {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Programmes dont le titre contient tous les mots de <paramref name="query"/> (sans accents ni casse),
    /// non terminés à <paramref name="from"/>, triés par début, au plus <paramref name="limit"/>.
    /// </summary>
    public List<EpgProgram> Search(string sourceId, string query, DateTimeOffset from, int limit)
    {
        var list = new List<EpgProgram>();
        var tokens = TextNorm.Tokens(query).Distinct(StringComparer.Ordinal).Take(MaxSearchTokens).ToArray();
        if (tokens.Length == 0 || limit <= 0) return list;
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            using var tx = c.BeginTransaction(deferred: true);
            var feeds = LoadFeeds(c, tx, sourceId);
            if (feeds.IsEmpty) return list;

            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            var sql = new StringBuilder("SELECT channel, ").Append(ProgramColumns).Append(" FROM epg_programs WHERE feed IN (");
            if (feeds.Xmltv != 0 && feeds.Provider != 0) sql.Append("$f0, $f1");
            else sql.Append("$f0");
            sql.Append(") AND stop > $from");
            cmd.Parameters.AddWithValue("$f0", feeds.Xmltv != 0 ? feeds.Xmltv : feeds.Provider);
            if (feeds.Xmltv != 0 && feeds.Provider != 0) cmd.Parameters.AddWithValue("$f1", feeds.Provider);
            cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            for (int i = 0; i < tokens.Length; i++)
            {
                sql.Append(" AND instr(title_norm, $t").Append(i).Append(") > 0");
                cmd.Parameters.AddWithValue("$t" + i, tokens[i]);
            }
            sql.Append(" ORDER BY start, channel LIMIT $lim");
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.CommandText = sql.ToString();

            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadProgram(r, r.GetString(0), 1));
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : recherche impossible : {ex.Message}");
        }
        return list;
    }

    /// <summary>Date du dernier import XMLTV réussi de la source.</summary>
    public DateTimeOffset? LastImport(string sourceId)
    {
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            var v = Scalar(c, null,
                "SELECT imported FROM epg_feeds WHERE source = $s AND origin = 0 AND state = 1 AND imported IS NOT NULL ORDER BY id DESC LIMIT 1",
                ("$s", sourceId));
            return v is long t ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : lecture de la date d'import impossible : {ex.Message}");
            return null;
        }
    }

    // =====================================================================================
    // Écriture directe, suppression, purge
    // =====================================================================================

    /// <summary>
    /// Enregistre des programmes obtenus auprès du fournisseur pour une clé : remplace les programmes existants
    /// de cette clé (toutes origines) qui chevauchent la période couverte. Non effacés par l'import XMLTV suivant.
    /// </summary>
    public void Upsert(string sourceId, string key, IReadOnlyList<EpgProgram> programs)
    {
        if (string.IsNullOrWhiteSpace(key) || programs is null || programs.Count == 0) return;
        var k = key.Trim().ToLowerInvariant();

        var rows = new List<(long Start, long Stop, EpgProgram P)>(programs.Count);
        foreach (var p in programs)
        {
            if (p is null) continue;
            long s = p.Start.ToUnixTimeSeconds(), e = p.End.ToUnixTimeSeconds();
            if (e > s) rows.Add((s, e, p));
        }
        if (rows.Count == 0) return;
        rows.Sort((a, b) => a.Start.CompareTo(b.Start));
        long lo = rows.Min(x => x.Start), hi = rows.Max(x => x.Stop), maxDur = rows.Max(x => x.Stop - x.Start);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            EnsureSchema();
            using var c = Db.Open();
            using var tx = c.BeginTransaction();

            long provider = Scalar(c, tx, "SELECT id FROM epg_feeds WHERE source = $s AND origin = 1 AND state = 1 ORDER BY id DESC LIMIT 1",
                ("$s", sourceId)) as long? ?? (long)Scalar(c, tx,
                "INSERT INTO epg_feeds(source, origin, state, created) VALUES ($s, 1, 1, $now); SELECT last_insert_rowid();",
                ("$s", sourceId), ("$now", now))!;
            var xmltv = Scalar(c, tx, "SELECT id FROM epg_feeds WHERE source = $s AND origin = 0 AND state = 1 ORDER BY id DESC LIMIT 1",
                ("$s", sourceId)) as long?;

            using (var del = Command(c, tx, "DELETE FROM epg_programs WHERE feed = $f AND channel = $c AND start < $hi AND stop > $lo",
                       ("$c", k), ("$hi", hi), ("$lo", lo)))
            {
                var pf = del.Parameters.Add("$f", SqliteType.Integer);
                pf.Value = provider;
                del.ExecuteNonQuery();
                if (xmltv is { } x)
                {
                    pf.Value = x;
                    del.ExecuteNonQuery();
                }
            }

            using (var ins = Command(c, tx,
                       "INSERT INTO epg_programs(feed, channel, start, stop, title, title_norm, descr, category, icon, episode) " +
                       "VALUES ($f, $c, $s, $e, $t, $n, $d, $g, $i, $p)", ("$f", provider), ("$c", k)))
            {
                var ps = ins.Parameters.Add("$s", SqliteType.Integer);
                var pe = ins.Parameters.Add("$e", SqliteType.Integer);
                var pt = ins.Parameters.Add("$t", SqliteType.Text);
                var pn = ins.Parameters.Add("$n", SqliteType.Text);
                var pd = ins.Parameters.Add("$d", SqliteType.Text);
                var pg = ins.Parameters.Add("$g", SqliteType.Text);
                var pi = ins.Parameters.Add("$i", SqliteType.Text);
                var pp = ins.Parameters.Add("$p", SqliteType.Text);
                foreach (var (s, e, p) in rows)
                {
                    var title = p.Title?.Trim() ?? "";
                    ps.Value = s;
                    pe.Value = e;
                    pt.Value = title;
                    pn.Value = TextNorm.Normalize(title);
                    pd.Value = (object?)NullIfBlank(p.Description) ?? DBNull.Value;
                    pg.Value = (object?)NullIfBlank(p.Category) ?? DBNull.Value;
                    pi.Value = (object?)NullIfBlank(p.IconUrl) ?? DBNull.Value;
                    pp.Value = (object?)NullIfBlank(p.EpisodeNum) ?? DBNull.Value;
                    ins.ExecuteNonQuery();
                }
            }

            Exec(c, tx, "UPDATE epg_feeds SET maxdur = MAX(maxdur, $d), imported = $now, " +
                        "programs = (SELECT COUNT(*) FROM epg_programs WHERE feed = $f) WHERE id = $f",
                ("$d", maxDur), ("$now", now), ("$f", provider));
            tx.Commit();
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : enregistrement des programmes de « {k} » impossible : {ex.Message}");
        }
    }

    /// <summary>Supprime tout le guide d'une source (XMLTV et programmes fournisseur).</summary>
    public void DeleteSource(string sourceId)
    {
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            DeleteFeeds(c, new WriteThrottle(), "SELECT id FROM epg_feeds WHERE source = $s", ("$s", sourceId));
        }
        catch (Exception ex)
        {
            Log.Warn($"Guide TV ({sourceId}) : suppression impossible : {ex.Message}");
        }
    }

    /// <summary>Supprime les programmes terminés depuis plus de 2 jours et les restes d'imports interrompus.</summary>
    public void PurgeOld()
    {
        try
        {
            EnsureSchema();
            using var c = Db.Open();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var throttle = new WriteThrottle();

            var feeds = new List<(long Id, int State, long Created)>();
            using (var cmd = Command(c, null, "SELECT id, state, created FROM epg_feeds"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) feeds.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt64(2)));

            int deletedRows = 0;
            using (var del = Command(c, null,
                       $"DELETE FROM epg_programs WHERE rowid IN (SELECT rowid FROM epg_programs WHERE feed = $f AND stop < $cut LIMIT {DeleteChunk})",
                       ("$cut", now - PurgeAgeSeconds)))
            {
                var pf = del.Parameters.Add("$f", SqliteType.Integer);
                foreach (var (id, state, created) in feeds)
                {
                    bool orphan = state == StateObsolete
                                  || (state == StateBuilding && created < now - OrphanFeedSeconds && !BuildingFeeds.ContainsKey(id));
                    if (orphan)
                    {
                        DeleteFeed(c, id, throttle);
                        continue;
                    }
                    pf.Value = id;
                    int n;
                    do
                    {
                        long started = Stopwatch.GetTimestamp();
                        n = del.ExecuteNonQuery();
                        throttle.Track(started);
                        deletedRows += n;
                    } while (n > 0);
                }
            }

            // Lignes dont le flux n'existe plus (suppression de source pendant un import, par exemple).
            var known = feeds.Select(f => f.Id).ToHashSet();
            var orphans = new List<long>();
            using (var cmd = Command(c, null, "SELECT DISTINCT feed FROM epg_programs"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var id = r.GetInt64(0);
                    if (!known.Contains(id) && !BuildingFeeds.ContainsKey(id)) orphans.Add(id);
                }
            foreach (var id in orphans) DeleteFeed(c, id, throttle);

            if (deletedRows > 0) Log.Info($"Guide TV : {deletedRows} programmes anciens supprimés.");
        }
        catch (Exception ex)
        {
            Log.Warn("Guide TV : purge impossible : " + ex.Message);
        }
    }

    // =====================================================================================
    // Outils internes
    // =====================================================================================

    private static void EnsureSchema()
    {
        if (Volatile.Read(ref _schemaReady) != 0) return;
        lock (SchemaGate)
        {
            if (_schemaReady != 0) return;
            using var c = Db.Open();
            using var tx = c.BeginTransaction();
            Exec(c, tx, "CREATE TABLE IF NOT EXISTS epg_meta(k TEXT PRIMARY KEY, v TEXT NOT NULL) WITHOUT ROWID;");
            var version = Scalar(c, tx, "SELECT v FROM epg_meta WHERE k = 'schema'") as string;
            var expected = SchemaVersion.ToString(CultureInfo.InvariantCulture);
            if (version != expected)
            {
                // Le guide n'est qu'un cache : en cas de changement de schéma, on repart de zéro.
                if (version is not null) Log.Info($"Guide TV : schéma {version} → {expected}, guide réinitialisé.");
                Exec(c, tx, DropSql);
                Exec(c, tx, "INSERT OR REPLACE INTO epg_meta(k, v) VALUES ('schema', $v)", ("$v", expected));
            }
            Exec(c, tx, CreateSql);
            tx.Commit();
            Volatile.Write(ref _schemaReady, 1);
        }
    }

    /// <summary>Flux actifs d'une source (0 = aucun).</summary>
    private readonly record struct Feeds(long Xmltv, long XmltvMaxDuration, long Provider, long ProviderMaxDuration)
    {
        public bool IsEmpty => Xmltv == 0 && Provider == 0;
    }

    private static Feeds LoadFeeds(SqliteConnection c, SqliteTransaction? tx, string sourceId)
    {
        long x = 0, xd = 0, p = 0, pd = 0;
        using var cmd = Command(c, tx, "SELECT id, origin, maxdur FROM epg_feeds WHERE source = $s AND state = 1 ORDER BY id", ("$s", sourceId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetInt32(1) == OriginXmltv) { x = r.GetInt64(0); xd = r.GetInt64(2); }
            else if (r.GetInt32(1) == OriginProvider) { p = r.GetInt64(0); pd = r.GetInt64(2); }
        }
        return new Feeds(x, xd, p, pd);
    }

    private static HashSet<string> LoadChannelSet(SqliteConnection c, SqliteTransaction? tx, long feed)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = Command(c, tx, "SELECT DISTINCT channel FROM epg_programs WHERE feed = $f", ("$f", feed));
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>
    /// Programmes d'une clé non terminés à <paramref name="from"/> et commençant avant <paramref name="to"/>,
    /// pour le flux XMLTV puis le flux fournisseur, fusionnés par heure de début.
    /// </summary>
    private static void Fetch(ProgramQuery q, Feeds feeds, KeyFilter? filter, string key, long from, long to, int limit, List<EpgProgram> into)
    {
        int before = into.Count;
        if (feeds.Xmltv != 0 && (filter?.Xmltv is null || filter.Xmltv.Contains(key)))
            q.Run(feeds.Xmltv, key, from - feeds.XmltvMaxDuration, to, from, limit, into);
        if (feeds.Provider != 0 && (filter?.Provider is null || filter.Provider.Contains(key)))
        {
            int mid = into.Count;
            q.Run(feeds.Provider, key, from - feeds.ProviderMaxDuration, to, from, limit, into);
            if (mid > before && into.Count > mid)
            {
                into.Sort(before, into.Count - before, ProgramByStart.Instance);
                if (limit > 0 && into.Count - before > limit) into.RemoveRange(before + limit, into.Count - before - limit);
            }
        }
    }

    /// <summary>Chaînes qui ont des programmes dans chaque flux : évite une requête par clé inconnue quand on interroge des milliers de clés.</summary>
    private sealed class KeyFilter
    {
        public HashSet<string>? Xmltv { get; private init; }
        public HashSet<string>? Provider { get; private init; }

        public static KeyFilter Load(SqliteConnection c, SqliteTransaction tx, Feeds feeds)
        {
            HashSet<string>? xmltv = null;
            if (feeds.Xmltv != 0)
            {
                xmltv = new HashSet<string>(StringComparer.Ordinal);
                using var cmd = Command(c, tx, "SELECT id FROM epg_channels WHERE feed = $f AND programs > 0", ("$f", feeds.Xmltv));
                using var r = cmd.ExecuteReader();
                while (r.Read()) xmltv.Add(r.GetString(0));
            }
            return new KeyFilter
            {
                Xmltv = xmltv,
                Provider = feeds.Provider != 0 ? LoadChannelSet(c, tx, feeds.Provider) : null,
            };
        }
    }

    private sealed class ProgramByStart : IComparer<EpgProgram>
    {
        public static readonly ProgramByStart Instance = new();
        public int Compare(EpgProgram? a, EpgProgram? b) => a!.Start.CompareTo(b!.Start);
    }

    /// <summary>
    /// Requête préparée réutilisée pour chaque clé : l'index (feed, channel, start, stop) fournit l'ordre
    /// et filtre la fin sans lire la table ; seules les lignes retenues sont lues.
    /// </summary>
    private sealed class ProgramQuery : IDisposable
    {
        private readonly SqliteCommand _cmd;
        private readonly SqliteParameter _feed, _channel, _lo, _hi, _from, _limit;

        public ProgramQuery(SqliteConnection c, SqliteTransaction tx)
        {
            _cmd = c.CreateCommand();
            _cmd.Transaction = tx;
            _cmd.CommandText = $"SELECT {ProgramColumns} FROM epg_programs " +
                               "WHERE feed = $f AND channel = $c AND start >= $lo AND start < $hi AND stop > $from " +
                               "ORDER BY start LIMIT $lim";
            _feed = _cmd.Parameters.Add("$f", SqliteType.Integer);
            _channel = _cmd.Parameters.Add("$c", SqliteType.Text);
            _lo = _cmd.Parameters.Add("$lo", SqliteType.Integer);
            _hi = _cmd.Parameters.Add("$hi", SqliteType.Integer);
            _from = _cmd.Parameters.Add("$from", SqliteType.Integer);
            _limit = _cmd.Parameters.Add("$lim", SqliteType.Integer);
        }

        public void Run(long feed, string key, long lo, long hi, long from, int limit, List<EpgProgram> into)
        {
            _feed.Value = feed;
            _channel.Value = key;
            _lo.Value = lo;
            _hi.Value = hi;
            _from.Value = from;
            _limit.Value = limit;
            using var r = _cmd.ExecuteReader();
            while (r.Read()) into.Add(ReadProgram(r, key, 0));
        }

        public void Dispose() => _cmd.Dispose();
    }

    private static EpgProgram ReadProgram(SqliteDataReader r, string key, int offset) => new()
    {
        ChannelKey = key,
        Start = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(offset)),
        End = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(offset + 1)),
        Title = r.GetString(offset + 2),
        Description = r.GetStringOrNull(offset + 3),
        Category = r.GetStringOrNull(offset + 4),
        IconUrl = r.GetStringOrNull(offset + 5),
        EpisodeNum = r.GetStringOrNull(offset + 6),
    };

    /// <summary>Supprime les flux renvoyés par <paramref name="selectIds"/> (lignes par lots, puis chaînes et flux).</summary>
    private static void DeleteFeeds(SqliteConnection c, WriteThrottle throttle, string selectIds, params (string Name, object? Value)[] args)
    {
        var ids = new List<long>();
        using (var cmd = Command(c, null, selectIds, args))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) ids.Add(r.GetInt64(0));
        foreach (var id in ids) DeleteFeed(c, id, throttle);
    }

    private static void DeleteFeed(SqliteConnection c, long feed, WriteThrottle throttle)
    {
        using (var cmd = Command(c, null,
                   $"DELETE FROM epg_programs WHERE rowid IN (SELECT rowid FROM epg_programs WHERE feed = $f LIMIT {DeleteChunk})",
                   ("$f", feed)))
        {
            while (true)
            {
                long started = Stopwatch.GetTimestamp();
                int n = cmd.ExecuteNonQuery();
                throttle.Track(started);
                if (n == 0) break;
            }
        }
        Exec(c, null,
            "DELETE FROM epg_channel_names WHERE feed = $f; DELETE FROM epg_channels WHERE feed = $f; DELETE FROM epg_feeds WHERE id = $f;",
            ("$f", feed));
    }

    private static void TryDeleteFeed(SqliteConnection c, long feed, WriteThrottle throttle)
    {
        try { DeleteFeed(c, feed, throttle); }
        catch (Exception ex) { Log.Warn($"Guide TV : nettoyage d'un import interrompu différé : {ex.Message}"); }
    }

    /// <summary>
    /// Évite de monopoliser le verrou d'écriture pendant les longues opérations (import, suppression d'un guide).
    /// Microsoft.Data.Sqlite réessaie environ toutes les 150 ms quand la base est occupée : après ~400 ms d'écriture
    /// quasi continue, on laisse la base libre un peu plus longtemps que ce délai pour que les autres écritures
    /// de l'application (reprise de lecture, favoris…) passent.
    /// </summary>
    private sealed class WriteThrottle
    {
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(170);
        private TimeSpan _used;
        private long _lastEnd;

        /// <summary>À appeler après chaque transaction d'écriture, avec l'horodatage de son début.</summary>
        public void Track(long started)
        {
            long end = Stopwatch.GetTimestamp();
            // Une pause suffisante a déjà eu lieu depuis la dernière écriture (réseau lent, analyse) : compteur remis à zéro.
            if (_lastEnd != 0 && Stopwatch.GetElapsedTime(_lastEnd, started) >= Pause) _used = TimeSpan.Zero;
            _used += Stopwatch.GetElapsedTime(started, end);
            _lastEnd = end;
            if (_used < Budget) return;
            Thread.Sleep(Pause);
            _used = TimeSpan.Zero;
            _lastEnd = Stopwatch.GetTimestamp();
        }
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private static int Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
