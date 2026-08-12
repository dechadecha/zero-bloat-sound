using Avalonia;
using Avalonia.Media;

namespace ZBS.UI.Desktop;

/// <summary>
/// Темизация (zbs/2): подменяет Zbs*-кисти и SystemAccentColor в ресурсах приложения.
/// Все потребители — через DynamicResource, так что перекраска мгновенная и обратимая.
/// Не задан токен — берётся дефолт «Voltage Dark».
/// </summary>
public static class ThemeService
{
    // json-токен → ресурс-кисть → дефолт.
    private static readonly (string Token, string Resource, string Default)[] Map =
    {
        ("bg", "ZbsBg", "#0E0E14"),
        ("surface1", "ZbsSurface1", "#16161F"),
        ("surface2", "ZbsSurface2", "#20202C"),
        ("surface3", "ZbsSurface3", "#2A2A38"),
        ("accent", "ZbsAccent", "#00E676"),
        ("text", "ZbsText", "#F0F0F3"),
        ("textMuted", "ZbsTextMuted", "#8A8A96"),
        ("faint", "ZbsTextFaint", "#5C5C68"),
        ("line", "ZbsLine", "#14FFFFFF"),
        ("ghost", "ZbsGhost", "#3A3A48"),
    };

    /// <summary>Применить палитру темы. null — вернуть дефолт.</summary>
    public static void Apply(IReadOnlyDictionary<string, string>? theme)
    {
        if (Application.Current is not { } app) return;
        var res = app.Resources;

        Color accent = Color.Parse("#00E676");
        Color bg = Color.Parse("#0E0E14");
        foreach (var (token, resource, def) in Map)
        {
            var hex = theme is not null && theme.TryGetValue(token, out var v) ? v : def;
            if (!Color.TryParse(hex, out var color))
                color = Color.Parse(def);
            res[resource] = new SolidColorBrush(color);
            if (resource == "ZbsAccent") accent = color;
            if (resource == "ZbsBg") bg = color;
        }

        // Производные: затемнённый акцент (hover play), полупрозрачное выделение,
        // цвет иконки на акцентной кнопке (по яркости акцента).
        res["ZbsAccentDim"] = new SolidColorBrush(Shade(accent, 0.82));
        res["ZbsSelection"] = new SolidColorBrush(new Color(0x1A, accent.R, accent.G, accent.B));
        var accentLuma = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        res["ZbsAccentInk"] = new SolidColorBrush(accentLuma > 0.55 ? Shade(bg, 1.0) : Colors.White);

        // Акцент системных контролов (слайдеры/выделение FluentTheme).
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorDark1"] = Shade(accent, 0.85);
        res["SystemAccentColorDark2"] = Shade(accent, 0.70);
        res["SystemAccentColorDark3"] = Shade(accent, 0.55);
        res["SystemAccentColorLight1"] = Tint(accent, 0.20);
        res["SystemAccentColorLight2"] = Tint(accent, 0.40);
        res["SystemAccentColorLight3"] = Tint(accent, 0.60);
    }

    private static Color Shade(Color c, double factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    private static Color Tint(Color c, double factor) =>
        Color.FromRgb(
            (byte)(c.R + (255 - c.R) * factor),
            (byte)(c.G + (255 - c.G) * factor),
            (byte)(c.B + (255 - c.B) * factor));
}
