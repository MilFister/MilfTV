using System.Globalization;
using System.Text.RegularExpressions;
using MilfTV.Core;

namespace MilfTV.Recording;

/// <summary>Noms et formats des fichiers d'enregistrement (programmés et immédiats).</summary>
public static partial class RecordingNames
{
    public const string DefaultTemplate = "{titre} - {chaine} {date} {heure}";

    /// <summary>Longueur maximale du nom (sans l'extension).</summary>
    private const int MaxLength = 80;

    /// <summary>Chemins attribués pendant cette session : deux enregistrements lancés au même moment
    /// n'écrivent jamais dans le même fichier, même s'il n'existe pas encore sur le disque.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Format normalisé : ts, mkv ou mp4 (ts par défaut).</summary>
    public static string NormalizeFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mkv" => "mkv",
        "mp4" => "mp4",
        _ => "ts"
    };

    /// <summary>Extension (avec le point) correspondant au format.</summary>
    public static string Extension(string? format) => "." + NormalizeFormat(format);

    /// <summary>Vrai si le format demande un changement de conteneur (mkv, mp4).</summary>
    public static bool IsRemux(string? format) => NormalizeFormat(format) != "ts";

    [GeneratedRegex(@"\{(titre|chaine|chaîne|date|heure)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    // Séparateurs répétés laissés par un jeton vide (« Titre -  - TF1 »).
    [GeneratedRegex(@"(\s[-–|])(\s[-–|])+(?=\s|$)")]
    private static partial Regex RepeatedSeparatorRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();

    private static readonly char[] EdgeChars = { ' ', '-', '–', '|', '_', ',', '.', ';' };

    /// <summary>Nom de fichier (avec extension, sans dossier) construit à partir du modèle.
    /// Jetons : {titre}, {chaine}, {date} (aaaa-mm-jj), {heure} (hh-mm), en heure locale.</summary>
    public static string Build(string? template, string? title, string? channel, DateTimeOffset start, string? format)
    {
        var tpl = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template!;
        var t = Clean(title);
        var c = Clean(channel);
        var local = start.ToLocalTime();
        var date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = local.ToString("HH-mm", CultureInfo.InvariantCulture);

        var name = Compose(tpl, t, c, date, time);
        // Titre trop long : on le raccourcit pour garder la chaîne, la date et l'heure.
        if (name.Length > MaxLength && t.Length > 0)
        {
            var keep = Math.Max(15, t.Length - (name.Length - MaxLength));
            if (keep < t.Length)
            {
                t = TextNorm.Truncate(t, keep).TrimEnd(EdgeChars);
                name = Compose(tpl, t, c, date, time);
            }
        }
        if (name.Length == 0) name = Compose(DefaultTemplate, t, c, date, time);
        if (name.Length > MaxLength) name = TextNorm.Truncate(name, MaxLength).TrimEnd(EdgeChars);
        // Dernière passe : caractères interdits par Windows (la longueur est déjà sous la limite).
        name = TextNorm.SafeFileName(name).Trim(EdgeChars);
        if (name.Length == 0) name = $"MilfTV {date} {time}";
        // Noms de périphériques réservés par Windows (CON, NUL, COM1…).
        // (aussi devant un point : « CON.Air » est refusé par Windows)
        if (DeviceNameRegex().IsMatch(name) || DeviceNameRegex().IsMatch(name.Split('.')[0].Trim())) name += "_";
        return name + Extension(format);
    }

    /// <summary>Chemin complet et libre dans le dossier : n'écrase jamais un fichier existant.</summary>
    public static string BuildPath(string folder, string? template, string? title, string? channel, DateTimeOffset start, string? format) =>
        UniquePath(Path.Combine(folder, Build(template, title, channel, start, format)));

    /// <summary>Ajoute « (2) », « (3) »… si le fichier existe déjà (ou a déjà été attribué), puis réserve le chemin.</summary>
    public static string UniquePath(string path)
    {
        lock (Reserved)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var candidate = path;
            for (int i = 2; i < 1000 && Taken(candidate); i++)
                candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (Taken(candidate)) candidate = Path.Combine(dir, $"{stem} ({DateTime.Now:HHmmssfff}){ext}");
            Reserved.Add(candidate);
            return candidate;
        }
    }

    private static bool Taken(string p)
    {
        if (Reserved.Contains(p)) return true;
        try { return File.Exists(p); }
        catch { return false; }
    }

    /// <summary>Valeur d'un jeton : sans préfixe de pays, sur une seule ligne, sans caractères interdits.</summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var v = SpacesRegex().Replace(TextNorm.StripPrefix(s), " ");
        return TextNorm.SafeFileName(v).Trim(EdgeChars);
    }

    private static string Compose(string template, string title, string channel, string date, string time)
    {
        // Remplacement par fonction (pas de motif de substitution) : le texte inséré est pris tel quel.
        var s = TokenRegex().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "titre" => title,
            "chaine" or "chaîne" => channel,
            "date" => date,
            _ => time
        });
        s = SpacesRegex().Replace(s, " ");
        s = RepeatedSeparatorRegex().Replace(s, m => m.Groups[1].Value);
        // Caractères interdits par Windows saisis dans le modèle (« : », « / »…) ; les valeurs des jetons sont déjà
        // nettoyées. Pas de TextNorm.SafeFileName ici : il couperait à 80 caractères avant le raccourcissement du titre.
        var invalid = Path.GetInvalidFileNameChars();
        s = string.Concat(s.Select(ch => invalid.Contains(ch) ? '_' : ch));
        return s.Trim(EdgeChars);
    }
}
