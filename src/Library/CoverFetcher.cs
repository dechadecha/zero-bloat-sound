using System.Net.Http;
using System.Text.Json;

namespace ZBS.Library;

/// <summary>
/// Автодотяжка обложек: MusicBrainz (поиск релиза) → Cover Art Archive (картинка).
/// Обе базы открытые, без API-ключей; MusicBrainz требует честный User-Agent и ≤1 req/s —
/// уважаем троттлингом.
/// </summary>
public sealed class CoverFetcher
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZeroBloatSound/0.1 (open-source player)");
        return client;
    }

    /// <summary>
    /// Definitive=true — базы честно ответили «обложки нет» (отказ можно кэшировать навсегда);
    /// false — сеть/таймаут/кривой ответ: пробовать в следующий раз, .none не писать.
    /// </summary>
    public async Task<(byte[]? Bytes, bool Definitive)> FetchFrontCoverAsync(string artist, string album, CancellationToken ct)
    {
        try
        {
            await Throttle.WaitAsync(ct);
            try
            {
                var sinceLast = DateTimeOffset.UtcNow - _lastRequest;
                if (sinceLast < TimeSpan.FromSeconds(1.1))
                    await Task.Delay(TimeSpan.FromSeconds(1.1) - sinceLast, ct);
                _lastRequest = DateTimeOffset.UtcNow;

                var query = Uri.EscapeDataString($"artist:\"{artist}\" AND release:\"{album}\"");
                var searchUrl = $"https://musicbrainz.org/ws/2/release/?query={query}&fmt=json&limit=1";
                using var searchResp = await Http.GetAsync(searchUrl, ct);
                if (!searchResp.IsSuccessStatusCode)
                    return (null, Definitive: false); // 5xx/429 — временное

                using var doc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("releases", out var releases) ||
                    releases.GetArrayLength() == 0)
                    return (null, Definitive: true); // релиз не найден — честный отказ
                var mbid = releases[0].GetProperty("id").GetString();
                if (string.IsNullOrEmpty(mbid)) return (null, Definitive: true);

                using var coverResp = await Http.GetAsync(
                    $"https://coverartarchive.org/release/{mbid}/front-250", ct);
                if (coverResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (null, Definitive: true); // у релиза нет фронт-обложки
                if (!coverResp.IsSuccessStatusCode)
                    return (null, Definitive: false);
                return (await coverResp.Content.ReadAsByteArrayAsync(ct), Definitive: true);
            }
            finally
            {
                Throttle.Release();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, Definitive: false); // нет сети/кривой ответ — не клеймим альбом навсегда
        }
    }
}
