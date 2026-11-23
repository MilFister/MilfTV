using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MilfTV.Localization;

namespace MilfTV.Core;

/// <summary>Normalisation de texte pour la recherche, affichages courants.</summary>
public static partial class TextNorm
{
    /// <summary>Minuscules, sans accents, seulement lettres/chiffres séparés par un espace.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        bool space = true;
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    public static string[] Tokens(string? query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Score de correspondance (0 = pas de correspondance). Tous les mots doivent être présents.</summary>
    public static int Score(string key, string[] tokens, string normalizedQuery)
    {
        if (tokens.Length == 0) return 0;
        int score = 0;
        foreach (var t in tokens)
        {
            int idx = key.IndexOf(t, StringComparison.Ordinal);
            if (idx < 0) return 0;
            bool wordStart = idx == 0 || key[idx - 1] == ' ';
            score += wordStart ? 20 : 5;
            // mot entier
            int end = idx + t.Length;
            if (wordStart && (end == key.Length || key[end] == ' ')) score += 10;
        }
        if (key == normalizedQuery) score += 200;
        else if (key.StartsWith(normalizedQuery, StringComparison.Ordinal)) score += 80;
        else if (key.Contains(normalizedQuery, StringComparison.Ordinal)) score += 30;
        // préférer les noms courts (plus proches de la requête)
        score += Math.Max(0, 30 - Math.Abs(key.Length - normalizedQuery.Length));
        return score;
    }

    public static string Initials(string? name)
    {
        var clean = StripPrefix(name ?? "");
        var parts = Normalize(clean).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
        return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }

    [GeneratedRegex(@"^\s*(\[[^\]]{1,12}\]|\|[^|]{1,12}\||[A-Z]{2,4}\s*[:|\-]\s+|[A-Z]{2,4}\s*\|)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    /// <summary>Retire les préfixes de pays/catégorie du type "FR: ", "|FR| ", "[FR] ".</summary>
    public static string StripPrefix(string name)
    {
        var s = name;
        for (int i = 0; i < 2; i++)
        {
            var m = PrefixRegex().Match(s);
            if (!m.Success || m.Length >= s.Length) break;
            s = s[m.Length..];
        }
        return s.Trim();
    }

    // Mots entiers (pas de lettre ou chiffre collé), et « 18+ » / « +18 » repérés même à côté d'espaces ou de ponctuation.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(adult|adults|adulte|adultes|xxx|porn|porno|hot|erotic|erotique|érotique|sexy)(?![\p{L}\p{N}])|(?<![0-9])(?:18\+|\+18)(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdultRegex();

    public static bool LooksAdult(string? name) => !string.IsNullOrEmpty(name) && AdultRegex().IsMatch(name);

    [GeneratedRegex(@"[\(\[]?\b(19[0-9][0-9]|20[0-4][0-9])\b[\)\]]?")]
    private static partial Regex YearRegex();

    /// <summary>Extrait l'année d'un titre « Film (2021) » et renvoie le titre nettoyé.</summary>
    public static (string Title, int? Year) SplitYear(string name)
    {
        var clean = StripPrefix(name);
        var matches = YearRegex().Matches(clean);
        if (matches.Count == 0) return (CleanTitle(clean), null);
        var m = matches[^1];
        var year = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var title = (clean[..m.Index] + clean[(m.Index + m.Length)..]).Trim(' ', '-', '|', '.');
        if (title.Length == 0) title = clean;
        return (CleanTitle(title), year);
    }

    [GeneratedRegex(@"\b(4k|uhd|fhd|hevc|h265|x265|vostfr|vf|vff|vfq|truefrench|1080p|720p|2160p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    public static string CleanTitle(string title)
    {
        var t = QualityRegex().Replace(title, " ");
        t = Regex.Replace(t, @"[\(\[\{]\s*[\)\]\}]", " ");
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t.Trim(' ', '-', '|', '.', ':');
    }

    public static string Duration(int seconds)
    {
        if (seconds <= 0) return "";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return Loc.T("Duration.HoursMinutes", (int)ts.TotalHours, ts.Minutes);
        return Loc.T("Duration.Minutes", ts.Minutes);
    }

    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public static string DayLabel(DateTimeOffset t)
    {
        var d = t.ToLocalTime().Date;
        var today = DateTime.Today;
        if (d == today) return Loc.T("Date.Today");
        if (d == today.AddDays(1)) return Loc.T("Date.Tomorrow");
        if (d == today.AddDays(-1)) return Loc.T("Date.Yesterday");
        var culture = Loc.Culture;
        var s = d.ToString(Loc.T("Date.DayFormat"), culture);
        return s.Length == 0 ? s : char.ToUpper(s[0], culture) + s[1..];
    }

    /// <summary>Date longue (« 12 mars 2021 ») dans la langue de l'interface.</summary>
    public static string FrDate(DateTimeOffset t) => t.ToLocalTime().ToString(Loc.T("Date.LongFormat"), Loc.Culture);

    public static string RelativeAgo(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return Loc.T("Ago.JustNow");
        if (d.TotalHours < 1) return Loc.T("Ago.Minutes", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return Loc.T("Ago.Hours", (int)d.TotalHours);
        if (d.TotalDays < 2) return Loc.T("Ago.Yesterday");
        return Loc.T("Ago.Days", (int)d.TotalDays);
    }

    public static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(invalid.Contains(c) ? '_' : c);
        var r = sb.ToString().Trim();
        return Truncate(r, 80);
    }

    /// <summary>Coupe à <paramref name="max"/> caractères sans séparer les deux moitiés d'un émoji
    /// (une moitié seule devient « � » une fois convertie en UTF-8 pour mpv : nom de fichier différent).</summary>
    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var n = max;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }
}
