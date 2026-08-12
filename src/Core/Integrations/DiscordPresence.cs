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
    private bool _disposed;
    private DateTime _nextConnectAllowed = DateTime.MinValue; // backoff: не долбим коннект, пока Discord закрыт

    public bool IsConnected { get { lock (_lock) return _pipe?.IsConnected == true; } }

    /// <summary>
    /// Задать App ID и попробовать подключиться сейчас (сброс backoff). Возвращает результат коннекта.
    /// Дальше SetListening/ClearActivity сами переподключаются при обрыве.
    /// </summary>
    public bool Connect(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        lock (_lock)
        {
            _appId = appId.Trim();
            _nextConnectAllowed = DateTime.MinValue; // явный жест пользователя — пробуем немедленно
            return EnsureConnected();
        }
    }

    // Гарантирует живой пайп (или false). Всё под _lock — коннект и запись атомарны, гонок check-then-act нет.
    // Пока Discord закрыт, повторные попытки экономим через backoff (иначе каждый апдейт = ~3 с в цикле пайпов).
    private bool EnsureConnected()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_appId)) return false;
        if (_pipe?.IsConnected == true) return true;
        if (DateTime.UtcNow < _nextConnectAllowed) return false;
        Close();
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
            catch (Exception) { Close(); /* пайп занят/нет/завис — следующий */ }
        }
        _nextConnectAllowed = DateTime.UtcNow.AddSeconds(30); // Discord не найден — не тревожим 30 с
        return false;
    }

    /// <summary>Показать «Слушает: артист — трек». duration 0 — без таймера (радио/пауза).</summary>
    public void SetListening(string title, string artist, double positionSeconds, double durationSeconds)
    {
        lock (_lock)
        {
            if (!EnsureConnected()) return;
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
                DrainResponse(); // прочитать ответный фрейм Discord — иначе буфер пайпа копится и презенс замерзает
            }
            catch (Exception) { Close(); } // Discord закрыли — не мешаем плееру
        }
    }

    /// <summary>Убрать активность (стоп/выключение тумблера).</summary>
    public void ClearActivity()
    {
        lock (_lock)
        {
            if (_pipe?.IsConnected != true) return; // нет коннекта — и убирать нечего (без backoff-долбёжки)
            try
            {
                Send(1, new
                {
                    cmd = "SET_ACTIVITY",
                    nonce = Guid.NewGuid().ToString(),
                    args = new { pid = Environment.ProcessId, activity = (object?)null },
                });
                DrainResponse();
            }
            catch (Exception) { Close(); }
        }
    }

    // Читаем и отбрасываем ответный фрейм на каждый SET_ACTIVITY. Если ответа нет (таймаут) — не рвём
    // коннект: дренаж best-effort, его задача лишь не давать входящему буферу пайпа переполниться.
    private void DrainResponse()
    {
        try { ReadFrame(); } catch (Exception) { /* нет ответа — в буфере и так пусто */ }
    }

    // Discord требует поля активности длиной 2–128 символов: пустое/односимвольное дополняем до двух.
    private static string Trunc(string s) => s.Length > 120 ? s[..120] : s.Length < 2 ? (s + "  ")[..2] : s;

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
        if (!ReadExact(header, 8)) return;
        var len = BitConverter.ToInt32(header, 4);
        if (len is <= 0 or > 65536) return;
        var body = new byte[len];
        ReadExact(body, len);
    }

    // NamedPipeClientStream не поддерживает ReadTimeout — ограничиваем сами: если Discord принял
    // пайп, но завис и не отвечает, без этого блокирующий Read висел бы вечно (и морозил вызвавший поток).
    private bool ReadExact(byte[] buf, int len)
    {
        var read = 0;
        while (read < len)
        {
            var task = _pipe!.ReadAsync(buf.AsMemory(read, len - read)).AsTask();
            if (!task.Wait(2000)) throw new TimeoutException("Discord IPC: нет ответа");
            var n = task.Result;
            if (n <= 0) break;
            read += n;
        }
        return read >= len;
    }

    private void Close()
    {
        try { _pipe?.Dispose(); } catch (Exception) { }
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; Close(); } // после этого EnsureConnected не воскресит пайп из фоновой задачи
    }
}
