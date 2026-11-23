using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MilfTV.Localization;

namespace MilfTV.Controls;

/// <summary>Convertisseurs utilisés dans les vues.</summary>
public static class Conv
{
    /// <summary>Vrai si la valeur (en texte) est égale au paramètre.</summary>
    public static readonly IValueConverter Eq = new FuncValueConverter<object?, object?, bool>((v, p) =>
        string.Equals(v?.ToString(), p?.ToString(), StringComparison.Ordinal));

    /// <summary>Pourcentage 0–100 → largeur relative (0–1) multipliée par le paramètre.</summary>
    public static readonly IValueConverter PercentToWidth = new FuncValueConverter<double, object?, double>((v, p) =>
        Math.Clamp(v, 0, 100) / 100.0 * (double.TryParse(p?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100));

    public static readonly IValueConverter HeartGlyph = new FuncValueConverter<bool, string>(b => b ? "" : "");
    public static readonly IValueConverter PlayPauseGlyph = new FuncValueConverter<bool, string>(paused => paused ? "" : "");
    public static readonly IValueConverter MuteGlyph = new FuncValueConverter<bool, string>(muted => muted ? "" : "");
    public static readonly IValueConverter LockGlyph = new FuncValueConverter<bool, string>(locked => locked ? "" : "");
    public static readonly IValueConverter ThemeGlyph = new FuncValueConverter<bool, string>(dark => dark ? "" : "");
    public static readonly IValueConverter ThemeLabel = new FuncValueConverter<bool, string>(dark => dark ? Loc.T("Theme.Light") : Loc.T("Theme.Dark"));
    public static readonly IValueConverter FullscreenGlyph = new FuncValueConverter<bool, string>(fs => fs ? "" : "");

    public static readonly IValueConverter Initials = new FuncValueConverter<string?, string>(s => Core.TextNorm.Initials(s));
    public static readonly IValueConverter CleanName = new FuncValueConverter<string?, string>(s =>
        Data.SettingsStore.Current.CleanNames ? Core.TextNorm.StripPrefix(s ?? "") : s ?? "");

    /// <summary>Titre d'affiche : sans préfixe de pays ni année (affichée à part).</summary>
    public static readonly IValueConverter PosterTitle = new FuncValueConverter<string?, string>(s => Core.TextNorm.SplitYear(s ?? "").Title);

    public static readonly IValueConverter Count = new FuncValueConverter<int, string>(n => n > 0 ? n.ToString("N0", Loc.Culture) : "");

    public static readonly IValueConverter Countdown = new FuncValueConverter<int, string>(n => Loc.T("Player.NextEpisodeIn", n));

    /// <summary>Kind de source → glyphe.</summary>
    public static readonly IValueConverter SourceGlyph = new FuncValueConverter<Core.SourceKind, string>(k => k switch
    {
        Core.SourceKind.Xtream => "",
        Core.SourceKind.M3u => "",
        _ => ""
    });

    /// <summary>Couleur hexadécimale (#RRGGBB) → pinceau.</summary>
    public static readonly IValueConverter HexBrush = new FuncValueConverter<string?, IBrush>(s =>
    {
        try { return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(s) ? "#7C5CFF" : s)); }
        catch { return new SolidColorBrush(Color.Parse("#7C5CFF")); }
    });

    public static readonly IValueConverter NonZero = new FuncValueConverter<int, bool>(n => n != 0);
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);

    public static readonly IValueConverter ProgressVisible = new FuncValueConverter<double, bool>(v => v > 0.5);

    public static readonly IValueConverter ToastBrush = new FuncValueConverter<bool, IBrush>(err =>
        err ? new SolidColorBrush(Color.Parse("#FF5A5F")) : new SolidColorBrush(Color.Parse("#3DD68C")));
}
