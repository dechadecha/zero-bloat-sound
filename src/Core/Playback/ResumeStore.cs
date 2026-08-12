using System.Text.Json;

namespace ZBS.Core.Playback;

/// <summary>
/// Память позиций длинных файлов (аудиокниги, миксы, DJ-сеты): ключ трека → секунды.
/// JSON рядом с настройками; пишется лениво, чтобы не пилить диск каждые 5 секунд.
/// </summary>
public sealed class ResumeStore
{
    /// <summary>Файлы короче этого не резюмируем — обычной музыке это не нужно.</summary>
    public const double MinDurationSeconds = 15 * 60;

    private readonly string _path;
    private readonly Dictionary<string, double> _positions;
    private bool _dirty;

    public ResumeStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "resume.json");
        _positions = LoadFile();
    }

    private Dictionary<string, double> LoadFile()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл резюме — не повод падать: начнём с чистого листа.
        }
        return new Dictionary<string, double>();
    }

    public bool TryGet(string key, out double seconds) => _positions.TryGetValue(key, out seconds);

    public void Set(string key, double seconds)
    {
        // Первые секунды не запоминаем — «продолжить с 0:03» бессмысленно.
        if (seconds < 10) return;
        _positions[key] = seconds;
        _dirty = true;
    }

    /// <summary>Трек доигран до конца — резюме больше не нужно.</summary>
    public void Clear(string key)
    {
        if (_positions.Remove(key))
            _dirty = true;
    }

    public void SaveIfDirty()
    {
        if (!_dirty) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_positions));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занятый диск — попробуем в следующий раз.
        }
    }
}
