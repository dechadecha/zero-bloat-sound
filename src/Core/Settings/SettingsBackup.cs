using System.IO.Compression;

namespace ZBS.Core.Settings;

/// <summary>
/// Бэкап/восстановление настроек одним файлом (.zip): настройки, избранное радио, подписки подкастов.
/// Кэши и временное (resume, loudness, обложки) НЕ включаем — восстановятся сами.
/// </summary>
public static class SettingsBackup
{
    private static readonly string[] Files = { "settings.json", "radio.json", "podcasts.json" };

    /// <summary>Собрать конфиг из папки настроек в zip-архив.</summary>
    public static void Export(string settingsDir, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var name in Files)
        {
            var path = Path.Combine(settingsDir, name);
            if (File.Exists(path)) zip.CreateEntryFromFile(path, name);
        }
    }

    /// <summary>Восстановить конфиг из zip в папку настроек (перезапись). Возвращает число файлов.</summary>
    public static int Import(string settingsDir, string zipPath)
    {
        Directory.CreateDirectory(settingsDir);
        var restored = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var safe = Path.GetFileName(entry.Name); // защита от zip-slip: только имя, без путей
            if (!Files.Contains(safe)) continue;      // чужие файлы в архиве игнорируем
            entry.ExtractToFile(Path.Combine(settingsDir, safe), overwrite: true);
            restored++;
        }
        return restored;
    }
}
