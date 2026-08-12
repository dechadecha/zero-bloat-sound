using ZBS.Core.Playback;
using ZBS.Core.Radio;
using ZBS.Core.Settings;

namespace ZBS.UI.Desktop.ViewModels;

/// <summary>Строка списка станций (интернет-режим).</summary>
public sealed class StationRow : ViewModelBase
{
    public StationRow(RadioStation s, bool favorite) { S = s; _favorite = favorite; }
    public RadioStation S { get; }

    private bool _favorite;
    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }
    public string Star => _favorite ? "★" : "☆";

    private bool _playing;
    public bool Playing { get => _playing; set => Set(ref _playing, value); }
}

/// <summary>
/// Экран «Радио»: тумблер режимов — интернет-каталог (radio-browser + избранное)
/// и «частотное» радио (FM-шкала с тюнером, те же эфирные станции через их потоки).
/// </summary>
public sealed class RadioViewModel : ViewModelBase
{
    private readonly PlaybackEngine _engine;
    private readonly AppSettings _settings;
    private readonly RadioFavorites _favorites;
    private readonly RadioBrowserClient _client = new();
    private CancellationTokenSource? _searchCts;

    public RadioViewModel(PlaybackEngine engine, AppSettings settings, string settingsDir)
    {
        _engine = engine;
        _settings = settings;
        _favorites = new RadioFavorites(settingsDir);
        _fmMhz = Math.Clamp(settings.FmTuner, FmStations.Min, FmStations.Max);
        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        NetModeCommand = new RelayCommand(() => FmMode = false);
        FmModeCommand = new RelayCommand(() => FmMode = true);
        FavoritesCommand = new RelayCommand(ShowFavorites);
        CheckStationsCommand = new RelayCommand(() => _ = CheckStationsAsync());
        PlaySelectedCommand = new RelayCommand(PlaySelected);
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite);
        StopCommand = new RelayCommand(StopRadio);
        RecCommand = new RelayCommand(ToggleRec);
        ShowFavorites();
        _engine.RadioMetaChanged += t => { NowPlayingTitle = t; };
        _engine.RadioStalled += () =>
        {
            Status = "Поток оборвался — проверь сеть и нажми станцию ещё раз";
            _playingUrl = null; // повторный тап той же станции должен переподключить
        };
        // Радио выключили извне (стоп с транспорта/пульта, запуск трека) — синхронизируем флаги,
        // иначе «Запись» и подсветка станции остаются висеть.
        _engine.TrackChanged += _ =>
        {
            if (_engine.CurrentIsRadio || _playingUrl is null) return;
            _playingUrl = null;
            foreach (var row in Stations) row.Playing = false;
            NowPlayingStation = "";
            NowPlayingTitle = "";
            if (Recording) { Recording = false; Status = "Запись остановлена (эфир выключен)"; }
            Raise(nameof(IsPlayingRadio));
        };
    }

    // ---- Режим: интернет / частотное ----

    public bool FmMode
    {
        get => _settings.RadioMode == "fm";
        set
        {
            _settings.RadioMode = value ? "fm" : "net";
            Raise();
            Raise(nameof(NetMode));
            // Смена режима всегда отменяет досыпающий тюнер (иначе он перебивал бы
            // интернет-станцию, выбранную сразу после ухода из FM).
            _fmTuneToken++;
            // Перешёл на «Частотное» — сразу ловим станцию на установленной частоте.
            if (value) _ = TuneAfterDelay(_fmTuneToken);
        }
    }

    public bool NetMode => !FmMode;

    // ---- Интернет-каталог ----

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    // ---- Фильтры каталога (страна + минимальный битрейт «для аудиофилов») ----

    public List<string> Countries { get; } = new()
    {
        "Страна: все", "Россия", "Украина", "Беларусь", "Казахстан",
        "Германия", "США", "Великобритания", "Франция", "Япония",
    };

    private static readonly string[] CountryCodes = { "", "RU", "UA", "BY", "KZ", "DE", "US", "GB", "FR", "JP" };

    private int _countryIndex;
    public int CountryIndex
    {
        get => _countryIndex;
        set { if (Set(ref _countryIndex, value)) _ = SearchAsync(); }
    }

    public List<string> Bitrates { get; } = new()
    {
        "Битрейт: любой", "от 128 кбит/с", "от 192 кбит/с", "от 256 кбит/с", "320 кбит/с",
    };

    private static readonly int[] BitrateMins = { 0, 128, 192, 256, 320 };

    private int _bitrateIndex;
    public int BitrateIndex
    {
        get => _bitrateIndex;
        set { if (Set(ref _bitrateIndex, value)) _ = SearchAsync(); }
    }

    /// <summary>«Обновить»: прозвон потоков в текущем списке, протухшие выкидываются.</summary>
    public RelayCommand CheckStationsCommand { get; private set; } = null!;

    private bool _checking;
    private int _stationsGen; // растёт при каждой подмене Stations: устаревший прозвон не затирает свежий список

    private async Task CheckStationsAsync()
    {
        if (_checking || Stations.Count == 0) return;
        _checking = true;
        var generation = _stationsGen;
        Status = "Прозваниваю потоки…";
        try
        {
            var rows = Stations.ToList();
            var alive = new bool[rows.Count];
            var sem = new SemaphoreSlim(8);
            await Task.WhenAll(rows.Select(async (row, i) =>
            {
                await sem.WaitAsync();
                try { alive[i] = await _client.IsAliveAsync(row.S.Url, CancellationToken.None); }
                finally { sem.Release(); }
            }));
            if (generation != _stationsGen) return; // пока звонили — список сменился (поиск/фильтр)
            var kept = rows.Where((_, i) => alive[i]).ToList();
            var dropped = rows.Count - kept.Count;
            SetStations(kept);
            Status = dropped == 0 ? "Все потоки живые" : $"Убрано протухших: {dropped}";
        }
        finally
        {
            _checking = false;
        }
    }

    private void SetStations(List<StationRow> rows)
    {
        _stationsGen++;
        Stations = rows;
        Raise(nameof(Stations));
    }

    public List<StationRow> Stations { get; private set; } = new();
    public RelayCommand SearchCommand { get; }
    public RelayCommand NetModeCommand { get; }
    public RelayCommand FmModeCommand { get; }
    public RelayCommand FavoritesCommand { get; }
    public RelayCommand PlaySelectedCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RecCommand { get; }

    private int _selectedIndex = -1;
    public int SelectedIndex { get => _selectedIndex; set => Set(ref _selectedIndex, value); }

    private string? _playingUrl;

    private void ShowFavorites()
    {
        if (_favorites.Items.Count == 0)
        {
            Status = "Избранного пока нет — найди станцию и нажми ☆";
            _ = SearchAsync(); // первым делом показываем топ каталога
            return;
        }
        SetStations(_favorites.Items.Select(s => Row(s)).ToList());
        Status = "Избранное";
    }

    private StationRow Row(RadioStation s) =>
        new(s, _favorites.Contains(s)) { Playing = s.Url == _playingUrl };

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Status = "Ищу станции…";
        try
        {
            var found = await _client.SearchAsync(Query, CountryCodes[_countryIndex], BitrateMins[_bitrateIndex], 60, cts.Token);
            if (cts.IsCancellationRequested) return;
            SetStations(found.Select(Row).ToList());
            Status = found.Count == 0
                ? "Ничего не нашлось"
                : string.IsNullOrWhiteSpace(Query) ? "Топ станций каталога" : $"Найдено: {found.Count}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Каталог недоступен: {ex.Message}";
        }
    }

    private void PlaySelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Stations.Count) return;
        Play(Stations[SelectedIndex].S.Name, Stations[SelectedIndex].S.Url);
    }

    private void Play(string name, string url)
    {
        _fmTuneToken++; // ручной выбор станции отменяет досыпающий FM-тюнер
        _ = PlayAsync(name, url);
    }

    private async Task PlayAsync(string name, string url)
    {
        StopRec();
        Status = $"Подключаюсь: {name}…";
        var ok = await _engine.PlayRadioAsync(name, url);
        _playingUrl = ok ? url : null;
        foreach (var row in Stations) row.Playing = row.S.Url == _playingUrl;
        NowPlayingStation = ok ? name : "";
        NowPlayingTitle = "";
        Status = ok ? $"В эфире: {name}" : $"Не удалось подключиться: {_engine.LastError}";
        Raise(nameof(IsPlayingRadio));
    }

    private void ToggleFavorite()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Stations.Count) return;
        var row = Stations[SelectedIndex];
        _favorites.Toggle(row.S);
        row.Favorite = _favorites.Contains(row.S);
        var i = SelectedIndex;
        SetStations(Stations.ToList()); // Star — computed, пересобираем привязку
        SelectedIndex = i;
    }

    private void StopRadio()
    {
        StopRec();
        _engine.StopRadio();
        _playingUrl = null;
        foreach (var row in Stations) row.Playing = false;
        NowPlayingStation = "";
        NowPlayingTitle = "";
        Raise(nameof(IsPlayingRadio));
    }

    // ---- Сейчас в эфире ----

    private string _nowStation = "";
    public string NowPlayingStation { get => _nowStation; private set => Set(ref _nowStation, value); }

    private string _nowTitle = "";
    public string NowPlayingTitle { get => _nowTitle; set => Set(ref _nowTitle, value); }

    public bool IsPlayingRadio => _engine.CurrentIsRadio;

    // ---- Запись эфира ----

    private bool _recording;
    public bool Recording { get => _recording; private set { if (Set(ref _recording, value)) Raise(nameof(RecText)); } }
    public string RecText => Recording ? "■ Остановить запись" : "● Записать эфир";

    private void ToggleRec()
    {
        if (Recording) { StopRec(); return; }
        if (!_engine.CurrentIsRadio) { Status = "Сначала включи станцию"; return; }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ZBS Записи");
        var station = string.IsNullOrWhiteSpace(NowPlayingStation) ? "Радио" : NowPlayingStation;
        foreach (var c in Path.GetInvalidFileNameChars()) station = station.Replace(c, '_');
        // MP3-перекодировка (192 кбит/с) — если включена и энкодер на месте; иначе сырой поток.
        var mp3 = _settings.RecordMp3 && PlaybackEngine.Mp3RecordingAvailable;
        var ext = mp3 ? ".mp3" : _engine.RadioRecordingExtension();
        // Секунды в имени: рестарт записи в ту же минуту не должен затирать предыдущий файл.
        var path = Path.Combine(dir, $"{station}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ext}");
        if (_engine.StartRadioRecording(path, mp3))
        {
            Recording = true;
            Status = $"Пишу эфир → {path}";
        }
        else
        {
            Status = $"Запись не началась: {_engine.LastError}";
        }
    }

    private void StopRec()
    {
        if (!Recording) return;
        _engine.StopRadioRecording();
        Recording = false;
        Status = "Запись сохранена (Музыка → ZBS Записи)";
    }

    // ---- Частотный режим (FM-шкала) ----

    private double _fmMhz;
    public double FmMhz
    {
        get => _fmMhz;
        set
        {
            if (!Set(ref _fmMhz, Math.Clamp(value, FmStations.Min, FmStations.Max))) return;
            _settings.FmTuner = _fmMhz;
            Raise(nameof(FmFreqText));
            Raise(nameof(FmStationName));
            // Дебаунс: пока крутишь ручку — не дёргаем сеть на каждой отметке.
            _fmTuneToken++;
            _ = TuneAfterDelay(_fmTuneToken);
        }
    }

    private int _fmTuneToken;

    private async Task TuneAfterDelay(int token)
    {
        await Task.Delay(450);
        if (token != _fmTuneToken || !FmMode) return;
        var station = FmStations.Nearest(_fmMhz);
        if (station is null)
        {
            if (_engine.CurrentIsRadio) { _engine.StopRadio(); _playingUrl = null; }
            Status = "…шипение между станциями…";
            NowPlayingStation = "";
            Raise(nameof(IsPlayingRadio));
            return;
        }
        // «Уже на этой волне» — только если поток реально играет: после обрыва/стопа переподключаемся.
        if (_playingUrl == station.Url && _engine.CurrentIsRadio
            && _engine.State == ZBS.Core.Audio.PlaybackState.Playing) return;
        _ = PlayAsync($"{station.Mhz:0.0} FM · {station.Name}", station.Url);
    }

    public string FmFreqText => $"{_fmMhz:0.0}";
    public string FmStationName => FmStations.Nearest(_fmMhz)?.Name ?? "— — —";

    public double FmMin => FmStations.Min;
    public double FmMax => FmStations.Max;

    /// <summary>Частоты станций — точки на рисованной шкале.</summary>
    public IReadOnlyList<double> FmStationFreqs { get; } = FmStations.All.Select(s => s.Mhz).ToList();

    // ---- Для веб-пульта: выбор станции с телефона ----

    public IReadOnlyList<RadioStation> FavoriteStations => _favorites.Items;

    public string? PlayingUrl => _playingUrl;

    /// <summary>Включить станцию из избранного (команда пульта).</summary>
    public void PlayFavorite(int index)
    {
        if (index < 0 || index >= _favorites.Items.Count) return;
        var s = _favorites.Items[index];
        Play(s.Name, s.Url);
    }

    /// <summary>Включить FM-станцию по индексу вшитого списка (команда пульта).</summary>
    public void PlayFmStation(int index)
    {
        if (index < 0 || index >= FmStations.All.Count) return;
        var s = FmStations.All[index];
        // Не через сеттер FmMhz: при равной частоте он выходит молча, а после обрыва
        // повторный тап той же станции обязан переподключить.
        _fmMhz = s.Mhz;
        _settings.FmTuner = s.Mhz;
        Raise(nameof(FmMhz));
        Raise(nameof(FmFreqText));
        Raise(nameof(FmStationName));
        _fmTuneToken++;
        Play($"{s.Mhz:0.0} FM · {s.Name}", s.Url);
    }
}
