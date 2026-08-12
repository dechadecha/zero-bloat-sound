using System.Text;

namespace ZBS.Core.Cast;

/// <summary>
/// Управление DLNA-рендерером: SOAP-вызовы AVTransport (URI/Play/Pause/Stop)
/// и RenderingControl (громкость). Ошибки — исключениями с текстом ответа.
/// </summary>
public sealed class DlnaControl : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const string AvNs = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string RcNs = "urn:schemas-upnp-org:service:RenderingControl:1";

    /// <summary>Скормить рендереру URL нашего медиасервера и запустить.</summary>
    public async Task PlayUriAsync(DlnaDevice device, string mediaUrl, string title, CancellationToken ct)
    {
        // Минимальный DIDL-Lite: многие телевизоры без метаданных отказываются играть.
        var didl = EscapeXml(
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
            $"<item id=\"zbs\" parentID=\"0\" restricted=\"1\"><dc:title>{EscapeXml(title)}</dc:title>" +
            "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
            $"<res protocolInfo=\"http-get:*:{MimeOf(mediaUrl)}:*\">{EscapeXml(mediaUrl)}</res>" +
            "</item></DIDL-Lite>");

        await SoapAsync(device.AvTransportUrl, AvNs, "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>{EscapeXml(mediaUrl)}</CurrentURI>" +
            $"<CurrentURIMetaData>{didl}</CurrentURIMetaData>", ct).ConfigureAwait(false);
        await SoapAsync(device.AvTransportUrl, AvNs, "Play",
            "<InstanceID>0</InstanceID><Speed>1</Speed>", ct).ConfigureAwait(false);
    }

    public Task PauseAsync(DlnaDevice device, CancellationToken ct) =>
        SoapAsync(device.AvTransportUrl, AvNs, "Pause", "<InstanceID>0</InstanceID>", ct);

    public Task ResumeAsync(DlnaDevice device, CancellationToken ct) =>
        SoapAsync(device.AvTransportUrl, AvNs, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>", ct);

    public Task StopAsync(DlnaDevice device, CancellationToken ct) =>
        SoapAsync(device.AvTransportUrl, AvNs, "Stop", "<InstanceID>0</InstanceID>", ct);

    /// <summary>Громкость 0..100 (если у устройства есть RenderingControl).</summary>
    public Task SetVolumeAsync(DlnaDevice device, int volume, CancellationToken ct)
    {
        if (device.RenderingControlUrl is null) return Task.CompletedTask;
        return SoapAsync(device.RenderingControlUrl, RcNs, "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{Math.Clamp(volume, 0, 100)}</DesiredVolume>", ct);
    }

    private async Task SoapAsync(string controlUrl, string serviceNs, string action, string argsXml, CancellationToken ct)
    {
        var envelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{serviceNs}\">{argsXml}</u:{action}>" +
            "</s:Body></s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceNs}#{action}\"");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var code = System.Text.RegularExpressions.Regex.Match(body, "<errorDescription>(.*?)</errorDescription>");
            throw new InvalidOperationException(
                $"{action}: {(int)resp.StatusCode} {(code.Success ? code.Groups[1].Value : resp.ReasonPhrase)}");
        }
    }

    internal static string MimeOf(string url)
    {
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" or ".opus" => "audio/ogg",
            ".m4a" or ".aac" => "audio/mp4",
            ".wav" => "audio/wav",
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream",
        };
    }

    private static string EscapeXml(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    public void Dispose() => _http.Dispose();
}
