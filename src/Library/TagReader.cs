using System.Text;

namespace ZBS.Library;

/// <summary>Метаданные файла, нужные медиатеке. Всё опциональное — битые теги не мешают жить.</summary>
public sealed record FileTags(
    string? Title,
    string? Artist,
    string? Album,
    string? Genre,
    int Year,
    double DurationSeconds,
    double? ReplayGainTrackDb);

/// <summary>Чтение тегов через TagLib#. Любой сбой тега — не сбой скана.</summary>
public static class TagReader
{
    private static readonly Encoding Latin1;
    private static readonly Encoding Cp1251;

    static TagReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Latin1 = Encoding.GetEncoding("ISO-8859-1");
        Cp1251 = Encoding.GetEncoding(1251);
    }

    public static FileTags Read(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;
            var rg = tag.ReplayGainTrackGain;
            return new FileTags(
                Title: Clean(tag.Title),
                Artist: Clean(tag.FirstPerformer ?? tag.FirstAlbumArtist),
                Album: Clean(tag.Album),
                Genre: NormalizeGenre(FixLegacyCyrillic(tag.FirstGenre)),
                Year: (int)tag.Year,
                DurationSeconds: file.Properties?.Duration.TotalSeconds ?? 0,
                ReplayGainTrackDb: double.IsNaN(rg) ? null : rg);
        }
        catch (Exception)
        {
            // Битый файл/незнакомый формат — запишем в базу без тегов, играть это не мешает.
            return new FileTags(null, null, null, null, 0, 0, null);
        }
    }

    private static string? Clean(string? value) =>
        StripSiteJunk(FixLegacyCyrillic(string.IsNullOrWhiteSpace(value) ? null : value.Trim()));

    /// <summary>
    /// Текст песни из тега (USLT/LYRICS) — читается по требованию (панель «Текст»),
    /// в базу не пишется. Нет текста или файл битый — null.
    /// </summary>
    public static string? ReadLyrics(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var lyrics = file.Tag.Lyrics;
            if (string.IsNullOrWhiteSpace(lyrics)) return null;
            return FixLegacyCyrillic(lyrics.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex UrlRe = new(
        @"https?://\S+", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Приписка сайта в скобках: только если внутри домен с TLD — «(zaycev.net)» убираем,
    // «(Long Mix)»/«(feat. X)» сохраняем.
    private static readonly System.Text.RegularExpressions.Regex SiteParenRe = new(
        @"[\(\[\{]\s*(?:www\.)?[\w.\- ]+\.(?:ru|net|com|org|info|me|biz|cc|su|ua|kz)\b[^)\]\}]*[\)\]\}]?",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Голый домен без скобок (напр. «wap.sasisa.ru»).
    private static readonly System.Text.RegularExpressions.Regex BareSiteRe = new(
        @"\b(?:www\.)?[\w\-]+(?:\.[\w\-]+)*\.(?:ru|net|com|org|info|me|biz|cc|su|ua|kz)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex MultiSpaceRe = new(
        @"\s{2,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Убирает мусор от сайтов-качалок (URL, «(zaycev.net)», префиксы) из строки тега.</summary>
    public static string? StripSiteJunk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var v = UrlRe.Replace(s, "");
        v = SiteParenRe.Replace(v, "");
        v = BareSiteRe.Replace(v, "");
        // Пустые скобки, оставшиеся после вырезания домена изнутри.
        v = System.Text.RegularExpressions.Regex.Replace(v, @"[\(\[\{]\s*[\)\]\}]", "");
        v = MultiSpaceRe.Replace(v, " ");
        // Висячие разделители по краям — но НЕ скобки (легитимные «(Long Mix)» не трогаем).
        v = v.Trim().Trim('-', '_', '.', '·', '|', ' ').Trim();
        v = MultiSpaceRe.Replace(v, " ");
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>Имя файла как запасное название: подчёркивания в пробелы + чистка сайт-мусора.</summary>
    public static string CleanFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath).Replace('_', ' ');
        return StripSiteJunk(name) ?? Path.GetFileNameWithoutExtension(filePath);
    }

    /// <summary>
    /// Восстановление русских тегов, записанных в windows-1251, но помеченных как Latin-1
    /// (болезнь старых mp3): TagLib отдаёт их «крякозябрами» вида «Áðàî». Определяем такой
    /// текст по преобладанию латинских «высоких» символов и перекодируем 1251→кириллица.
    /// Нормальную латиницу и уже-кириллицу не трогаем.
    /// </summary>
    public static string? FixLegacyCyrillic(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Any(c => c is >= 'Ѐ' and <= 'ӿ')) return s; // уже кириллица — тег корректный

        // Ищем самую длинную СЕРИЮ подряд идущих «высоких» символов: кириллическое слово в
        // Latin-1-мусоре даёт ≥2 подряд («Áåñòà»), а реальный акцент в латинице — одиночный (café).
        // Так ловим и смешанные строки вида «(rap-game.ru) Áåñòà», где кириллица в меньшинстве.
        int maxRun = 0, run = 0;
        foreach (var c in s)
        {
            if (c > 'ÿ') return s; // есть символы вне Latin-1 — это не 1251-мусор
            if (c is (>= 'À' and <= 'ÿ') or '¨' or '¸')
            {
                if (++run > maxRun) maxRun = run;
            }
            else run = 0;
        }
        if (maxRun < 2) return s;

        try
        {
            var recovered = Cp1251.GetString(Latin1.GetBytes(s));
            if (recovered.Any(c => c is >= 'Ѐ' and <= 'ӿ') && !recovered.Contains('�'))
                return recovered;
        }
        catch (Exception)
        {
            // не вышло — оставляем как есть
        }
        return s;
    }

    private static readonly System.Text.RegularExpressions.Regex SeparatorRuns =
        new(@"[\s\-_]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Жанры в тегах — зоопарк. Приводим к единому виду:
    /// - многозначный тег («Hip-hop, rap», «Rock/Metal», «Pop;Dance») → первый жанр;
    /// - дефис/подчёркивание/повторные пробелы → один пробел («Hip-hop» == «Hip hop»);
    /// - регистр → Первая заглавная, остальное строчными («BLUES»/«blues» → «Blues»);
    /// - числовые коды ID3v1 («255», «(17)») и односимвольный мусор → null.
    /// НЕ трогаем склейки без разделителя («folkrock», «ambientgenre») — это уже тег-редактор.
    /// </summary>
    public static string? NormalizeGenre(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();

        // Многозначный тег — берём первый жанр (разделители значений: , ; / \ |).
        var sep = value.IndexOfAny(new[] { ',', ';', '/', '\\', '|' });
        if (sep > 0) value = value[..sep].Trim();

        if (value.StartsWith('(') && value.EndsWith(')'))
            value = value[1..^1].Trim(); // «(17)» — обёртка числового кода

        value = SeparatorRuns.Replace(value, " ").Trim();
        if (value.Length < 2) return null;
        if (value.All(char.IsDigit)) return null; // числовой код ID3v1 — не имя жанра
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
