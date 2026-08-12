namespace ZBS.Plugins.Api;

/// <summary>Снимок играющего трека, отдаваемый плагинам (без утечки внутренних типов ядра).</summary>
public sealed record PluginTrackInfo(
    string Title,
    string Artist,
    string? Album,
    double DurationSeconds,
    string FilePath);

/// <summary>
/// Сервисы плеера, доступные плагину. Хост реализует это и передаёт в <see cref="IGeneralPlugin.OnLoad"/>.
/// Стабильная поверхность: расширяется только добавлением, без ломающих изменений.
/// </summary>
public interface IPluginHost
{
    /// <summary>Строка в лог плеера (диагностика плагина).</summary>
    void Log(string message);

    /// <summary>Версия хоста (плеера) — плагин может проверить совместимость.</summary>
    string HostVersion { get; }

    /// <summary>Текущий трек (null — ничего не играет/радио).</summary>
    PluginTrackInfo? CurrentTrack { get; }

    /// <summary>Сменился трек (null — остановка/радио без метаданных).</summary>
    event Action<PluginTrackInfo?> TrackChanged;

    /// <summary>Изменилось состояние воспроизведения (true — играет, false — пауза/стоп).</summary>
    event Action<bool> PlayingChanged;
}
