using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ZBS.Core.Audio;
using ZBS.Core.Playback;
using ZBS.Core.Playlist;
using ZBS.Core.Settings;
using ZBS.Library;
using ZBS.Plugins.Api;
using ZBS.UI.Desktop.Localization;
using ZBS.UI.Desktop.Plugins;

namespace ZBS.UI.Desktop.ViewModels;

/// <summary>Полоса эквалайзера: подпись частоты + гейн в дБ (слайдер).</summary>
public sealed class EqBand : ViewModelBase
{
    private readonly Action _apply;
    private double _gain;

    public string Label { get; }

    public EqBand(string label, double gain, Action apply)
    {
        Label = label;
        _gain = gain;
        _apply = apply;
    }

    public double Gain
    {
        get => _gain;
        set { if (Set(ref _gain, Math.Round(value))) _apply(); }
    }

    /// <summary>Установка из пресета — без каскада применений на каждую полосу.</summary>
    public void SetSilent(double gain)
    {
        _gain = Math.Round(gain);
        Raise(nameof(Gain));
    }
}

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;

    private string _trackTitle = Loc.T("NoTrack");
    private string _timeText = "0:00 / 0:00";
    private string _statusText = "";
    private double _positionSeconds;
    private double _durationSeconds = 1;
    private int _selectedIndex = -1;
    private bool _isPlaying;
    private bool _changingTrack;

    /// <summary>Просилка папки — вешается вьюхой (диалогу нужно окно). VM про окна не знает.</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>Повторный запуск без аргументов: вьюха поднимает и фокусирует окно.</summary>
    public event Action? ActivateRequested;

    /// <summary>Сменился играющий трек — вьюха подматывает видимый список к нему.</summary>
    public event Action? ScrollToPlaying;

    /// <summary>Индекс играющего трека в очереди (для автопрокрутки плейлиста).</summary>
    public int PlayingQueueIndex => _engine.CurrentIndex;

    /// <summary>Плейлист заменяется целиком (одно уведомление вместо шторма CollectionChanged на 20к треков).</summary>
    public IReadOnlyList<Track> Tracks { get; private set; } = Array.Empty<Track>();

    public AsyncRelayCommand OpenFolderCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand ShuffleCommand { get; }
    public RelayCommand RepeatCommand { get; }
    public RelayCommand AbLoopCommand { get; }
    public RelayCommand EnqueueSelectedCommand { get; }
    public AsyncRelayCommand SavePlaylistCommand { get; }
    public RelayCommand JumpCommand { get; }
    public ParamRelayCommand RateSelectedCommand { get; }

    /// <summary>Оценка трека плейлиста — уходит в медиатеку (вешает App).</summary>
    public Action<Track, int>? RateHandler { get; set; }

    public SleepTimer Sleep { get; } = new();

    // ================= Режимы (DESIGN.md §11): Список / Обзор / Настройки / Настройка =================

    public enum UiMode { Browse, Overview, Settings, SettingsDetail }
    public enum NavSection { AllTracks, Folders, Ratings, LibraryManage, Queue, Radio, Podcasts }

    private UiMode _mode = UiMode.Browse;
    private NavSection _section = NavSection.AllTracks;

    public UiMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(IsBrowse));
            Raise(nameof(IsNowPlaying));
            Raise(nameof(IsSettings));
            Raise(nameof(IsSettingsDetail));
            Raise(nameof(SidebarVisible));
            RaiseSections();
            if (value == UiMode.Overview) RefreshQueue();
        }
    }

    public bool IsBrowse => _mode == UiMode.Browse;
    public bool IsNowPlaying => _mode == UiMode.Overview;
    public bool IsSettings => _mode == UiMode.Settings;
    public bool IsSettingsDetail => _mode == UiMode.SettingsDetail;
    /// <summary>Сайдбар живёт в Списке и на сетке Настроек; Обзор и страница настройки — без него.</summary>
    public bool SidebarVisible => _mode is UiMode.Browse or UiMode.Settings;

    private bool _sidebarRail;
    /// <summary>Сайдбар ужат до рельса-иконок (ставит вьюха по фактической ширине колонки).</summary>
    public bool SidebarRail
    {
        get => _sidebarRail;
        set { if (Set(ref _sidebarRail, value)) Raise(nameof(SidebarLabels)); }
    }
    public bool SidebarLabels => !_sidebarRail;

    public NavSection Section
    {
        get => _section;
        private set
        {
            if (!Set(ref _section, value)) return;
            Raise(nameof(IsSectionAllTracks));
            Raise(nameof(IsSectionFolders));
            Raise(nameof(IsSectionRatings));
            Raise(nameof(IsSectionLibrary));
            Raise(nameof(IsSectionQueue));
            RaiseSections();
        }
    }

    private void RaiseSections()
    {
        Raise(nameof(ShowLibraryList));
        Raise(nameof(ShowFolders));
        Raise(nameof(ShowLibraryManage));
        Raise(nameof(ShowQueue));
        Raise(nameof(ShowRadio));
        Raise(nameof(ShowPodcasts));
    }

    public bool IsSectionAllTracks => _section == NavSection.AllTracks;
    public bool IsSectionFolders => _section == NavSection.Folders;
    public bool IsSectionRatings => _section == NavSection.Ratings;
    public bool IsSectionLibrary => _section == NavSection.LibraryManage;
    public bool IsSectionQueue => _section == NavSection.Queue;

    // Контент-панели видны ТОЛЬКО в режиме Browse — иначе Настройки/Обзор рисуются поверх списка.
    public bool ShowLibraryList => IsBrowse && _section is NavSection.AllTracks or NavSection.Ratings;
    public bool ShowFolders => IsBrowse && _section == NavSection.Folders;
    public bool ShowLibraryManage => IsBrowse && _section == NavSection.LibraryManage;
    public bool ShowQueue => IsBrowse && _section == NavSection.Queue;
    public bool ShowRadio => IsBrowse && _section == NavSection.Radio;
    public bool ShowPodcasts => IsBrowse && _section == NavSection.Podcasts;
    public bool IsSectionRadio => _section == NavSection.Radio;
    public bool IsSectionPodcasts => _section == NavSection.Podcasts;

    public ParamRelayCommand NavCommand { get; private set; } = null!;
    public RelayCommand OpenSettingsCommand { get; private set; } = null!;
    public RelayCommand OpenOverviewCommand { get; private set; } = null!;
    public RelayCommand BackCommand { get; private set; } = null!;

    private void NavTo(NavSection section)
    {
        Mode = UiMode.Browse;
        Section = section;
        if (Library is null) return;
        var rated = section == NavSection.Ratings;
        if (Library.RatedOnly != rated) Library.RatedOnly = rated;
    }

    /// <summary>Esc/стрелка назад: всегда один шаг назад. true — нажатие обработано.</summary>
    public bool HandleBack()
    {
        switch (_mode)
        {
            case UiMode.SettingsDetail:
                Mode = UiMode.Settings;
                return true;
            case UiMode.Settings:
                Mode = UiMode.Browse;
                return true;
            case UiMode.Overview:
                if (LyricsOpen) { LyricsOpen = false; return true; }
                if (QueueOpen) { QueueOpen = false; return true; }
                Mode = UiMode.Browse;
                return true;
            default:
                if (DetailTextMode) { DetailTextMode = false; return true; }
                if (DetailOpen) { CloseDetail(); return true; }
                return false;
        }
    }

    // ---- Компакт: включается размером окна (вьюха дёргает по SizeChanged) ----

    private bool _isCompact;
    public bool IsCompact
    {
        get => _isCompact;
        set { if (Set(ref _isCompact, value)) Raise(nameof(IsFull)); }
    }
    public bool IsFull => !_isCompact;

    /// <summary>Кнопка «развернуть» в компакте — окно возвращает себе нормальный размер.</summary>
    public event Action? ExpandRequested;
    public RelayCommand ExpandCommand { get; private set; } = null!;

    // ---- Обзор: очередь сбоку + текст песни ----

    private bool _queueOpen;
    public bool QueueOpen { get => _queueOpen; private set => Set(ref _queueOpen, value); }
    public RelayCommand ToggleQueueCommand { get; private set; } = null!;

    private bool _lyricsOpen;
    public bool LyricsOpen { get => _lyricsOpen; private set => Set(ref _lyricsOpen, value); }
    public RelayCommand ToggleLyricsCommand { get; private set; } = null!;

    private string _lyricsText = "";
    public string LyricsText { get => _lyricsText; private set => Set(ref _lyricsText, value); }

    public sealed record QueueItem(int Index, string Display, bool IsCurrent)
    {
        public string Label => IsCurrent ? $"▶  {Display}" : Display;
    }

    public IReadOnlyList<QueueItem> QueueItems { get; private set; } = Array.Empty<QueueItem>();
    public ParamRelayCommand PlayQueueItemCommand { get; private set; } = null!;

    private void RefreshQueue()
    {
        var current = _engine.CurrentIndex;
        var start = Math.Max(0, current);
        QueueItems = Tracks
            .Skip(start)
            .Take(60)
            .Select((t, i) => new QueueItem(start + i, t.DisplayName, start + i == current))
            .ToList();
        Raise(nameof(QueueItems));
    }

    private int _lyricsGeneration;

    private IReadOnlyList<LrcLine>? _syncedLyrics;
    private int _syncedIndex = -1;

    public IReadOnlyList<KaraokeLine> KaraokeLines { get; private set; } = Array.Empty<KaraokeLine>();
    public bool LyricsSynced => _syncedLyrics is not null;
    public bool LyricsPlain => _syncedLyrics is null;

    /// <summary>Вьюха скроллит подсвеченную строку в центр.</summary>
    public event Action<int>? KaraokeLineChanged;

    private async Task LoadLyricsAsync()
    {
        var generation = ++_lyricsGeneration;
        var index = _engine.CurrentIndex;
        if (index < 0 || index >= Tracks.Count) { SetPlainLyrics(Loc.T("NoLyrics")); return; }
        var path = Tracks[index].FilePath;
        // Приоритет: .lrc рядом с треком → LRC-таймкоды в теге → простой текст тега.
        var (synced, plain) = await Task.Run(() =>
        {
            var sidecar = LrcParser.ReadSidecar(path);
            var parsed = LrcParser.Parse(sidecar);
            if (parsed is not null) return ((IReadOnlyList<LrcLine>?)parsed, (string?)null);
            var tag = TagReader.ReadLyrics(path);
            parsed = LrcParser.Parse(tag);
            if (parsed is not null) return (parsed, null);
            var text = tag ?? (sidecar is null ? null : LrcParser.StripTimestamps(sidecar));
            return (null, text);
        });
        if (generation != _lyricsGeneration) return; // быстрый Next: не показываем текст чужого трека

        if (synced is not null)
        {
            _syncedLyrics = synced;
            _syncedIndex = -1;
            KaraokeLines = synced.Select(l => new KaraokeLine(l.Text)).ToList();
            Raise(nameof(KaraokeLines));
            Raise(nameof(LyricsSynced));
            Raise(nameof(LyricsPlain));
            SyncKaraoke(); // сразу подсветить строку на текущей позиции
        }
        else
        {
            SetPlainLyrics(plain ?? Loc.T("NoLyrics"));
        }
    }

    private void SetPlainLyrics(string text)
    {
        _syncedLyrics = null;
        _syncedIndex = -1;
        KaraokeLines = Array.Empty<KaraokeLine>();
        Raise(nameof(KaraokeLines));
        Raise(nameof(LyricsSynced));
        Raise(nameof(LyricsPlain));
        LyricsText = text;
    }

    /// <summary>Тик позиции → подсветка текущей строки титров (только когда панель открыта).</summary>
    private void SyncKaraoke()
    {
        var lines = _syncedLyrics;
        if (lines is null || !LyricsOpen) return;
        var idx = LrcParser.IndexFor(lines, TimeSpan.FromSeconds(_engine.PositionSeconds));
        if (idx == _syncedIndex) return;
        if (_syncedIndex >= 0 && _syncedIndex < KaraokeLines.Count) KaraokeLines[_syncedIndex].Current = false;
        _syncedIndex = idx;
        if (idx >= 0 && idx < KaraokeLines.Count)
        {
            KaraokeLines[idx].Current = true;
            KaraokeLineChanged?.Invoke(idx);
        }
    }

    // Название/исполнитель раздельно для Обзора: «Артист — Название» из тегов
    // либо «Артист - Название» из имени файла.
    private static int SplitPoint(string name, out int sepLen)
    {
        var sep = name.IndexOf(" — ", StringComparison.Ordinal);
        if (sep > 0) { sepLen = 3; return sep; }
        sep = name.IndexOf(" - ", StringComparison.Ordinal);
        sepLen = 3;
        return sep;
    }

    public string OverviewTitle
    {
        get
        {
            // Радио: крупно — что в эфире (ICY), под ним — станция.
            if (_engine.CurrentIsRadio)
                return string.IsNullOrEmpty(Radio.NowPlayingTitle)
                    ? _engine.RadioStationName ?? "Радио"
                    : Radio.NowPlayingTitle;
            var name = TrackTitle;
            var sep = SplitPoint(name, out var len);
            return sep > 0 ? name[(sep + len)..].Trim() : name;
        }
    }

    public string OverviewArtist
    {
        get
        {
            if (_engine.CurrentIsRadio)
                return string.IsNullOrEmpty(Radio.NowPlayingTitle) ? "" : _engine.RadioStationName ?? "";
            var name = TrackTitle;
            var sep = SplitPoint(name, out _);
            return sep > 0 ? name[..sep].Trim() : "";
        }
    }

    // ---- Деталь трека (клик в списке медиатеки): мета + текст ----

    private LibraryTrack? _detailTrack;
    private bool _detailOpen;
    private bool _detailTextMode;
    private string _detailLyrics = "";
    private string _detailYear = "";
    private Bitmap? _detailCover;
    private int _detailGeneration;

    public bool DetailOpen { get => _detailOpen; private set => Set(ref _detailOpen, value); }
    public bool DetailTextMode
    {
        get => _detailTextMode;
        private set { if (Set(ref _detailTextMode, value)) Raise(nameof(DetailMetaVisible)); }
    }
    public bool DetailMetaVisible => !_detailTextMode;
    public Bitmap? DetailCover { get => _detailCover; private set => Set(ref _detailCover, value); }

    public string DetailTitle => _detailTrack?.Title ?? _detailTrack?.Display ?? "";
    public string DetailArtist => _detailTrack?.Artist ?? "";
    public string DetailAlbum => _detailTrack?.Album ?? "—";
    public string DetailGenre => _detailTrack?.Genre ?? "—";
    public string DetailYear => _detailYear;
    public string DetailFormat => _detailTrack is null ? "" :
        Path.GetExtension(_detailTrack.FilePath).TrimStart('.').ToUpperInvariant();
    public string DetailDuration => _detailTrack is null ? "" : Fmt(_detailTrack.DurationSeconds);
    public string DetailStars => _detailTrack is null ? "" :
        new string('★', _detailTrack.Rating) + new string('☆', 5 - _detailTrack.Rating);
    /// <summary>Звёзды детали в два цвета: заполненные — акцентом, пустые — muted.</summary>
    public string DetailStarsOn => _detailTrack is null ? "" : new string('★', _detailTrack.Rating);
    public string DetailStarsOff => _detailTrack is null ? "" : new string('☆', 5 - _detailTrack.Rating);
    public string DetailLyrics { get => _detailLyrics; private set => Set(ref _detailLyrics, value); }

    public RelayCommand CloseDetailCommand { get; private set; } = null!;
    public RelayCommand ToggleDetailTextCommand { get; private set; } = null!;

    public void ShowDetail(LibraryTrack track)
    {
        _detailTrack = track;
        DetailTextMode = false;
        DetailOpen = true;
        _detailYear = "";
        RaiseDetail();
        var generation = ++_detailGeneration;
        _ = Task.Run(() =>
        {
            var year = TagReader.Read(track.FilePath).Year;
            var lyrics = TagReader.ReadLyrics(track.FilePath);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _detailGeneration) return;
                _detailYear = year > 0 ? year.ToString() : "—";
                DetailLyrics = lyrics ?? Loc.T("NoLyrics");
                Raise(nameof(DetailYear));
            });
        });
        _ = UpdateDetailCoverAsync(track, generation);
    }

    private async Task UpdateDetailCoverAsync(LibraryTrack track, int generation)
    {
        if (CoverProvider is null) { SetDetailCover(null); return; }
        try
        {
            var bytes = await CoverProvider(LibraryService.ToTrack(track));
            if (generation != _detailGeneration) return;
            SetDetailCover(bytes is null ? null : new Bitmap(new MemoryStream(bytes)));
        }
        catch (Exception)
        {
            if (generation == _detailGeneration) SetDetailCover(null);
        }
    }

    private void CloseDetail()
    {
        DetailOpen = false;
        DetailTextMode = false;
        _detailTrack = null;
        SetDetailCover(null);
    }

    private void RaiseDetail()
    {
        Raise(nameof(DetailTitle));
        Raise(nameof(DetailArtist));
        Raise(nameof(DetailAlbum));
        Raise(nameof(DetailGenre));
        Raise(nameof(DetailYear));
        Raise(nameof(DetailFormat));
        Raise(nameof(DetailDuration));
        Raise(nameof(DetailStars));
        Raise(nameof(DetailStarsOn));
        Raise(nameof(DetailStarsOff));
    }

    // ---- Настройки: плитки → страница раздела ----

    private string _settingsTile = "";
    public string SettingsTile { get => _settingsTile; private set => Set(ref _settingsTile, value); }
    public ParamRelayCommand OpenTileCommand { get; private set; } = null!;

    public bool TileIsAudio => _settingsTile == "audio";
    public bool TileIsEq => _settingsTile == "eq";
    public bool TileIsViz => _settingsTile == "viz";
    public bool TileIsLook => _settingsTile == "look";
    public bool TileIsNet => _settingsTile == "net";
    public bool TileIsStub => _settingsTile is "keys" or "upd" or "";

    // ---- M3.2: эквалайзер (10 полос + пресеты, вкл. «АУФ») ----

    private static readonly string[] EqLabels = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    private static readonly Dictionary<string, double[]> EqPresets = new()
    {
        ["flat"] = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["auf"] = new double[] { 8, 7, 5, 2, 0, 0, 2, 3, 3, 2 },
        ["rock"] = new double[] { 5, 4, -1, -3, -1, 2, 5, 6, 6, 6 },
        ["pop"] = new double[] { -1, 3, 5, 5, 3, -1, -2, -2, -1, -1 },
        ["bass"] = new double[] { 8, 7, 6, 4, 2, 0, 0, 0, 0, 0 },
        ["voice"] = new double[] { -3, -2, 0, 2, 4, 4, 3, 1, 0, -1 },
    };

    public IReadOnlyList<EqBand> EqBands { get; private set; } = Array.Empty<EqBand>();
    public ParamRelayCommand EqPresetCommand { get; private set; } = null!;

    public bool EqOn
    {
        get => _settings.EqEnabled;
        set
        {
            _settings.EqEnabled = value;
            ApplyEq();
            Raise();
        }
    }

    private void InitEq()
    {
        var gains = new double[10];
        var parts = _settings.EqGains.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Min(10, parts.Length); i++)
            if (double.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out var g))
                gains[i] = Math.Clamp(g, -12, 12);
        EqBands = EqLabels.Select((l, i) => new EqBand(l, gains[i], OnEqBandChanged)).ToList();
        ApplyEq();
    }

    private void OnEqBandChanged()
    {
        SaveEqGains();
        ApplyEq();
    }

    private void SaveEqGains() =>
        _settings.EqGains = string.Join(';',
            EqBands.Select(b => b.Gain.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private void ApplyEq() =>
        _engine.SetEqualizer(EqOn ? EqBands.Select(b => (float)b.Gain).ToArray() : null);

    private void ApplyEqPreset(string name)
    {
        if (!EqPresets.TryGetValue(name, out var gains)) return;
        for (var i = 0; i < EqBands.Count; i++)
            EqBands[i].SetSilent(gains[i]);
        if (!EqOn) { _settings.EqEnabled = true; Raise(nameof(EqOn)); }
        SaveEqGains();
        ApplyEq();
    }

    // ---- M3.2: визуализатор и магнитное окно ----

    public bool VizOn
    {
        get => _settings.VisualizerEnabled;
        set { _settings.VisualizerEnabled = value; Raise(); Raise(nameof(VizCompactVisible)); }
    }

    public bool VizInCompactOn
    {
        get => _settings.VisualizerInCompact;
        set { _settings.VisualizerInCompact = value; Raise(); Raise(nameof(VizCompactVisible)); }
    }

    public bool VizCompactVisible => _settings.VisualizerEnabled && _settings.VisualizerInCompact;

    /// <summary>Режим визуализатора (0..3). Меняется кликом по визуализатору, сохраняется.</summary>
    public int VisualizerMode => _settings.VisualizerMode;

    /// <summary>Следующий режим визуализатора по кругу; возвращает новое значение (для вьюхи).</summary>
    public int CycleVisualizerMode()
    {
        _settings.VisualizerMode = (_settings.VisualizerMode + 1) % 4;
        Raise(nameof(VisualizerMode));
        return _settings.VisualizerMode;
    }

    public bool MagnetOn
    {
        get => _settings.MagneticSnap;
        set { _settings.MagneticSnap = value; Raise(); }
    }

    // ---- M3.3/M3.4: скины (.wsz/.zbs) ----

    /// <summary>Папка скинов: выбранная пользователем или стандартная.</summary>
    public string SkinsDir =>
        string.IsNullOrEmpty(_settings.SkinsFolder)
            ? Path.Combine(SettingsStore.ResolveDirectory(), "skins")
            : _settings.SkinsFolder;

    public IReadOnlyList<string> SkinsList { get; private set; } = Array.Empty<string>();
    private int _selectedSkinIndex = -1;
    public int SelectedSkinIndex { get => _selectedSkinIndex; set => Set(ref _selectedSkinIndex, value); }

    public bool SkinActive => !string.IsNullOrEmpty(_settings.SkinFile);

    /// <summary>Открыть скин-окно (вьюха создаёт SkinWindow). Параметр — полный путь.</summary>
    public event Action<string>? SkinOpenRequested;
    public event Action? SkinCloseRequested;

    /// <summary>Диалог выбора скин-файлов (вешает вьюха): «скины лежат где угодно» → копируем к себе.</summary>
    public Func<Task<IReadOnlyList<string>>>? SkinFilesPicker { get; set; }

    public AsyncRelayCommand AddSkinsCommand { get; private set; } = null!;
    public AsyncRelayCommand ChooseSkinsFolderCommand { get; private set; } = null!;
    public RelayCommand ResetSkinsFolderCommand { get; private set; } = null!;
    public RelayCommand RefreshSkinsCommand { get; private set; } = null!;

    /// <summary>Выбрана нестандартная папка — показать кнопку возврата к стандартной.</summary>
    public bool IsCustomSkinsDir => !string.IsNullOrEmpty(_settings.SkinsFolder);
    public RelayCommand ApplySkinCommand { get; private set; } = null!;
    public RelayCommand DisableSkinCommand { get; private set; } = null!;
    public RelayCommand ConvertSkinCommand { get; private set; } = null!;

    private async Task AddSkinsAsync()
    {
        if (SkinFilesPicker is null) return;
        var files = await SkinFilesPicker();
        if (files.Count == 0) return;
        Directory.CreateDirectory(SkinsDir);
        var added = 0;
        foreach (var file in files)
        {
            if (!file.EndsWith(".wsz", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".zbs", StringComparison.OrdinalIgnoreCase))
                continue;
            var dst = Path.Combine(SkinsDir, Path.GetFileName(file));
            try
            {
                File.Copy(file, dst, overwrite: true);
                added++;
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
            }
        }
        if (added > 0)
        {
            RefreshSkins();
            // Добавили один — сразу выделяем его: следующий клик «Применить» уже про него.
            if (added == 1)
                SelectedSkinIndex = SkinsList.ToList()
                    .FindIndex(n => n.Equals(Path.GetFileName(files[0]), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void RefreshSkins()
    {
        // Сохраняем выбор по имени: список пересобирается (конвертация добавляет .zbs),
        // индексы сдвигаются — иначе «Применить» уходил бы в другой файл.
        var keep = SelectedSkinIndex >= 0 && SelectedSkinIndex < SkinsList.Count
            ? SkinsList[SelectedSkinIndex]
            : null;
        Directory.CreateDirectory(SkinsDir);
        SkinsList = Directory.EnumerateFiles(SkinsDir)
            .Where(f => f.EndsWith(".wsz", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".zbs", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Raise(nameof(SkinsList));
        Raise(nameof(HasSkins));
        SelectedSkinIndex = keep is null ? -1 : SkinsList.ToList().IndexOf(keep);
    }

    public bool HasSkins => SkinsList.Count > 0;

    private void ApplySelectedSkin()
    {
        if (SelectedSkinIndex < 0 || SelectedSkinIndex >= SkinsList.Count) return;
        ApplySkinFile(SkinsList[SelectedSkinIndex]);
    }

    /// <summary>
    /// Два рода скинов: ТЕМА (только палитра) перекрашивает наш UI на месте;
    /// классика (.wsz/конвертированный .zbs с элементами) — отдельное ретро-окно.
    /// </summary>
    private void ApplySkinFile(string file)
    {
        var path = Path.Combine(SkinsDir, file);
        ZBS.Skins.SkinPackage package;
        try
        {
            package = ZBS.Skins.SkinPackage.Load(path);
        }
        catch (Exception ex)
        {
            ReportSkinError(ex.Message);
            return;
        }

        _settings.SkinFile = file;
        Raise(nameof(SkinActive));

        if (package.IsTheme)
        {
            SkinCloseRequested?.Invoke(); // ретро-окно, если было, убираем
            ThemeService.Apply(package.Model.Manifest.Theme);
        }
        else
        {
            // Ретро: если у пакета есть и палитра — применяем и её.
            if (package.Model.Manifest.Theme is { Count: > 0 } theme)
                ThemeService.Apply(theme);
            SkinOpenRequested?.Invoke(path);
        }
    }

    /// <summary>Ошибка загрузки скина (битый zip и т.п.) — показать в статусе, скин сбросить.
    /// Старое скин-окно тоже закрываем: иначе оно висело бы сиротой при погасшей кнопке «Выключить».</summary>
    public void ReportSkinError(string message)
    {
        _settings.SkinFile = "";
        Raise(nameof(SkinActive));
        StatusText = message;
        SkinCloseRequested?.Invoke();
    }

    /// <summary>Автооткрытие сохранённого скина при старте (зовёт вьюха после показа окна).</summary>
    public void OpenSavedSkinIfAny()
    {
        if (string.IsNullOrEmpty(_settings.SkinFile)) return;
        if (File.Exists(Path.Combine(SkinsDir, _settings.SkinFile)))
            ApplySkinFile(_settings.SkinFile);
        else { _settings.SkinFile = ""; Raise(nameof(SkinActive)); }
    }

    private void DisableSkin()
    {
        _settings.SkinFile = "";
        Raise(nameof(SkinActive));
        ThemeService.Apply(null); // палитра — на дефолт
        SkinCloseRequested?.Invoke();
    }

    /// <summary>Пользователь закрыл скин-окно крестиком: это «выключить скин», а не «спрятать до рестарта».</summary>
    public void OnSkinWindowClosedByUser()
    {
        if (!SkinActive) return; // закрытие пришло из DisableSkin — уже сброшено
        _settings.SkinFile = "";
        Raise(nameof(SkinActive));
        ThemeService.Apply(null); // ретро мог тащить палитру — возвращаем дефолт
    }

    private void ConvertSelectedSkin()
    {
        if (SelectedSkinIndex < 0 || SelectedSkinIndex >= SkinsList.Count) return;
        var file = SkinsList[SelectedSkinIndex];
        if (!file.EndsWith(".wsz", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var src = Path.Combine(SkinsDir, file);
            var dst = Path.ChangeExtension(src, ".zbs");
            // Существующий .zbs не перетираем молча — там могут быть ручные правки layout.json.
            for (var n = 2; File.Exists(dst); n++)
                dst = Path.Combine(SkinsDir,
                    $"{Path.GetFileNameWithoutExtension(file)} ({n}).zbs");
            ZBS.Skins.SkinPackage.Load(src).SaveAsZbs(dst);
            StatusText = Loc.T("SkinConverted", Path.GetFileName(dst));
            RefreshSkins();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    // ---- M3.4: waveform-сикбар ----

    public bool WaveformOn
    {
        get => _settings.WaveformSeekbar;
        set
        {
            _settings.WaveformSeekbar = value;
            Raise();
            Raise(nameof(WaveformVisible));
            // Включили посреди трека — пики надо посчитать сейчас, а не со следующего трека.
            if (value && _peaks is null)
                UpdateWaveform(_engine.CurrentTrack);
        }
    }

    private float[]? _peaks;
    private int _waveGeneration;
    /// <summary>Пики текущего трека (вьюха кормит WaveformControl).</summary>
    public float[]? Peaks { get => _peaks; private set { _peaks = value; Raise(); Raise(nameof(WaveformVisible)); } }

    public bool WaveformVisible => WaveformOn && _peaks is not null;

    public double WaveProgress => DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) : 0;

    private void UpdateWaveform(Track? track)
    {
        var generation = ++_waveGeneration;
        if (track is null || !WaveformOn) { Peaks = null; return; }
        var path = track.FilePath;
        _ = Task.Run(() =>
        {
            // Отмена по поколению внутри декода: быстрый Next не должен копить
            // параллельные полные декоды двухчасовых миксов.
            var peaks = _engine.ComputeWaveform(path, isStale: () => generation != _waveGeneration);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _waveGeneration) Peaks = peaks;
            });
        });
    }

    /// <summary>FFT для SpectrumControl (вьюха вешает как источник).</summary>
    public bool GetSpectrumData(float[] buffer) => _engine.GetSpectrum(buffer);

    // ---- M4: видео ----

    private bool _isVideoActive;
    /// <summary>Играет видео: в Обзоре вместо обложки — поверхность mpv.</summary>
    public bool IsVideoActive
    {
        get => _isVideoActive;
        private set { if (Set(ref _isVideoActive, value)) Raise(nameof(CoverVisibleInOverview)); }
    }
    public bool CoverVisibleInOverview => !_isVideoActive;

    /// <summary>HWND хоста видео → mpv (вешает App при наличии libmpv).</summary>
    public Action<IntPtr>? VideoSurfaceSetter { get; set; }

    /// <summary>Вкл/выкл декодирование видео (свернули окно → выкл, звук идёт).</summary>
    public Action<bool>? VideoDecodingSetter { get; set; }

    public void SetWindowMinimized(bool minimized) => VideoDecodingSetter?.Invoke(!minimized);

    // ---- M5 «Сеть и выводы» ----

    public RadioViewModel Radio { get; private set; } = null!;
    public PodcastsViewModel Podcasts { get; private set; } = null!;

    private readonly ZBS.Core.Remote.WebRemote _remote = new();

    public bool WebRemoteOn
    {
        get => _settings.WebRemoteEnabled;
        set
        {
            _settings.WebRemoteEnabled = value;
            if (value) StartRemote(); else _remote.Stop();
            Raise();
            Raise(nameof(RemoteLinkText));
            Raise(nameof(RemoteQr));
            Raise(nameof(RemoteQrVisible));
        }
    }

    private void StartRemote()
    {
        if (!_remote.Start())
            StatusText = "Веб-пульт не запустился (порт 8973 занят?)";
    }

    public string RemoteLinkText
    {
        get
        {
            if (!_remote.IsRunning) return Loc.T("RemoteOffHint");
            var links = _remote.Links;
            if (links.Count == 0) return "(нет сети)";
            // Ссылка на каждый интерфейс: телефон должен быть в той же сети РОУТЕРА
            // (Wi-Fi на самом ПК не нужен — провод и Wi-Fi одного роутера это одна сеть).
            return Loc.T("RemoteOpenOnPhone") + "\n" + string.Join("\n", links)
                   + "\nНе открывается — разреши доступ в брандмауэре Windows (диалог при первом включении).";
        }
    }

    /// <summary>QR первой ссылки — сканируешь телефоном прямо с экрана.</summary>
    public Avalonia.Media.Imaging.WriteableBitmap? RemoteQr
    {
        get
        {
            if (!_remote.IsRunning) return null;
            var link = _remote.Links.FirstOrDefault();
            if (link is null) return null;
            try { return QrHelper.Render(link); }
            catch (Exception) { return null; }
        }
    }

    public bool RemoteQrVisible => _remote.IsRunning && _remote.Links.Count > 0;

    /// <summary>Статус для пульта: зовётся из сетевого потока — читаем всё через UI-диспетчер.</summary>
    private string RemoteStatusJson()
    {
        string title = "", subtitle = "";
        double position = 0, duration = 0, volume = 0;
        var playing = false;
        Dispatcher.UIThread.Invoke(() =>
        {
            if (_engine.CurrentIsRadio)
            {
                title = _engine.RadioStationName ?? "Радио";
                subtitle = Radio.NowPlayingTitle;
            }
            else if (_engine.CurrentTrack is { } t)
            {
                title = MiniTitle;
                subtitle = MiniSubtitle;
            }
            position = _engine.PositionSeconds;
            duration = _engine.CurrentIsRadio ? 0 : _engine.DurationSeconds;
            volume = _engine.Volume;
            playing = _engine.State == PlaybackState.Playing;
        });
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            title, subtitle, position, duration, volume, playing,
        });
    }

    /// <summary>Списки для вкладок пульта (зовётся из сетевого потока — всё через UI-диспетчер).</summary>
    private string RemoteListJson(string what, string query)
    {
        return Dispatcher.UIThread.Invoke(() =>
        {
            switch (what)
            {
                case "queue":
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        items = Tracks.Select((t, i) => new
                        {
                            index = i, name = t.DisplayName, current = i == _engine.CurrentIndex,
                        }).ToArray(),
                    });
                case "radio":
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        favorites = Radio.FavoriteStations.Select((s, i) => new
                        {
                            index = i, name = s.Name, detail = s.Subtitle, current = s.Url == Radio.PlayingUrl,
                        }).ToArray(),
                        fm = ZBS.Core.Radio.FmStations.All.Select((s, i) => new
                        {
                            index = i, name = s.Name, detail = $"{s.Mhz:0.0} FM", current = s.Url == Radio.PlayingUrl,
                        }).ToArray(),
                    });
                case "podcasts":
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        feeds = Podcasts.Feeds.Select((f, i) => new { index = i, name = f.Title }).ToArray(),
                    });
                case "episodes":
                {
                    var feed = int.TryParse(System.Text.RegularExpressions.Regex.Match(query, @"feed=(\d+)").Groups[1].Value, out var f) ? f : -1;
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        items = Podcasts.EpisodesOf(feed).Select((e, i) => new
                        {
                            index = i, name = e.Title, detail = e.Subtitle,
                        }).ToArray(),
                    });
                }
                default:
                    return "{}";
            }
        });
    }

    private bool RemoteCommand(string name, string? arg)
    {
        var ok = true;
        Dispatcher.UIThread.Invoke(() =>
        {
            switch (name)
            {
                case "playpause": _engine.PlayPause(); SyncPlayState(); break;
                case "next": _engine.Next(); break;
                case "prev": _engine.Previous(); break;
                case "stop": _engine.Stop(); SyncPlayState(); break;
                case "play" when int.TryParse(arg, out var idx):
                    _engine.PlayAt(idx);
                    break;
                case "radiofav" when int.TryParse(arg, out var fav):
                    Radio.PlayFavorite(fav);
                    break;
                case "radiofm" when int.TryParse(arg, out var fm):
                    Radio.PlayFmStation(fm);
                    break;
                case "podcast" when arg?.Split(':') is [var fs, var es]
                                    && int.TryParse(fs, out var feed) && int.TryParse(es, out var ep):
                    Podcasts.PlayFromRemote(feed, ep);
                    break;
                case "volume" when double.TryParse(arg, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v):
                    Volume = Math.Clamp(v, 0, 1);
                    break;
                case "seek" when double.TryParse(arg, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f) && !_engine.CurrentIsRadio:
                    _engine.PositionSeconds = Math.Clamp(f, 0, 1) * _engine.DurationSeconds;
                    UpdateDiscord(); // перемотка с пульта — переякорим таймер Discord
                    break;
                default: ok = false; break;
            }
        });
        return ok;
    }

    // ---- M6: интеграции ----

    private readonly ZBS.Core.Integrations.DiscordPresence _discord = new();
    private Task _discordChain = Task.CompletedTask; // очередь апдейтов Discord — строгий порядок FIFO
    private readonly object _discordChainLock = new();

    // Все обращения к Discord — через одну FIFO-очередь на пуле потоков (TaskScheduler.Default!
    // без него ContinueWith в enable-пути захватил бы UI-планировщик и блокирующий пайп-IO завис бы на UI).
    private void EnqueueDiscord(Action action)
    {
        lock (_discordChainLock)
            _discordChain = _discordChain.ContinueWith(_ => action(), TaskScheduler.Default);
    }

    public bool RecordMp3On
    {
        get => _settings.RecordMp3;
        set { _settings.RecordMp3 = value; Raise(); }
    }

    public bool Mp3EncoderPresent => PlaybackEngine.Mp3RecordingAvailable;

    private static string EffectiveDiscordAppId => ZBS.Core.Integrations.DiscordPresence.DefaultAppId;

    private string _discordStatus = "";
    public string DiscordStatus { get => _discordStatus; private set => Set(ref _discordStatus, value); }

    public bool DiscordOn
    {
        get => _settings.DiscordPresence;
        set
        {
            _settings.DiscordPresence = value;
            Raise();
            if (value)
            {
                // Коннект (до 10 пайпов × 300 мс + чтение READY) — в фоне: иначе морозил бы UI.
                DiscordStatus = "Подключаюсь к Discord…";
                var appId = EffectiveDiscordAppId;
                Task.Run(() => _discord.Connect(appId)).ContinueWith(t =>
                {
                    var ok = t.Status == TaskStatus.RanToCompletion && t.Result;
                    DiscordStatus = ok ? "Подключено к Discord"
                        : "Discord не найден (нужен запущенный десктоп-клиент)";
                    if (ok) UpdateDiscord();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            else
            {
                // Через ту же очередь: иначе ClearActivity мог обогнать уже поставленный в очередь
                // SetListening, и презенс «воскрес» бы после выключения тумблера.
                EnqueueDiscord(() => _discord.ClearActivity());
                DiscordStatus = "";
            }
        }
    }

    private void UpdateDiscord()
    {
        if (!_settings.DiscordPresence) return;
        // Снимаем состояние на UI-потоке, шлём в Discord — в фоне (пайп-запись блокирующая).
        var radio = _engine.CurrentIsRadio;
        var stationName = _engine.RadioStationName ?? "Радио";
        var radioTitle = Radio.NowPlayingTitle;
        var hasTrack = _engine.CurrentTrack is not null && _engine.State != PlaybackState.Stopped;
        var paused = _engine.State == PlaybackState.Paused;
        var title = OverviewTitle;
        var artist = OverviewArtist;
        var pos = _engine.PositionSeconds;
        var dur = paused ? 0 : _engine.DurationSeconds; // на паузе без таймера — иначе Discord крутит прогресс дальше
        // Сериализуем апдейты в цепочку: два быстрых Next не должны примениться в обратном порядке
        // (иначе Discord показал бы старый трек). SetListening сам переподключается по backoff.
        EnqueueDiscord(() =>
        {
            if (radio) _discord.SetListening(stationName, radioTitle, 0, 0);
            else if (hasTrack) _discord.SetListening(title, artist, pos, dur);
            else _discord.ClearActivity();
        });
    }

    // ---- M6: плагины ----

    private readonly PluginHost _plugins = new(
        typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0");

    /// <summary>Число загруженных плагинов (для статуса в настройках).</summary>
    public int PluginCount => _plugins.Loaded.Count;

    private static string PluginsDir => System.IO.Path.Combine(AppContext.BaseDirectory, "plugins");

    public bool PluginsOn
    {
        get => _settings.PluginsEnabled;
        set
        {
            if (_settings.PluginsEnabled == value) return;
            _settings.PluginsEnabled = value;
            Raise();
            if (value) _plugins.LoadFrom(PluginsDir);
            else _plugins.UnloadAll();
            Raise(nameof(PluginCount));
        }
    }

    // Снимок трека для плагинов (радио/стоп → null).
    private PluginTrackInfo? BuildPluginTrack(Track? track) =>
        track is null || _engine.CurrentIsRadio
            ? null
            : new PluginTrackInfo(OverviewTitle, OverviewArtist, null, _engine.DurationSeconds, track.FilePath);

    // ---- M6: Last.fm скробблинг ----

    private readonly ZBS.Core.Integrations.LastFmScrobbler _lastfm = new();
    private DateTimeOffset _trackStartedAt = DateTimeOffset.UtcNow;

    public bool LastfmOn
    {
        get => _settings.LastfmEnabled;
        set
        {
            _settings.LastfmEnabled = value;
            Raise();
            LastfmStatus = !value ? ""
                : _lastfm.Ready ? $"Скробблит как {_settings.LastfmUser}"
                : "Заполни ключи (last.fm/api → Create API account) и войди";
        }
    }

    public string LastfmApiKey
    {
        get => _settings.LastfmApiKey;
        set { _settings.LastfmApiKey = value.Trim(); _lastfm.ApiKey = _settings.LastfmApiKey; Raise(); }
    }

    public string LastfmApiSecret
    {
        get => _settings.LastfmApiSecret;
        set { _settings.LastfmApiSecret = value.Trim(); _lastfm.ApiSecret = _settings.LastfmApiSecret; Raise(); }
    }

    private string _lastfmLogin = "";
    public string LastfmLogin { get => _lastfmLogin; set => Set(ref _lastfmLogin, value); }

    private string _lastfmPassword = "";
    public string LastfmPassword { get => _lastfmPassword; set => Set(ref _lastfmPassword, value); }

    private string _lastfmStatus = "";
    public string LastfmStatus { get => _lastfmStatus; private set => Set(ref _lastfmStatus, value); }

    public RelayCommand LastfmLoginCommand { get; private set; } = null!;

    private async Task LastfmLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(LastfmApiKey) || string.IsNullOrWhiteSpace(LastfmApiSecret))
        {
            LastfmStatus = "Сначала ключи: last.fm/api → Create API account → API key + Shared secret";
            return;
        }
        if (string.IsNullOrWhiteSpace(LastfmLogin) || string.IsNullOrWhiteSpace(LastfmPassword))
        {
            LastfmStatus = "Введи логин и пароль Last.fm (пароль не сохраняется)";
            return;
        }
        try
        {
            LastfmStatus = "Вхожу…";
            var user = await _lastfm.AuthAsync(LastfmLogin, LastfmPassword, CancellationToken.None);
            _settings.LastfmSessionKey = _lastfm.SessionKey;
            _settings.LastfmUser = user;
            LastfmPassword = ""; // пароль отработал и забыт
            LastfmStatus = $"Готово — скробблит как {user}";
        }
        catch (Exception ex)
        {
            LastfmStatus = ex.Message;
        }
    }

    // «Артист — Название» из имени трека (та же логика, что в Обзоре).
    private static (string Artist, string Title) SplitArtistTitle(string display)
    {
        var sep = SplitPoint(display, out var len);
        return sep > 0 ? (display[..sep].Trim(), display[(sep + len)..].Trim()) : ("", display);
    }

    private void LastfmNowPlaying(Track? track)
    {
        if (!_settings.LastfmEnabled || !_lastfm.Ready || track is null || _engine.CurrentIsRadio) return;
        var (artist, title) = SplitArtistTitle(track.DisplayName);
        if (artist.Length == 0) return; // Last.fm без артиста не принимает — не мусорим
        Observe(_lastfm.NowPlayingAsync(artist, title, CancellationToken.None));
    }

    private void LastfmScrobble(Track track)
    {
        if (!_settings.LastfmEnabled || !_lastfm.Ready || _engine.CurrentIsRadio) return;
        if (_engine.DurationSeconds is > 0 and <= 30) return; // Last.fm не принимает треки ≤30 с
        var (artist, title) = SplitArtistTitle(track.DisplayName);
        if (artist.Length == 0) return;
        Observe(_lastfm.ScrobbleAsync(artist, title, _trackStartedAt, CancellationToken.None));
    }

    // Fire-and-forget с наблюдением: ошибку (истёк ключ, сеть) видно в статусе, а не «скробблю…» врёт молча.
    private void Observe(Task task) => task.ContinueWith(t =>
    {
        if (t.Exception?.GetBaseException() is { } ex)
            Dispatcher.UIThread.Post(() => LastfmStatus = ex.Message);
    }, TaskContinuationOptions.OnlyOnFaulted);

    // ---- M5.2: DLNA-кастинг (телевизор/колонка в локальной сети) ----

    private readonly ZBS.Core.Cast.DlnaControl _dlna = new();
    private readonly ZBS.Core.Cast.MediaServer _mediaServer = new();
    private List<ZBS.Core.Cast.DlnaDevice> _castDevices = new();
    private ZBS.Core.Cast.DlnaDevice? _castingTo;

    public List<string> CastDevices { get; private set; } = new();

    private int _castDeviceIndex = -1;
    public int CastDeviceIndex { get => _castDeviceIndex; set => Set(ref _castDeviceIndex, value); }

    private string _castStatus = "";
    public string CastStatus { get => _castStatus; private set => Set(ref _castStatus, value); }

    public bool IsCasting => _castingTo is not null;

    public RelayCommand FindCastCommand { get; private set; } = null!;
    public RelayCommand CastPlayCommand { get; private set; } = null!;
    public RelayCommand CastStopCommand { get; private set; } = null!;

    private double _castVolume = 30;
    public double CastVolume
    {
        get => _castVolume;
        set
        {
            if (!Set(ref _castVolume, value) || _castingTo is null) return;
            var device = _castingTo;
            _ = _dlna.SetVolumeAsync(device, (int)value, CancellationToken.None);
        }
    }

    private async Task FindCastAsync()
    {
        CastStatus = "Ищу устройства в сети (SSDP)…";
        try
        {
            var found = await ZBS.Core.Cast.SsdpDiscovery.FindRenderersAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
            _castDevices = found.ToList();
            CastDevices = found.Select(d => d.Name).ToList();
            Raise(nameof(CastDevices));
            if (CastDeviceIndex < 0 && CastDevices.Count > 0) CastDeviceIndex = 0;
            CastStatus = found.Count == 0
                ? "Устройств не найдено (DLNA у телевизора включён? та же сеть?)"
                : $"Найдено: {found.Count}";
        }
        catch (Exception ex)
        {
            CastStatus = $"Поиск не удался: {ex.Message}";
        }
    }

    private async Task CastPlayAsync()
    {
        if (_castDeviceIndex < 0 || _castDeviceIndex >= _castDevices.Count)
        {
            CastStatus = "Сначала найди и выбери устройство";
            return;
        }
        if (_engine.CurrentIsRadio)
        {
            CastStatus = "Радио на устройство пока не кастится — только файлы (трек/эпизод)";
            return;
        }
        if (_engine.CurrentTrack is not { } track)
        {
            CastStatus = "Нечего кастить — включи трек";
            return;
        }
        var device = _castDevices[_castDeviceIndex];
        var ip = ZBS.Core.Remote.WebRemote.BestLanIp();
        if (ip is null) { CastStatus = "Не нашёл LAN-адрес машины"; return; }
        try
        {
            var url = _mediaServer.Serve(track.FilePath, ip);
            if (url is null) { CastStatus = "Медиасервер не запустился (порт 8974 занят?)"; return; }
            CastStatus = $"Отправляю на {device.Name}…";
            await _dlna.PlayUriAsync(device, url, track.DisplayName, CancellationToken.None);
            _castingTo = device;
            Raise(nameof(IsCasting));
            if (_engine.State == PlaybackState.Playing) _engine.PlayPause(); // локально — тишина
            CastStatus = $"Играет на {device.Name}: {track.DisplayName}";
        }
        catch (Exception ex)
        {
            _mediaServer.Stop();
            CastStatus = $"Не вышло: {ex.Message}";
        }
    }

    private async Task CastStopAsync()
    {
        if (_castingTo is { } device)
        {
            try { await _dlna.StopAsync(device, CancellationToken.None); }
            catch (Exception) { }
        }
        _mediaServer.Stop();
        _castingTo = null;
        Raise(nameof(IsCasting));
        CastStatus = "Кастинг остановлен";
    }

    // Селектор устройства вывода (BASS): «Системное по умолчанию» + живые устройства.
    public List<string> OutputDevices { get; private set; } = new();
    private List<int> _outputDeviceIndices = new();

    private void InitOutputDevices()
    {
        if (_engine.Backend is not ZBS.Core.Audio.Bass.BassAudioBackend bass) return;
        var devices = bass.ListOutputDevices();
        OutputDevices = new List<string> { Loc.T("DeviceDefault") };
        _outputDeviceIndices = new List<int> { -1 };
        foreach (var (index, name, isDefault) in devices)
        {
            OutputDevices.Add(isDefault ? $"{name} ✓" : name);
            _outputDeviceIndices.Add(index);
        }
        Raise(nameof(OutputDevices));
        var saved = _outputDeviceIndices.IndexOf(_settings.OutputDevice);
        _outputDeviceSelected = saved < 0 ? 0 : saved;
        if (_settings.OutputDevice > 0 && saved > 0)
            bass.SetOutputDevice(_settings.OutputDevice);
        Raise(nameof(OutputDeviceSelected));
    }

    private int _outputDeviceSelected;
    public int OutputDeviceSelected
    {
        get => _outputDeviceSelected;
        set
        {
            if (!Set(ref _outputDeviceSelected, value)) return;
            if (value < 0 || value >= _outputDeviceIndices.Count) return;
            var device = _outputDeviceIndices[value];
            _settings.OutputDevice = device;
            if (_engine.Backend is ZBS.Core.Audio.Bass.BassAudioBackend bass)
            {
                // «Системное по умолчанию» = устройство с IsDefault (BASS-индекс 1 — просто
                // первое перечисленное, на мульти-картах это не то же самое).
                var target = device < 0
                    ? bass.ListOutputDevices().FirstOrDefault(d => d.IsDefault).Index
                    : device;
                if (target <= 0) target = 1; // экзотика без дефолтного — хоть какое-то
                if (!bass.SetOutputDevice(target)) StatusText = bass.LastError ?? "Не удалось переключить вывод";
            }
        }
    }

    public string TileTitle => _settingsTile switch
    {
        "viz" => Loc.T("TileVisualizer"),
        "eq" => Loc.T("TileEq"),
        "audio" => Loc.T("TileAudio"),
        "keys" => Loc.T("TileKeys"),
        "look" => Loc.T("TileLook"),
        "net" => Loc.T("TileNet"),
        "upd" => Loc.T("TileUpdates"),
        _ => Loc.T("SettingsTitle"),
    };

    public string TileStubText => _settingsTile switch
    {
        "keys" => Loc.T("KeysInfo"),
        "look" => Loc.T("LookInfo"),
        "upd" => Loc.T("UpdatesInfo"),
        _ => Loc.T("ComingM32"),
    };

    // Аудио-движок: живые настройки (закон проекта — всё вкл/выкл).
    public double CrossfadeSeconds
    {
        get => _settings.CrossfadeSeconds;
        set
        {
            _settings.CrossfadeSeconds = Math.Round(value, 1);
            _engine.CrossfadeSeconds = _settings.CrossfadeSeconds;
            Raise();
            Raise(nameof(CrossfadeLabel));
        }
    }
    public string CrossfadeLabel => CrossfadeSeconds <= 0 ? Loc.T("SetOffShort") : Loc.T("SetSeconds", CrossfadeSeconds);

    public double SmoothPauseMs
    {
        get => _settings.SmoothPauseMs;
        set
        {
            _settings.SmoothPauseMs = (int)value;
            _engine.SmoothPauseMs = _settings.SmoothPauseMs;
            Raise();
            Raise(nameof(SmoothPauseLabel));
        }
    }
    public string SmoothPauseLabel => SmoothPauseMs <= 0 ? Loc.T("SetOffShort") : Loc.T("SetMs", (int)SmoothPauseMs);

    public bool ReplayGainOn
    {
        get => _settings.ReplayGain;
        set { _settings.ReplayGain = value; _engine.ReplayGainEnabled = value; Raise(); }
    }

    public bool ResumeLongOn
    {
        get => _settings.ResumeLongFiles;
        set { _settings.ResumeLongFiles = value; _engine.ResumeLongFiles = value; Raise(); }
    }

    // Локализованные подписи новых панелей.
    public string NavAllTracksText => Loc.T("NavAllTracks");
    public string NavFoldersText => Loc.T("NavFolders");
    public string NavRatingsText => Loc.T("NavRatings");
    public string NavQueueText => Loc.T("NavQueue");
    public string NavRadioText => Loc.T("NavRadio");
    public string NavPodcastsText => Loc.T("NavPodcasts");
    public string NavSettingsText => Loc.T("NavSettings");
    public string SecPhonotekaText => Loc.T("SecPhonoteka");
    public string SecPlaylistsText => Loc.T("SecPlaylists");
    public string BackText => Loc.T("Back");
    public string QueueTitleText => Loc.T("QueueTitle");
    public string TextBtnText => Loc.T("TextBtn");
    public string SettingsTitleText => Loc.T("SettingsTitle");
    public string TileVisualizerText => Loc.T("TileVisualizer");
    public string TileVisualizerHint => Loc.T("TileVisualizerHint");
    public string TileEqText => Loc.T("TileEq");
    public string TileEqHint => Loc.T("TileEqHint");
    public string TileAudioText => Loc.T("TileAudio");
    public string TileAudioHint => Loc.T("TileAudioHint");
    public string TileKeysText => Loc.T("TileKeys");
    public string TileKeysHint => Loc.T("TileKeysHint");
    public string TileLookText => Loc.T("TileLook");
    public string TileLookHint => Loc.T("TileLookHint");
    public string TileNetText => Loc.T("TileNet");
    public string TileNetHint => Loc.T("TileNetHint");

    // Секции «Эфир» отключаемы (закон проекта: любая фича — вкл/выкл).
    public bool RadioSectionOn
    {
        get => _settings.RadioEnabled;
        set
        {
            _settings.RadioEnabled = value;
            if (!value && Section == NavSection.Radio) NavTo(NavSection.AllTracks);
            Raise();
        }
    }

    public bool PodcastsSectionOn
    {
        get => _settings.PodcastsEnabled;
        set
        {
            _settings.PodcastsEnabled = value;
            if (!value && Section == NavSection.Podcasts) NavTo(NavSection.AllTracks);
            Raise();
        }
    }

    public bool AirSectionVisible => RadioSectionOn || PodcastsSectionOn;
    public string TileUpdatesText => Loc.T("TileUpdates");
    public string TileUpdatesHint => Loc.T("TileUpdatesHint");
    public string SetCrossfadeText => Loc.T("SetCrossfade");
    public string SetSmoothPauseText => Loc.T("SetSmoothPause");
    public string SetReplayGainText => Loc.T("SetReplayGain");
    public string SetResumeLongText => Loc.T("SetResumeLong");
    public string DetailAlbumText => Loc.T("DetailAlbum");
    public string DetailGenreText => Loc.T("DetailGenre");
    public string DetailYearText => Loc.T("DetailYear");
    public string DetailFormatText => Loc.T("DetailFormat");
    public string DetailDurationText => Loc.T("DetailDuration");
    public string MiniTipText => Loc.T("MiniTip");
    public string SearchShortText => Loc.T("SearchShort");
    public string GenrePrefixText => Loc.T("GenrePrefix");
    public string ColHeaderText => Loc.T("ColHeader");
    public string ColTimeText => Loc.T("ColTime");
    public string NavLibraryText => Loc.T("LibraryTab");
    public string EqOnLblText => Loc.T("EqOnLbl");
    public string PresetsLblText => Loc.T("PresetsLbl");
    public string PresetFlatText => Loc.T("PresetFlat");
    public string PresetAufText => Loc.T("PresetAuf");
    public string PresetRockText => Loc.T("PresetRock");
    public string PresetPopText => Loc.T("PresetPop");
    public string PresetBassText => Loc.T("PresetBass");
    public string PresetVoiceText => Loc.T("PresetVoice");
    public string VizOnLblText => Loc.T("VizOnLbl");
    public string VizCompactLblText => Loc.T("VizCompactLbl");
    public string MagnetLblText => Loc.T("MagnetLbl");
    public string MagnetHintText => Loc.T("MagnetHint");
    public string LookInfoText => Loc.T("LookInfo");
    public string SkinsTitleText => Loc.T("SkinsTitle");
    public string SkinApplyText => Loc.T("SkinApply");
    public string SkinDisableText => Loc.T("SkinDisable");
    public string SkinRefreshText => Loc.T("SkinRefresh");
    public string SkinFolderText => Loc.T("SkinFolder");
    public string SkinAddText => Loc.T("SkinAdd");
    public string SkinFolderResetText => Loc.T("SkinFolderReset");
    public string SkinConvertText => Loc.T("SkinConvert");
    public string SkinNoneHintText => Loc.T("SkinNoneHint");
    public string WaveformLblText => Loc.T("WaveformLbl");

    public RelayCommand ToggleNowPlayingCommand { get; }
    public string NowPlayingTip => Loc.T("NowPlayingTip");
    public string NowPlayingSubtitle => OverviewArtist;

    /// <summary>Панель медиатеки (создаётся в App и прикрепляется сюда).</summary>
    public LibraryViewModel? Library { get; private set; }

    /// <summary>Диалог сохранения плейлиста — вешается вьюхой.</summary>
    public Func<Task<string?>>? PlaylistSavePicker { get; set; }

    /// <summary>Поставщик обложки трека (App: теги → папка → Cover Art Archive).</summary>
    public Func<Track, Task<byte[]?>>? CoverProvider { get; set; }

    private Bitmap? _cover;
    private int _coverGeneration;
    public Bitmap? Cover { get => _cover; private set => Set(ref _cover, value); }

    /// <summary>Замена обложки с утилизацией старой: Bitmap держит нативную память Skia,
    /// без Dispose длинная сессия текла на десятки МБ. Dispose — следующим кадром,
    /// когда UI уже не рисует старую картинку.</summary>
    private void SetCover(Bitmap? value)
    {
        if (ReferenceEquals(_cover, value)) return;
        var old = _cover;
        Cover = value;
        DisposeLater(old);
    }

    private void SetDetailCover(Bitmap? value)
    {
        if (ReferenceEquals(_detailCover, value)) return;
        var old = _detailCover;
        DetailCover = value;
        DisposeLater(old);
    }

    private static void DisposeLater(Bitmap? bitmap)
    {
        if (bitmap is null) return;
        Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
    }

    private async Task UpdateCoverAsync(Track? track)
    {
        var generation = ++_coverGeneration;
        if (track is null || CoverProvider is null)
        {
            SetCover(null);
            return;
        }
        try
        {
            var bytes = await CoverProvider(track);
            if (generation != _coverGeneration) return; // трек уже сменился
            SetCover(bytes is null ? null : new Bitmap(new MemoryStream(bytes)));
        }
        catch (Exception)
        {
            if (generation == _coverGeneration)
                SetCover(null); // битая картинка — просто без обложки
        }
    }

    public MainViewModel(PlaybackEngine engine, SettingsStore store, AppSettings settings)
    {
        _engine = engine;
        _store = store;
        _settings = settings;

        _engine.Volume = settings.Volume;
        // Движок маршалит свои события на UI-поток сам — подписки простые.
        _engine.Dispatch = a => Dispatcher.UIThread.Post(a);
        _engine.TrackChanged += OnTrackChanged;
        _engine.PlaylistChanged += RefreshPlaylist;

        // Тумблеры движка из настроек (закон проекта: всё вкл/выкл).
        _engine.CrossfadeSeconds = settings.CrossfadeSeconds;
        _engine.SmoothPauseMs = settings.SmoothPauseMs;
        _engine.ResumeLongFiles = settings.ResumeLongFiles;
        _engine.ReplayGainEnabled = settings.ReplayGain;
        _engine.Shuffle = ParseShuffle(settings.Shuffle);
        _engine.Repeat = ParseRepeat(settings.Repeat);

        OpenFolderCommand = new AsyncRelayCommand(PickFolderAsync, ex => StatusText = ex.Message);
        PlayPauseCommand = new RelayCommand(() =>
        {
            _engine.PlayPause();
            SyncPlayState();
            Podcasts?.SavePlayingPosition(); // пауза = точка возврата эпизода
            UpdateDiscord();
        });
        StopCommand = new RelayCommand(() => { _engine.Stop(); SyncPlayState(); });
        NextCommand = new RelayCommand(_engine.Next);
        PreviousCommand = new RelayCommand(_engine.Previous);
        ShuffleCommand = new RelayCommand(CycleShuffle);
        RepeatCommand = new RelayCommand(CycleRepeat);
        AbLoopCommand = new RelayCommand(() => { _engine.AbLoopToggle(); Raise(nameof(AbGlyph)); Raise(nameof(AbActive)); });
        EnqueueSelectedCommand = new RelayCommand(() =>
        {
            if (SelectedIndex >= 0) _engine.Enqueue(SelectedIndex);
        });
        SavePlaylistCommand = new AsyncRelayCommand(SavePlaylistAsync, ex => StatusText = ex.Message);
        JumpCommand = new RelayCommand(JumpToNextMatch);
        ToggleNowPlayingCommand = new RelayCommand(() =>
            Mode = _mode == UiMode.Overview ? UiMode.Browse : UiMode.Overview);

        // Режимы и навигация (канон DESIGN.md §11).
        NavCommand = new ParamRelayCommand(p =>
        {
            if (Enum.TryParse<NavSection>(p?.ToString(), out var section)) NavTo(section);
        });
        OpenSettingsCommand = new RelayCommand(() => Mode = UiMode.Settings);
        OpenOverviewCommand = new RelayCommand(() => Mode = UiMode.Overview);
        BackCommand = new RelayCommand(() => HandleBack());
        ToggleQueueCommand = new RelayCommand(() => QueueOpen = !QueueOpen);
        ToggleLyricsCommand = new RelayCommand(() =>
        {
            LyricsOpen = !LyricsOpen;
            if (LyricsOpen) _ = LoadLyricsAsync();
        });
        PlayQueueItemCommand = new ParamRelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var index)) _engine.PlayAt(index);
        });
        CloseDetailCommand = new RelayCommand(CloseDetail);
        ToggleDetailTextCommand = new RelayCommand(() => DetailTextMode = !DetailTextMode);
        OpenTileCommand = new ParamRelayCommand(p =>
        {
            SettingsTile = p?.ToString() ?? "";
            Raise(nameof(TileTitle));
            Raise(nameof(TileStubText));
            Raise(nameof(TileIsAudio));
            Raise(nameof(TileIsEq));
            Raise(nameof(TileIsViz));
            Raise(nameof(TileIsLook));
            Raise(nameof(TileIsNet));
            Raise(nameof(TileIsStub));
            Mode = UiMode.SettingsDetail;
        });
        EqPresetCommand = new ParamRelayCommand(p => ApplyEqPreset(p?.ToString() ?? ""));
        InitEq();
        CycleSpeedCommand = new RelayCommand(() =>
        {
            var i = Array.IndexOf(SpeedValues, Speed);
            Speed = SpeedValues[(i + 1) % SpeedValues.Length];
            Raise(nameof(SpeedLabel));
        });
        ExpandCommand = new RelayCommand(() => ExpandRequested?.Invoke());

        // ---- M5 «Сеть и выводы» ----
        var settingsDir = SettingsStore.ResolveDirectory();
        Radio = new RadioViewModel(engine, settings, settingsDir);
        Podcasts = new PodcastsViewModel(engine, settingsDir);
        _engine.RadioMetaChanged += _ =>
        {
            Raise(nameof(MiniTitle)); Raise(nameof(MiniSubtitle));
            Raise(nameof(OverviewTitle)); Raise(nameof(OverviewArtist));
            UpdateDiscord(); // сменился трек в эфире
        };
        if (settings.DiscordPresence)
        {
            // Коннект в фоне: синхронно в конструкторе он морозил бы старт до ~3 с (10 пайпов × 300 мс).
            var appId = EffectiveDiscordAppId;
            Task.Run(() =>
            {
                if (_discord.Connect(appId))
                    Dispatcher.UIThread.Post(() => DiscordStatus = "Подключено к Discord");
            });
        }
        InitOutputDevices();
        // Last.fm: ключи из настроек + скроббл на «прослушано» (движок сам считает 50%).
        _lastfm.ApiKey = settings.LastfmApiKey;
        _lastfm.ApiSecret = settings.LastfmApiSecret;
        _lastfm.SessionKey = settings.LastfmSessionKey;
        LastfmLoginCommand = new RelayCommand(() => _ = LastfmLoginAsync());
        _engine.TrackPlayedEnough += LastfmScrobble;
        if (settings.PluginsEnabled) _plugins.LoadFrom(PluginsDir); // M6: general-плагины
        FindCastCommand = new RelayCommand(() => _ = FindCastAsync());
        CastPlayCommand = new RelayCommand(() => _ = CastPlayAsync());
        CastStopCommand = new RelayCommand(() => _ = CastStopAsync());
        _remote.StatusProvider = RemoteStatusJson;
        _remote.CommandHandler = RemoteCommand;
        _remote.ListProvider = RemoteListJson;
        if (settings.WebRemoteEnabled) StartRemote();
        // Сменилась сеть (другой Wi-Fi/роутер, VPN) — ссылка и QR обязаны пересчитаться,
        // иначе на экране висит адрес старой сети.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                Raise(nameof(RemoteLinkText));
                Raise(nameof(RemoteQr));
                Raise(nameof(RemoteQrVisible));
            });

        // M3.3/M3.4: скины.
        AddSkinsCommand = new AsyncRelayCommand(AddSkinsAsync, ex => StatusText = ex.Message);
        RefreshSkinsCommand = new RelayCommand(RefreshSkins);
        ApplySkinCommand = new RelayCommand(ApplySelectedSkin);
        DisableSkinCommand = new RelayCommand(DisableSkin);
        ConvertSkinCommand = new RelayCommand(ConvertSelectedSkin);
        // «Папка скинов…» — ВЫБОР каталога со скинами (диалог), а не открытие проводника.
        ChooseSkinsFolderCommand = new AsyncRelayCommand(async () =>
        {
            if (FolderPicker is null) return;
            var folder = await FolderPicker();
            if (folder is null) return;
            _settings.SkinsFolder = folder;
            Raise(nameof(SkinsDir));
            Raise(nameof(IsCustomSkinsDir));
            RefreshSkins();
        }, ex => StatusText = ex.Message);
        ResetSkinsFolderCommand = new RelayCommand(() =>
        {
            _settings.SkinsFolder = "";
            Raise(nameof(SkinsDir));
            Raise(nameof(IsCustomSkinsDir));
            RefreshSkins();
        });
        RefreshSkins();
        RateSelectedCommand = new ParamRelayCommand(p =>
        {
            if (SelectedIndex >= 0 && SelectedIndex < Tracks.Count &&
                int.TryParse(p?.ToString(), out var rating))
                RateHandler?.Invoke(Tracks[SelectedIndex], rating);
        });

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private static ShuffleMode ParseShuffle(string s) => s switch
    {
        "random" => ShuffleMode.Random,
        "smart" => ShuffleMode.Smart,
        _ => ShuffleMode.Off,
    };

    private static RepeatMode ParseRepeat(string s) => s switch
    {
        "all" => RepeatMode.All,
        "one" => RepeatMode.One,
        _ => RepeatMode.None,
    };

    private void CycleShuffle()
    {
        _engine.Shuffle = _engine.Shuffle switch
        {
            ShuffleMode.Off => ShuffleMode.Random,
            ShuffleMode.Random => ShuffleMode.Smart,
            _ => ShuffleMode.Off,
        };
        _settings.Shuffle = _engine.Shuffle switch
        {
            ShuffleMode.Random => "random",
            ShuffleMode.Smart => "smart",
            _ => "off",
        };
        Raise(nameof(ShuffleGlyph));
        Raise(nameof(ShuffleTip));
        Raise(nameof(ShuffleActive));
    }

    private void CycleRepeat()
    {
        _engine.Repeat = _engine.Repeat switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None,
        };
        _settings.Repeat = _engine.Repeat switch
        {
            RepeatMode.All => "all",
            RepeatMode.One => "one",
            _ => "none",
        };
        Raise(nameof(RepeatGlyph));
        Raise(nameof(RepeatTip));
        Raise(nameof(RepeatActive));
        Raise(nameof(RepeatOne));
    }

    // Флаги активности для подсветки иконок (зелёным) и выбора иконки повтора.
    public bool ShuffleActive => _engine.Shuffle != ShuffleMode.Off;
    public bool RepeatActive => _engine.Repeat != RepeatMode.None;
    public bool RepeatOne => _engine.Repeat == RepeatMode.One;
    public bool AbActive => _engine.AbLoopActive;

    public string ShuffleGlyph => _engine.Shuffle switch
    {
        ShuffleMode.Off => "🔀✕",
        ShuffleMode.Random => "🔀",
        _ => "🔀★",
    };

    public string ShuffleTip => Loc.T(_engine.Shuffle switch
    {
        ShuffleMode.Off => "ShuffleOff",
        ShuffleMode.Random => "ShuffleRandom",
        _ => "ShuffleSmart",
    });

    public string RepeatGlyph => _engine.Repeat switch
    {
        RepeatMode.None => "🔁✕",
        RepeatMode.All => "🔁",
        _ => "🔂",
    };

    public string RepeatTip => Loc.T(_engine.Repeat switch
    {
        RepeatMode.None => "RepeatNone",
        RepeatMode.All => "RepeatAll",
        _ => "RepeatOne",
    });

    public string AbGlyph => _engine.AbLoopActive ? "A↔B" : _engine.LoopAStart >= 0 ? "A→?" : "A-B";
    public string AbTip => Loc.T("AbTip");
    public string PlayNextText => Loc.T("PlayNext");
    public string SpeedTip => Loc.T("SpeedTip");

    public bool SpeedSupported => _engine.SpeedSupported;
    public double[] SpeedValues { get; } = { 0.75, 1.0, 1.25, 1.5, 2.0 };

    public double Speed
    {
        get => _engine.Speed;
        set { _engine.Speed = value; Raise(); Raise(nameof(SpeedLabel)); Raise(nameof(SpeedNumText)); }
    }

    /// <summary>Скорость по канону — «×1.5», не голый ComboBox. Клик циклит по значениям.</summary>
    public string SpeedLabel => $"×{Speed:0.##}";
    /// <summary>Число для чипа «1.0 ×» в транспорте (число жирное, × — muted).</summary>
    public string SpeedNumText => Speed.ToString("0.0#");
    public RelayCommand CycleSpeedCommand { get; private set; } = null!;

    /// <summary>Медиатека прикрепляется после создания (App): играть выборку / дозагрузить.</summary>
    public void AttachLibrary(LibraryViewModel library)
    {
        Library = library;
        library.PlayRequested = (results, startIndex) =>
        {
            var tracks = results.Select(LibraryService.ToTrack).ToList();
            _engine.SetPlaylist(tracks, autoplay: false);
            _engine.PlayAt(startIndex);
        };
        library.AppendRequested = results =>
            _engine.AppendTracks(results.Select(LibraryService.ToTrack).ToList());
        // Клик по треку → карточка-деталь справа. Только когда список реально на экране:
        // экран «Библиотека» делит SelectedIndex с ним, и без гейта его выборы вхолостую
        // грузили обложку/текст в невидимую панель.
        library.DetailRequested = t => { if (ShowLibraryList) ShowDetail(t); };
        Raise(nameof(Library));
    }

    // ---- Jump-to-file: поле над плейлистом, Enter — сыграть следующее совпадение ----

    private string _jumpQuery = "";
    public string JumpQuery { get => _jumpQuery; set => Set(ref _jumpQuery, value); }
    public string JumpWatermark => Loc.T("JumpWatermark");

    private void JumpToNextMatch()
    {
        if (string.IsNullOrWhiteSpace(JumpQuery) || Tracks.Count == 0) return;
        var start = SelectedIndex + 1;
        for (var offset = 0; offset < Tracks.Count; offset++)
        {
            var i = (start + offset) % Tracks.Count;
            if (Tracks[i].DisplayName.Contains(JumpQuery, StringComparison.OrdinalIgnoreCase))
            {
                _engine.PlayAt(i);
                return;
            }
        }
    }

    private async Task SavePlaylistAsync()
    {
        if (PlaylistSavePicker is null || Tracks.Count == 0) return;
        var path = await PlaylistSavePicker();
        if (path is null) return;
        var snapshot = Tracks;
        await Task.Run(() => PlaylistIO.SaveM3u8(path, snapshot));
        StatusText = Loc.T("PlaylistSaved", Path.GetFileName(path));
    }

    public string SavePlaylistText => Loc.T("SavePlaylist");
    public string ClearRatingText => Loc.T("Rate");

    /// <summary>Сон-таймер из трея: 0 — выключить.</summary>
    public void SetSleepTimer(int minutes)
    {
        if (minutes <= 0)
        {
            Sleep.Cancel();
            return;
        }
        // Таймер стреляет из пула потоков — движок трогаем только с UI-потока.
        Sleep.Start(TimeSpan.FromMinutes(minutes),
            () => Dispatcher.UIThread.Post(() => _engine.FadeOutAndStop(3000)));
    }

    // Локализованные подписи для XAML (язык применяется до создания окна).
    public string AppTitle => Loc.T("AppTitle");
    public string OpenFolderText => Loc.T("OpenFolder");
    public string EmptyHintText => Loc.T("EmptyHint");
    public string PlaylistTabText => Loc.T("PlaylistTab");
    public bool HasTracks => Tracks.Count > 0;

    public string TrackTitle { get => _trackTitle; private set => Set(ref _trackTitle, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }
    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) Raise(nameof(MiniSubtitle)); }
    }

    // Время по бокам прогресса (канон: «1:12 ——— 2:36», не одной строкой).
    private string _timePos = "0:00";
    private string _timeDur = "0:00";
    public string TimePosText { get => _timePos; private set => Set(ref _timePos, value); }
    public string TimeDurText { get => _timeDur; private set => Set(ref _timeDur, value); }

    /// <summary>Первая строка nowmini: название без «Артист — » (он на второй строке).</summary>
    public string MiniTitle => OverviewTitle;

    /// <summary>Вторая строка nowmini: исполнитель, а когда его нет — статус (скан/ошибки).</summary>
    public string MiniSubtitle
    {
        get
        {
            var artist = OverviewArtist;
            return string.IsNullOrEmpty(artist) ? StatusText : artist;
        }
    }
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayPauseGlyph)); } }
    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, value); }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!Set(ref _positionSeconds, value)) return;
            // Пока меняется трек, слайдер может коэрсить Value из-за смены Maximum —
            // это НЕ перемотка пользователя, движок не трогаем.
            if (_changingTrack) return;
            if (Math.Abs(_engine.PositionSeconds - value) > 0.3) // мелкая подгонка ползунком тоже перемотка
            {
                _engine.PositionSeconds = value;
                UpdateDiscord(); // перемотка сдвинула позицию — переякорим таймер Discord
            }
        }
    }

    public double Volume
    {
        get => _engine.Volume;
        set { _engine.Volume = value; _settings.Volume = value; Raise(); }
    }

    public int SelectedIndex { get => _selectedIndex; set => Set(ref _selectedIndex, value); }

    public void PlaySelected()
    {
        if (SelectedIndex >= 0)
            _engine.PlayAt(SelectedIndex);
    }

    /// <summary>
    /// Открывает набор путей (файлы/папки вперемешку) ОДНИМ плейлистом.
    /// Скан — в фоне (большая библиотека не должна вешать окно), применение — на UI-потоке.
    /// </summary>
    public async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var firstFolder = paths.FirstOrDefault(Directory.Exists);
            if (firstFolder is not null)
                _settings.LastFolder = firstFolder;

            StatusText = Loc.T("Scanning");
            var tracks = await Task.Run(() => FolderScanner.CollectFromPaths(paths));
            _engine.SetPlaylist(tracks);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Аргументы от второго экземпляра: поднять окно и открыть, что прислали.</summary>
    public void HandleExternalArgs(string[] args) =>
        Dispatcher.UIThread.Post(() =>
        {
            ActivateRequested?.Invoke();
            var paths = args
                .Where(a => a != SingleInstance.ActivateCommand && !string.IsNullOrWhiteSpace(a))
                .ToArray();
            if (paths.Length > 0)
                _ = OpenPathsAsync(paths);
        });

    private async Task PickFolderAsync()
    {
        if (FolderPicker is null) return;
        var folder = await FolderPicker();
        if (folder is not null)
            await OpenPathsAsync(new[] { folder });
    }

    private void OnTrackChanged(Track? track)
    {
        // Порядок важен: сначала обнуляем позицию, потом длительность — иначе слайдер
        // при уменьшении Maximum коэрсит старое Value и «доматывает» новый трек до конца.
        _changingTrack = true;
        try
        {
            _positionSeconds = 0;
            Raise(nameof(PositionSeconds));
            DurationSeconds = Math.Max(1, _engine.DurationSeconds);
        }
        finally
        {
            _changingTrack = false;
        }

        // Имя без подчёркиваний/сайт-мусора: тег, а без тега — чищенное имя файла.
        TrackTitle = track is null
            ? Loc.T("NoTrack")
            : track.Title ?? TagReader.CleanFileName(track.FilePath);
        SelectedIndex = _engine.CurrentIndex;
        StatusText = _engine.LastError ?? Loc.T("TracksCount", Tracks.Count);
        SyncPlayState();
        _ = UpdateCoverAsync(track);
        Library?.SetPlayingId(track?.ResumeKey); // зелёные полоски в списке
        ScrollToPlaying?.Invoke();               // список подматывается к играющему (шаффл/по порядку)
        _plugins.NotifyTrackChanged(BuildPluginTrack(track)); // M6: событие трека плагинам

        // M4: ЛЮБОЙ старт видео-трека (в т.ч. повторный клик по нему же) разворачивает Обзор;
        // аудио — просто убирает поверхность.
        var video = _engine.CurrentIsVideo;
        IsVideoActive = video;
        if (video && track is not null)
            Mode = UiMode.Overview;

        UpdateWaveform(track); // M3.4: пики трека фоном
        Raise(nameof(OverviewTitle));
        Raise(nameof(OverviewArtist));
        Raise(nameof(NowPlayingSubtitle));
        Raise(nameof(MiniSubtitle));
        Raise(nameof(MiniTitle));
        if (_mode == UiMode.Overview) RefreshQueue();
        // Титры перезагружаем независимо от режима: панель могла остаться открытой,
        // пока пользователь в списке — иначе по возвращении караоке едет по чужому тексту.
        if (LyricsOpen) _ = LoadLyricsAsync();
        UpdateDiscord(); // «слушает …» в профиле
        _trackStartedAt = DateTimeOffset.UtcNow;
        LastfmNowPlaying(track);
    }

    private void RefreshPlaylist()
    {
        Tracks = _engine.Tracks.ToList();
        Raise(nameof(Tracks));
        StatusText = Loc.T("TracksCount", Tracks.Count);
        Raise(nameof(HasTracks));
        if (_mode == UiMode.Overview) RefreshQueue();
    }

    private void Tick()
    {
        _engine.Poll(); // A-B петля, кроссфейд, резюме — всё на UI-потоке

        // Обновляем поле напрямую, чтобы сеттер не принял тик за перемотку пользователя.
        // Maximum слайдера — раньше Value: иначе при удлинении трека слайдер коэрсит
        // позицию по старому максимуму и двусторонняя привязка делает ложный seek.
        DurationSeconds = Math.Max(1, _engine.DurationSeconds);
        _positionSeconds = _engine.PositionSeconds;
        Raise(nameof(PositionSeconds));
        TimeText = $"{Fmt(_engine.PositionSeconds)} / {Fmt(_engine.DurationSeconds)}";
        TimePosText = Fmt(_engine.PositionSeconds);
        TimeDurText = Fmt(_engine.DurationSeconds);
        Raise(nameof(WaveProgress));
        SyncKaraoke();
        SyncPlayState();
    }

    private void SyncPlayState()
    {
        IsPlaying = _engine.State == PlaybackState.Playing;
        _plugins.NotifyPlayingChanged(IsPlaying); // M6: событие состояния плагинам
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    /// <summary>Размер окна на выход — зовётся и с Closing, и при ShutdownRequested (до Save).</summary>
    public void RememberWindowSize(double width, double height)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
    }

    public double InitialWidth => _settings.WindowWidth;
    public double InitialHeight => _settings.WindowHeight;

    public void Dispose()
    {
        _timer.Stop();
        Sleep.Dispose();
        Podcasts?.SavePlayingPosition(); // место в эпизоде не теряется при выходе
        _mediaServer.Dispose();
        _dlna.Dispose();
        _discord.Dispose();
        _lastfm.Dispose();
        _plugins.Dispose();
        _remote.Dispose();
        _store.Save(_settings);
        _engine.Dispose();
    }
}

/// <summary>Строка караоке-титров (.lrc): INPC-подсветка текущей без пересборки списка.</summary>
public sealed class KaraokeLine : System.ComponentModel.INotifyPropertyChanged
{
    public KaraokeLine(string text) => Text = string.IsNullOrWhiteSpace(text) ? "♪" : text;
    public string Text { get; }

    private bool _current;
    public bool Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Current)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
