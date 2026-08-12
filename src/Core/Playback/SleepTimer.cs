namespace ZBS.Core.Playback;

/// <summary>
/// Сон-таймер: «выключись через N минут» с плавным затуханием.
/// Колбэк вызывается из пула потоков — подписчик маршалит сам (как и события движка).
/// </summary>
public sealed class SleepTimer : IDisposable
{
    private CancellationTokenSource? _cts;

    /// <summary>Время вышло (после этого движок уже попросили погаснуть).</summary>
    public event Action? Elapsed;

    public DateTimeOffset? FiresAt { get; private set; }
    public bool IsActive => FiresAt.HasValue;

    /// <summary>Запускает (или перезапускает) таймер. fadeAction — как гасить звук.</summary>
    public void Start(TimeSpan after, Action fadeAction)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        FiresAt = DateTimeOffset.Now + after;
        _ = Task.Delay(after, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            FiresAt = null;
            try { fadeAction(); }
            catch { /* гасим молча — плеер не должен упасть от сон-таймера */ }
            Elapsed?.Invoke();
        }, TaskScheduler.Default);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        FiresAt = null;
    }

    public void Dispose() => Cancel();
}
