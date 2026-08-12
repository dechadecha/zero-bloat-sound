using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ZBS.Skins;
using ZBS.UI.Desktop.ViewModels;

namespace ZBS.UI.Desktop.Controls;

/// <summary>
/// Рендерер скина (M3.3): рисует SkinModel из спрайт-листов одним контролом.
/// Пиксель-арт масштабируется целым множителем без сглаживания (классика должна хрустеть).
/// Ввод: кнопки/тумблеры/слайдеры по хит-зонам, всё остальное — перетаскивание окна.
/// </summary>
public sealed class SkinControl : Control
{
    private const double Scale = 2;

    private readonly SkinPackage _package;
    private readonly MainViewModel _vm;
    private readonly Action _closeRequested;
    private readonly Action _minimizeRequested;
    private readonly Dictionary<string, Bitmap> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    private SkinElement? _pressed;
    private SkinElement? _dragSlider;
    private int _tick;
    private string? _fallbackText;
    private FormattedText? _fallbackFt;

    public SkinControl(SkinPackage package, MainViewModel vm, Action closeRequested, Action minimizeRequested)
    {
        _package = package;
        _vm = vm;
        _closeRequested = closeRequested;
        _minimizeRequested = minimizeRequested;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        foreach (var (name, bytes) in package.Images)
        {
            if (!name.EndsWith(".bmp") && !name.EndsWith(".png")) continue;
            try
            {
                _sheets[name] = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                // битый лист — элементы на нём просто не отрисуются
            }
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) =>
        {
            _tick++;
            InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        foreach (var b in _sheets.Values) b.Dispose();
        _sheets.Clear();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = _package.Model.Manifest.Size;
        return new Size(size[0] * Scale, size[1] * Scale);
    }

    private static Rect D(int x, int y, int w, int h) => new(x * Scale, y * Scale, w * Scale, h * Scale);

    /// <summary>
    /// DrawImage с клипом source-прямоугольника по фактическому размеру листа:
    /// реальные .wsz часто с урезанными BMP (volume.bmp без строки бегунка и т.п.) —
    /// выход за границы даёт мусор или исключение рендера. Обрезаем src и dest согласованно.
    /// </summary>
    private static void DrawClamped(DrawingContext ctx, Bitmap sheet, Rect src, Rect dest)
    {
        var bounds = new Rect(sheet.Size);
        var clipped = src.Intersect(bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) return;
        if (clipped != src)
        {
            dest = new Rect(
                dest.X + (clipped.X - src.X) * Scale,
                dest.Y + (clipped.Y - src.Y) * Scale,
                clipped.Width * Scale,
                clipped.Height * Scale);
        }
        ctx.DrawImage(sheet, clipped, dest);
    }

    private double Progress =>
        _vm.DurationSeconds > 0 ? Math.Clamp(_vm.PositionSeconds / _vm.DurationSeconds, 0, 1) : 0;

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        foreach (var el in _package.Model.Elements)
        {
            if (!_sheets.TryGetValue(el.Sheet, out var sheet))
            {
                // Скин без шрифта (text.bmp) — название рисуем обычным текстом.
                if (el.Type == SkinElementType.Marquee)
                    DrawMarquee(ctx, null, el);
                continue;
            }
            switch (el.Type)
            {
                case SkinElementType.Background:
                    var src = el.Src is { } s
                        ? new Rect(s[0], s[1], el.W, el.H)
                        : new Rect(0, 0, el.W, el.H);
                    DrawClamped(ctx, sheet, src, D(el.X, el.Y, el.W, el.H));
                    break;

                case SkinElementType.Button:
                    DrawFrame(ctx, sheet, el, _pressed == el ? el.SrcPressed : el.Src);
                    break;

                case SkinElementType.Toggle:
                    var on = ToggleState(el.Action);
                    var frame = on
                        ? (_pressed == el ? el.SrcOnPressed : el.SrcOn)
                        : (_pressed == el ? el.SrcPressed : el.Src);
                    DrawFrame(ctx, sheet, el, frame);
                    break;

                case SkinElementType.SliderH:
                    DrawSlider(ctx, sheet, el);
                    break;

                case SkinElementType.Digits:
                    DrawTime(ctx, sheet, el);
                    break;

                case SkinElementType.Marquee:
                    DrawMarquee(ctx, sheet, el);
                    break;

                case SkinElementType.Indicator:
                    var ind = _vm.IsPlaying ? el.Src : _vm.PositionSeconds > 0.5 ? el.SrcPressed : el.SrcOn;
                    DrawFrame(ctx, sheet, el, ind);
                    break;
            }
        }
    }

    private void DrawFrame(DrawingContext ctx, Bitmap sheet, SkinElement el, int[]? src)
    {
        src ??= el.Src;
        if (src is null) return;
        DrawClamped(ctx, sheet, new Rect(src[0], src[1], el.W, el.H), D(el.X, el.Y, el.W, el.H));
    }

    private void DrawSlider(DrawingContext ctx, Bitmap sheet, SkinElement el)
    {
        var value = el.Action == "Volume" ? Math.Clamp(_vm.Volume, 0, 1) : Progress;

        if (el.SrcBg is { } bg)
        {
            var frame = el.Frames > 1 ? (int)Math.Round(value * (el.Frames - 1)) : 0;
            var srcY = bg[1] + frame * el.FrameStride;
            DrawClamped(ctx, sheet, new Rect(bg[0], srcY, el.W, el.H), D(el.X, el.Y, el.W, el.H));
        }

        if (el.Thumb is { } t && _sheets.TryGetValue(el.ThumbSheet ?? el.Sheet, out var thumbSheet))
        {
            var pressed = _dragSlider == el && el.ThumbPressed is not null;
            var sx = pressed ? el.ThumbPressed![0] : t[0];
            var sy = pressed ? el.ThumbPressed![1] : t[1];
            var tx = el.X + (int)Math.Round(value * (el.W - t[2]));
            var ty = el.Y + Math.Max(0, (el.H - t[3]) / 2);
            DrawClamped(ctx, thumbSheet, new Rect(sx, sy, t[2], t[3]), D(tx, ty, t[2], t[3]));
        }
    }

    private void DrawTime(DrawingContext ctx, Bitmap sheet, SkinElement el)
    {
        if (el.DigitsX is null) return;
        var total = (int)Math.Max(0, _vm.PositionSeconds);
        var mm = Math.Min(99, total / 60);
        var ss = total % 60;
        int[] digits = { mm / 10, mm % 10, ss / 10, ss % 10 };
        for (var i = 0; i < 4 && i < el.DigitsX.Length; i++)
            DrawClamped(ctx, sheet,
                new Rect(digits[i] * el.DigitW, 0, el.DigitW, el.DigitH),
                D(el.DigitsX[i], el.Y, el.DigitW, el.DigitH));
    }

    private const string FontRow0 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ\"@   ";
    private const string FontRow1 = "0123456789 ._:()-'!_+\\/[]^&%,=$#";

    private void DrawMarquee(DrawingContext ctx, Bitmap? sheet, SkinElement el)
    {
        var text = _vm.MiniTitle;
        if (string.IsNullOrWhiteSpace(text)) text = "ZERO-BLOAT SOUND";

        // Кириллица/нет шрифта/кривые размеры глифа — фолбэк обычным текстом.
        var upper = text.ToUpperInvariant();
        var hasUnknown = sheet is null || el.CharW <= 0 || el.CharH <= 0 ||
                         upper.Any(c => FontRow0.IndexOf(c) < 0 && FontRow1.IndexOf(c) < 0);
        if (hasUnknown)
        {
            // FormattedText кэшируется по строке: тик 15 раз/с не должен аллоцировать.
            if (_fallbackText != text || _fallbackFt is null)
            {
                _fallbackText = text;
                _fallbackFt = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, Typeface.Default, 6.5 * Scale, Brushes.LightGreen);
            }
            using (ctx.PushClip(D(el.X, el.Y - 1, el.W, Math.Max(el.CharH, 6) + 2)))
                ctx.DrawText(_fallbackFt, new Point(el.X * Scale, (el.Y - 1) * Scale));
            return;
        }

        var visible = el.W / el.CharW;
        var padded = upper + "   *** ";
        var offset = (_tick / 3) % padded.Length;
        for (var i = 0; i < visible; i++)
        {
            var c = padded[(offset + i) % padded.Length];
            int row, col;
            var idx0 = FontRow0.IndexOf(c);
            if (idx0 >= 0) { row = 0; col = idx0; }
            else
            {
                var idx1 = FontRow1.IndexOf(c);
                if (idx1 < 0) continue;
                row = 1;
                col = idx1;
            }
            DrawClamped(ctx, sheet!,
                new Rect(col * el.CharW, row * el.CharH, el.CharW, el.CharH),
                D(el.X + i * el.CharW, el.Y, el.CharW, el.CharH));
        }
    }

    private bool ToggleState(string? action) => action switch
    {
        "Shuffle" => _vm.ShuffleActive,
        "Repeat" => _vm.RepeatActive,
        _ => false,
    };

    // ---------- ввод ----------

    private SkinElement? HitTest(Point p)
    {
        var x = p.X / Scale;
        var y = p.Y / Scale;
        for (var i = _package.Model.Elements.Count - 1; i >= 0; i--)
        {
            var el = _package.Model.Elements[i];
            if (el.Type is SkinElementType.Background or SkinElementType.Digits
                or SkinElementType.Marquee or SkinElementType.Indicator)
                continue;
            if (x >= el.X && x < el.X + el.W && y >= el.Y && y < el.Y + el.H)
                return el;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Только левая кнопка: правой в классике — меню, а не «нажать Close».
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var hit = HitTest(e.GetPosition(this));
        if (hit is null)
        {
            // Пустое место (и титлбар) — тащим окно, как в классике.
            if (VisualRoot is Window w)
                w.BeginMoveDrag(e);
            return;
        }

        if (hit.Type == SkinElementType.SliderH)
        {
            _dragSlider = hit;
            ApplySlider(hit, e.GetPosition(this));
        }
        else
        {
            _pressed = hit;
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragSlider is { } slider)
            ApplySlider(slider, e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is { } pressed && HitTest(e.GetPosition(this)) == pressed)
            Execute(pressed.Action);
        _pressed = null;
        _dragSlider = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Alt-Tab/попап посреди драга: без сброса залипший _dragSlider сикал бы при простом наведении.
        _pressed = null;
        _dragSlider = null;
        InvalidateVisual();
    }

    private void ApplySlider(SkinElement el, Point p)
    {
        var thumbW = el.Thumb is { } t ? t[2] : 0;
        var travel = Math.Max(1, el.W - thumbW);
        var value = Math.Clamp((p.X / Scale - el.X - thumbW / 2.0) / travel, 0, 1);
        if (el.Action == "Volume")
            _vm.Volume = value;
        else if (_vm.DurationSeconds > 0)
            _vm.PositionSeconds = value * _vm.DurationSeconds;
        InvalidateVisual();
    }

    private void Execute(string? action)
    {
        switch (action)
        {
            case "PlayPause":
            case "Pause": _vm.PlayPauseCommand.Execute(null); break;
            case "Stop": _vm.StopCommand.Execute(null); break;
            case "Next": _vm.NextCommand.Execute(null); break;
            case "Prev": _vm.PreviousCommand.Execute(null); break;
            case "Eject": _vm.OpenFolderCommand.Execute(null); break;
            case "Shuffle": _vm.ShuffleCommand.Execute(null); break;
            case "Repeat": _vm.RepeatCommand.Execute(null); break;
            case "Close": _closeRequested(); break;
            case "Minimize": _minimizeRequested(); break;
        }
    }
}
