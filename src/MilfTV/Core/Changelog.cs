using System.Text.Json;
using Avalonia.Platform;
using MilfTV.Data;
using MilfTV.Localization;

namespace MilfTV.Core;

/// <summary>Une rubrique des notes d'une version (titre facultatif, puis la liste des modifications).</summary>
public sealed class ChangelogSection
{
    public string? Title { get; init; }
    public required IReadOnlyList<string> Items { get; init; }
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>Une version dans les notes de version.</summary>
public sealed class ChangelogEntry
{
    public required string Version { get; init; }
    public required string DateText { get; init; }
    public string? Summary { get; init; }
    public required IReadOnlyList<ChangelogSection> Sections { get; init; }
    /// <summary>Version en cours d'utilisation.</summary>
    public bool IsCurrent { get; init; }
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
}

/// <summary>Notes de version embarquées (Assets/changelog.json), dans les 6 langues de l'application (l'anglais,
/// puis le français, servent de repli). Le même fichier produit les CHANGELOG du dépôt et les notes jointes aux
/// versions publiées (build\changelog.ps1, build\release.ps1).
/// Format : versions[] avec version, date, summary{langue}, sections{langue}[{title, items[]}] ;
/// une simple liste par langue (version[langue] = [...]) reste acceptée.</summary>
public static class Changelog
{
    private static readonly string[] Fallbacks = { "en", "fr" };

    public static IReadOnlyList<ChangelogEntry> Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://MilfTV/Assets/changelog.json"));
            using var doc = JsonDocument.Parse(stream);
            var current = Normalize(AppVersion.Display);
            var list = new List<ChangelogEntry>();
            foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                var version = v.GetProperty("version").GetString() ?? "";
                var date = v.TryGetProperty("date", out var d) && DateOnly.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var day)
                    ? day.ToString("d MMMM yyyy", Loc.Culture)
                    : "";
                list.Add(new ChangelogEntry
                {
                    Version = version,
                    DateText = date,
                    Summary = Localized(v, "summary", e => e.ValueKind == JsonValueKind.String ? e.GetString() : null),
                    Sections = Localized(v, "sections", ReadSections) ?? Localized(v, null, ReadFlat) ?? Array.Empty<ChangelogSection>(),
                    IsCurrent = Normalize(version) == current,
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Notes de version illisibles : " + ex.Message);
            return Array.Empty<ChangelogEntry>();
        }
    }

    /// <summary>Valeur dans la langue de l'interface, sinon en anglais, sinon en français.
    /// property null : les langues sont directement des propriétés de la version (ancien format).</summary>
    private static T? Localized<T>(JsonElement version, string? property, Func<JsonElement, T?> read) where T : class
    {
        JsonElement holder = version;
        if (property is not null && (!version.TryGetProperty(property, out holder) || holder.ValueKind != JsonValueKind.Object)) return null;
        foreach (var lang in Fallbacks.Prepend(Loc.Lang))
            if (holder.TryGetProperty(lang, out var e) && read(e) is { } value) return value;
        return null;
    }

    private static IReadOnlyList<ChangelogSection>? ReadSections(JsonElement e) =>
        e.ValueKind != JsonValueKind.Array ? null : e.EnumerateArray().Select(s => new ChangelogSection
        {
            Title = s.TryGetProperty("title", out var t) ? t.GetString() : null,
            Items = s.TryGetProperty("items", out var items) ? Strings(items) : Array.Empty<string>(),
        }).ToList();

    private static IReadOnlyList<ChangelogSection>? ReadFlat(JsonElement e) =>
        e.ValueKind != JsonValueKind.Array ? null : new[] { new ChangelogSection { Items = Strings(e) } };

    private static List<string> Strings(JsonElement arr) =>
        arr.ValueKind != JsonValueKind.Array ? new List<string>()
            : arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

    /// <summary>« 1.8 » et « 1.8.0 » désignent la même version ; les suffixes de bêta sont gardés.</summary>
    private static string Normalize(string v)
    {
        v = v.Trim();
        return !v.Contains('-') && System.Version.TryParse(v, out var parsed) ? parsed.ToString(3) : v;
    }

    /// <summary>Premier lancement d'une nouvelle version : vrai s'il faut annoncer les nouveautés (pas pour une
    /// première installation ni juste après une restauration). La version vue est retenue dans les réglages.</summary>
    public static bool TakeUpdateAnnouncement()
    {
        var s = SettingsStore.Current;
        var current = Normalize(AppVersion.Display);
        var last = s.LastRunVersion is { } l ? Normalize(l) : null;
        if (last == current) return false;
        s.LastRunVersion = current;
        SettingsStore.Save();
        if (Backup.RestoredAtStartup is not null) return false;
        // Réglages antérieurs à cette fonction : un utilisateur qui a déjà des profils vient d'être mis à jour.
        return last is not null || s.Profiles.Count > 0;
    }
}
