using Avalonia;
using MilfTV.Localization;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MilfTV.Theming;

public sealed record ColorTheme(string Id, string FrenchName, Color Accent, double Saturation = 1.0)
{
    public string Name => Localization.Loc.T("Theme." + Id);
    public IBrush Swatch => new SolidColorBrush(Accent);
}

/// <summary>Thèmes clair / sombre et palettes de couleurs. Toutes les couleurs de l'interface
/// sont des pinceaux nommés dont on modifie la couleur à chaud (DynamicResource).</summary>
public static class ThemeService
{
    public static IReadOnlyList<ColorTheme> Themes { get; } = new[]
    {
        new ColorTheme("minuit", "Minuit", Color.Parse("#7C5CFF")),
        new ColorTheme("ocean", "Océan", Color.Parse("#2F8CFF")),
        new ColorTheme("lagon", "Lagon", Color.Parse("#14B8A6")),
        new ColorTheme("emeraude", "Émeraude", Color.Parse("#22C55E")),
        new ColorTheme("ambre", "Ambre", Color.Parse("#F59E0B")),
        new ColorTheme("corail", "Corail", Color.Parse("#FF6B4A")),
        new ColorTheme("rubis", "Rubis", Color.Parse("#E5484D")),
        new ColorTheme("rose", "Rose", Color.Parse("#EC4899")),
        new ColorTheme("lavande", "Lavande", Color.Parse("#A78BFA")),
        new ColorTheme("graphite", "Graphite", Color.Parse("#94A3B8"), 0.15),
    };

    public static string CurrentMode { get; private set; } = "Dark";
    public static ColorTheme Current { get; private set; } = Themes[0];
    public static bool IsDark { get; private set; } = true;

    public static event Action? Changed;

    private static readonly string[] Keys =
    {
        "AppBg", "AppBgAlt", "Surface", "SurfaceHover", "SurfaceActive", "SurfaceRaised", "BorderSubtle", "BorderStrong",
        "TextPrimary", "TextSecondary", "TextMuted", "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentSofter",
        "OnAccent", "Danger", "Success", "Warning", "Live", "Scrim", "ScrimStrong", "Placeholder", "PlaceholderText", "GlassBg", "Shadow", "AppBgClear"
    };

    public static void Apply(string? mode, string? themeId)
    {
        var app = Application.Current;
        if (app is null) return;
        CurrentMode = mode is "Light" or "Dark" or "System" ? mode : "Dark";
        Current = Themes.FirstOrDefault(t => t.Id == themeId) ?? Themes[0];

        IsDark = CurrentMode switch
        {
            "Light" => false,
            "Dark" => true,
            _ => app.PlatformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light
        };
        app.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        var p = Build(Current, IsDark);
        foreach (var key in Keys)
        {
            var color = p[key];
            if (app.Resources.TryGetResource(key, null, out var existing) && existing is SolidColorBrush b)
                b.Color = color;
            else
                app.Resources[key] = new SolidColorBrush(color);
            app.Resources[key + "Color"] = color;
        }

        // Couleur d'accent des contrôles Fluent (curseurs, interrupteurs, sélection…)
        foreach (var style in app.Styles)
        {
            if (style is FluentTheme fluent)
            {
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    if (!fluent.Palettes.TryGetValue(variant, out var palette))
                        fluent.Palettes[variant] = palette = new ColorPaletteResources();
                    var dark = variant == ThemeVariant.Dark;
                    var pp = dark == IsDark ? p : Build(Current, dark);
                    palette.Accent = pp["Accent"];
                    palette.RegionColor = pp["AppBg"];
                    palette.AltHigh = pp["AppBg"];
                    palette.ChromeMediumLow = pp["Surface"];
                    palette.ChromeLow = pp["SurfaceHover"];
                }
            }
        }
        app.Resources["SystemAccentColor"] = p["Accent"];
        Changed?.Invoke();
    }

    private static Dictionary<string, Color> Build(ColorTheme t, bool dark)
    {
        var (h, s, l) = ToHsl(t.Accent);
        double ts = Math.Min(s, 1.0) * t.Saturation; // saturation de la teinte des fonds
        var d = new Dictionary<string, Color>();
        if (dark)
        {
            d["AppBg"] = Hsl(h, 0.22 * ts, 0.065);
            d["AppBgAlt"] = Hsl(h, 0.22 * ts, 0.085);
            d["Surface"] = Hsl(h, 0.18 * ts, 0.115);
            d["SurfaceHover"] = Hsl(h, 0.18 * ts, 0.15);
            d["SurfaceActive"] = Hsl(h, 0.20 * ts, 0.19);
            d["SurfaceRaised"] = Hsl(h, 0.18 * ts, 0.135);
            d["BorderSubtle"] = Hsl(h, 0.16 * ts, 0.17);
            d["BorderStrong"] = Hsl(h, 0.14 * ts, 0.27);
            d["TextPrimary"] = Hsl(h, 0.15 * ts, 0.96);
            d["TextSecondary"] = Hsl(h, 0.10 * ts, 0.74);
            d["TextMuted"] = Hsl(h, 0.08 * ts, 0.53);
            var accent = t.Accent;
            d["Accent"] = accent;
            d["AccentHover"] = Hsl(h, s, Math.Min(0.85, l + 0.07));
            d["AccentPressed"] = Hsl(h, s, Math.Max(0.2, l - 0.06));
            d["AccentSoft"] = WithAlpha(accent, 0x38);
            d["AccentSofter"] = WithAlpha(accent, 0x1C);
            d["Placeholder"] = Hsl(h, 0.20 * ts, 0.16);
            d["PlaceholderText"] = Hsl(h, 0.20 * ts, 0.42);
            d["GlassBg"] = WithAlpha(Hsl(h, 0.20 * ts, 0.08), 0xE6);
            d["Shadow"] = Color.FromArgb(0x66, 0, 0, 0);
        }
        else
        {
            d["AppBg"] = Hsl(h, 0.32 * ts, 0.965);
            d["AppBgAlt"] = Hsl(h, 0.30 * ts, 0.94);
            d["Surface"] = Colors.White;
            d["SurfaceHover"] = Hsl(h, 0.35 * ts, 0.945);
            d["SurfaceActive"] = Hsl(h, 0.40 * ts, 0.91);
            d["SurfaceRaised"] = Colors.White;
            d["BorderSubtle"] = Hsl(h, 0.20 * ts, 0.89);
            d["BorderStrong"] = Hsl(h, 0.15 * ts, 0.78);
            d["TextPrimary"] = Hsl(h, 0.30 * ts, 0.11);
            d["TextSecondary"] = Hsl(h, 0.12 * ts, 0.36);
            d["TextMuted"] = Hsl(h, 0.08 * ts, 0.52);
            var accent = Hsl(h, s, Math.Clamp(l - 0.06, 0.32, 0.55));
            d["Accent"] = accent;
            d["AccentHover"] = Hsl(h, s, Math.Clamp(l - 0.12, 0.26, 0.5));
            d["AccentPressed"] = Hsl(h, s, Math.Clamp(l - 0.18, 0.2, 0.45));
            d["AccentSoft"] = WithAlpha(accent, 0x2A);
            d["AccentSofter"] = WithAlpha(accent, 0x14);
            d["Placeholder"] = Hsl(h, 0.30 * ts, 0.90);
            d["PlaceholderText"] = Hsl(h, 0.25 * ts, 0.58);
            d["GlassBg"] = WithAlpha(Colors.White, 0xEB);
            d["Shadow"] = Color.FromArgb(0x30, 0x20, 0x20, 0x40);
        }
        d["OnAccent"] = Luminance(d["Accent"]) > 0.55 ? Color.Parse("#111111") : Colors.White;
        d["Danger"] = Color.Parse(dark ? "#FF5A5F" : "#D93036");
        d["Success"] = Color.Parse(dark ? "#3DD68C" : "#18A058");
        d["Warning"] = Color.Parse(dark ? "#FFB224" : "#C27C0E");
        d["Live"] = Color.Parse("#FF3B4E");
        d["AppBgClear"] = WithAlpha(d["AppBg"], 0);
        d["Scrim"] = Color.FromArgb(0x99, 0, 0, 0);
        d["ScrimStrong"] = Color.FromArgb(0xD9, 0x05, 0x05, 0x08);
        return d;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-9) s = 0;
        else
        {
            var dd = max - min;
            s = l > 0.5 ? dd / (2 - max - min) : dd / (max + min);
            if (max == r) h = (g - b) / dd + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / dd + 2;
            else h = (r - g) / dd + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static Color Hsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
