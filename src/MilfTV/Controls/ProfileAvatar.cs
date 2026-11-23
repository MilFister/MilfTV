using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MilfTV.Core;
using MilfTV.Data;

namespace MilfTV.Controls;

/// <summary>Pastille ronde d'un profil : couleur du profil, avec son initiale, un émoji ou une image importée.</summary>
public sealed class ProfileAvatar : Border
{
    public static readonly StyledProperty<UserProfile?> ProfileProperty =
        AvaloniaProperty.Register<ProfileAvatar, UserProfile?>(nameof(Profile));
    public static readonly StyledProperty<string?> ProfileColorProperty =
        AvaloniaProperty.Register<ProfileAvatar, string?>(nameof(ProfileColor));
    public static readonly StyledProperty<string?> InitialProperty =
        AvaloniaProperty.Register<ProfileAvatar, string?>(nameof(Initial));
    public static readonly StyledProperty<string?> AvatarProperty =
        AvaloniaProperty.Register<ProfileAvatar, string?>(nameof(Avatar));

    /// <summary>Raccourci : prend la couleur, l'initiale et l'avatar du profil.</summary>
    public UserProfile? Profile { get => GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public string? ProfileColor { get => GetValue(ProfileColorProperty); set => SetValue(ProfileColorProperty, value); }
    public string? Initial { get => GetValue(InitialProperty); set => SetValue(InitialProperty, value); }
    public string? Avatar { get => GetValue(AvatarProperty); set => SetValue(AvatarProperty, value); }

    private readonly TextBlock _text = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brushes.White,
        FontWeight = FontWeight.Bold,
    };
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };

    public ProfileAvatar()
    {
        ClipToBounds = true;
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProfileProperty)
        {
            if (Profile is { } p)
            {
                ProfileColor = p.Color;
                Initial = p.Initial;
                Avatar = p.Avatar;
            }
            else
            {
                ProfileColor = null;
                Initial = "?";
                Avatar = null;
            }
        }
        else if (change.Property == ProfileColorProperty || change.Property == InitialProperty || change.Property == AvatarProperty)
            Refresh();
        else if (change.Property == BoundsProperty)
            Resize();
    }

    private void Refresh()
    {
        Background = new SolidColorBrush(Color.TryParse(ProfileColor, out var c) ? c : Color.Parse("#7C5CFF"));
        var avatar = Avatar;
        if (ProfileAvatars.LoadImage(avatar) is { } bmp)
        {
            _image.Source = bmp;
            Child = _image;
        }
        else
        {
            var emoji = ProfileAvatars.Emoji(avatar);
            _text.Text = emoji ?? (string.IsNullOrWhiteSpace(Initial) ? "?" : Initial);
            _text.FontFamily = emoji is null ? FontFamily.Default : new FontFamily("Segoe UI Emoji");
            _text.FontWeight = emoji is null ? FontWeight.Bold : FontWeight.Normal;
            Child = _text;
        }
        Resize();
    }

    private void Resize()
    {
        var d = Math.Min(Bounds.Width, Bounds.Height);
        if (d <= 0) d = double.IsNaN(Width) ? 40 : Width;
        CornerRadius = new CornerRadius(d / 2);
        // Émoji un peu plus grand que l'initiale pour remplir le rond.
        _text.FontSize = Math.Max(8, d * (ProfileAvatars.Emoji(Avatar) is null ? 0.42 : 0.56));
    }
}

/// <summary>Galerie d'émojis, import et nettoyage des images d'avatar.</summary>
public static class ProfileAvatars
{
    public static readonly string[] Emojis =
    {
        "🦊", "🐼", "🐱", "🐶", "🦁", "🐯", "🐸", "🐵",
        "🐧", "🐨", "🦄", "🐙", "🐻", "🐰", "🦖", "🐲",
        "🚀", "⚽", "🎮", "🎧", "🎨", "🌟", "🍕", "👾",
    };

    private const int ImageSize = 256;
    private static readonly Dictionary<string, (DateTime Stamp, Bitmap Bitmap)> Cache = new();

    public static string? Emoji(string? avatar) => avatar is { Length: > 6 } a && a.StartsWith("emoji:", StringComparison.Ordinal) ? a[6..] : null;

    private static string? FileName(string? avatar) =>
        avatar is { Length: > 5 } a && a.StartsWith("file:", StringComparison.Ordinal) ? Path.GetFileName(a[5..]) : null;

    public static string? FilePath(string? avatar) => FileName(avatar) is { } n ? Path.Combine(AppPaths.Avatars, n) : null;

    /// <summary>Image importée (mise en cache, relue si le fichier change). Null si absente ou illisible.</summary>
    public static Bitmap? LoadImage(string? avatar)
    {
        if (FilePath(avatar) is not { } path || !File.Exists(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Bitmap;
                var bmp = new Bitmap(new MemoryStream(File.ReadAllBytes(path)));
                Cache[path] = (stamp, bmp);
                return bmp;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Avatar illisible : " + ex.Message);
            return null;
        }
    }

    /// <summary>Importe une image : recadrage carré centré, réduction en 256 × 256, enregistrement en PNG.
    /// Renvoie la valeur d'avatar « file:… », ou null si l'image ne peut pas être lue.</summary>
    public static string? Import(Stream source)
    {
        try
        {
            using var src = new Bitmap(source);
            var w = src.Size.Width;
            var h = src.Size.Height;
            var side = Math.Min(w, h);
            using var rtb = new RenderTargetBitmap(new PixelSize(ImageSize, ImageSize), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(src, new Rect((w - side) / 2, (h - side) / 2, side, side), new Rect(0, 0, ImageSize, ImageSize));
            var name = Guid.NewGuid().ToString("N")[..16] + ".png";
            rtb.Save(Path.Combine(AppPaths.Avatars, name));
            Log.Info($"Avatar importé : {src.PixelSize.Width}×{src.PixelSize.Height} → {name}");
            return "file:" + name;
        }
        catch (Exception ex)
        {
            Log.Warn("Import d'avatar impossible : " + ex.Message);
            return null;
        }
    }

    /// <summary>Supprime le fichier d'un avatar image (profil supprimé, image remplacée).</summary>
    public static void DeleteFile(string? avatar)
    {
        if (FilePath(avatar) is not { } path) return;
        try
        {
            lock (Cache) Cache.Remove(path);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { Log.Warn("Suppression d'avatar : " + ex.Message); }
    }

    /// <summary>Au démarrage : retire les images importées puis abandonnées (édition annulée).</summary>
    public static void CleanupUnused()
    {
        try
        {
            var used = SettingsStore.Current.Profiles.Select(p => FileName(p.Avatar)).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(AppPaths.Avatars, "*.png"))
                if (!used.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch (Exception ex) { Log.Warn("Nettoyage des avatars : " + ex.Message); }
    }
}
