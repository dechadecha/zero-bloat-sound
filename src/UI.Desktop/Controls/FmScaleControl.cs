using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZBS.UI.Desktop.Controls;

/// <summary>
/// Шкала частот «как на старом магнитофоне»: риски 87.5–108 МГц, цифры, точки станций
/// и красная стрелка-указатель текущей частоты. Лежит под ползунком тюнера.
/// </summary>
public sealed class FmScaleControl : Control
{
    public static readonly StyledProperty<double> FrequencyProperty =
        AvaloniaProperty.Register<FmScaleControl, double>(nameof(Frequency), 100.0);

    public static readonly StyledProperty<double> MinFreqProperty =
        AvaloniaProperty.Register<FmScaleControl, double>(nameof(MinFreq), 87.5);

    public static readonly StyledProperty<double> MaxFreqProperty =
        AvaloniaProperty.Register<FmScaleControl, double>(nameof(MaxFreq), 108.0);

    public double Frequency { get => GetValue(FrequencyProperty); set => SetValue(FrequencyProperty, value); }
    public double MinFreq { get => GetValue(MinFreqProperty); set => SetValue(MinFreqProperty, value); }
    public double MaxFreq { get => GetValue(MaxFreqProperty); set => SetValue(MaxFreqProperty, value); }

    /// <summary>Частоты станций для точек на шкале (ставит вьюха).</summary>
    public IReadOnlyList<double>? StationMhz { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrequencyProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 20 || b.Height <= 10) return;

        var res = Avalonia.Application.Current?.Resources;
        var muted = res?["ZbsTextMuted"] as IBrush ?? Brushes.Gray;
        var faint = res?["ZbsTextFaint"] as IBrush ?? Brushes.DimGray;
        var accent = res?["ZbsAccent"] as IBrush ?? Brushes.LimeGreen;

        var min = MinFreq;
        var span = MaxFreq - min;
        // Отступы по краям — под половину thumb'а слайдера, чтобы шкала совпадала с ползунком.
        const double pad = 10;
        var w = b.Width - pad * 2;
        double X(double mhz) => pad + (mhz - min) / span * w;

        var tickPen = new Pen(faint, 1);
        var majorPen = new Pen(muted, 1);
        var baseY = 6.0;

        // Риски: каждые 0.5 МГц мелкая, каждый целый МГц — крупная, каждые 2 МГц — цифра.
        for (var f = Math.Ceiling(min * 2) / 2; f <= MaxFreq + 0.001; f += 0.5)
        {
            var x = X(f);
            var isMajor = Math.Abs(f - Math.Round(f)) < 0.001;
            var h = isMajor ? 14.0 : 7.0;
            ctx.DrawLine(isMajor ? majorPen : tickPen, new Point(x, baseY), new Point(x, baseY + h));
            if (isMajor && (int)Math.Round(f) % 2 == 0)
            {
                var ft = new FormattedText(((int)Math.Round(f)).ToString(),
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default), 10, faint);
                ctx.DrawText(ft, new Point(x - ft.Width / 2, baseY + 17));
            }
        }

        // Точки станций — «огоньки» на шкале.
        if (StationMhz is not null)
        {
            foreach (var mhz in StationMhz)
            {
                var x = X(mhz);
                ctx.DrawEllipse(accent, null, new Point(x, baseY + 34), 2.2, 2.2);
            }
        }

        // Стрелка текущей частоты — во всю высоту шкалы.
        var fx = X(Math.Clamp(Frequency, min, MaxFreq));
        var needle = new Pen(accent, 2);
        ctx.DrawLine(needle, new Point(fx, 0), new Point(fx, b.Height - 2));
    }
}
