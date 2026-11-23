using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MilfTV.Localization;

namespace MilfTV.Controls;

/// <summary>Barre de progression du lecteur : fine au repos, épaissie au survol, info-bulle de temps.</summary>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<SeekBar, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<SeekBar, double>(nameof(Maximum), 1);
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<SeekBar, double>(nameof(Value));
    public static readonly StyledProperty<bool> IsLiveProperty = AvaloniaProperty.Register<SeekBar, bool>(nameof(IsLive));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SeekBar, IBrush?>(nameof(Fill));

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public event Action<double>? SeekRequested;
    public event Action? Interacted;

    private bool _hover;
    private bool _dragging;
    private double? _hoverX;
    private double _dragValue;

    static SeekBar()
    {
        AffectsRender<SeekBar>(MinimumProperty, MaximumProperty, ValueProperty, IsLiveProperty, FillProperty);
    }

    public SeekBar()
    {
        Height = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private double Range => Math.Max(0.001, Maximum - Minimum);
    private double XFor(double v) => Math.Clamp((v - Minimum) / Range, 0, 1) * Bounds.Width;
    private double ValueAt(double x) => Minimum + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * Range;

    private string Label(double v)
    {
        if (IsLive)
        {
            var behind = Maximum - v;
            return behind < 3 ? Loc.T("Player.Live") : "−" + Format(behind);
        }
        return Format(v - Minimum);
    }

    private static string Format(double s)
    {
        var ts = TimeSpan.FromSeconds(double.IsFinite(s) ? Math.Clamp(s, 0, 360000) : 0);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var cy = Bounds.Height / 2 + 4;
        var thick = _hover || _dragging ? 6.0 : 4.0;
        var track = new Rect(0, cy - thick / 2, w, thick);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), null, new RoundedRect(track, thick / 2));

        var v = _dragging ? _dragValue : Value;
        var x = XFor(v);
        var fill = Fill ?? Brushes.White;
        ctx.DrawRectangle(fill, null, new RoundedRect(new Rect(0, cy - thick / 2, x, thick), thick / 2));

        if (_hoverX is { } hx && !_dragging)
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), null,
                new RoundedRect(new Rect(Math.Min(x, hx), cy - thick / 2, Math.Abs(hx - x), thick), thick / 2));

        if (_hover || _dragging)
            ctx.DrawEllipse(Brushes.White, null, new Point(x, cy), 8, 8);

        var tipX = _dragging ? x : _hoverX;
        if (tipX is { } tx)
        {
            var text = new FormattedText(Label(_dragging ? _dragValue : ValueAt(tx)), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyle.Normal, FontWeight.SemiBold), 12, Brushes.White);
            var bw = text.Width + 16;
            var bx = Math.Clamp(tx - bw / 2, 0, Math.Max(0, w - bw));
            var bubble = new Rect(bx, cy - 34, bw, 22);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x14)), null, new RoundedRect(bubble, 6));
            ctx.DrawText(text, new Point(bx + 8, bubble.Y + 3));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        Interacted?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        Interacted?.Invoke();
        if (_dragging) _dragValue = ValueAt(x);
        else _hoverX = Math.Clamp(x, 0, Bounds.Width);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragValue = ValueAt(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        SeekRequested?.Invoke(_dragValue);
        e.Handled = true;
        InvalidateVisual();
    }
}
