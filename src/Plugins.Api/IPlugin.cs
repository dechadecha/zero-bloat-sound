namespace ZBS.Plugins.Api;

/// <summary>
/// Базовый контракт плагина. Конкретные категории (general / source / dsp / visualizer / output)
/// наследуют этот интерфейс. Реализация должна иметь публичный конструктор без аргументов —
/// хост создаёт плагин через него.
/// </summary>
public interface IPlugin
{
    /// <summary>Стабильный идентификатор (reverse-dns, напр. «ru.denisgolub.zbs.discord»). Ключ в реестре/настройках.</summary>
    string Id { get; }

    /// <summary>Человекочитаемое имя для списка плагинов.</summary>
    string Name { get; }

    /// <summary>Версия плагина (semver).</summary>
    string Version { get; }
}
