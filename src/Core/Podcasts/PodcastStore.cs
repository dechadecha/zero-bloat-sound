using System.Text.Json;

namespace ZBS.Core.Podcasts;

/// <summary>
/// Подписки + позиции прослушивания эпизодов — podcasts.json рядом с настройками.
/// Позиция пишется на паузе/выходе, эпизод продолжается с места остановки.
/// </summary>
public sealed class PodcastStore
{
    public sealed class State
    {
        public List<PodcastFeed> Feeds { get; set; } = new();
        public Dictionary<string, double> Positions { get; set; } = new(); // guid → секунды
        public HashSet<string> Finished { get; set; } = new();             // доигранные guid
        // Кэш эпизодов: без него после рестарта списки пусты до похода в сеть (офлайн — навсегда).
        public Dictionary<string, List<PodcastEpisode>> Episodes { get; set; } = new();
    }

    private readonly string _path;
    private readonly State _state;

    public PodcastStore(string settingsDir)
    {
        _path = Path.Combine(settingsDir, "podcasts.json");
        _state = Load();
    }

    private State Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
        }
        catch (Exception) { }
        return new State();
    }

    public IReadOnlyList<PodcastFeed> Feeds => _state.Feeds;

    public void AddOrUpdateFeed(PodcastFeed feed)
    {
        _state.Feeds.RemoveAll(f => f.FeedUrl == feed.FeedUrl);
        _state.Feeds.Add(feed);
        Save();
    }

    public void RemoveFeed(string feedUrl)
    {
        _state.Feeds.RemoveAll(f => f.FeedUrl == feedUrl);
        Save();
    }

    public IReadOnlyList<PodcastEpisode> CachedEpisodes(string feedUrl) =>
        _state.Episodes.TryGetValue(feedUrl, out var list) ? list : Array.Empty<PodcastEpisode>();

    public void SaveEpisodes(string feedUrl, IReadOnlyList<PodcastEpisode> episodes)
    {
        _state.Episodes[feedUrl] = episodes.ToList();
        Save();
    }

    public double PositionOf(PodcastEpisode ep) =>
        _state.Positions.TryGetValue(ep.Guid, out var p) ? p : 0;

    public bool IsFinished(PodcastEpisode ep) => _state.Finished.Contains(ep.Guid);

    /// <summary>Запомнить позицию (начало/конец эпизода — сброс/пометка «прослушан»).</summary>
    public void SavePosition(PodcastEpisode ep, double seconds, double duration)
    {
        if (duration > 60 && seconds >= duration - 30)
        {
            _state.Positions.Remove(ep.Guid);
            _state.Finished.Add(ep.Guid);
        }
        else if (seconds > 15)
        {
            _state.Positions[ep.Guid] = seconds;
        }
        else
        {
            _state.Positions.Remove(ep.Guid);
        }
        Save();
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception) { }
    }
}
