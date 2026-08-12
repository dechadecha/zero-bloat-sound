using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZBS.UI.Desktop.Controls;

/// <summary>
/// Waveform-сикбар (M3.4): рисует пики трека, сыгранная часть — акцентом.
/// Лежит ПОД обычным слайдером (перемотка остаётся у слайдера) — чистая подложка.
/// </summary>
public sealed class WaveformControl : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformControl, double>(nameof(Progress));

    private static readonly IBrush Played = new SolidColorBrush(Color.Parse("#00E676"));
    private static readonly IBrush Rest = new SolidColorBrush(Color.Parse("#2A2A38"));

    private float[]? _peaks;

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public void SetPeaks(float[]? peaks)
    {
        _peaks = peaks;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProgressProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var peaks = _peaks;
        var b = Bounds;
        if (peaks is null || peaks.Length == 0 || b.Width <= 0 || b.Height <= 0) return;

        // Кисти из живых ресурсов: тема перекрашивает waveform на лету.
        var res = Application.Current?.Resources;
        var played = res?["ZbsAccent"] as IBrush ?? Played;
        var rest = res?["ZbsSurface3"] as IBrush ?? Rest;

        var mid = b.Height / 2;
        var barW = b.Width / peaks.Length;
        var playedUntil = Progress * b.Width;
        for (var i = 0; i < peaks.Length; i++)
        {
            var h = Math.Max(1.2, peaks[i] * b.Height * 0.92);
            var x = i * barW;
            var rect = new Rect(x, mid - h / 2, Math.Max(0.8, barW * 0.72), h);
            ctx.DrawRectangle(x <= playedUntil ? played : rest, null, rect);
        }
    }
}
