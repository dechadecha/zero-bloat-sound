using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ZBS.UI.Desktop.Controls;

/// <summary>
/// Базовый визуализатор M3.2: столбики спектра из FFT движка.
/// Источник данных вешает вьюха (SpectrumSource); контрол сам тикает таймером,
/// пока видим, и плавно затухает на паузе/тишине. Рисование — DrawingContext, без бинарей.
/// </summary>
public sealed class SpectrumControl : Control
{
    public static readonly StyledProperty<int> BarCountProperty =
        AvaloniaProperty.Register<SpectrumControl, int>(nameof(BarCount), 28);

    public static readonly StyledProperty<IBrush> BarBrushProperty =
        AvaloniaProperty.Register<SpectrumControl, IBrush>(nameof(BarBrush),
            new SolidColorBrush(Color.Parse("#00E676")));

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

        // Акцент из живых ресурсов: .zbs-тема перекрашивает спектр без перезапуска.
        var brush = Application.Current?.Resources["ZbsAccent"] as IBrush ?? BarBrush;

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
}
