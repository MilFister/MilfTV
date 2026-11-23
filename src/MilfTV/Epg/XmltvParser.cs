using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using MilfTV.Localization;

namespace MilfTV.Epg;

/// <summary>Chaîne déclarée dans un fichier XMLTV (&lt;channel&gt;).</summary>
internal sealed class XmltvChannel(string id)
{
    /// <summary>Identifiant en minuscules (invariant).</summary>
    public string Id { get; } = id;
    public List<string> Names { get; } = new(2);
    public string? Icon { get; set; }
}

/// <summary>Programme lu dans un fichier XMLTV (&lt;programme&gt;). Heures en secondes Unix (UTC).</summary>
internal sealed class XmltvProgramme
{
    /// <summary>Identifiant de chaîne en minuscules (invariant).</summary>
    public string Channel = "";
    public long Start;
    public long Stop;
    public string Title = "";
    public string? Desc;
    public string? Category;
    public string? Icon;
    public string? EpisodeNum;
}

/// <summary>Fichier lisible mais qui n'est pas un XMLTV exploitable (message destiné à l'utilisateur).</summary>
internal sealed class XmltvFormatException(string message) : Exception(message);

/// <summary>
/// Lecteur XMLTV en flux (XmlReader asynchrone) : rien n'est chargé en entier en mémoire.
/// Les programmes hors de la fenêtre [minStop ; maxStart] sont sautés sans lire leur contenu.
/// Les éléments sont transmis au fil de l'eau par <see cref="ChannelFound"/> et <see cref="ProgrammeFound"/>.
/// </summary>
internal sealed class XmltvParser
{
    private const long DefaultDuration = 3600;
    private const long MaxGapForMissingStop = 24 * 3600;

    private static readonly long UnixEpochDay = new DateOnly(1970, 1, 1).DayNumber;

    static XmltvParser()
    {
        // Certains fichiers déclarent encoding="windows-1252" : nécessite le fournisseur de pages de codes.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* ignoré */ }
    }

    private readonly long _minStop;
    private readonly long _maxStart;
    private readonly CancellationToken _ct;
    private readonly Dictionary<string, XmltvProgramme> _pendingStop = new(StringComparer.Ordinal);
    private string? _lastRawChannel;
    private string _lastChannel = "";

    // Noms atomisés (comparaison par référence, plus rapide qu'une comparaison de chaînes).
    private object _nProgramme = "", _nChannel = "", _nTitle = "", _nDesc = "", _nCategory = "",
        _nIcon = "", _nEpisode = "", _nDisplayName = "";

    /// <param name="minStop">Les programmes dont la fin est ≤ minStop sont ignorés.</param>
    /// <param name="maxStart">Les programmes dont le début est ≥ maxStart sont ignorés.</param>
    public XmltvParser(long minStop, long maxStart, CancellationToken ct)
    {
        _minStop = minStop;
        _maxStart = maxStart;
        _ct = ct;
    }

    public Action<XmltvChannel>? ChannelFound { get; init; }
    public Action<XmltvProgramme>? ProgrammeFound { get; init; }

    /// <summary>Appelé tous les <see cref="ProgressInterval"/> programmes lus (nombre lus jusqu'ici).</summary>
    public Action<int>? Progress { get; init; }
    public int ProgressInterval { get; init; } = 5000;

    /// <summary>Éléments &lt;programme&gt; rencontrés.</summary>
    public int Seen { get; private set; }
    /// <summary>Programmes transmis à <see cref="ProgrammeFound"/>.</summary>
    public int Kept { get; private set; }
    /// <summary>Programmes ignorés (heures illisibles, fin ≤ début, sans titre).</summary>
    public int Invalid { get; private set; }
    /// <summary>Programmes ignorés car hors de la fenêtre conservée.</summary>
    public int OutOfWindow { get; private set; }
    public int Channels { get; private set; }

    // ------------------------------------------------------------------ décompression

    /// <summary>
    /// Détecte la compression par les octets magiques (pas seulement l'extension) et renvoie le flux décodé.
    /// gzip (1F 8B) est décompressé, éventuellement imbriqué ; ZIP et xz sont refusés avec un message clair.
    /// Les lectures asynchrones du flux renvoyé utilisent <paramref name="ct"/>.
    /// </summary>
    public static async Task<Stream> DecodeAsync(Stream input, CancellationToken ct)
    {
        Stream current = input;
        for (int level = 0; level < 3; level++)
        {
            var peek = new PeekStream(current, ct);
            var head = await peek.PeekAsync(2).ConfigureAwait(false);
            if (head.Length == 2)
            {
                var b0 = head.Span[0];
                var b1 = head.Span[1];
                if (b0 == 0x1F && b1 == 0x8B)
                {
                    current = new GZipStream(peek, CompressionMode.Decompress, leaveOpen: false);
                    continue;
                }
                if (b0 == (byte)'P' && b1 == (byte)'K')
                {
                    peek.Dispose();
                    throw new XmltvFormatException(Loc.T("Epg.ZipUnsupported"));
                }
                if (b0 == 0xFD && b1 == 0x37)
                {
                    peek.Dispose();
                    throw new XmltvFormatException(Loc.T("Epg.XzUnsupported"));
                }
            }
            return peek;
        }
        current.Dispose();
        throw new XmltvFormatException(Loc.T("Epg.MultiCompressed"));
    }

    // ------------------------------------------------------------------ lecture

    public async Task ParseAsync(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        using var r = XmlReader.Create(stream, settings);
        var nt = r.NameTable;
        _nProgramme = nt.Add("programme");
        _nChannel = nt.Add("channel");
        _nTitle = nt.Add("title");
        _nDesc = nt.Add("desc");
        _nCategory = nt.Add("category");
        _nIcon = nt.Add("icon");
        _nEpisode = nt.Add("episode-num");
        _nDisplayName = nt.Add("display-name");

        if (await r.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element
            || !string.Equals(r.LocalName, "tv", StringComparison.OrdinalIgnoreCase))
            throw new XmltvFormatException(Loc.T("Epg.NotXmltv"));

        if (!r.IsEmptyElement)
        {
            int rootDepth = r.Depth;
            await r.ReadAsync().ConfigureAwait(false);
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    object name = r.LocalName;
                    if (name == _nProgramme) await ReadProgrammeAsync(r).ConfigureAwait(false);
                    else if (name == _nChannel) await ReadChannelAsync(r).ConfigureAwait(false);
                    else await r.SkipAsync().ConfigureAwait(false);
                    continue;
                }
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == rootDepth) break;
                await r.ReadAsync().ConfigureAwait(false);
            }
        }

        // Programmes restés sans heure de fin (dernier de leur chaîne) : durée par défaut.
        foreach (var p in _pendingStop.Values)
        {
            p.Stop = p.Start + DefaultDuration;
            Emit(p);
        }
        _pendingStop.Clear();
    }

    private async Task ReadChannelAsync(XmlReader r)
    {
        var rawId = r.GetAttribute("id");
        if (string.IsNullOrWhiteSpace(rawId))
        {
            await r.SkipAsync().ConfigureAwait(false);
            return;
        }

        var ch = new XmltvChannel(rawId.Trim().ToLowerInvariant());
        if (r.IsEmptyElement)
        {
            await r.ReadAsync().ConfigureAwait(false);
        }
        else
        {
            int depth = r.Depth;
            await r.ReadAsync().ConfigureAwait(false);
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth)
                {
                    await r.ReadAsync().ConfigureAwait(false);
                    break;
                }
                if (r.NodeType != XmlNodeType.Element)
                {
                    await r.ReadAsync().ConfigureAwait(false);
                    continue;
                }

                object name = r.LocalName;
                if (name == _nDisplayName)
                {
                    var text = (await ReadTextAsync(r).ConfigureAwait(false)).Trim();
                    if (text.Length > 0) ch.Names.Add(text);
                }
                else if (name == _nIcon)
                {
                    ch.Icon ??= NullIfBlank(r.GetAttribute("src"));
                    await r.SkipAsync().ConfigureAwait(false);
                }
                else
                {
                    await r.SkipAsync().ConfigureAwait(false);
                }
            }
        }

        Channels++;
        ChannelFound?.Invoke(ch);
    }

    private async Task ReadProgrammeAsync(XmlReader r)
    {
        Seen++;
        if (Seen % ProgressInterval == 0) Progress?.Invoke(Seen);
        if ((Seen & 1023) == 0) _ct.ThrowIfCancellationRequested();

        var rawChannel = r.GetAttribute("channel");
        if (string.IsNullOrWhiteSpace(rawChannel) || !TryParseTime(r.GetAttribute("start"), out var start))
        {
            Invalid++;
            await r.SkipAsync().ConfigureAwait(false);
            return;
        }
        var channel = ChannelKey(rawChannel);

        // Le programme précédent de cette chaîne n'avait pas d'heure de fin : il se termine quand celui-ci commence.
        if (_pendingStop.Remove(channel, out var previous))
        {
            var gap = start - previous.Start;
            previous.Stop = gap > 0 && gap <= MaxGapForMissingStop ? start : previous.Start + DefaultDuration;
            Emit(previous);
        }

        var rawStop = r.GetAttribute("stop");
        bool hasStop = !string.IsNullOrWhiteSpace(rawStop);
        long stop = 0;
        if (hasStop)
        {
            if (!TryParseTime(rawStop, out stop) || stop <= start)
            {
                Invalid++;
                await r.SkipAsync().ConfigureAwait(false);
                return;
            }
            if (stop <= _minStop || start >= _maxStart)
            {
                OutOfWindow++;
                await r.SkipAsync().ConfigureAwait(false);
                return;
            }
        }
        else if (start >= _maxStart)
        {
            OutOfWindow++;
            await r.SkipAsync().ConfigureAwait(false);
            return;
        }

        var p = new XmltvProgramme { Channel = channel, Start = start, Stop = stop };
        if (r.IsEmptyElement)
        {
            await r.ReadAsync().ConfigureAwait(false);
        }
        else
        {
            bool titleFr = false, descFr = false, onscreen = false;
            int depth = r.Depth;
            await r.ReadAsync().ConfigureAwait(false);
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth)
                {
                    await r.ReadAsync().ConfigureAwait(false);
                    break;
                }
                if (r.NodeType != XmlNodeType.Element)
                {
                    await r.ReadAsync().ConfigureAwait(false);
                    continue;
                }

                object name = r.LocalName;
                if (name == _nTitle)
                {
                    bool fr = IsFrench(r.GetAttribute("lang"));
                    if (p.Title.Length == 0 || (fr && !titleFr))
                    {
                        var text = (await ReadTextAsync(r).ConfigureAwait(false)).Trim();
                        if (text.Length > 0) { p.Title = text; titleFr = fr; }
                    }
                    else await r.SkipAsync().ConfigureAwait(false);
                }
                else if (name == _nDesc)
                {
                    bool fr = IsFrench(r.GetAttribute("lang"));
                    if (p.Desc is null || (fr && !descFr))
                    {
                        var text = (await ReadTextAsync(r).ConfigureAwait(false)).Trim();
                        if (text.Length > 0) { p.Desc = text; descFr = fr; }
                    }
                    else await r.SkipAsync().ConfigureAwait(false);
                }
                else if (name == _nCategory)
                {
                    if (p.Category is null) p.Category = NullIfBlank((await ReadTextAsync(r).ConfigureAwait(false)).Trim());
                    else await r.SkipAsync().ConfigureAwait(false);
                }
                else if (name == _nIcon)
                {
                    p.Icon ??= NullIfBlank(r.GetAttribute("src"));
                    await r.SkipAsync().ConfigureAwait(false);
                }
                else if (name == _nEpisode && !onscreen)
                {
                    var system = r.GetAttribute("system");
                    if (string.Equals(system, "onscreen", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = (await ReadTextAsync(r).ConfigureAwait(false)).Trim();
                        if (text.Length > 0) { p.EpisodeNum = text; onscreen = true; }
                    }
                    else if (string.Equals(system, "xmltv_ns", StringComparison.OrdinalIgnoreCase) && p.EpisodeNum is null)
                    {
                        p.EpisodeNum = ConvertXmltvNs(await ReadTextAsync(r).ConfigureAwait(false));
                    }
                    else await r.SkipAsync().ConfigureAwait(false);
                }
                else
                {
                    await r.SkipAsync().ConfigureAwait(false);
                }
            }
        }

        if (p.Title.Length == 0)
        {
            Invalid++;
            return;
        }
        if (!hasStop)
        {
            _pendingStop[channel] = p;
            return;
        }
        Emit(p);
    }

    private void Emit(XmltvProgramme p)
    {
        if (p.Stop > _minStop && p.Start < _maxStart && p.Stop > p.Start)
        {
            Kept++;
            ProgrammeFound?.Invoke(p);
        }
        else OutOfWindow++;
    }

    /// <summary>Identifiant de chaîne en minuscules ; réutilise la chaîne précédente (les programmes sont groupés par chaîne).</summary>
    private string ChannelKey(string raw)
    {
        if (raw == _lastRawChannel) return _lastChannel;
        _lastRawChannel = raw;
        _lastChannel = raw.Trim().ToLowerInvariant();
        return _lastChannel;
    }

    /// <summary>Texte d'un élément (enfants éventuels ignorés). Laisse le lecteur après la balise fermante.</summary>
    private static async Task<string> ReadTextAsync(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            await r.ReadAsync().ConfigureAwait(false);
            return "";
        }

        int depth = r.Depth;
        string? first = null;
        StringBuilder? sb = null;
        await r.ReadAsync().ConfigureAwait(false);
        while (!r.EOF)
        {
            switch (r.NodeType)
            {
                case XmlNodeType.EndElement when r.Depth == depth:
                    await r.ReadAsync().ConfigureAwait(false);
                    return sb?.ToString() ?? first ?? "";
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    var v = await r.GetValueAsync().ConfigureAwait(false);
                    if (first is null) first = v;
                    else (sb ??= new StringBuilder(first)).Append(v);
                    await r.ReadAsync().ConfigureAwait(false);
                    break;
                case XmlNodeType.Element:
                    await r.SkipAsync().ConfigureAwait(false);
                    break;
                default:
                    await r.ReadAsync().ConfigureAwait(false);
                    break;
            }
        }
        return sb?.ToString() ?? first ?? "";
    }

    private static bool IsFrench(string? lang) =>
        lang is not null && lang.TrimStart().StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------------------ formats

    /// <summary>
    /// Heure XMLTV → secondes Unix (UTC). Formats acceptés : « 20261121203000 +0100 », sans secondes (202611212030),
    /// sans heure (20261121), sans décalage (UTC supposé), décalage collé (« 20261121203000+0100 ») ou « +01:00 ».
    /// </summary>
    internal static bool TryParseTime(string? text, out long unix)
    {
        unix = 0;
        if (text is null) return false;
        var s = text.AsSpan().Trim();

        int n = 0;
        while (n < s.Length && char.IsAsciiDigit(s[n])) n++;
        if (n < 8 || n > 14 || (n & 1) != 0) return false;

        int year = Digits(s, 0, 4), month = Digits(s, 4, 2), day = Digits(s, 6, 2);
        int hour = n >= 10 ? Digits(s, 8, 2) : 0;
        int minute = n >= 12 ? Digits(s, 10, 2) : 0;
        int second = n >= 14 ? Digits(s, 12, 2) : 0;
        if (year is < 1970 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 60)
            return false;
        if (second == 60) second = 59;

        long days = new DateOnly(year, month, day).DayNumber - UnixEpochDay;
        long t = days * 86400 + hour * 3600L + minute * 60L + second;

        var rest = s[n..].TrimStart();
        if (rest.Length > 0 && rest[0] == '.')
        {
            // fractions de seconde éventuelles
            int i = 1;
            while (i < rest.Length && char.IsAsciiDigit(rest[i])) i++;
            rest = rest[i..].TrimStart();
        }

        if (rest.Length > 0 && (rest[0] == '+' || rest[0] == '-'))
        {
            int sign = rest[0] == '-' ? -1 : 1;
            int value = 0, count = 0;
            for (int i = 1; i < rest.Length && count < 4; i++)
            {
                char c = rest[i];
                if (char.IsAsciiDigit(c)) { value = value * 10 + (c - '0'); count++; }
                else if (c == ':' && count > 0) continue;
                else break;
            }
            int hh, mm;
            switch (count)
            {
                case 4: hh = value / 100; mm = value % 100; break;
                case 3: hh = value / 100; mm = value % 100; break; // « +100 » = +01:00
                case 2:
                case 1: hh = value; mm = 0; break;
                default: return false;
            }
            if (hh > 14 || mm > 59) return false;
            t -= sign * (hh * 3600L + mm * 60L);
        }
        // « Z », « UTC », « GMT » ou absence de décalage : UTC.

        unix = t;
        return true;
    }

    private static int Digits(ReadOnlySpan<char> s, int start, int length)
    {
        int v = 0;
        for (int i = start; i < start + length; i++) v = v * 10 + (s[i] - '0');
        return v;
    }

    /// <summary>« saison.épisode.partie » (base 0, « n/total » accepté) → « S01E02 ». Null si inexploitable.</summary>
    internal static string? ConvertXmltvNs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('.');
        if (parts.Length < 2) return null;
        int? season = Part(parts[0]);
        int? episode = Part(parts[1]);
        if (season is { } s && episode is { } e) return $"S{s + 1:00}E{e + 1:00}";
        if (episode is { } e2) return $"E{e2 + 1:00}";
        if (season is { } s2) return $"S{s2 + 1:00}";
        return null;

        static int? Part(string p)
        {
            var slash = p.IndexOf('/');
            var v = (slash >= 0 ? p[..slash] : p).Trim();
            return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < 10000 ? n : null;
        }
    }
}

/// <summary>
/// Flux en lecture seule permettant de lire les premiers octets (détection de format) sans les perdre,
/// et d'imposer un jeton d'annulation aux lectures asynchrones (XmlReader et GZipStream n'en transmettent pas).
/// </summary>
internal sealed class PeekStream(Stream inner, CancellationToken ct) : Stream
{
    private byte[] _head = [];
    private int _pos;
    private int _len;

    /// <summary>Lit jusqu'à <paramref name="count"/> octets qui seront rendus par les lectures suivantes. À appeler avant toute lecture.</summary>
    public async Task<ReadOnlyMemory<byte>> PeekAsync(int count)
    {
        var buffer = new byte[count];
        int n = await inner.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        _head = buffer;
        _pos = 0;
        _len = n;
        return buffer.AsMemory(0, n);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos < _len)
        {
            int n = Math.Min(buffer.Length, _len - _pos);
            _head.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        return inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pos < _len)
        {
            int n = Math.Min(buffer.Length, _len - _pos);
            _head.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return new ValueTask<int>(n);
        }
        return inner.ReadAsync(buffer, cancellationToken.CanBeCanceled ? cancellationToken : ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
