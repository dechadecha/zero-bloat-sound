using System.Text;
using System.Text.Json;

namespace ZBS.Core.Integrations;

/// <summary>
/// Discord Rich Presence («слушает …») через локальный IPC-пайп Discord — без сторонних библиотек.
/// Нужен App ID из Discord Developer Portal (создаётся за минуту, имя приложения = подпись в статусе).
/// Discord не запущен / ID пуст — тихо ничего не делает. По умолчанию ВЫКЛ (закон проекта).
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>Официальный App ID «Zero-Bloat Sound» (публичный, один на всех пользователей).</summary>
    public const string DefaultAppId = "1536838597280407612";

    private System.IO.Pipes.NamedPipeClientStream? _pipe;
    private string _appId = "";
    private readonly object _lock = new();

    public bool IsConnected { get { lock (_lock) return _pipe?.IsConnected == true; } }

    /// <summary>Подключиться к Discord (пробует пайпы discord-ipc-0..9). false — Discord не найден.</summary>
    public bool Connect(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        lock (_lock)
        {
            Close();
            _appId = appId.Trim();
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(".", $"discord-ipc-{i}",
                        System.IO.Pipes.PipeDirection.InOut);
                    pipe.Connect(300);
                    _pipe = pipe;
                    Send(0, new { v = 1, client_id = _appId }); // handshake
                    ReadFrame();                                 // READY (или ошибка — узнаем при активности)
                    return true;
                }
                catch (Exception) { /* пайп занят/нет — следующий */ }
            }
            _pipe = null;
            return false;
        }
    }

    /// <summary>Показать «Слушает: артист — трек». duration 0 — без таймера (радио/пауза).</summary>
    public void SetListening(string title, string artist, double positionSeconds, double durationSeconds)
    {
        lock (_lock)
        {
            if (_pipe?.IsConnected != true) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            object? timestamps = durationSeconds > 1
                ? new { start = now - (long)positionSeconds, end = now - (long)positionSeconds + (long)durationSeconds }
                : null;
            try
            {
                Send(1, new
                {
                    cmd = "SET_ACTIVITY",
                    nonce = Guid.NewGuid().ToString(),
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = new
                        {
                            type = 2, // Listening
                            details = Trunc(title),
                            state = Trunc(string.IsNullOrWhiteSpace(artist) ? "Zero-Bloat Sound" : artist),
                            timestamps,
                            // Ключ «logo» из Rich Presence → Art Assets приложения;
                            // пока ассет не залит — Discord просто не покажет картинку.
                            assets = new { large_image = "logo", large_text = "Zero-Bloat Sound" },
                        },
                    },
                });
            }
            catch (Exception) { Close(); } // Discord закрыли — не мешаем плееру
        }
    }

    /// <summary>Убрать активность (стоп/выключение тумблера).</summary>
    public void ClearActivity()
    {
        lock (_lock)
        {
            if (_pipe?.IsConnected != true) return;
            try
            {
                Send(1, new
                {
                    cmd = "SET_ACTIVITY",
                    nonce = Guid.NewGuid().ToString(),
                    args = new { pid = Environment.ProcessId, activity = (object?)null },
                });
            }
            catch (Exception) { Close(); }
        }
    }

    private static string Trunc(string s) => s.Length > 120 ? s[..120] : s.Length < 2 ? s + " " : s;

    private void Send(int op, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        _pipe!.Write(frame, 0, frame.Length);
        _pipe.Flush();
    }

    private void ReadFrame()
    {
        var header = new byte[8];
        var got = _pipe!.Read(header, 0, 8);
        if (got < 8) return;
        var len = BitConverter.ToInt32(header, 4);
        if (len is <= 0 or > 65536) return;
        var body = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = _pipe.Read(body, read, len - read);
            if (n <= 0) break;
            read += n;
        }
    }

    private void Close()
    {
        try { _pipe?.Dispose(); } catch (Exception) { }
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }
}
