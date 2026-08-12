using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ZBS.UI.Desktop.Controls;

/// <summary>Режим отрисовки визуализатора (переключается кликом, сохраняется в настройках).</summary>
public enum VizMode { Bars, Wave, SmoothPeaks, Radial }

/// <summary>
/// Визуализатор спектра из FFT движка. Режимы: столбики / волна-осциллограф /
/// сглаженные пики / радиальное кольцо. Источник данных вешает вьюха (SpectrumSource);
/// контрол сам тикает таймером, пока видим, и плавно затухает на паузе/тишине.
/// </summary>
public sealed class SpectrumControl : Control
{
    public static readonly StyledProperty<int> BarCountProperty =
        AvaloniaProperty.Register<SpectrumControl, int>(nameof(BarCount), 28);

    public static readonly StyledProperty<IBrush> BarBrushProperty =
        AvaloniaProperty.Register<SpectrumControl, IBrush>(nameof(BarBrush),
            new SolidColorBrush(Color.Parse("#00E676")));

    public static readonly StyledProperty<VizMode> ModeProperty =
        AvaloniaProperty.Register<SpectrumControl, VizMode>(nameof(Mode));

    public int BarCount
    {
        get => GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public IBrush BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public VizMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Забор FFT у движка (true — данные есть). Вешает вьюха после DataContext.</summary>
    public Func<float[], bool>? SpectrumSource { get; set; }

    private readonly float[] _fft = new float[1024]; // FFT2048 → 1024 бина
    private double[] _bars = Array.Empty<double>();
    private DispatcherTimer? _timer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
    }

    private void Tick()
    {
        if (!IsEffectivelyVisible) return;
        // Свёрнутое окно остаётся «effectively visible» — не жжём CPU на FFT в трее.
        if (VisualRoot is Window { WindowState: WindowState.Minimized }) return;
        var count = Math.Max(4, BarCount);
        if (_bars.Length != count) _bars = new double[count];

        var hasData = SpectrumSource?.Invoke(_fft) == true;
        for (var i = 0; i < count; i++)
        {
            double target = 0;
            if (hasData)
            {
                // Логарифмическая раскладка бинов по полосам: басам — узко, верхам — широко.
                var lo = BinFor(i, count);
                var hi = Math.Max(lo + 1, BinFor(i + 1, count));
                float peak = 0;
                for (var b = lo; b < hi && b < _fft.Length; b++)
                    if (_fft[b] > peak) peak = _fft[b];
                target = Math.Clamp(Math.Sqrt(peak) * 3.0, 0, 1);
            }
            // Вверх — мгновенно, вниз — плавное затухание (так живее).
            _bars[i] = target >= _bars[i] ? target : Math.Max(0, _bars[i] - 0.06);
        }
        InvalidateVisual();
    }

    private int BinFor(int band, int count) =>
        (int)Math.Pow(_fft.Length - 1, (double)band / count);

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var count = _bars.Length;
        if (count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        // Акцент из живых ресурсов: .zbs-тема перекрашивает визуализатор без перезапуска.
        var brush = Application.Current?.Resources["ZbsAccent"] as IBrush ?? BarBrush;

        switch (Mode)
        {
            case VizMode.Wave: RenderWave(context, bounds, brush); break;
            case VizMode.SmoothPeaks: RenderSmooth(context, bounds, brush); break;
            case VizMode.Radial: RenderRadial(context, bounds, brush); break;
            default: RenderBars(context, bounds, brush); break;
        }
    }

    private void RenderBars(DrawingContext context, Rect bounds, IBrush brush)
    {
        var count = _bars.Length;
        var gap = Math.Max(1, bounds.Width / count * 0.25);
        var barWidth = (bounds.Width - gap * (count - 1)) / count;
        if (barWidth <= 0) return;
        for (var i = 0; i < count; i++)
        {
            var h = Math.Max(2, _bars[i] * bounds.Height);
            var rect = new Rect(i * (barWidth + gap), bounds.Height - h, barWidth, h);
            context.DrawRectangle(brush, null, new RoundedRect(rect, 1.5));
        }
    }

    // Осциллограф: симметричная волна вокруг центра, амплитуда — по спектру. «Колеблется в такт».
    private void RenderWave(DrawingContext context, Rect bounds, IBrush brush)
    {
        var count = _bars.Length;
        var cy = bounds.Height / 2;
        var amp = bounds.Height / 2 * 0.92;
        var top = new Point[count];
        var bottom = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var x = count == 1 ? 0 : (double)i / (count - 1) * bounds.Width;
            var v = _bars[i] * amp;
            top[i] = new Point(x, cy - v);
            bottom[count - 1 - i] = new Point(x, cy + v);
        }
        var fill = SmoothClosed(top, bottom);
        context.DrawGeometry(Dim(brush, 0.28), null, fill);              // мягкая заливка тела волны
        var line = SmoothOpen(top);
        var pen = new Pen(brush, 1.6, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, pen, line);                          // яркая верхняя линия
        context.DrawGeometry(null, new Pen(Dim(brush, 0.5), 1.2), SmoothOpen(bottom));
    }

    // Сглаженные пики: холмы от нижней кромки, плавная кривая по верхам полос.
    private void RenderSmooth(DrawingContext context, Rect bounds, IBrush brush)
    {
        var count = _bars.Length;
        var pts = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var x = count == 1 ? 0 : (double)i / (count - 1) * bounds.Width;
            var h = Math.Max(2, _bars[i] * bounds.Height * 0.98);
            pts[i] = new Point(x, bounds.Height - h);
        }
        var floor = new[] { new Point(bounds.Width, bounds.Height), new Point(0, bounds.Height) };
        context.DrawGeometry(Dim(brush, 0.30), null, SmoothClosed(pts, floor));
        context.DrawGeometry(null, new Pen(brush, 1.8, lineCap: PenLineCap.Round), SmoothOpen(pts));
    }

    // Радиальное кольцо: спектр по окружности (перекликается с логотипом-кольцом ZBS).
    private void RenderRadial(DrawingContext context, Rect bounds, IBrush brush)
    {
        var count = _bars.Length;
        var cx = bounds.Width / 2;
        var cy = bounds.Height / 2;
        var r0 = Math.Min(bounds.Width, bounds.Height) * 0.26;
        var span = Math.Min(bounds.Width, bounds.Height) * 0.22;
        var pts = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var ang = (double)i / count * Math.PI * 2 - Math.PI / 2;
            var r = r0 + _bars[i] * span;
            pts[i] = new Point(cx + Math.Cos(ang) * r, cy + Math.Sin(ang) * r);
        }
        var ring = SmoothClosedLoop(pts);
        context.DrawGeometry(Dim(brush, 0.22), null, ring);
        context.DrawGeometry(null, new Pen(brush, 1.8, lineCap: PenLineCap.Round), ring);
    }

    // --- сглаживание квадратичными безье через середины отрезков ---

    private static StreamGeometry SmoothOpen(IReadOnlyList<Point> p)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        if (p.Count == 0) return g;
        c.BeginFigure(p[0], isFilled: false);
        AppendSmooth(c, p, false);
        c.EndFigure(false);
        return g;
    }

    private static StreamGeometry SmoothClosed(IReadOnlyList<Point> top, IReadOnlyList<Point> tail)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        if (top.Count == 0) return g;
        c.BeginFigure(top[0], isFilled: true);
        AppendSmooth(c, top, false);
        foreach (var pt in tail) c.LineTo(pt);
        c.EndFigure(true);
        return g;
    }

    private static StreamGeometry SmoothClosedLoop(IReadOnlyList<Point> p)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        if (p.Count == 0) return g;
        c.BeginFigure(Mid(p[^1], p[0]), isFilled: true);
        AppendSmooth(c, p, true);
        c.EndFigure(true);
        return g;
    }

    private static void AppendSmooth(StreamGeometryContext c, IReadOnlyList<Point> p, bool loop)
    {
        for (var i = 0; i < p.Count - (loop ? 0 : 1); i++)
        {
            var cur = p[i];
            var next = p[(i + 1) % p.Count];
            c.QuadraticBezierTo(cur, Mid(cur, next)); // control = вершина, конец = середина к следующей
        }
        if (!loop) c.LineTo(p[^1]);
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static IBrush Dim(IBrush brush, double opacity) =>
        brush is ISolidColorBrush s
            ? new SolidColorBrush(s.Color, opacity)
            : new SolidColorBrush(Colors.LimeGreen, opacity);
}
