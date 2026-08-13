using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZBS.Core.Lyrics;

/// <summary>Результат поиска текста: сам текст, синхронный ли (LRC-таймкоды) и источник.</summary>
public sealed record LyricsResult(string Text, bool Synced, string Source);

/// <summary>
/// Ищет текст песни в открытых источниках (LRCLIB, NetEase, Genius) и — при наличии
/// пользовательского токена — в Яндекс.Музыке. Порядок: LRCLIB → NetEase → Яндекс → Genius.
/// Приоритет синхронного текста над обычным. Совпадение проверяется по названию/исполнителю.
/// </summary>
public sealed class LyricsFetcher
{
    private readonly HttpClient _http;
    private readonly Func<string?> _yandexToken;

    public LyricsFetcher(HttpClient http, Func<string?> yandexToken)
    {
        _http = http;
        _yandexToken = yandexToken;
    }

    public async Task<LyricsResult?> FetchAsync(string artist, string title, string? album,
        int durationSec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return null;

        foreach (var source in Sources())
        {
            try
            {
                var r = await source(artist, title, album, durationSec, ct).ConfigureAwait(false);
                if (r is not null && !string.IsNullOrWhiteSpace(r.Text)) return r;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* источник отвалился — пробуем следующий */ }
        }
        return null;
    }

    private IEnumerable<Func<string, string, string?, int, CancellationToken, Task<LyricsResult?>>> Sources()
    {
        yield return LrcLibAsync;
        yield return NetEaseAsync;
        if (!string.IsNullOrWhiteSpace(_yandexToken())) yield return YandexAsync;
        yield return GeniusAsync;
    }

    // ---------- LRCLIB (открытый, synced) ----------
    private async Task<LyricsResult?> LrcLibAsync(string artist, string title, string? album, int dur, CancellationToken ct)
    {
        var url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(artist) +
                  "&track_name=" + Uri.EscapeDataString(title);
        if (!string.IsNullOrWhiteSpace(album)) url += "&album_name=" + Uri.EscapeDataString(album);
        if (dur > 0) url += "&duration=" + dur;
        using var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var res = LrcFromJson(doc.RootElement, "lrclib");
            if (res is not null) return res;
        }
        // мягкий поиск, если строгое совпадение не нашлось
        var s = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(artist + " " + title);
        using var sresp = await GetAsync(s, ct).ConfigureAwait(false);
        if (!sresp.IsSuccessStatusCode) return null;
        using var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var it in sdoc.RootElement.EnumerateArray())
        {
            if (TitleMatch(Str(it, "trackName"), title) && ArtistOverlap(Str(it, "artistName"), artist))
            {
                var res = LrcFromJson(it, "lrclib");
                if (res is not null) return res;
            }
        }
        return null;
    }

    private static LyricsResult? LrcFromJson(JsonElement e, string src)
    {
        var synced = Str(e, "syncedLyrics");
        if (!string.IsNullOrWhiteSpace(synced)) return new LyricsResult(synced.Trim(), true, src);
        var plain = Str(e, "plainLyrics");
        if (!string.IsNullOrWhiteSpace(plain)) return new LyricsResult(plain.Trim(), false, src);
        return null;
    }

    // ---------- NetEase (открытый, synced) ----------
    private async Task<LyricsResult?> NetEaseAsync(string artist, string title, string? album, int dur, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(artist + " " + title);
        var searchUrl = "https://music.163.com/api/search/get/?s=" + q + "&type=1&limit=8";
        using var sr = await GetAsync(searchUrl, ct, netease: true).ConfigureAwait(false);
        if (!sr.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await sr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var sg in songs.EnumerateArray())
        {
            if (!TitleMatch(Str(sg, "name"), title)) continue;
            var arts = sg.TryGetProperty("artists", out var aa) && aa.ValueKind == JsonValueKind.Array
                ? string.Join(" ", aa.EnumerateArray().Select(a => Str(a, "name"))) : "";
            if (!ArtistOverlap(arts, artist)) continue;
            if (dur > 0 && sg.TryGetProperty("duration", out var dm) && dm.TryGetInt64(out var ms) &&
                ms > 0 && Math.Abs(ms / 1000.0 - dur) > 15) continue;
            var id = sg.TryGetProperty("id", out var idv) ? idv.GetRawText() : null;
            if (id is null) continue;
            var lyricUrl = "https://music.163.com/api/song/lyric?id=" + id + "&lv=1&kv=1&tv=-1";
            using var lr = await GetAsync(lyricUrl, ct, netease: true).ConfigureAwait(false);
            if (!lr.IsSuccessStatusCode) continue;
            using var ldoc = JsonDocument.Parse(await lr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (ldoc.RootElement.TryGetProperty("lrc", out var lrc))
            {
                var text = CleanNetEase(Str(lrc, "lyric"));
                if (!string.IsNullOrWhiteSpace(text))
                    return new LyricsResult(text.Trim(), HasTimestamps(text), "netease");
            }
        }
        return null;
    }

    private static readonly Regex NeCredit = new(
        @"^\s*(作词|作曲|编曲|制作|混音|录音|监制|和声|吉他|贝斯|鼓|Producer|Composer|Lyricist)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CleanNetEase(string? ly)
    {
        if (string.IsNullOrEmpty(ly)) return "";
        var sb = new StringBuilder();
        foreach (var line in ly.Split('\n'))
        {
            var body = Regex.Replace(line, @"^\[[^\]]*\]", "").Trim();
            if (NeCredit.IsMatch(body)) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }

    // ---------- Яндекс.Музыка (по токену пользователя, synced) ----------
    private const string YandexSignKey = "p93jhgh689SBReK6ghtw62";

    private async Task<LyricsResult?> YandexAsync(string artist, string title, string? album, int dur, CancellationToken ct)
    {
        var token = _yandexToken();
        if (string.IsNullOrWhiteSpace(token)) return null;
        var q = Uri.EscapeDataString(artist + " " + title);
        var searchUrl = "https://api.music.yandex.net/search?text=" + q + "&type=track&page=0";
        using var sr = await GetAsync(searchUrl, ct, yandexToken: token).ConfigureAwait(false);
        if (!sr.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await sr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("result", out var res) ||
            !res.TryGetProperty("tracks", out var tracks) ||
            !tracks.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var t in items.EnumerateArray())
        {
            if (!TitleMatch(Str(t, "title"), title)) continue;
            var arts = t.TryGetProperty("artists", out var aa) && aa.ValueKind == JsonValueKind.Array
                ? string.Join(" ", aa.EnumerateArray().Select(a => Str(a, "name"))) : "";
            if (!ArtistOverlap(arts, artist)) continue;
            if (dur > 0 && t.TryGetProperty("durationMs", out var dm) && dm.TryGetInt64(out var ms) &&
                ms > 0 && Math.Abs(ms / 1000.0 - dur) > 15) continue;
            if (!t.TryGetProperty("id", out var idv)) continue;
            var id = idv.ValueKind == JsonValueKind.String ? idv.GetString()! : idv.GetRawText();

            // проверяем наличие текста
            if (t.TryGetProperty("lyricsInfo", out var li))
            {
                var hasSync = li.TryGetProperty("hasAvailableSyncLyrics", out var hs) && hs.GetBoolean();
                var hasText = li.TryGetProperty("hasAvailableTextLyrics", out var ht) && ht.GetBoolean();
                if (!hasSync && !hasText) continue;
            }
            var text = await YandexLyricsAsync(id, token, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                return new LyricsResult(text.Trim(), HasTimestamps(text), "yandex");
        }
        return null;
    }

    private async Task<string?> YandexLyricsAsync(string trackId, string token, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var numId = new string(trackId.TakeWhile(char.IsDigit).ToArray());
        if (numId.Length == 0) numId = trackId;
        var msg = numId + ts.ToString(CultureInfo.InvariantCulture);
        string sign;
        using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(YandexSignKey)))
            sign = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(msg)));
        var url = $"https://api.music.yandex.net/tracks/{trackId}/lyrics?format=LRC&timeStamp={ts}&sign=" +
                  Uri.EscapeDataString(sign);
        using var r = await GetAsync(url, ct, yandexToken: token).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("result", out var res)) return null;
        var dl = Str(res, "downloadUrl");
        if (string.IsNullOrWhiteSpace(dl)) return null;
        using var lr = await GetAsync(dl, ct).ConfigureAwait(false);
        if (!lr.IsSuccessStatusCode) return null;
        return await lr.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // ---------- Genius (открытый скрап, plain) ----------
    private async Task<LyricsResult?> GeniusAsync(string artist, string title, string? album, int dur, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(artist + " " + title);
        using var sr = await GetAsync("https://genius.com/api/search/multi?q=" + q, ct).ConfigureAwait(false);
        if (!sr.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await sr.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("sections", out var sections)) return null;
        foreach (var sec in sections.EnumerateArray())
        {
            if (!sec.TryGetProperty("hits", out var hits)) continue;
            foreach (var hit in hits.EnumerateArray())
            {
                if (Str(hit, "type") != "song") continue;
                if (!hit.TryGetProperty("result", out var res)) continue;
                if (!TitleMatch(Str(res, "title"), title)) continue;
                if (!ArtistOverlap(Str(res, "artist_names"), artist)) continue;
                var pageUrl = Str(res, "url");
                if (string.IsNullOrWhiteSpace(pageUrl)) continue;
                using var pr = await GetAsync(pageUrl, ct).ConfigureAwait(false);
                if (!pr.IsSuccessStatusCode) continue;
                var html = await pr.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var text = ExtractGenius(html);
                if (!string.IsNullOrWhiteSpace(text))
                    return new LyricsResult(text.Trim(), false, "genius");
            }
        }
        return null;
    }

    /// <summary>Достаёт текст из блоков data-lyrics-container с учётом вложенных div.</summary>
    private static string ExtractGenius(string html)
    {
        var sb = new StringBuilder();
        const string marker = "data-lyrics-container=\"true\"";
        int idx = 0;
        while ((idx = html.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
        {
            var open = html.IndexOf('>', idx);
            if (open < 0) break;
            // ищем закрывающий </div> с учётом вложенности
            int depth = 1, p = open + 1, contentStart = open + 1;
            while (p < html.Length && depth > 0)
            {
                int nextOpen = html.IndexOf("<div", p, StringComparison.OrdinalIgnoreCase);
                int nextClose = html.IndexOf("</div>", p, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0) break;
                if (nextOpen >= 0 && nextOpen < nextClose) { depth++; p = nextOpen + 4; }
                else { depth--; if (depth == 0) { sb.Append(html.Substring(contentStart, nextClose - contentStart)); p = nextClose + 6; } else p = nextClose + 6; }
            }
            idx = p;
            sb.Append('\n');
        }
        var raw = sb.ToString();
        raw = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"<[^>]+>", "");
        raw = System.Net.WebUtility.HtmlDecode(raw);
        return CleanGenius(raw);
    }

    private static string CleanGenius(string txt)
    {
        if (string.IsNullOrWhiteSpace(txt)) return "";
        int i = txt.IndexOf("[Текст песни", StringComparison.Ordinal);
        if (i < 0)
        {
            var m = Regex.Match(txt, @"\n?\[(Куплет|Интро|Intro|Verse|Припев|Chorus|Hook|Bridge|Аутро|Outro)");
            i = m.Success ? m.Index : -1;
        }
        if (i >= 0) txt = txt.Substring(i);
        else { int j = txt.IndexOf(" Lyrics", StringComparison.Ordinal); if (j >= 0 && j < 300) txt = txt.Substring(j + 7); }
        txt = Regex.Replace(txt, @"^\[Текст песни[^\]]*\]\s*", "");
        txt = txt.Replace("You might also like", "\n");
        txt = Regex.Replace(txt, @"\d*Embed\s*$", "");
        txt = Regex.Replace(txt, @"\n{3,}", "\n\n").Trim();
        return txt.Length > 40 ? txt : "";
    }

    // ---------- HTTP + сопоставление ----------
    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct,
        bool netease = false, string? yandexToken = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            netease ? "Mozilla/5.0" : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZBS/1.0");
        if (netease) req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
        if (yandexToken is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + yandexToken);
            req.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
            req.Headers.Remove("User-Agent");
            req.Headers.TryAddWithoutValidation("User-Agent", "Yandex-Music-API");
        }
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static bool HasTimestamps(string? t)
        => !string.IsNullOrEmpty(t) && Regex.IsMatch(t, @"\[\d{1,2}:\d{2}");

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.ToLowerInvariant().Replace('ё', 'е');
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", " ");
        s = Regex.Replace(s, @"\b(feat|ft|prod)\b.*", " ");
        return Regex.Replace(s, @"[^0-9a-zа-я]+", "");
    }

    private static HashSet<string> Toks(string? s)
    {
        s = (s ?? "").ToLowerInvariant().Replace('ё', 'е');
        return Regex.Split(s, @"[^0-9a-zа-я]+").Where(t => t.Length >= 2).ToHashSet();
    }

    private static bool TitleMatch(string? a, string? b)
    {
        var na = Norm(a); var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        return na.Length >= 5 && nb.Length >= 5 && (na.Contains(nb) || nb.Contains(na));
    }

    private static bool ArtistOverlap(string? a, string? b) => Toks(a).Overlaps(Toks(b));
}
