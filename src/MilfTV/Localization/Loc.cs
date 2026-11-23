using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace MilfTV.Localization;

public sealed record LanguageInfo(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>Traductions de l'interface. Les textes sont dans Assets/i18n/{langue}.json (clé → texte).
/// Le français est la langue de référence (complète) et sert de repli.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static IReadOnlyList<LanguageInfo> Languages { get; } = new[]
    {
        new LanguageInfo("fr", "Français"),
        new LanguageInfo("en", "English"),
        new LanguageInfo("es", "Español"),
        new LanguageInfo("de", "Deutsch"),
        new LanguageInfo("it", "Italiano"),
        new LanguageInfo("pt", "Português"),
    };

    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _fallback = new();

    /// <summary>Code de la langue active (fr, en, es, de, it, pt).</summary>
    public static string Lang { get; private set; } = "fr";

    /// <summary>Culture utilisée pour les dates et les nombres.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr-FR");

    public static event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Accès par indexeur pour les liaisons XAML ({l:Tr Clé}).</summary>
    public string this[string key] => T(key);

    /// <summary>Incrémenté à chaque changement de langue : les liaisons {l:Tr} l'observent pour se mettre à jour.</summary>
    public int Version { get; private set; }

    public static string T(string key) =>
        _current.TryGetValue(key, out var s) ? s :
        _fallback.TryGetValue(key, out s) ? s : key;

    public static string T(string key, params object?[] args)
    {
        try { return string.Format(Culture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    /// <summary>Langue de Windows si elle est prise en charge, sinon l'anglais.</summary>
    public static string SystemLanguage()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    /// <summary>Active une langue ("auto" ou null = langue de Windows).</summary>
    public static void SetLanguage(string? code)
    {
        var lang = string.IsNullOrWhiteSpace(code) || code == "auto" ? SystemLanguage() : code.ToLowerInvariant();
        if (Languages.All(l => l.Code != lang)) lang = "en";
        if (_fallback.Count == 0) _fallback = Load("fr");
        _current = lang == "fr" ? _fallback : Load(lang);
        var changed = lang != Lang;
        Lang = lang;
        Culture = CultureInfo.GetCultureInfo(CultureName(lang));
        Instance.Version++;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Version)));
        if (changed) LanguageChanged?.Invoke();
    }

    public static string CultureName(string lang) => lang switch
    {
        "fr" => "fr-FR",
        "en" => "en-GB",
        "es" => "es-ES",
        "de" => "de-DE",
        "it" => "it-IT",
        "pt" => "pt-PT",
        _ => "en-GB"
    };

    /// <summary>Langue des fiches TMDB.</summary>
    public static string TmdbLanguage => Lang switch
    {
        "fr" => "fr-FR",
        "es" => "es-ES",
        "de" => "de-DE",
        "it" => "it-IT",
        "pt" => "pt-PT",
        _ => "en-US"
    };

    /// <summary>Langues audio préférées par défaut (codes mpv) pour une langue d'interface.</summary>
    public static string DefaultAudioLanguages(string lang) => lang switch
    {
        "fr" => "fr,fre,fra,fr-FR,en,eng",
        "es" => "es,spa,en,eng",
        "de" => "de,ger,deu,en,eng",
        "it" => "it,ita,en,eng",
        "pt" => "pt,por,en,eng",
        _ => "en,eng"
    };

    public static string DefaultSubtitleLanguages(string lang) => lang switch
    {
        "fr" => "fr,fre,fra",
        "es" => "es,spa",
        "de" => "de,ger,deu",
        "it" => "it,ita",
        "pt" => "pt,por",
        _ => "en,eng"
    };

    private static Dictionary<string, string> Load(string lang)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://MilfTV/Assets/i18n/{lang}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Traductions {lang} introuvables : {ex.Message}");
            return new();
        }
    }

    // ───────────── Outils XAML ─────────────

    /// <summary>Convertisseur : ConverterParameter = clé d'un texte contenant {0}, la valeur liée remplace {0}.</summary>
    public static readonly IValueConverter Format = new FuncValueConverter<object?, object?, string>((v, p) =>
        T(p?.ToString() ?? "", v));

    /// <summary>Convertisseur multiple : premier élément = clé, suivants = valeurs.</summary>
    public static readonly IMultiValueConverter FormatMulti = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        if (list.Count == 0) return "";
        return T(list[0]?.ToString() ?? "", list.Skip(1).ToArray());
    });
}

/// <summary>Extension de balisage : Text="{l:Tr Nav.Home}". Se met à jour à chaud lors d'un changement de langue.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Loc.Version))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = KeyConverter,
            ConverterParameter = Key,
        };

    private static readonly IValueConverter KeyConverter =
        new FuncValueConverter<int, object?, string>((_, key) => Loc.T(key?.ToString() ?? ""));
}
