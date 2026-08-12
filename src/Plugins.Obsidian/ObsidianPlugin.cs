using System.Reflection;
using System.Text;
using System.Text.Json;
using ZBS.Plugins.Api;

namespace ZBS.Plugins.Obsidian;

/// <summary>
/// Пример-плагин поверх ZBS Plugin API: журнал прослушивания в заметку Obsidian.
/// На смену трека дописывает строку в markdown-файл. Настройка — рядом с dll в obsidian.json.
/// </summary>
public sealed class ObsidianPlugin : IGeneralPlugin
{
    public string Id => "ru.denisgolub.zbs.obsidian";
    public string Name => "Obsidian — журнал прослушивания";
    public string Version => "1.0.0";

    private IPluginHost? _host;
    private readonly ObsidianConfig? _injected;
    private ObsidianConfig _cfg = new();
    private string? _lastKey;

    public ObsidianPlugin() { }

    /// <summary>Тестовый конструктор: конфиг задаётся напрямую, без чтения obsidian.json.</summary>
    internal ObsidianPlugin(ObsidianConfig config) => _injected = config;

    public void OnLoad(IPluginHost host)
    {
        _host = host;
        _cfg = _injected ?? LoadConfig(host);
        if (string.IsNullOrWhiteSpace(_cfg.Vault) && string.IsNullOrWhiteSpace(_cfg.Note))
            host.Log("Obsidian: укажите путь в obsidian.json (vault или note) — журнал пока отключён.");
        host.TrackChanged += OnTrackChanged;
    }

    public void OnUnload()
    {
        if (_host is not null) _host.TrackChanged -= OnTrackChanged;
        _host = null;
    }

    private void OnTrackChanged(PluginTrackInfo? track)
    {
        if (track is null) return; // стоп/радио без метаданных — не пишем
        var artist = string.IsNullOrWhiteSpace(track.Artist) ? "?" : track.Artist;
        var key = $"{artist}{track.Title}";
        if (_cfg.Dedupe && key == _lastKey) return; // тот же трек подряд — не дублируем
        var path = ResolveNotePath();
        if (path is null) return;

        var now = DateTime.Now;
        var line = _cfg.Format
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{artist}", artist)
            .Replace("{title}", track.Title);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            _lastKey = key;
        }
        catch (Exception ex)
        {
            _host?.Log($"Obsidian: не записалось ({ex.Message})");
        }
    }

    // Явный файл note приоритетнее; иначе — ежедневная заметка <vault>/<yyyy-MM-dd>.md.
    private string? ResolveNotePath()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.Note)) return _cfg.Note;
        if (!string.IsNullOrWhiteSpace(_cfg.Vault))
            return Path.Combine(_cfg.Vault!, $"{DateTime.Now:yyyy-MM-dd}.md");
        return null;
    }

    private static ObsidianConfig LoadConfig(IPluginHost host)
    {
        try
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            var file = Path.Combine(dir, "obsidian.json");
            if (!File.Exists(file)) return new ObsidianConfig();
            var cfg = JsonSerializer.Deserialize<ObsidianConfig>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cfg ?? new ObsidianConfig();
        }
        catch (Exception ex)
        {
            host.Log($"Obsidian: obsidian.json не прочитан ({ex.Message})");
            return new ObsidianConfig();
        }
    }
}

/// <summary>Настройка плагина (obsidian.json рядом с dll).</summary>
public sealed class ObsidianConfig
{
    /// <summary>Полный путь к markdown-файлу заметки. Приоритетнее Vault.</summary>
    public string? Note { get; set; }

    /// <summary>Папка хранилища Obsidian — тогда пишем в ежедневную заметку yyyy-MM-dd.md.</summary>
    public string? Vault { get; set; }

    /// <summary>Формат строки. Плейсхолдеры: {date} {time} {artist} {title}.</summary>
    public string Format { get; set; } = "- {time} 🎵 {artist} — {title}";

    /// <summary>Не дублировать один и тот же трек подряд.</summary>
    public bool Dedupe { get; set; } = true;
}
