using System.Xml;

namespace ZBS.Core.Podcasts;

/// <summary>
/// Загрузка и разбор RSS-фидов подкастов (RSS 2.0 + itunes-теги). Всё сетевое — из фона.
/// </summary>
public sealed class PodcastClient : IDisposable
{
    private readonly HttpClient _http;

    public PodcastClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZeroBloatSound/0.1");
    }

    /// <summary>
    /// Умная загрузка: принимает и RSS-ссылку, и обычную страницу подкаста —
    /// если по ссылке HTML, ищет в нём объявленный RSS (link rel="alternate") и идёт туда.
    /// </summary>
    public async Task<(PodcastFeed Feed, IReadOnlyList<PodcastEpisode> Episodes)> FetchAutoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await FetchAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception first) when (first is not OperationCanceledException)
        {
            // Не RSS — возможно страница подкаста. Ищем объявленный фид в HTML.
            string html;
            try { html = await _http.GetStringAsync(url, ct).ConfigureAwait(false); }
            catch (Exception) { throw first; }
            var feedUrl = FindFeedLink(html, url);
            if (feedUrl is null) throw new InvalidOperationException(
                "по ссылке нет RSS и на странице не нашлось объявленного фида");
            return await FetchAsync(feedUrl, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ссылка на RSS из HTML-страницы (link rel alternate / явные *.rss|feed-ссылки).</summary>
    internal static string? FindFeedLink(string html, string baseUrl)
    {
        // <link ... type="application/rss+xml" ... href="..."> — атрибуты в любом порядке.
        foreach (System.Text.RegularExpressions.Match link in System.Text.RegularExpressions.Regex.Matches(
                     html, "<link\\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var tag = link.Value;
            // Только RSS: Atom наш парсер не понимает, а у подкастов индустрия — RSS 2.0.
            if (!tag.Contains("application/rss+xml", StringComparison.OrdinalIgnoreCase)) continue;
            var href = System.Text.RegularExpressions.Regex.Match(tag,
                "href\\s*=\\s*[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!href.Success) continue;
            return Resolve(baseUrl, href.Groups[1].Value);
        }
        // Фолбэк: первая ссылка, похожая на фид.
        var m = System.Text.RegularExpressions.Regex.Match(html,
            "href\\s*=\\s*[\"']([^\"']*(?:\\.rss|/feed/?|/rss/?|podcast\\.xml)[^\"']*)[\"']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? Resolve(baseUrl, m.Groups[1].Value) : null;
    }

    private static string? Resolve(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        return Uri.TryCreate(new Uri(baseUrl), href, out var rel) ? rel.ToString() : null;
    }

    /// <summary>Читает фид: шапка подкаста + эпизоды (новые сверху).</summary>
    public async Task<(PodcastFeed Feed, IReadOnlyList<PodcastEpisode> Episodes)> FetchAsync(string feedUrl, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(feedUrl, ct).ConfigureAwait(false);
        var doc = new XmlDocument();
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
               {
                   DtdProcessing = DtdProcessing.Ignore,
                   XmlResolver = null, // никакой подгрузки внешних сущностей из чужого XML
                   Async = false,
               }))
        {
            doc.Load(reader);
        }

        var channel = doc.SelectSingleNode("/rss/channel") ?? throw new InvalidOperationException("не RSS-фид");
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("itunes", "http://www.itunes.com/dtds/podcast-1.0.dtd");

        var title = channel.SelectSingleNode("title")?.InnerText.Trim() ?? feedUrl;
        var author = channel.SelectSingleNode("itunes:author", ns)?.InnerText.Trim() ?? "";
        var image = (channel.SelectSingleNode("itunes:image/@href", ns)
                     ?? channel.SelectSingleNode("image/url"))?.InnerText?.Trim()
                    ?? (channel.SelectSingleNode("itunes:image", ns) as XmlElement)?.GetAttribute("href");
        var feed = new PodcastFeed(feedUrl, title, author, string.IsNullOrWhiteSpace(image) ? null : image);

        var episodes = new List<PodcastEpisode>();
        foreach (XmlNode item in channel.SelectNodes("item")!)
        {
            var enclosure = item.SelectSingleNode("enclosure") as XmlElement;
            var audio = enclosure?.GetAttribute("url");
            if (string.IsNullOrWhiteSpace(audio)) continue;
            // Самодельные фиды кладут относительный enclosure — резолвим против адреса фида.
            if (!Uri.TryCreate(audio, UriKind.Absolute, out _))
                audio = Resolve(feedUrl, audio!) ?? audio;

            var epTitle = item.SelectSingleNode("title")?.InnerText.Trim() ?? "(без названия)";
            var guid = item.SelectSingleNode("guid")?.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(guid)) guid = audio;

            DateTime? published = null;
            var pub = item.SelectSingleNode("pubDate")?.InnerText;
            if (!string.IsNullOrWhiteSpace(pub) && DateTimeExtensionsTryParse(pub, out var dt))
                published = dt;

            var duration = ParseItunesDuration(item.SelectSingleNode("itunes:duration", ns)?.InnerText);
            var desc = item.SelectSingleNode("description")?.InnerText;
            if (desc is { Length: > 600 }) desc = desc[..600];

            episodes.Add(new PodcastEpisode(feedUrl, guid!, epTitle, audio!, published, duration, desc));
        }

        // Обычно фид уже новые-сверху, но встречается и наоборот.
        episodes.Sort((a, b) => Nullable.Compare(b.Published, a.Published));
        return (feed, episodes);
    }

    private static bool DateTimeExtensionsTryParse(string s, out DateTime dt)
    {
        // RFC 822 ("Tue, 05 Aug 2026 10:00:00 GMT") + вольности реальных фидов.
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out dt)) return true;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+[A-Z]{2,4}$", "");
        return DateTime.TryParse(cleaned, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out dt);
    }

    private static double ParseItunesDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (double.TryParse(s, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs > 60)
            return secs; // голые секунды
        var parts = s.Split(':');
        try
        {
            return parts.Length switch
            {
                3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                2 => int.Parse(parts[0]) * 60 + double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                _ => 0,
            };
        }
        catch (FormatException) { return 0; }
    }

    /// <summary>
    /// Скачивает эпизод в кэш (для перемотки и офлайна). Уже скачан — возвращает сразу.
    /// progress: 0..1 (или -1, если размер неизвестен).
    /// </summary>
    public async Task<string> DownloadEpisodeAsync(PodcastEpisode ep, string cacheDir,
        IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        var ext = GuessExtension(ep.AudioUrl);
        // Стабильный хэш guid'а: string.GetHashCode рандомизирован на каждый запуск процесса —
        // с ним кэш «не находился» после рестарта и эпизоды перекачивались заново.
        var stableHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ep.Guid)))[..12];
        var name = SafeName($"{ep.Title}_{stableHash}") + ext;
        var path = Path.Combine(cacheDir, name);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        var tmp = path + ".part";
        using var resp = await _http.GetAsync(ep.AudioUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                progress?.Report(total > 0 ? (double)done / total : -1);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static string GuessExtension(string url)
    {
        var clean = url.Split('?')[0].Split('#')[0];
        var ext = Path.GetExtension(clean).ToLowerInvariant();
        return ext is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav" ? ext : ".mp3";
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 80 ? s[..80] : s;
    }

    public void Dispose() => _http.Dispose();
}
