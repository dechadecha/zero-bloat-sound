namespace ZBS.Core.Audio;

/// <summary>
/// Контракт аудиодвижка. Единственная точка, через которую плеер говорит со звуком:
/// UI и модули не знают, что под капотом BASS (или что-то другое в будущем).
/// Потоковая модель: все вызовы — с одного (UI) потока; PlaybackEnded приходит из потока движка.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>Инициализация устройства вывода. deviceIndex: -1 — по умолчанию, 0 — «без звука» (тесты/CI).</summary>
    bool Init(int deviceIndex = -1);

    /// <summary>
    /// Загружает файл (или его сегмент — для CUE-треков) и готовит к воспроизведению.
    /// startSeconds/endSeconds — абсолютные границы внутри файла; null end — до конца.
    /// </summary>
    bool Load(string filePath, double startSeconds = 0, double? endSeconds = null);

    /// <summary>
    /// Кроссфейд: новый файл стартует тихо и нарастает, текущий гаснет и освобождается.
    /// Конец старого трека при этом НЕ считается «окончанием» (переход уже случился).
    /// gainDb — ReplayGain-поправка нового трека (слайд целится сразу в скорректированную громкость).
    /// </summary>
    bool CrossfadeTo(string filePath, double startSeconds, double? endSeconds, double fadeSeconds, double gainDb = 0);

    void Play();
    void Pause();
    void Stop();

    /// <summary>Мягкая пауза: гасим громкость за fadeMs и ставим на паузу. 0 — обычная пауза.</summary>
    void FadePause(int fadeMs);

    /// <summary>Мягкое продолжение: старт с нуля громкости и нарастание за fadeMs.</summary>
    void FadeResume(int fadeMs);

    /// <summary>Плавное затухание и полная остановка (сон-таймер).</summary>
    void FadeOutAndStop(int fadeMs);

    PlaybackState State { get; }

    /// <summary>Длительность текущего трека/сегмента в секундах (0, если ничего не загружено).</summary>
    double DurationSeconds { get; }

    /// <summary>Позиция внутри трека/сегмента. Присваивание — перемотка.</summary>
    double PositionSeconds { get; set; }

    /// <summary>Громкость 0..1.</summary>
    double Volume { get; set; }

    /// <summary>
    /// Поправка громкости текущего трека в дБ (ReplayGain). Применяется поверх Volume,
    /// сбрасывается при каждом Load. 0 — без поправки.
    /// </summary>
    double TrackGainDb { get; set; }

    /// <summary>Скорость 0.5..2.0 с сохранением тона (bass_fx). Без bass_fx — игнорируется.</summary>
    double Speed { get; set; }

    /// <summary>Доступна ли смена скорости (нашёлся ли bass_fx).</summary>
    bool SpeedSupported { get; }

    /// <summary>Трек/сегмент доиграл до конца (из потока движка — подписчик маршалит сам).</summary>
    event Action? PlaybackEnded;

    /// <summary>Последняя ошибка движка в человекочитаемом виде.</summary>
    string? LastError { get; }

    /// <summary>
    /// Проверка «файл вообще открывается декодером» (сканер битых).
    /// Не трогает текущее воспроизведение, безопасно звать во время игры.
    /// </summary>
    bool CanDecode(string filePath);

    /// <summary>
    /// Эквалайзер: 10 полос (31.5…16к Гц), гейны в дБ (−12..+12).
    /// null — выключить (эффекты сняты). Применяется к текущему и всем будущим трекам.
    /// </summary>
    void SetEqualizer(float[]? gains);

    /// <summary>
    /// FFT-спектр играющего звука для визуализатора: заполняет buffer (магнитуды 0..1).
    /// Длина 512/1024 (FFT 1024/2048). false — тишина/нет трека (рисуем затухание).
    /// </summary>
    bool GetSpectrum(float[] buffer);
}

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}
