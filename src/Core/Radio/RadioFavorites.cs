using System.Text.Json;

namespace ZBS.Core.Radio;

/// <summary>Избранные станции — radio.json рядом с настройками. Ошибки I/O молча глотаются (не критично).</summary>
public sealed class RadioFavorites
{
    private readonly string _path;
    private readonly List<RadioStation> _items = new();

    public RadioFavorites(string settingsDir)
    {
        _path = Path.Combine(settingsDir, "radio.json");
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<RadioStation>>(File.ReadAllText(_path));
                if (loaded is not null) _items.AddRange(loaded);
            }
        }
        catch (Exception) { /* битый файл — начинаем с пустого */ }
    }

    public IReadOnlyList<RadioStation> Items => _items;

    public bool Contains(RadioStation s) => _items.Any(i => i.Uuid == s.Uuid || i.Url == s.Url);

    public void Toggle(RadioStation s)
    {
        var existing = _items.FirstOrDefault(i => i.Uuid == s.Uuid || i.Url == s.Url);
        if (existing is not null) _items.Remove(existing);
        else _items.Add(s);
        Save();
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception) { }
    }
}
