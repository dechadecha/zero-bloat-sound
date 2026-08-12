namespace ZBS.Outputs;

/// <summary>
/// Заглушка модуля выводов (M5): селектор устройств 3 уровня, bit-perfect WASAPI,
/// Chromecast, DLNA, AirPlay (TODO), опрос форматов, группы устройств (мультирум).
/// Каждый вендор — отдельный файл-реализация IAudioOutput.
/// </summary>
public static class OutputsModule
{
    public const string Name = "Outputs";
}
