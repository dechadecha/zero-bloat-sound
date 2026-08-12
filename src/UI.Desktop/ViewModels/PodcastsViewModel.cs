using ZBS.Core.Playback;
using ZBS.Core.Podcasts;
using ZBS.Core.Settings;

namespace ZBS.UI.Desktop.ViewModels;

/// <summary>Строка эпизода: заголовок, дата/длительность, прогресс скачивания/прослушивания.</summary>
public sealed class EpisodeRow : ViewModelBase
{
    public EpisodeRow(PodcastEpisode ep, string state) { Ep = ep; _state = state; }
    public PodcastEpisode Ep { get; }

    private string _state;
    public string State { get => _state; set => Set(ref _state, value); }
}

/// <summary>
/// Экран «Подкасты»: подписки по RSS-ссылке, список эпизодов, кэш-скачивание
/// (полная перемотка + офлайн) и память позиции каждого эпизода.
/// </summary>
public sealed class PodcastsViewModel : ViewModelBase
{
    private readonly PlaybackEngine _engine;
    private readonly PodcastStore _storeP;
    private readonly PodcastClient _client = new();
    private readonly string _cacheDir;
    private readonly Dictionary<string, IReadOnlyList<PodcastEpisode>> _episodesByFeed = new();
    private PodcastEpisode? _playingEpisode;
    private string? _playingPath; // путь кэш-файла игрáющего эпизода

    public PodcastsViewModel(PlaybackEngine engine, string settingsDir)
    {
        _engine = engine;
        _storeP = new PodcastStore(settingsDir);
        _cacheDir = Path.Combine(settingsDir, "podcasts-cache");
        AddFeedCommand = new RelayCommand(() => _ = AddFeedAsync());
        RemoveFeedCommand = new RelayCommand(RemoveFeed);
        RefreshCommand = new RelayCommand(() => _ = RefreshAllAsync());
        PlayEpisodeCommand = new RelayCommand(() => _ = PlaySelectedAsync());
        // Эпизоды из кэша — списки живут и офлайн, и до похода в сеть.
        foreach (var feed in _storeP.Feeds)
        {
            var cached = _storeP.CachedEpisodes(feed.FeedUrl);
            if (cached.Count > 0) _episodesByFeed[feed.FeedUrl] = cached;
        }
        RebuildFeeds();
        if (_storeP.Feeds.Count > 0) _ = RefreshAllAsync(); // автопроверка новых выпусков
        // Ушли с эпизода на другой контент — позиция чужого трека НЕ должна писаться в подкаст.
        _engine.TrackChanged += t =>
        {
            if (_playingEpisode is not null && t?.FilePath != _playingPath)
            {
                _playingEpisode = null;
                _playingPath = null;
            }
        };
        // Эпизод доиграл до конца сам — честная пометка «прослушан».
        _engine.TrackEndedNaturally += t =>
        {
            if (_playingEpisode is { } ep && t.FilePath == _playingPath)
            {
                _storeP.SavePosition(ep, double.MaxValue, Math.Max(61, _engine.DurationSeconds));
                _playingEpisode = null;
                _playingPath = null;
                ShowEpisodes(); // галочка «✓» появляется сразу
            }
        };
    }

    public List<PodcastFeed> Feeds { get; private set; } = new();
    public List<EpisodeRow> Episodes { get; private set; } = new();

    private int _feedIndex = -1;
    public int FeedIndex
    {
        get => _feedIndex;
        set { if (Set(ref _feedIndex, value)) ShowEpisodes(); }
    }

    private int _episodeIndex = -1;
    public int EpisodeIndex { get => _episodeIndex; set => Set(ref _episodeIndex, value); }

    private string _feedUrl = "";
    public string FeedUrl { get => _feedUrl; set => Set(ref _feedUrl, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public RelayCommand AddFeedCommand { get; }
    public RelayCommand RemoveFeedCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand PlayEpisodeCommand { get; }

    private void RebuildFeeds()
    {
        Feeds = _storeP.Feeds.OrderBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        Raise(nameof(Feeds));
        Raise(nameof(HasFeeds));
        if (_feedIndex >= Feeds.Count) FeedIndex = Feeds.Count - 1;
    }

    public bool HasFeeds => Feeds.Count > 0;

    private async Task AddFeedAsync()
    {
        var url = FeedUrl.Trim();
        if (string.IsNullOrWhiteSpace(url)) { Status = "Вставь RSS-ссылку подкаста"; return; }
        Status = "Читаю фид…";
        try
        {
            var (feed, episodes) = await _client.FetchAutoAsync(url, CancellationToken.None);
            _storeP.AddOrUpdateFeed(feed);
            _storeP.SaveEpisodes(feed.FeedUrl, episodes);
            _episodesByFeed[feed.FeedUrl] = episodes;
            FeedUrl = "";
            RebuildFeeds();
            FeedIndex = Feeds.FindIndex(f => f.FeedUrl == feed.FeedUrl);
            Status = $"Подписка: {feed.Title} ({episodes.Count} выпусков)";
        }
        catch (Exception ex)
        {
            Status = $"Фид не читается: {ex.Message}";
        }
    }

    private void RemoveFeed()
    {
        if (_feedIndex < 0 || _feedIndex >= Feeds.Count) return;
        var feed = Feeds[_feedIndex];
        _storeP.RemoveFeed(feed.FeedUrl);
        _episodesByFeed.Remove(feed.FeedUrl);
        RebuildFeeds();
        ShowEpisodes();
        Status = $"Подписка удалена: {feed.Title}";
    }

    private async Task RefreshAllAsync()
    {
        Status = "Проверяю новые выпуски…";
        var errors = 0;
        foreach (var feed in _storeP.Feeds.ToList())
        {
            try
            {
                var (updated, episodes) = await _client.FetchAsync(feed.FeedUrl, CancellationToken.None);
                _storeP.AddOrUpdateFeed(updated);
                _storeP.SaveEpisodes(feed.FeedUrl, episodes);
                _episodesByFeed[feed.FeedUrl] = episodes;
            }
            catch (Exception) { errors++; }
        }
        RebuildFeeds();
        ShowEpisodes();
        Status = errors == 0 ? "Фиды обновлены" : $"Фиды обновлены (недоступно: {errors})";
    }

    private void ShowEpisodes()
    {
        if (_feedIndex < 0 || _feedIndex >= Feeds.Count)
        {
            Episodes = new List<EpisodeRow>();
            Raise(nameof(Episodes));
            return;
        }
        var feed = Feeds[_feedIndex];
        var eps = _episodesByFeed.TryGetValue(feed.FeedUrl, out var list) ? list : Array.Empty<PodcastEpisode>();
        Episodes = eps.Select(e => new EpisodeRow(e, StateOf(e))).ToList();
        Raise(nameof(Episodes));
    }

    private string StateOf(PodcastEpisode e)
    {
        if (_storeP.IsFinished(e)) return "✓ прослушан";
        var pos = _storeP.PositionOf(e);
        if (pos > 0)
        {
            var ts = TimeSpan.FromSeconds(pos);
            return $"⏸ {(ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"))}";
        }
        return "";
    }

    private async Task PlaySelectedAsync()
    {
        if (_episodeIndex < 0 || _episodeIndex >= Episodes.Count) return;
        var row = Episodes[_episodeIndex];
        SavePlayingPosition(); // прежний эпизод — зафиксировать место
        Status = "Скачиваю выпуск…";
        row.State = "⬇ скачивается…";
        try
        {
            var progress = new Progress<double>(p =>
                row.State = p >= 0 ? $"⬇ {p * 100:0}%" : "⬇ скачивается…");
            var path = await _client.DownloadEpisodeAsync(row.Ep, _cacheDir, progress, CancellationToken.None);
            // Эпизод играет как обычный локальный файл (полная перемотка/скорость/EQ),
            // одиночным плейлистом — как открытие файла извне.
            _engine.SetPlaylist(new[] { new ZBS.Core.Playlist.Track(path, row.Ep.Title) });
            _playingEpisode = row.Ep;
            _playingPath = path;
            var resume = _storeP.PositionOf(row.Ep);
            if (resume > 15) _engine.PositionSeconds = resume;
            row.State = "▶ играет";
            Status = row.Ep.Title;
        }
        catch (Exception ex)
        {
            row.State = "ошибка";
            Status = $"Не скачался: {ex.Message}";
        }
    }

    /// <summary>Эпизоды фида для веб-пульта (что уже загружено в память).</summary>
    public IReadOnlyList<PodcastEpisode> EpisodesOf(int feedIndex)
    {
        if (feedIndex < 0 || feedIndex >= Feeds.Count) return Array.Empty<PodcastEpisode>();
        return _episodesByFeed.TryGetValue(Feeds[feedIndex].FeedUrl, out var list)
            ? list
            : Array.Empty<PodcastEpisode>();
    }

    /// <summary>Запуск эпизода с телефона (команда пульта).</summary>
    public void PlayFromRemote(int feedIndex, int episodeIndex)
    {
        if (feedIndex < 0 || feedIndex >= Feeds.Count) return;
        FeedIndex = feedIndex;
        if (episodeIndex < 0 || episodeIndex >= Episodes.Count) return;
        EpisodeIndex = episodeIndex;
        _ = PlaySelectedAsync();
    }

    /// <summary>Зафиксировать позицию игрáющего эпизода (зовётся с паузы/выхода/смены).</summary>
    public void SavePlayingPosition()
    {
        // Только когда в движке действительно наш эпизод: иначе пауза на обычном треке
        // записала бы его позицию (и даже «прослушан») в чужой подкаст.
        if (_playingEpisode is null || _engine.CurrentTrack?.FilePath != _playingPath) return;
        _storeP.SavePosition(_playingEpisode, _engine.PositionSeconds, _engine.DurationSeconds);
    }
}
