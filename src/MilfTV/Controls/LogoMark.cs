using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MilfTV.Core;

namespace MilfTV.Controls;

/// <summary>Logo vectoriel de MilfTV (carré arrondi en dégradé + bouton lecture).</summary>
public sealed class LogoMark : Control
{
    private static readonly IBrush Gradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#6D5BFF"), 0),
            new GradientStop(Color.Parse("#B14DFF"), 0.5),
            new GradientStop(Color.Parse("#FF4D8D"), 1),
        }
    };

    private static readonly IBrush Gloss = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x40, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.6),
        }
    };

    public override void Render(DrawingContext ctx)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0) return;
        var ox = (Bounds.Width - s) / 2;
        var oy = (Bounds.Height - s) / 2;
        var rect = new Rect(ox, oy, s, s);
        var r = s * 0.27;
        ctx.DrawRectangle(Gradient, null, new RoundedRect(rect, r));
        ctx.DrawRectangle(Gloss, null, new RoundedRect(rect, r));

        // écran stylisé
        var screen = new Rect(ox + s * 0.17, oy + s * 0.24, s * 0.66, s * 0.48);
        ctx.DrawRectangle(null, new Pen(Brushes.White, Math.Max(1, s * 0.055)), new RoundedRect(screen, s * 0.1));
        // pied
        ctx.DrawLine(new Pen(Brushes.White, Math.Max(1, s * 0.055), lineCap: PenLineCap.Round),
            new Point(ox + s * 0.38, oy + s * 0.83), new Point(ox + s * 0.62, oy + s * 0.83));

        // triangle de lecture
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(ox + s * 0.44, oy + s * 0.36), true);
            g.LineTo(new Point(ox + s * 0.44, oy + s * 0.60));
            g.LineTo(new Point(ox + s * 0.63, oy + s * 0.48));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Brushes.White, new Pen(Brushes.White, Math.Max(0.5, s * 0.04), lineJoin: PenLineJoin.Round), geo);
    }
}

/// <summary>Génère l'icône .ico de l'application à partir du logo (mode développeur : --make-icon chemin.ico).</summary>
public static class IconMakerImpl
{
    public static void Run(string output)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (var size in sizes)
        {
            var logo = new LogoMark { Width = size, Height = size };
            logo.Measure(new Size(size, size));
            logo.Arrange(new Rect(0, 0, size, size));
            using var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            rtb.Render(logo);
            using var ms = new MemoryStream();
            rtb.Save(ms);
            pngs.Add(ms.ToArray());
            if (size == 256) File.WriteAllBytes(Path.ChangeExtension(output, ".png"), ms.ToArray());
        }
        using var fs = File.Create(output);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        Log.Info("Icône générée : " + output);
    }
}
