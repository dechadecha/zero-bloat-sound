using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ZBS.UI.Desktop;

/// <summary>
/// Один экземпляр плеера на сессию пользователя: второй запуск пересылает аргументы
/// первому через именованный канал и завершается (иначе два плеера дерутся за звук).
/// Имена mutex и pipe скоупятся пользователем и сессией согласованно — иначе при
/// переключении пользователей Windows mutex «свой», а pipe «чужой».
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Команда-сентинел «просто подними окно» (повторный запуск без аргументов).</summary>
    public const string ActivateCommand = "//activate";

    private static readonly string Scope =
        $"{Environment.UserDomainName}.{Environment.UserName}.s{Process.GetCurrentProcess().SessionId}";
    private static readonly string MutexName = $"ZeroBloatSound.SingleInstance.{Scope}";
    private static readonly string PipeName = $"ZeroBloatSound.Args.{Scope}";

    private readonly Mutex _mutex;
    private readonly bool _isPrimary;
    private CancellationTokenSource? _cts;

    public bool IsPrimary => _isPrimary;

    /// <summary>Аргументы, присланные вторым экземпляром (приходят из фонового потока).</summary>
    public event Action<string[]>? ArgsReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);
    }

    /// <summary>Первый экземпляр: начать слушать аргументы от последующих запусков.</summary>
    public void StartListening()
    {
        if (!_isPrimary) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync(ct);
                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (args.Length > 0)
                    ArgsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Имя занято/канал оборван: не умираем молча и не крутим горячий цикл — пробуем снова с паузой.
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Второй экземпляр: отправить аргументы первому. true — доставлено.</summary>
    public static bool ForwardArgs(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(string.Join('\n', args));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_isPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
