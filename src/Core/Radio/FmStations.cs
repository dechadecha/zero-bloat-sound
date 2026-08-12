namespace ZBS.Core.Radio;

/// <summary>Станция «частотного» режима: обычное FM-радио, пойманное через интернет-поток.</summary>
public sealed record FmStation(double Mhz, string Name, string Url);

/// <summary>
/// Вшитая шкала FM-станций (частоты Москвы) с их официальными интернет-потоками.
/// Тюнер крутится по шкале 87.5–108.0 и «ловит» ближайшую станцию.
/// ⚠️ Каждый URL из списка ПРОВЕРЕН живьём 12.08.2026 (см. probe в скретчпаде):
/// протухнет — станция молчит, лечится заменой URL здесь.
/// </summary>
public static class FmStations
{
    public const double Min = 87.5;
    public const double Max = 108.0;

    public static readonly IReadOnlyList<FmStation> All = new FmStation[]
    {
        new(87.5, "Business FM", "https://bfm.hostingradio.ru:8004/fm"),
        new(88.3, "Ретро FM", "https://retroserver.streamr.ru:8043/retro256.mp3"),
        new(91.6, "Радио Культура", "https://icecast-vgtrk.cdnvideo.ru/kulturafm_mp3_192kbps"),
        new(96.0, "Дорожное радио", "https://dorognoe.hostingradio.ru:8000/dorognoe"),
        new(97.6, "Вести FM", "https://icecast-vgtrk.cdnvideo.ru/vestifm_mp3_192kbps"),
        new(98.4, "Радио Рекорд", "https://radiorecord.hostingradio.ru/rr_main96.aacp"),
        new(101.2, "DFM", "https://dfm.hostingradio.ru/dfm96.aacp"),
        new(101.7, "Наше Радио", "https://nashe1.hostingradio.ru/nashe-128.mp3"),
        new(102.1, "Радио Монте-Карло", "https://montecarlo.hostingradio.ru/montecarlo96.aacp"),
        new(103.0, "Радио Шансон", "https://chanson.hostingradio.ru:8041/chanson128.mp3"),
        new(103.4, "Радио Маяк", "https://icecast-vgtrk.cdnvideo.ru/mayakfm_mp3_192kbps"),
        new(103.7, "Радио Максимум", "https://maximum.hostingradio.ru/maximum96.aacp"),
        new(104.7, "Радио 7", "https://radio7.hostingradio.ru:8040/radio7128.mp3"),
        new(105.7, "Русское Радио", "https://rusradio.hostingradio.ru/rusradio96.aacp"),
        new(106.2, "Европа Плюс", "https://ep128.hostingradio.ru:8030/ep128"),
        new(107.4, "Хит FM", "https://hitfm.hostingradio.ru/hitfm96.aacp"),
    };

    /// <summary>Ближайшая станция к положению тюнера (или null — «шипение между станциями»).</summary>
    public static FmStation? Nearest(double mhz)
    {
        FmStation? best = null;
        var bestDist = double.MaxValue;
        foreach (var s in All)
        {
            var d = Math.Abs(s.Mhz - mhz);
            if (d < bestDist) { bestDist = d; best = s; }
        }
        return bestDist <= 0.35 ? best : null;
    }
}
