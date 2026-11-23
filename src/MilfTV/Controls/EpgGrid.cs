using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MilfTV.Core;
using MilfTV.Images;
using MilfTV.Localization;

namespace MilfTV.Controls;

public interface IEpgGridSource
{
    IReadOnlyList<LiveChannel> Channels { get; }
    DateTimeOffset DayStart { get; }
    /// <summary>Programmes chargés d'une chaîne, ou null si pas encore chargés.</summary>
    IReadOnlyList<EpgProgram>? GetPrograms(LiveChannel ch);
    void EnsureLoaded(int firstRow, int lastRow);
    event Action? Changed;
}

/// <summary>Grille du guide TV dessinée à la main (rapide même avec des milliers de chaînes).</summary>
public sealed class EpgGrid : Control
{
    public static readonly StyledProperty<IEpgGridSource?> SourceProperty =
        AvaloniaProperty.Register<EpgGrid, IEpgGridSource?>(nameof(Source));

    public static readonly StyledProperty<EpgProgram?> SelectedProgramProperty =
        AvaloniaProperty.Register<EpgGrid, EpgProgram?>(nameof(SelectedProgram));

    public static readonly StyledProperty<int> ScrollToNowRequestProperty =
        AvaloniaProperty.Register<EpgGrid, int>(nameof(ScrollToNowRequest));

    public IEpgGridSource? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public EpgProgram? SelectedProgram { get => GetValue(SelectedProgramProperty); set => SetValue(SelectedProgramProperty, value); }
    public int ScrollToNowRequest { get => GetValue(ScrollToNowRequestProperty); set => SetValue(ScrollToNowRequestProperty, value); }

    public event Action<LiveChannel, EpgProgram>? ProgramClicked;
    public event Action<LiveChannel, EpgProgram?>? ProgramActivated;

    private const double RowHeight = 62;
    private const double HeaderHeight = 42;
    private const double ChannelWidth = 230;
    private const double PxPerMinute = 4.6;
    private const double TotalMinutes = 24 * 60 + 180; // de -1h à +26h

    private double _scrollX;
    private double _scrollY;
    private Point? _dragStart;
    private Point _dragOrigin;
    private bool _dragged;
    private readonly Dictionary<string, Bitmap?> _logos = new();
    private readonly DispatcherTimer _tick;
    private (LiveChannel Ch, EpgProgram P)? _hover;

    public EpgGrid()
    {
        ClipToBounds = true;
        Focusable = true;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => InvalidateVisual());

    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tick.Start();
        Theming.ThemeService.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tick.Stop();
        // Événement statique : sans désabonnement, chaque grille (et ses logos) resterait en mémoire.
        Theming.ThemeService.Changed -= InvalidateVisual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is IEpgGridSource o) o.Changed -= OnSourceChanged;
            if (change.NewValue is IEpgGridSource n) n.Changed += OnSourceChanged;
            OnSourceChanged();
        }
        else if (change.Property == SelectedProgramProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == ScrollToNowRequestProperty)
        {
            Dispatcher.UIThread.Post(ScrollToNow, DispatcherPriority.Background);
        }
    }

    private bool _firstLayout = true;

    private void OnSourceChanged()
    {
        _scrollY = Math.Min(_scrollY, MaxScrollY);
        if (_firstLayout && Bounds.Width > 0)
        {
            _firstLayout = false;
            ScrollToNow();
        }
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_firstLayout && e.NewSize.Width > 0 && Source is not null)
        {
            _firstLayout = false;
            ScrollToNow();
        }
    }

    private double GridStartMinutes => -60;
    private DateTimeOffset GridStart => (Source?.DayStart ?? DateTimeOffset.Now.Date).AddMinutes(GridStartMinutes);
    private double ContentWidth => TotalMinutes * PxPerMinute;
    private double ViewWidth => Math.Max(0, Bounds.Width - ChannelWidth);
    private double ViewHeight => Math.Max(0, Bounds.Height - HeaderHeight);
    private double MaxScrollX => Math.Max(0, ContentWidth - ViewWidth);
    private double MaxScrollY => Math.Max(0, (Source?.Channels.Count ?? 0) * RowHeight - ViewHeight);

    private double XOf(DateTimeOffset t) => ChannelWidth + (t - GridStart).TotalMinutes * PxPerMinute - _scrollX;

    public void ScrollToNow()
    {
        var now = DateTimeOffset.Now;
        var x = (now - GridStart).TotalMinutes * PxPerMinute - 60 * PxPerMinute * 0.5;
        if (Source is not null && Math.Abs((now - Source.DayStart).TotalHours - 12) > 14)
            x = 18 * 60 * PxPerMinute; // autre jour : début de soirée
        _scrollX = Math.Clamp(x, 0, MaxScrollX);
        InvalidateVisual();
    }

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext ctx)
    {
        var src = Source;
        var w = Bounds.Width;
        var h = Bounds.Height;
        var bg = Res("AppBg", Brushes.Black);
        var surface = Res("Surface", Brushes.DimGray);
        var surfaceHover = Res("SurfaceHover", Brushes.Gray);
        var border = Res("BorderSubtle", Brushes.Gray);
        var text = Res("TextPrimary", Brushes.White);
        var text2 = Res("TextSecondary", Brushes.LightGray);
        var muted = Res("TextMuted", Brushes.Gray);
        var accent = Res("Accent", Brushes.MediumPurple);
        var accentSoft = Res("AccentSoft", Brushes.MediumPurple);
        var live = Res("Live", Brushes.Red);
        var panel = Res("AppBgAlt", Brushes.Black);

        ctx.FillRectangle(bg, new Rect(0, 0, w, h));
        if (src is null || src.Channels.Count == 0)
        {
            DrawText(ctx, Loc.T("Epg.NoChannelInCategory"), new Point(24, HeaderHeight + 24), 14, text2, w - 48);
            return;
        }

        var channels = src.Channels;
        int first = Math.Max(0, (int)(_scrollY / RowHeight));
        int last = Math.Min(channels.Count - 1, (int)((_scrollY + ViewHeight) / RowHeight) + 1);
        src.EnsureLoaded(first, last);

        var now = DateTimeOffset.Now;
        var nowX = XOf(now);
        var viewStart = GridStart.AddMinutes(_scrollX / PxPerMinute);
        var viewEnd = viewStart.AddMinutes(ViewWidth / PxPerMinute);

        // ── programmes
        using (ctx.PushClip(new Rect(ChannelWidth, HeaderHeight, ViewWidth, ViewHeight)))
        {
            for (int i = first; i <= last; i++)
            {
                var ch = channels[i];
                var y = HeaderHeight + i * RowHeight - _scrollY;
                var programs = src.GetPrograms(ch);
                if (programs is null || programs.Count == 0)
                {
                    var r = new Rect(ChannelWidth + 2, y + 3, ViewWidth - 4, RowHeight - 6);
                    ctx.DrawRectangle(surface, null, new RoundedRect(r, 8));
                    DrawText(ctx, programs is null ? "…" : Loc.T("Epg.NoProgram"), new Point(ChannelWidth + 14, y + RowHeight / 2 - 9), 13, muted, ViewWidth - 30);
                    continue;
                }
                foreach (var p in programs)
                {
                    if (p.End <= viewStart || p.Start >= viewEnd) continue;
                    var x1 = Math.Max(XOf(p.Start), ChannelWidth - 6);
                    var x2 = XOf(p.End);
                    if (x2 - x1 < 2) continue;
                    var rect = new Rect(x1 + 2, y + 3, Math.Max(1, x2 - x1 - 4), RowHeight - 6);
                    bool isNow = p.IsLiveAt(now);
                    bool isPast = p.End <= now;
                    bool selected = SelectedProgram is { } sp && sp.ChannelKey == p.ChannelKey && sp.Start == p.Start;
                    bool hover = _hover is { } hv && hv.P == p;
                    var fill = isNow ? accentSoft : hover ? surfaceHover : surface;
                    ctx.DrawRectangle(fill, selected ? new Pen(accent, 2) : null, new RoundedRect(rect, 8));
                    if (isNow)
                    {
                        var prog = p.ProgressAt(now) / 100.0;
                        ctx.DrawRectangle(accent, null, new RoundedRect(new Rect(rect.X + 8, rect.Bottom - 5, Math.Max(0, (rect.Width - 16) * prog), 2.5), 1.2));
                    }
                    if (rect.Width > 26)
                    {
                        var tx = Math.Max(rect.X + 10, ChannelWidth + 8);
                        var maxW = rect.Right - tx - 8;
                        if (maxW > 12)
                        {
                            DrawText(ctx, p.Title, new Point(tx, rect.Y + 9), 13.5, isPast ? text2 : text, maxW, FontWeight.SemiBold);
                            DrawText(ctx, p.TimeRange, new Point(tx, rect.Y + 30), 11.5, isPast ? muted : text2, maxW);
                        }
                    }
                }
            }
            // ligne « maintenant »
            if (nowX >= ChannelWidth && nowX <= w)
                ctx.FillRectangle(live, new Rect(nowX - 1, HeaderHeight, 2, ViewHeight));
        }

        // ── en-tête horaire
        ctx.FillRectangle(panel, new Rect(0, 0, w, HeaderHeight));
        ctx.FillRectangle(border, new Rect(0, HeaderHeight - 1, w, 1));
        using (ctx.PushClip(new Rect(ChannelWidth, 0, ViewWidth, HeaderHeight)))
        {
            var t = new DateTimeOffset(viewStart.Year, viewStart.Month, viewStart.Day, viewStart.Hour, viewStart.Minute >= 30 ? 30 : 0, 0, viewStart.Offset);
            while (t < viewEnd)
            {
                var x = XOf(t);
                bool full = t.Minute == 0;
                ctx.FillRectangle(border, new Rect(x, full ? 22 : 28, 1, full ? 20 : 14));
                var label = t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                DrawText(ctx, label, new Point(x + 6, 12), full ? 13 : 12, full ? text : muted, 60, full ? FontWeight.SemiBold : FontWeight.Normal);
                t = t.AddMinutes(30);
            }
            if (nowX >= ChannelWidth && nowX <= w)
            {
                var nowLabel = new RoundedRect(new Rect(nowX - 26, 10, 52, 22), 11);
                ctx.DrawRectangle(live, null, nowLabel);
                DrawText(ctx, now.ToLocalTime().ToString("HH:mm"), new Point(nowX - 18, 13), 12, Brushes.White, 40, FontWeight.Bold);
            }
        }

        // ── colonne des chaînes
        ctx.FillRectangle(panel, new Rect(0, HeaderHeight, ChannelWidth, ViewHeight));
        ctx.FillRectangle(border, new Rect(ChannelWidth - 1, 0, 1, h));
        DrawText(ctx, Source?.DayStart is { } ds ? TextNorm.DayLabel(ds) : "", new Point(16, 12), 13, text, ChannelWidth - 30, FontWeight.SemiBold);
        using (ctx.PushClip(new Rect(0, HeaderHeight, ChannelWidth, ViewHeight)))
        {
            for (int i = first; i <= last; i++)
            {
                var ch = channels[i];
                var y = HeaderHeight + i * RowHeight - _scrollY;
                if (ch.IsPlaying) ctx.FillRectangle(accentSoft, new Rect(0, y, ChannelWidth - 1, RowHeight));
                var logoRect = new Rect(12, y + 11, 56, 40);
                ctx.DrawRectangle(surface, null, new RoundedRect(logoRect, 8));
                var logo = GetLogo(ch);
                if (logo is not null)
                {
                    var s = logo.Size;
                    var scale = Math.Min((logoRect.Width - 8) / s.Width, (logoRect.Height - 8) / s.Height);
                    var dw = s.Width * scale;
                    var dh = s.Height * scale;
                    ctx.DrawImage(logo, new Rect(0, 0, s.Width, s.Height),
                        new Rect(logoRect.X + (logoRect.Width - dw) / 2, logoRect.Y + (logoRect.Height - dh) / 2, dw, dh));
                }
                else
                {
                    DrawText(ctx, ch.Initials, new Point(logoRect.X + 14, logoRect.Y + 11), 13, muted, 40, FontWeight.Bold);
                }
                if (ch.Number > 0)
                    DrawText(ctx, ch.Number.ToString(CultureInfo.InvariantCulture), new Point(78, y + 12), 11, muted, 60);
                DrawText(ctx, TextNorm.StripPrefix(ch.Name), new Point(78, ch.Number > 0 ? y + 27 : y + 21), 13.5, text, ChannelWidth - 92, FontWeight.Medium);
                ctx.FillRectangle(border, new Rect(8, y + RowHeight - 0.5, ChannelWidth - 16, 0.5));
            }
        }
    }

    private Bitmap? GetLogo(LiveChannel ch)
    {
        var url = ch.ImageUrl;
        if (string.IsNullOrEmpty(url)) return null;
        if (_logos.TryGetValue(url, out var bmp)) return bmp;
        _logos[url] = null;
        _ = LoadLogoAsync(url);
        return null;
    }

    private async Task LoadLogoAsync(string url)
    {
        var bmp = await ImageLoader.GetAsync(url, 112);
        if (bmp is null) return;
        _logos[url] = bmp;
        InvalidateVisual();
    }

    private static readonly Typeface Regular = new(FontFamily.Default);

    private static void DrawText(DrawingContext ctx, string text, Point origin, double size, IBrush brush, double maxWidth, FontWeight weight = FontWeight.Normal)
    {
        if (maxWidth <= 4 || string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        ctx.DrawText(ft, origin);
    }

    // ───────────── Interaction ─────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            _scrollX = Math.Clamp(_scrollX - (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 120, 0, MaxScrollX);
        else
            _scrollY = Math.Clamp(_scrollY - e.Delta.Y * RowHeight * 1.5, 0, MaxScrollY);
        InvalidateVisual();
        e.Handled = true;
    }

    private (LiveChannel Ch, EpgProgram? P, bool OnChannel)? HitTest(Point p)
    {
        var src = Source;
        if (src is null || p.Y < HeaderHeight) return null;
        int row = (int)((p.Y - HeaderHeight + _scrollY) / RowHeight);
        if (row < 0 || row >= src.Channels.Count) return null;
        var ch = src.Channels[row];
        if (p.X < ChannelWidth) return (ch, null, true);
        var t = GridStart.AddMinutes((p.X - ChannelWidth + _scrollX) / PxPerMinute);
        var prog = src.GetPrograms(ch)?.FirstOrDefault(x => x.Start <= t && t < x.End);
        return (ch, prog, false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetPosition(this);
        _dragStart = pt;
        _dragOrigin = new Point(_scrollX, _scrollY);
        _dragged = false;
        e.Pointer.Capture(this);
        if (e.ClickCount == 2)
        {
            var hit = HitTest(pt);
            if (hit is { } h) ProgramActivated?.Invoke(h.Ch, h.P);
            _dragStart = null;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        if (_dragStart is { } start && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var dx = pt.X - start.X;
            var dy = pt.Y - start.Y;
            if (_dragged || Math.Abs(dx) > 6 || Math.Abs(dy) > 6)
            {
                _dragged = true;
                _scrollX = Math.Clamp(_dragOrigin.X - dx, 0, MaxScrollX);
                _scrollY = Math.Clamp(_dragOrigin.Y - dy, 0, MaxScrollY);
                Cursor = new Cursor(StandardCursorType.SizeAll);
                InvalidateVisual();
            }
            return;
        }
        var hit = HitTest(pt);
        var newHover = hit is { P: { } p } h ? (h.Ch, p) : ((LiveChannel, EpgProgram)?)null;
        if (!Equals(newHover, _hover))
        {
            _hover = newHover;
            Cursor = hit is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        if (_dragStart is { } start && !_dragged)
        {
            var hit = HitTest(start);
            if (hit is { } h)
            {
                if (h.OnChannel) ProgramActivated?.Invoke(h.Ch, null);
                else if (h.P is not null) ProgramClicked?.Invoke(h.Ch, h.P);
            }
        }
        _dragStart = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Down: _scrollY = Math.Clamp(_scrollY + RowHeight, 0, MaxScrollY); break;
            case Key.Up: _scrollY = Math.Clamp(_scrollY - RowHeight, 0, MaxScrollY); break;
            case Key.PageDown: _scrollY = Math.Clamp(_scrollY + ViewHeight, 0, MaxScrollY); break;
            case Key.PageUp: _scrollY = Math.Clamp(_scrollY - ViewHeight, 0, MaxScrollY); break;
            case Key.Right: _scrollX = Math.Clamp(_scrollX + 30 * PxPerMinute, 0, MaxScrollX); break;
            case Key.Left: _scrollX = Math.Clamp(_scrollX - 30 * PxPerMinute, 0, MaxScrollX); break;
            default: return;
        }
        e.Handled = true;
        InvalidateVisual();
    }
}
