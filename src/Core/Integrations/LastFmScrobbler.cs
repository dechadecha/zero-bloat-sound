using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZBS.Core.Integrations;

/// <summary>
/// Last.fm-скробблер (audioscrobbler API 2.0, без сторонних библиотек).
/// Ключи (API key + secret) пользователь создаёт на last.fm/api; пароль НЕ хранится —
/// разово меняется на session key (он бессрочный и персистится в настройках).
/// </summary>
public sealed class LastFmScrobbler : IDisposable
{
    private const string ApiRoot = "https://ws.audioscrobbler.com/2.0/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string SessionKey { get; set; } = "";

    public bool Ready => !string.IsNullOrWhiteSpace(ApiKey)
                         && !string.IsNullOrWhiteSpace(ApiSecret)
                         && !string.IsNullOrWhiteSpace(SessionKey);

    /// <summary>Логин: обменивает пароль на session key. Возвращает имя пользователя.</summary>
    public async Task<string> AuthAsync(string username, string password, CancellationToken ct)
    {
        var result = await CallAsync(new Dictionary<string, string>
        {
            ["method"] = "auth.getMobileSession",
            ["username"] = username,
            ["password"] = password,
        }, ct).ConfigureAwait(false);
        var session = result.RootElement.GetProperty("session");
        SessionKey = session.GetProperty("key").GetString() ?? "";
        return session.GetProperty("name").GetString() ?? username;
    }

    /// <summary>«Сейчас играет» в профиле (живёт ~длительность трека).</summary>
    public Task NowPlayingAsync(string artist, string track, CancellationToken ct) =>
        CallSessionAsync("track.updateNowPlaying", artist, track, null, ct);

    /// <summary>Скроббл (правило Last.fm: слушал ≥50% или ≥4 минут).</summary>
    public Task ScrobbleAsync(string artist, string track, DateTimeOffset startedAt, CancellationToken ct) =>
        CallSessionAsync("track.scrobble", artist, track, startedAt.ToUnixTimeSeconds(), ct);

    private async Task CallSessionAsync(string method, string artist, string track, long? timestamp, CancellationToken ct)
    {
        if (!Ready || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(track)) return;
        var args = new Dictionary<string, string>
        {
            ["method"] = method,
            ["artist"] = artist,
            ["track"] = track,
            ["sk"] = SessionKey,
        };
        if (timestamp is { } ts) args["timestamp"] = ts.ToString();
        await CallAsync(args, ct).ConfigureAwait(false);
    }

    /// <summary>Подписанный POST: api_sig = md5(отсортированные параметры + secret), format=json.</summary>
    private async Task<JsonDocument> CallAsync(Dictionary<string, string> args, CancellationToken ct)
    {
        args["api_key"] = ApiKey;
        var sig = new StringBuilder();
        foreach (var (k, v) in args.OrderBy(p => p.Key, StringComparer.Ordinal))
            sig.Append(k).Append(v);
        sig.Append(ApiSecret);
        args["api_sig"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sig.ToString()))).ToLowerInvariant();
        args["format"] = "json"; // после подписи: format в неё не входит

        using var content = new FormUrlEncodedContent(args);
        using var resp = await _http.PostAsync(ApiRoot, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            doc.Dispose();
            throw new InvalidOperationException($"Last.fm: {message ?? $"ошибка {err}"}");
        }
        return doc;
    }

    public void Dispose() => _http.Dispose();
}
