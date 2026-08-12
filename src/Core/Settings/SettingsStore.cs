using System.Text.Json;

namespace ZBS.Core.Settings;

/// <summary>
/// JSON-хранилище настроек. Портативный режим: файл portable.txt рядом с exe —
/// настройки живут в папке приложения; иначе — в профиле пользователя.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string? overrideDirectory = null)
    {
        var dir = overrideDirectory ?? ResolveDirectory();
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    public static string ResolveDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            return Path.Combine(exeDir, "data");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ZeroBloatSound");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битые настройки не должны ронять плеер — стартуем с дефолтом.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Недоступный диск — не повод падать при выходе.
        }
    }
}
