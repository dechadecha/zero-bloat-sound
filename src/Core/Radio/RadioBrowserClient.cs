using System.Text.Json;

namespace ZBS.Core.Radio;

/// <summary>
/// Каталог станций radio-browser.info (открытое community-API, без ключей).
/// Все методы сетевые — звать из фона; ошибки сети наружу исключением.
/// </summary>
public sealed class RadioBrowserClient : IDisposable
{
    // Зеркала: all.* раунд-робинит DNS, но бывает капризен — пробуем по очереди.
    private static readonly string[] Hosts =
    {
        "https://all.api.radio-browser.info",
        "https://de1.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
    };

    private readonly HttpClient _http;

    public RadioBrowserClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // API просит представляться — вежливый User-Agent обязателен.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZeroBloatSound/0.1");
    }

    /// <summary>Поиск по имени с фильтрами (пустой запрос — топ по голосам). countryCode "" — все страны.</summary>
    public async Task<IReadOnlyList<RadioStation>> SearchAsync(string query, string countryCode, int minBitrate,
        int limit, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(query.Trim());
        var path = $"/json/stations/search?name={q}&limit={limit}&hidebroken=true&order=votes&reverse=true";
        if (!string.IsNullOrWhiteSpace(countryCode)) path += $"&countrycode={countryCode}";
        if (minBitrate > 0) path += $"&bitrateMin={minBitrate}";

        Exception? last = null;
        foreach (var host in Hosts)
        {
            try
            {
                using var resp = await _http.GetAsync(host + path, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var list = new List<RadioStation>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var url = Str(el, "url_resolved");
                    if (string.IsNullOrWhiteSpace(url)) url = Str(el, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    list.Add(new RadioStation(
                        Str(el, "stationuuid"),
                        Str(el, "name").Trim(),
                        url,
                        Str(el, "country"),
                        Str(el, "tags"),
                        el.TryGetProperty("bitrate", out var b) && b.TryGetInt32(out var br) ? br : 0,
                        Str(el, "codec")));
                }
                return list;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Сюда попадает и TaskCanceledException от таймаута HttpClient —
                // висящее зеркало (главный кейс фейловера) обязано вести к следующему.
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("radio-browser недоступен");
    }

    /// <summary>Жив ли поток станции (короткий GET). Для чистки протухших из списка.</summary>
    public async Task<bool> IsAliveAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Shoutcast v1 отвечает «ICY 200 OK» — HttpClient давится, а BASS такое играет.
            // Проверяем сырым сокетом, прежде чем хоронить станцию.
            return await ProbeRawAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Сырой probe для не-HTTP серверов (Shoutcast v1): коннект + первая строка ответа.</summary>
    private static async Task<bool> ProbeRawAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var request = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Host}\r\nUser-Agent: WinampMPEG/5.0\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);
            var buffer = new byte[16];
            var got = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            var head = System.Text.Encoding.ASCII.GetString(buffer, 0, got);
            return head.StartsWith("ICY 200", StringComparison.Ordinal)
                   || head.StartsWith("HTTP/", StringComparison.Ordinal) && head.Contains(" 200");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
