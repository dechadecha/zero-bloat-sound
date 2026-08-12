namespace ZBS.Plugins.Api;

/// <summary>
/// Базовый маркер плагина. Полные контракты (источники, DSP, визуализации, выводы,
/// general) стабилизируются к M6 — но интерфейсная сборка существует с M0,
/// чтобы ядро с первого дня строилось вокруг расширяемости.
/// </summary>
public interface IPlugin
{
    string Name { get; }
    string Version { get; }
}
