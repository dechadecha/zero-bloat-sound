using ZBS.Core.Audio;
using ZBS.Core.Playlist;

namespace ZBS.Core.Playback;

/// <summary>
/// Фасад воспроизведения: плейлист, порядок (очередь/шаффл/повторы), кроссфейд,
/// A-B повтор, резюме длинных файлов, мягкая пауза. UI говорит только с ним.
/// Потоковая модель: все входные точки — с одного (UI) потока; конец трека из потока
/// BASS маршалится через <see cref="Dispatch"/>; Poll() зовётся UI-таймером.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly IAudioBackend _backend;
    private IAudioBackend _active; // текущий тракт: BASS (аудио) или mpv (видео)
    private double _volume = 1.0;
    private double _speed = 1.0;
    private readonly List<Track> _tracks = new();
    private readonly PlayOrder _order = new();
    private readonly ResumeStore? _resume;
    private int _currentIndex = -1;
    private int _pollCounter;
    private int _loadGeneration; // каждый Load/кроссфейд; устаревшие «конец трека» отбрасываются
    private bool _crossfadeFailedForTrack; // не ретраить кроссфейд каждые 400 мс на битом «следующем»
    private bool _suppressResumeSaveOnce;  // доигранный файл не должен воскресать в resume.json

    public event Action<Track?>? TrackChanged;
    public event Action? PlaylistChanged;

    /// <summary>Маршализатор на UI-поток (Dispatcher.UIThread.Post). Без него события синхронные.</summary>
    public Action<Action>? Dispatch { get; set; }

    /// <summary>Кроссфейд между треками, сек. 0 — выключен (обычный мгновенный переход).</summary>
    public double CrossfadeSeconds { get; set; }

    /// <summary>Мягкая пауза/старт, мс. 0 — выключена.</summary>
    public int SmoothPauseMs { get; set; } = 200;

    /// <summary>Резюме длинных файлов (аудиокниги/миксы). Тумблер.</summary>
    public bool ResumeLongFiles { get; set; } = true;

    /// <summary>ReplayGain-выравнивание громкости. Тумблер.</summary>
    public bool ReplayGainEnabled { get; set; } = true;

    /// <summary>Откуда брать RG-поправку трека в дБ (обычно — из медиатеки). null — поправки нет.</summary>
    public Func<string, double?>? GainProvider { get; set; }

    /// <summary>Трек «засчитан» (доигран или прослушан ≥50%) — для счётчиков медиатеки.</summary>
    public event Action<Track>? TrackPlayedEnough;

    private bool _playedMarked;
    private double _accumPlayed;      // фактически наслушано секунд (не позиция — перемотка не считается)
    private double _lastPollPos = -1; // -1 = ждём первый опрос после смены трека

    public double LoopAStart { get; private set; } = -1;
    public double LoopBEnd { get; private set; } = -1;
    public bool AbLoopActive => LoopAStart >= 0 && LoopBEnd > LoopAStart;

    public PlaybackEngine(IAudioBackend backend, ResumeStore? resume = null)
    {
        _backend = backend;
        _active = backend;
        _resume = resume;
        _backend.PlaybackEnded += OnPlaybackEnded;
    }

    // ---- M4: видео-тракт (libmpv). Аудио и видео живут в одном плейлисте ----

    /// <summary>Видео-бэкенд (если подключён) — тот же контракт, что и аудио.</summary>
    public IAudioBackend? VideoBackend { get; private set; }

    /// <summary>«Этот путь — видео?» (обычно VideoFiles.IsVideo).</summary>
    public Func<string, bool>? IsVideoFile { get; set; }

    /// <summary>Сейчас играет видео-тракт (UI показывает поверхность вместо обложки).</summary>
    public bool CurrentIsVideo => !ReferenceEquals(_active, _backend);

    /// <summary>true — переключились на видео, false — вернулись на аудио.</summary>
    public event Action<bool>? VideoActiveChanged;

    public void AttachVideoBackend(IAudioBackend video, Func<string, bool> isVideoFile)
    {
        VideoBackend = video;
        IsVideoFile = isVideoFile;
        video.PlaybackEnded += OnPlaybackEnded;
    }

    // ---- M5.1: радио (живой URL-поток через BASS) ----

    /// <summary>Аудио-бэкенд (BASS) — для фич, живущих ниже общего контракта (радио/устройства).</summary>
    public IAudioBackend Backend => _backend;

    /// <summary>Играет радио (не элемент плейлиста): без длительности, перемотки и авто-next.</summary>
    public bool CurrentIsRadio { get; private set; }

    /// <summary>Имя игрáющей станции (для UI, пока ICY не пришёл).</summary>
    public string? RadioStationName { get; private set; }

    /// <summary>«Сейчас в эфире» из ICY-меты (уже на UI-потоке через Dispatch).</summary>
    public event Action<string>? RadioMetaChanged;

    /// <summary>Поток радио оборвался (сеть) — UI показывает статус, автоперехода нет.</summary>
    public event Action? RadioStalled;

    /// <summary>
    /// Запустить станцию. Сетевой коннект — в фоне (мёртвая станция раньше морозила UI до 8 с);
    /// пока грузится, пользователь может успеть включить другое — тогда результат тихо выбрасывается.
    /// </summary>
    public async Task<bool> PlayRadioAsync(string name, string url)
    {
        MarkPlayedIfEnough();
        SaveResumeIfNeeded();
        SwitchActive(false); // радио всегда на BASS-тракте
        var generation = ++_loadGeneration;
        ClearAbLoop();
        if (_backend is not Audio.Bass.BassAudioBackend bass) return false;
        if (!_radioMetaHooked)
        {
            _radioMetaHooked = true;
            bass.RadioMetaChanged += title =>
            {
                if (Dispatch is { } d) d(() => RadioMetaChanged?.Invoke(title));
                else RadioMetaChanged?.Invoke(title);
            };
        }

        var handle = await Task.Run(() => bass.PreconnectUrl(url)).ConfigureAwait(true);
        if (generation != _loadGeneration)
        {
            // Пока коннектились, включили что-то другое — созданный стрим никому не нужен.
            if (handle != 0) ManagedBass.Bass.StreamFree(handle);
            return false;
        }
        var ok = handle != 0;
        if (ok)
        {
            bass.AdoptUrlStream(handle);
            _playedMarked = true; // радио не считается прослушиванием трека
            bass.Play();
        }
        CurrentIsRadio = ok;
        RadioStationName = ok ? name : null;
        TrackChanged?.Invoke(null); // UI перерисовывает транспорт под радио
        return ok;
    }

    /// <summary>Остановить радио: стрим освобождается целиком (иначе сеть продолжает качать,
    /// а «запись» после стопа писала бы пустоту).</summary>
    public void StopRadio()
    {
        if (!CurrentIsRadio) return;
        if (_backend is Audio.Bass.BassAudioBackend bass) bass.UnloadRadio();
        else _active.Stop();
        CurrentIsRadio = false;
        RadioStationName = null;
        TrackChanged?.Invoke(null);
    }

    private bool _radioMetaHooked;

    /// <summary>Запись эфира: mp3-перекодировка (если энкодер на месте) или сырые байты потока.</summary>
    public bool StartRadioRecording(string path, bool asMp3) =>
        _backend is Audio.Bass.BassAudioBackend b
        && (asMp3 ? b.StartRecordingMp3(path) : b.StartRecording(path));

    /// <summary>Доступна ли перекодировка записи в MP3.</summary>
    public static bool Mp3RecordingAvailable => Audio.Bass.BassAudioBackend.Mp3EncoderAvailable;

    public void StopRadioRecording()
    {
        if (_backend is Audio.Bass.BassAudioBackend b) b.StopRecording();
    }

    public bool IsRadioRecording => _backend is Audio.Bass.BassAudioBackend b && b.IsRecording;

    /// <summary>Расширение файла записи по кодеку станции (.mp3/.ogg/.aac/.opus).</summary>
    public string RadioRecordingExtension() =>
        _backend is Audio.Bass.BassAudioBackend b ? b.RecordingExtension() : ".mp3";

    private void SwitchActive(bool video)
    {
        var target = video && VideoBackend is not null ? VideoBackend : _backend;
        if (ReferenceEquals(target, _active)) return;
        _active.Stop(); // старый тракт замолкает: два звука разом не нужны
        _active = target;
        _active.Volume = _volume;
        _active.Speed = _speed;
        VideoActiveChanged?.Invoke(!ReferenceEquals(target, _backend));
    }

    public IReadOnlyList<Track> Tracks => _tracks;
    public Track? CurrentTrack => _currentIndex >= 0 && _currentIndex < _tracks.Count ? _tracks[_currentIndex] : null;
    public int CurrentIndex => _currentIndex;

    public PlaybackState State => _active.State;
    public double DurationSeconds => _active.DurationSeconds;
    public double PositionSeconds { get => _active.PositionSeconds; set => _active.PositionSeconds = value; }

    // Громкость/скорость кэшируются: при переключении тракта новый получает актуальные значения.
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            _backend.Volume = value;
            if (VideoBackend is { } v) v.Volume = value;
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            _speed = value;
            _backend.Speed = value;
            if (VideoBackend is { } v) v.Speed = value;
        }
    }

    public bool SpeedSupported => _active.SpeedSupported;
    public string? LastError => _active.LastError;

    /// <summary>Эквалайзер: 10 гейнов в дБ или null (выкл). Только аудио-тракт.</summary>
    public void SetEqualizer(float[]? gains) => _backend.SetEqualizer(gains);

    /// <summary>FFT-спектр для визуализатора. У видео спектра нет — тишина.</summary>
    public bool GetSpectrum(float[] buffer) => !CurrentIsVideo && _backend.GetSpectrum(buffer);

    /// <summary>Пики трека для waveform-сикбара (фоново, decode-стримом). Видео — null.</summary>
    public float[]? ComputeWaveform(string filePath, int buckets = 400, Func<bool>? isStale = null) =>
        IsVideoFile?.Invoke(filePath) == true ? null : Audio.Bass.BassWaveform.ComputePeaks(filePath, buckets, isStale);

    public ShuffleMode Shuffle
    {
        get => _order.Shuffle;
        set
        {
            var wasSmart = _order.Shuffle == ShuffleMode.Smart;
            _order.Shuffle = value;
            if (value == ShuffleMode.Smart && !wasSmart)
                _order.ActivateSmart(_currentIndex); // свежий мешок с текущего трека
            else
                _order.SyncTo(_currentIndex);
        }
    }

    public RepeatMode Repeat { get => _order.Repeat; set => _order.Repeat = value; }
    public int QueueLength => _order.QueueLength;

    /// <summary>Заменяет плейлист целиком и играет по текущему режиму порядка.</summary>
    public void SetPlaylist(IReadOnlyList<Track> tracks, bool autoplay = true)
    {
        _tracks.Clear();
        _tracks.AddRange(tracks);
        _currentIndex = -1;
        _order.Reset(_tracks.Count);
        ClearAbLoop();
        PlaylistChanged?.Invoke();
        if (autoplay && _tracks.Count > 0)
            TryPlayAt(_order.First());
    }

    /// <summary>«Сыграть следующим»: трек вклинивается, плейлист продолжает как шёл.</summary>
    public void Enqueue(int index) => _order.Enqueue(index);

    /// <summary>Дозагрузка в конец плейлиста (из медиатеки), без сброса очереди и шаффла.</summary>
    public void AppendTracks(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        _tracks.AddRange(tracks);
        _order.Extend(_tracks.Count);
        PlaylistChanged?.Invoke();
    }

    /// <summary>Явный выбор пользователя: играем ровно этот трек; при ошибке никуда не прыгаем.</summary>
    public void PlayAt(int index)
    {
        _order.SyncTo(index);
        TryPlayAt(index);
    }

    private bool TryPlayAt(int index)
    {
        if (index < 0 || index >= _tracks.Count) return false;
        MarkPlayedIfEnough();
        SaveResumeIfNeeded();
        _currentIndex = index;
        CurrentIsRadio = false; // возврат из радио в плейлист
        RadioStationName = null;
        ClearAbLoop();
        var track = _tracks[index];
        SwitchActive(IsVideoFile?.Invoke(track.FilePath) == true); // видео → mpv, аудио → BASS
        ++_loadGeneration; // конец ПРЕДЫДУЩЕГО трека, застрявший в диспетчере, больше не считается
        var loaded = _active.Load(track.FilePath, track.StartSeconds, track.EndSeconds);
        if (loaded)
        {
            _active.TrackGainDb = GainFor(track);
            ResetPlayCounters();
            _crossfadeFailedForTrack = false;
            RestoreResumeIfAny(track);
            _active.Play();
        }
        else if (CurrentIsVideo)
        {
            SwitchActive(false); // видео не загрузилось — не оставляем UI на пустой чёрной поверхности
        }
        TrackChanged?.Invoke(track);
        return loaded;
    }

    /// <summary>Применить поправку громкости к уже игрáющему треку (дообсчитанный RMS-анализ).</summary>
    public void ApplyGainNow(double gainDb) => _active.TrackGainDb = gainDb;

    private double GainFor(Track track) =>
        ReplayGainEnabled ? GainProvider?.Invoke(track.FilePath) ?? 0 : 0;

    /// <summary>Трек доиграл до конца сам (не пропущен) — уже на UI-потоке.</summary>
    public event Action<Track>? TrackEndedNaturally;

    /// <summary>
    /// Засчитываем прослушивание по правилу Last.fm: фактически наслушано ≥50% ИЛИ ≥4 минут
    /// (что раньше). Считаем реально проигранное время (см. Poll), а не позицию — иначе
    /// перемотка в конец и Next засчитывали бы «полное» прослушивание за секунду.
    /// </summary>
    private void MarkPlayedIfEnough()
    {
        if (_playedMarked || CurrentTrack is not { } track) return;
        var duration = _active.DurationSeconds;
        if (duration > 0 && (_accumPlayed >= duration * 0.5 || _accumPlayed >= 240))
        {
            _playedMarked = true;
            TrackPlayedEnough?.Invoke(track);
        }
    }

    /// <summary>Сброс счётчиков прослушивания при смене трека.</summary>
    private void ResetPlayCounters()
    {
        _playedMarked = false;
        _accumPlayed = 0;
        _lastPollPos = -1;
    }

    public void PlayPause()
    {
        switch (_active.State)
        {
            case PlaybackState.Playing:
                _active.FadePause(SmoothPauseMs);
                break;
            case PlaybackState.Paused:
                _active.FadeResume(SmoothPauseMs);
                break;
            case PlaybackState.Stopped when _tracks.Count > 0:
                TryPlayAt(_currentIndex < 0 ? _order.First() : _currentIndex);
                break;
        }
    }

    public void Stop()
    {
        // Стоп на радио = полное выключение станции (иначе сеть качает дальше,
        // а UI-флаги записи/радио остаются висеть).
        if (CurrentIsRadio) { StopRadio(); return; }
        SaveResumeIfNeeded();
        _active.Stop();
    }

    public void Next() => AdvanceFrom(_currentIndex, manual: true);

    public void Previous()
    {
        // Классика плееров: дальше 3 секунд — в начало трека, иначе — предыдущий.
        if (_active.PositionSeconds > 3 || _currentIndex <= 0)
            _active.PositionSeconds = 0;
        else
            PlayAt(_currentIndex - 1);
    }

    /// <summary>A-B: первый вызов ставит точку A, второй — B и включает петлю, третий — сбрасывает.</summary>
    public void AbLoopToggle()
    {
        if (AbLoopActive) { ClearAbLoop(); return; }
        if (LoopAStart < 0) { LoopAStart = _active.PositionSeconds; return; }
        var b = _active.PositionSeconds;
        if (b > LoopAStart + 0.5) LoopBEnd = b;
        else ClearAbLoop();
    }

    public void ClearAbLoop()
    {
        LoopAStart = -1;
        LoopBEnd = -1;
    }

    /// <summary>Сон-таймер просит погаснуть: плавное затухание и стоп.</summary>
    public void FadeOutAndStop(int fadeMs)
    {
        SaveResumeIfNeeded();
        _active.FadeOutAndStop(fadeMs);
    }

    /// <summary>
    /// Пульс от UI-таймера (~400 мс): A-B петля, окно кроссфейда, периодика резюме.
    /// Всё на UI-потоке — гонок нет.
    /// </summary>
    public void Poll()
    {
        if (_active.State != PlaybackState.Playing) return;

        // Копим фактически наслушанное: только нормальное продвижение (маленький шаг вперёд).
        // Перемотка/резюме-прыжок дают большой скачок — не засчитываем, просто пересинхроним.
        var pos = _active.PositionSeconds;
        if (_lastPollPos >= 0)
        {
            var delta = pos - _lastPollPos;
            if (delta > 0 && delta < 2) _accumPlayed += delta;
        }
        _lastPollPos = pos;
        MarkPlayedIfEnough();

        if (AbLoopActive && _active.PositionSeconds >= LoopBEnd)
            _active.PositionSeconds = LoopAStart;

        if (CrossfadeSeconds > 0 && !AbLoopActive)
            TryStartCrossfade();

        if (++_pollCounter % 12 == 0) // ~каждые 5 секунд
            SaveResumeIfNeeded(persist: true);
    }

    private void TryStartCrossfade()
    {
        if (_crossfadeFailedForTrack) return; // уже пробовали в этом треке — не выгребаем очередь ретраями
        if (CurrentIsVideo) return;           // видео не кроссфейдим — стык обычным переходом
        var duration = _active.DurationSeconds;
        var remaining = duration - _active.PositionSeconds;
        if (duration < CrossfadeSeconds * 2 || remaining > CrossfadeSeconds) return;

        var next = _order.NextAfter(_currentIndex, manual: false);
        if (next < 0 || next >= _tracks.Count)
        {
            _crossfadeFailedForTrack = true; // порядок исчерпан — дождёмся естественного конца
            return;
        }

        var track = _tracks[next];
        if (VideoBackend is not null && IsVideoFile?.Invoke(track.FilePath) == true)
        {
            // Следующий — видео: кроссфейд между трактами невозможен, дождёмся конца.
            _order.PushFront(next);
            _crossfadeFailedForTrack = true;
            return;
        }
        if (ResumeLongFiles && _resume is not null && _resume.TryGet(track.ResumeKey, out _))
        {
            // У следующего есть сохранённая позиция (аудиокнига/микс): кроссфейд стартовал бы
            // его с нуля и затёр бы резюме. Обычный переход через TryPlayAt позицию восстановит.
            _order.PushFront(next);
            _crossfadeFailedForTrack = true;
            return;
        }
        MarkPlayedIfEnough();
        if (_active.CrossfadeTo(track.FilePath, track.StartSeconds, track.EndSeconds, CrossfadeSeconds, GainFor(track)))
        {
            // Чистим резюме уходящего ТОЛЬКО при удавшемся переходе — иначе, упади кроссфейд,
            // позиция ещё играющего длинного файла оказалась бы стёрта.
            if (CurrentTrack is { } leaving)
                _resume?.Clear(leaving.ResumeKey);
            _currentIndex = next;
            ++_loadGeneration; // старый конец больше не «окончание»
            ResetPlayCounters();
            _crossfadeFailedForTrack = false;
            ClearAbLoop();
            TrackChanged?.Invoke(track);
        }
        else
        {
            _order.PushFront(next);          // потреблённый элемент возвращается в очередь
            _crossfadeFailedForTrack = true; // и до конца трека больше не дёргаемся
        }
    }

    private void OnPlaybackEnded()
    {
        // Из потока BASS/mpv — уводим на UI-поток. Поколение фиксируем в момент события:
        // если пользователь успел запустить другой трек (особенно асинхронный mpv-Load,
        // у которого State/Duration ещё «пустые»), устаревший конец не должен листать плейлист.
        var generation = _loadGeneration;
        if (Dispatch is { } d)
            d(() => HandleTrackEnded(generation));
        else
            HandleTrackEnded(generation);
    }

    private void HandleTrackEnded(int generation)
    {
        if (generation != _loadGeneration) return; // конец чужого (уже заменённого) трека
        // Радио «закончиться» не может — конец потока означает обрыв сети.
        if (CurrentIsRadio)
        {
            RadioStalled?.Invoke();
            return;
        }
        // Кроссфейд уже перевёл нас на следующий трек, а это — запоздавший sync старого:
        // новый стрим играет и до его конца далеко. Второй переход был бы пропуском трека.
        if (_active.State == PlaybackState.Playing &&
            _active.DurationSeconds - _active.PositionSeconds > Math.Max(1.0, CrossfadeSeconds))
            return;

        // A-B петля с точкой B у самого конца: sync конца опережает Poll — держим петлю.
        if (AbLoopActive)
        {
            var a = LoopAStart;
            var b = LoopBEnd;
            if (TryPlayAt(_currentIndex))
            {
                LoopAStart = a;
                LoopBEnd = b;
                _active.PositionSeconds = a;
            }
            return;
        }

        // Трек доиграл сам до конца — считаем услышанным всё до финальной позиции (в реальной
        // игре Poll набрал бы это сам; на естественном конце страхуемся от недобора счётчика).
        _accumPlayed = Math.Max(_accumPlayed, _active.PositionSeconds);
        MarkPlayedIfEnough();
        if (CurrentTrack is { } finished)
        {
            _resume?.Clear(finished.ResumeKey); // доиграл — резюме не нужно
            _suppressResumeSaveOnce = true;     // и SaveResume при переходе не должен его воскресить
            TrackEndedNaturally?.Invoke(finished); // подкасты помечают эпизод «прослушан»
        }
        AdvanceFrom(_currentIndex, manual: false);
    }

    /// <summary>Переход по порядку с пропуском битых файлов (не больше одного круга).</summary>
    private void AdvanceFrom(int current, bool manual)
    {
        var attempts = 0;
        var index = current;
        while (attempts++ <= _tracks.Count)
        {
            index = _order.NextAfter(index, manual);
            if (index < 0) break;
            if (TryPlayAt(index)) return;
        }
        // Играть больше нечего. Для CUE-сегмента это важно: его «конец» — только sync,
        // физически стрим продолжает играть файл дальше — глушим явно.
        if (!manual)
            _active.Stop();
    }

    private void RestoreResumeIfAny(Track track)
    {
        if (!ResumeLongFiles || _resume is null) return;
        if (_active.DurationSeconds < ResumeStore.MinDurationSeconds) return;
        if (_resume.TryGet(track.ResumeKey, out var seconds) && seconds < _active.DurationSeconds - 30)
            _active.PositionSeconds = seconds;
    }

    private void SaveResumeIfNeeded(bool persist = false)
    {
        if (!ResumeLongFiles || _resume is null) return;
        if (_suppressResumeSaveOnce)
        {
            _suppressResumeSaveOnce = false; // только что доигранный файл не пересохраняем
        }
        else if (CurrentTrack is { } track && _active.DurationSeconds >= ResumeStore.MinDurationSeconds)
        {
            _resume.Set(track.ResumeKey, _active.PositionSeconds);
        }
        if (persist)
            _resume.SaveIfDirty();
    }

    public void Dispose()
    {
        SaveResumeIfNeeded(persist: true);
        _backend.PlaybackEnded -= OnPlaybackEnded;
        _backend.Dispose();
        if (VideoBackend is { } video)
        {
            video.PlaybackEnded -= OnPlaybackEnded;
            video.Dispose();
        }
    }
}
