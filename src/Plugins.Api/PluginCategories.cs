namespace ZBS.Plugins.Api;

/// <summary>
/// «Общий» плагин: подписывается на события плеера через хост (аналог встроенных
/// Discord Rich Presence / Last.fm). Самая развязанная категория — стабильна с M6.
/// </summary>
public interface IGeneralPlugin : IPlugin
{
    /// <summary>Плагин включён: получает хост, подписывается на события. Исключения ловит загрузчик.</summary>
    void OnLoad(IPluginHost host);

    /// <summary>Плагин выключается: отписаться, освободить ресурсы.</summary>
    void OnUnload();
}

// ─── WIP-категории ───
// Контракты ниже стабилизируются позже, когда для них готовы host-абстракции
// (звуковой тракт BASS, поверхность рисования Avalonia, реестр выводов). Пока — маркеры,
// чтобы загрузчик и реестр уже умели их различать, а форма не ломала совместимость.

/// <summary>Источник контента (стрим-сервис, сетевая библиотека): резолвит ссылки/списки. WIP.</summary>
public interface ISourcePlugin : IPlugin { }

/// <summary>DSP-обработка PCM в тракте (эффекты). WIP: сигнатура Process зафиксируется вместе с трактом.</summary>
public interface IDspPlugin : IPlugin { }

/// <summary>Визуализация (рисует из FFT/волны). WIP: ждёт абстракции поверхности рисования.</summary>
public interface IVisualizerPlugin : IPlugin { }

/// <summary>Аудио-вывод (свой бэкенд устройства). WIP: ждёт реестр выводов.</summary>
public interface IOutputPlugin : IPlugin { }
