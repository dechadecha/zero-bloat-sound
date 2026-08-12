using ZBS.Plugins.Api;

namespace ZBS.UI.Desktop.Plugins;

/// <summary>
/// Мост «плеер ↔ плагины»: реализует <see cref="IPluginHost"/>, грузит плагины из папки и
/// прокидывает им события трека/состояния. Исключения плагинов ИЗОЛИРОВАНЫ — кривой плагин
/// не роняет плеер (ни при загрузке, ни в обработчике события).
/// </summary>
public sealed class PluginHost : IPluginHost, IDisposable
{
    private readonly List<IGeneralPlugin> _general = new();
    private readonly Action<string>? _log;
    private PluginTrackInfo? _current;

    public PluginHost(string hostVersion, Action<string>? log = null)
    {
        HostVersion = hostVersion;
        _log = log;
    }

    public string HostVersion { get; }
    public PluginTrackInfo? CurrentTrack => _current;
    public event Action<PluginTrackInfo?>? TrackChanged;
    public event Action<bool>? PlayingChanged;

    public void Log(string message) => _log?.Invoke($"[plugin] {message}");

    /// <summary>Список загруженных плагинов (для настроек/диагностики).</summary>
    public IReadOnlyList<IPlugin> Loaded => _general;

    /// <summary>Загрузить плагины из папки и включить general-плагины. Нет папки — тихо ничего.</summary>
    public void LoadFrom(string directory)
    {
        foreach (var lp in PluginLoader.FromDirectory(directory, m => Log(m)))
        {
            if (lp.Plugin is not IGeneralPlugin g) continue; // прочие категории — WIP, пока не активируем
            try
            {
                g.OnLoad(this);
                _general.Add(g);
                Log($"загружен: {g.Name} {g.Version}");
            }
            catch (Exception ex)
            {
                Log($"{g.Id}: OnLoad упал ({ex.Message})");
            }
        }
    }

    /// <summary>Сменился трек — уведомить плагины (null — стоп/радио).</summary>
    public void NotifyTrackChanged(PluginTrackInfo? track)
    {
        _current = track;
        Fire(TrackChanged, track);
    }

    /// <summary>Изменилось состояние воспроизведения.</summary>
    public void NotifyPlayingChanged(bool playing) => Fire(PlayingChanged, playing);

    // Вызываем подписчиков по одному: исключение в одном плагине не мешает остальным и не всплывает в плеер.
    private void Fire<T>(Action<T>? evt, T arg)
    {
        if (evt is null) return;
        foreach (var d in evt.GetInvocationList())
        {
            try { ((Action<T>)d)(arg); }
            catch (Exception ex) { Log($"обработчик события упал ({ex.Message})"); }
        }
    }

    /// <summary>Выгрузить все плагины (выключение тумблера на лету).</summary>
    public void UnloadAll()
    {
        foreach (var g in _general)
        {
            try { g.OnUnload(); } catch (Exception ex) { Log($"{g.Id}: OnUnload упал ({ex.Message})"); }
        }
        _general.Clear();
    }

    public void Dispose() => UnloadAll();
}
