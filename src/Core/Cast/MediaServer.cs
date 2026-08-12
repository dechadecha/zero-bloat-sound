using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZBS.Core.Cast;

/// <summary>
/// Мини-HTTP-сервер, отдающий рендереру ОДИН текущий файл (с Range — телевизоры мотают им).
/// Токен в пути защищает от случайных сканов; слушает только пока идёт каст.
/// </summary>
public sealed class MediaServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string? _filePath;
    private string _token = "";

    public int Port { get; private set; }

    /// <summary>Начать отдавать файл; возвращает URL для рендерера (наш LAN-адрес).</summary>
    public string? Serve(string filePath, string localIp, int port = 8974)
    {
        Stop();
        try
        {
            _filePath = filePath;
            _token = Guid.NewGuid().ToString("N")[..10];
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Port = port;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(_listener, _cts.Token);
            var ext = Path.GetExtension(filePath);
            return $"http://{localIp}:{port}/media/{_token}{ext}";
        }
        catch (Exception)
        {
            Stop();
            return null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
        _filePath = null;
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = HandleClient(client, ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromMinutes(30)); // длинный файл качается долго — но не вечно
                var rct = reqCts.Token;

                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(rct).ConfigureAwait(false);
                if (requestLine is null) return;
                long rangeStart = -1, rangeEnd = -1;
                for (var i = 0; i < 60; i++)
                {
                    var header = await reader.ReadLineAsync(rct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(header)) break;
                    if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(header, @"bytes=(\d*)-(\d*)");
                        if (m.Success)
                        {
                            if (m.Groups[1].Value.Length > 0) rangeStart = long.Parse(m.Groups[1].Value);
                            if (m.Groups[2].Value.Length > 0) rangeEnd = long.Parse(m.Groups[2].Value);
                        }
                    }
                }

                var parts = requestLine.Split(' ');
                var path = _filePath;
                if (parts.Length < 2 || path is null || !parts[1].Contains(_token, StringComparison.Ordinal))
                {
                    await WriteHeader(stream, "404 Not Found", 0, null, rct).ConfigureAwait(false);
                    return;
                }
                var head = parts[0] == "HEAD";

                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var total = file.Length;
                var start = rangeStart < 0 ? 0 : Math.Min(rangeStart, total);
                var end = rangeEnd < 0 || rangeEnd >= total ? total - 1 : rangeEnd;
                var length = Math.Max(0, end - start + 1);
                var partial = rangeStart >= 0;

                await WriteHeader(stream,
                    partial ? "206 Partial Content" : "200 OK", length,
                    partial ? $"bytes {start}-{end}/{total}" : null, rct,
                    DlnaControl.MimeOf(path)).ConfigureAwait(false);
                if (head) return;

                file.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[81920];
                long sent = 0;
                while (sent < length)
                {
                    var toRead = (int)Math.Min(buffer.Length, length - sent);
                    var got = await file.ReadAsync(buffer.AsMemory(0, toRead), rct).ConfigureAwait(false);
                    if (got <= 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, got), rct).ConfigureAwait(false);
                    sent += got;
                }
            }
            catch (Exception) { /* рендерер оборвал соединение — норма при перемотке */ }
        }
    }

    private static async Task WriteHeader(NetworkStream stream, string status, long length,
        string? contentRange, CancellationToken ct, string contentType = "application/octet-stream")
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\n");
        sb.Append("Accept-Ranges: bytes\r\nConnection: close\r\n");
        if (contentRange is not null) sb.Append($"Content-Range: {contentRange}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
