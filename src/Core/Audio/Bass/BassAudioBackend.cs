using ManagedBass;
using ManagedBass.Fx;

namespace ZBS.Core.Audio.Bass;

/// <summary>
/// Реализация IAudioBackend на BASS (+bass_fx для скорости). Живёт только внутри Core.
/// Сегменты (CUE) реализованы стартовой позицией + one-shot POS-sync на границе.
/// </summary>
public sealed class BassAudioBackend : IAudioBackend
{
    private int _stream;
    private int _oldFadingStream; // хвост кроссфейда: глушится Pause/Stop и освобождается по таймеру
    private int _endSyncHandle;
    private double _segStart;
    private double? _segEnd;
    private double _volume = 1.0;
    private double _speed = 1.0;
    private double _gainDb;
    private bool _initialized;
    private bool _useFx = true; // выключается сам, если bass_fx не завёлся
    private int _fadeToken;     // отменяет хвост предыдущего фейда при быстрых кликах
    private readonly List<int> _plugins = new();
    // Делегат держим в поле: лямбду соберёт GC и нативный колбэк упадёт.
    private readonly SyncProcedure _endSync;

    public event Action? PlaybackEnded;

    /// <summary>
    /// Маршализатор на UI-поток для отложенных хвостов фейдов (ставит App).
    /// Без него Task.Delay-продолжения из пула гоняются с UI-потоком за _stream/_fadeToken:
    /// BASS переиспользует int-хэндлы, и хвост мог запаузить СЛЕДУЮЩИЙ трек.
    /// </summary>
    public Action<Action>? Dispatch { get; set; }

    private void OnUi(Action action)
    {
        var d = Dispatch;
        if (d is null) action();
        else d(action);
    }

    public string? LastError { get; private set; }

    public bool SpeedSupported => _useFx;

    public BassAudioBackend()
    {
        // Исключение, покинувшее нативный колбэк BASS, роняет процесс — глушим всегда.
        _endSync = (_, _, _, _) =>
        {
            try { PlaybackEnded?.Invoke(); }
            catch { /* подписчик обязан сам маршалить и обрабатывать */ }
        };
    }

    public bool Init(int deviceIndex = -1)
    {
        if (_initialized) return true;
        if (!ManagedBass.Bass.Init(deviceIndex))
        {
            LastError = $"Bass.Init: {ManagedBass.Bass.LastError}";
            return false;
        }
        _initialized = true;
        // Сетевые потоки (радио): некоторые Icecast-сервера режут незнакомые агенты,
        // а дефолтный таймаут коннекта у BASS слишком терпеливый для перебора станций.
        ManagedBass.Bass.NetAgent = "WinampMPEG/5.0";
        ManagedBass.Bass.Configure(ManagedBass.Configuration.NetTimeOut, 8000);
        LoadPlugins();
        return true;
    }

    private void LoadPlugins()
    {
        var dir = AppContext.BaseDirectory;
        string[] names = OperatingSystem.IsWindows()
            ? new[] { "bassflac.dll", "bassopus.dll", "bass_aac.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libbassflac.dylib", "libbassopus.dylib" }
                : new[] { "libbassflac.so", "libbassopus.so" };
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var h = ManagedBass.Bass.PluginLoad(path);
            if (h != 0) _plugins.Add(h);
        }
    }

    public bool Load(string filePath, double startSeconds = 0, double? endSeconds = null)
    {
        FreeStream();
        _gainDb = 0; // поправка принадлежит треку — новый трек начинает с нуля
        _stream = CreateStreamMaybeFx(filePath);
        if (_stream == 0) return false;
        SetupLoaded(_stream, startSeconds, endSeconds, _volume);
        return true;
    }

    public bool CrossfadeTo(string filePath, double startSeconds, double? endSeconds, double fadeSeconds, double gainDb = 0)
    {
        var next = CreateStreamMaybeFx(filePath);
        if (next == 0) return false;

        ++_fadeToken; // отменяем хвосты незавершённых FadePause/FadeOutAndStop старого стрима
        var old = _stream;
        // EQ уходящего трека живёт до конца фейда: иначе ApplyEq(next) снял бы полосы
        // со старого стрима и его тембр слышимо «схлопывался» в начале кроссфейда.
        _outgoingPeakEq = _peakEq;
        _peakEq = null;
        // Остаток сегмента старого трека (CUE): фейд не должен уехать за границу —
        // иначе начало следующего cue-трека звучит эхом из двух стримов.
        var fadeMs = Math.Max(50, (int)(fadeSeconds * 1000));
        if (old != 0)
        {
            if (_endSyncHandle != 0)
                ManagedBass.Bass.ChannelRemoveSync(old, _endSyncHandle); // конец старого больше не «окончание»
            if (_endSafetySync != 0)
                ManagedBass.Bass.ChannelRemoveSync(old, _endSafetySync); // и страховка сегмента тоже
            if (_segEnd.HasValue)
            {
                var posBytes = ManagedBass.Bass.ChannelGetPosition(old);
                var pos = posBytes < 0 ? 0 : ManagedBass.Bass.ChannelBytes2Seconds(old, posBytes);
                var remainMs = (int)Math.Max(50, (_segEnd.Value - pos) * 1000);
                fadeMs = Math.Min(fadeMs, remainMs);
            }
        }

        _gainDb = Math.Clamp(gainDb, -24, 24);
        SetupLoaded(next, startSeconds, endSeconds, volume: 0);
        ManagedBass.Bass.ChannelPlay(next);
        ManagedBass.Bass.ChannelSlideAttribute(next, ChannelAttribute.Volume, (float)EffectiveVolume(), fadeMs);

        if (old != 0)
        {
            FreeOldFading(); // если предыдущий хвост ещё жив — глушим сразу
            _oldFadingStream = old;
            _oldPeakEq = _outgoingPeakEq;
            _outgoingPeakEq = null;
            ManagedBass.Bass.ChannelSlideAttribute(old, ChannelAttribute.Volume, 0f, fadeMs);
            _ = Task.Delay(fadeMs + 150).ContinueWith(_ => OnUi(() =>
            {
                if (_oldFadingStream == old)
                {
                    _oldFadingStream = 0;
                    _oldPeakEq?.Dispose();
                    _oldPeakEq = null;
                    ManagedBass.Bass.StreamFree(old);
                }
            }));
        }
        return true;
    }

    /// <summary>Немедленно глушит и освобождает затухающий хвост кроссфейда (Pause/Stop/новый кроссфейд).</summary>
    private void FreeOldFading()
    {
        var old = _oldFadingStream;
        if (old == 0) return;
        _oldFadingStream = 0;
        _oldPeakEq?.Dispose();
        _oldPeakEq = null;
        ManagedBass.Bass.ChannelStop(old);
        ManagedBass.Bass.StreamFree(old);
    }

    /// <summary>Общий хвост загрузки: сегмент, громкость, sync конца. Пишет поля текущего стрима.</summary>
    private void SetupLoaded(int handle, double start, double? end, double volume)
    {
        _stream = handle;
        _segStart = Math.Max(0, start);
        _segEnd = end;
        if (_segStart > 0)
            ManagedBass.Bass.ChannelSetPosition(handle, ManagedBass.Bass.ChannelSeconds2Bytes(handle, _segStart));
        ManagedBass.Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, volume);
        ManagedBass.Bass.ChannelSetAttribute(handle, ChannelAttribute.Tempo, SpeedToTempo(_speed));
        ApplyEq(handle); // EQ живёт на стриме — новый трек получает те же полосы

        // Кривые cue (end <= start) играем без границы, как обычный файл.
        if (end.HasValue && end.Value <= _segStart + 0.05)
        {
            _segEnd = null;
            end = null;
        }
        _endSyncHandle = end.HasValue
            ? ManagedBass.Bass.ChannelSetSync(handle, SyncFlags.Position | SyncFlags.Onetime,
                ManagedBass.Bass.ChannelSeconds2Bytes(handle, end.Value), _endSync)
            : ManagedBass.Bass.ChannelSetSync(handle, SyncFlags.End | SyncFlags.Onetime, 0, _endSync);
        // Сегмент с end за пределами файла: POS-sync никогда не сработает — End-страховка.
        // Для валидного сегмента она безвредна: POS сработает раньше, стрим освободится при переходе.
        _endSafetySync = end.HasValue
            ? ManagedBass.Bass.ChannelSetSync(handle, SyncFlags.End | SyncFlags.Onetime, 0, _endSync)
            : 0;
    }

    private int _endSafetySync; // страховочный End-sync cue-сегмента: снимается при кроссфейде

    private int CreateStreamMaybeFx(string filePath)
    {
        LastError = null;
        if (_useFx)
        {
            var src = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode);
            if (src != 0)
            {
                var tempo = BassFx.TempoCreate(src, BassFlags.FxFreeSource);
                if (tempo != 0) return tempo;
                ManagedBass.Bass.StreamFree(src);
                _useFx = false; // bass_fx отсутствует/не завёлся — дальше живём без скорости
            }
            // src == 0 — файл не открылся вовсе; обычный CreateStream ниже даст ту же ошибку с текстом.
        }
        var plain = ManagedBass.Bass.CreateStream(filePath);
        if (plain == 0)
            LastError = $"CreateStream({Path.GetFileName(filePath)}): {ManagedBass.Bass.LastError}";
        return plain;
    }

    private static float SpeedToTempo(double speed) => (float)((speed - 1.0) * 100.0);

    public bool CanDecode(string filePath)
    {
        // Decode-стрим не занимает устройство и не мешает играющему каналу.
        var h = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode);
        if (h == 0) return false;
        ManagedBass.Bass.StreamFree(h);
        return true;
    }

    public void Play()
    {
        if (_stream != 0) ManagedBass.Bass.ChannelPlay(_stream);
    }

    public void Pause()
    {
        FreeOldFading();
        if (_stream != 0) ManagedBass.Bass.ChannelPause(_stream);
    }

    public void Stop()
    {
        FreeOldFading();
        if (_stream == 0) return;
        ManagedBass.Bass.ChannelStop(_stream);
        ManagedBass.Bass.ChannelSetPosition(_stream, ManagedBass.Bass.ChannelSeconds2Bytes(_stream, _segStart));
    }

    public void FadePause(int fadeMs)
    {
        FreeOldFading();
        if (_stream == 0) return;
        if (fadeMs <= 0) { Pause(); return; }
        var stream = _stream;
        var token = ++_fadeToken;
        ManagedBass.Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, 0f, fadeMs);
        _ = Task.Delay(fadeMs + 30).ContinueWith(_ => OnUi(() =>
        {
            if (token != _fadeToken || stream != _stream) return; // успели нажать что-то ещё
            ManagedBass.Bass.ChannelPause(stream);
            ManagedBass.Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, (float)EffectiveVolume());
        }));
    }

    public void FadeResume(int fadeMs)
    {
        if (_stream == 0) return;
        var token = ++_fadeToken;
        _ = token; // токен нужен только чтобы отменить хвост незавершённого FadePause
        if (fadeMs <= 0) { Play(); return; }
        ManagedBass.Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, 0f);
        ManagedBass.Bass.ChannelPlay(_stream);
        ManagedBass.Bass.ChannelSlideAttribute(_stream, ChannelAttribute.Volume, (float)EffectiveVolume(), fadeMs);
    }

    public void FadeOutAndStop(int fadeMs)
    {
        FreeOldFading();
        if (_stream == 0) return;
        var stream = _stream;
        var token = ++_fadeToken;
        ManagedBass.Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, 0f, Math.Max(50, fadeMs));
        _ = Task.Delay(fadeMs + 50).ContinueWith(_ => OnUi(() =>
        {
            if (token != _fadeToken || stream != _stream) return;
            ManagedBass.Bass.ChannelStop(stream);
            ManagedBass.Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, (float)EffectiveVolume());
        }));
    }

    public PlaybackState State
    {
        get
        {
            if (_stream == 0) return PlaybackState.Stopped;
            return ManagedBass.Bass.ChannelIsActive(_stream) switch
            {
                ManagedBass.PlaybackState.Playing or ManagedBass.PlaybackState.Stalled => PlaybackState.Playing,
                ManagedBass.PlaybackState.Paused => PlaybackState.Paused,
                _ => PlaybackState.Stopped,
            };
        }
    }

    public double DurationSeconds
    {
        get
        {
            if (_stream == 0) return 0;
            var fullBytes = ManagedBass.Bass.ChannelGetLength(_stream);
            if (fullBytes < 0) return 0;
            var full = ManagedBass.Bass.ChannelBytes2Seconds(_stream, fullBytes);
            return Math.Max(0, (_segEnd ?? full) - _segStart);
        }
    }

    public double PositionSeconds
    {
        get
        {
            if (_stream == 0) return 0;
            var bytes = ManagedBass.Bass.ChannelGetPosition(_stream);
            if (bytes < 0) return 0;
            return Math.Max(0, ManagedBass.Bass.ChannelBytes2Seconds(_stream, bytes) - _segStart);
        }
        set
        {
            if (_stream == 0) return;
            var absolute = _segStart + Math.Clamp(value, 0, Math.Max(0, DurationSeconds - 0.05));
            ManagedBass.Bass.ChannelSetPosition(_stream, ManagedBass.Bass.ChannelSeconds2Bytes(_stream, absolute));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    public double TrackGainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = Math.Clamp(value, -24, 24);
            ApplyVolume();
        }
    }

    /// <summary>
    /// Итоговая громкость = пользовательская × ReplayGain-поправка трека.
    /// Кламп сверху в 1.0 осознанный: положительный RG (+N дБ у тихих треков) жертвуем
    /// ради гарантии отсутствия клиппинга; выравнивание работает «вниз».
    /// </summary>
    private double EffectiveVolume() =>
        Math.Clamp(_volume * Math.Pow(10, _gainDb / 20.0), 0, 1);

    private void ApplyVolume()
    {
        if (_stream == 0) return;
        ManagedBass.Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, EffectiveVolume());
    }

    public double Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 0.5, 2.0);
            if (_useFx && _stream != 0)
                ManagedBass.Bass.ChannelSetAttribute(_stream, ChannelAttribute.Tempo, SpeedToTempo(_speed));
        }
    }

    // ---- Эквалайзер: BFX_PEAKEQ (bass_fx) на текущем стриме, переприменяется на каждом новом ----

    private static readonly double[] EqCenters = { 31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private float[]? _eqGains;
    private PeakEQ? _peakEq;         // EQ текущего стрима
    private PeakEQ? _outgoingPeakEq; // EQ уходящего в кроссфейд (переезжает в _oldPeakEq)
    private PeakEQ? _oldPeakEq;      // EQ затухающего хвоста — умирает вместе с ним

    public void SetEqualizer(float[]? gains)
    {
        _eqGains = gains is { Length: 10 } ? (float[])gains.Clone() : null;
        ApplyEq(_stream);
    }

    private void ApplyEq(int handle)
    {
        // Dispose снимает эффект со своего канала; мёртвый хэндл — безвредный false внутри.
        _peakEq?.Dispose();
        _peakEq = null;
        if (handle == 0 || _eqGains is null || !_useFx) return; // без bass_fx EQ недоступен

        var eq = new PeakEQ(handle, Q: 0, Bandwith: 1.0); // ~октава на полосу для 10-полосника
        for (var i = 0; i < EqCenters.Length; i++)
        {
            var band = eq.AddBand(EqCenters[i]);
            eq.UpdateBand(band, Math.Clamp(_eqGains[i], -15f, 15f));
        }
        _peakEq = eq;
    }

    public bool GetSpectrum(float[] buffer)
    {
        if (_stream == 0 || State != PlaybackState.Playing) return false;
        var flag = buffer.Length switch
        {
            512 => (int)DataFlags.FFT1024,
            1024 => (int)DataFlags.FFT2048,
            _ => (int)DataFlags.FFT2048,
        };
        return ManagedBass.Bass.ChannelGetData(_stream, buffer, flag) > 0;
    }

    private void FreeStream()
    {
        ++_fadeToken; // любой висящий фейд-хвост больше не должен трогать состояние
        FreeOldFading();
        StopRecording(); // запись эфира привязана к живому стриму
        IsRadio = false;
        if (_stream == 0) return;
        ManagedBass.Bass.StreamFree(_stream);
        _stream = 0;
        _endSyncHandle = 0;
        _endSafetySync = 0;
        _segStart = 0;
        _segEnd = null;
        _peakEq?.Dispose(); // эффект умер вместе со стримом
        _peakEq = null;
    }

    // ---- M5.1: интернет-радио (Shoutcast/Icecast) ----

    /// <summary>Текущий стрим — радио (живой URL-поток, без длительности/перемотки).</summary>
    public bool IsRadio { get; private set; }

    /// <summary>ICY-метаданные «сейчас в эфире» (из сетевого потока BASS — подписчик маршалит сам).</summary>
    public event Action<string>? RadioMetaChanged;

    // Делегаты держим в полях: GC не должен собрать колбэки, пока жив нативный стрим.
    private ManagedBass.DownloadProcedure? _downloadProc;
    private ManagedBass.SyncProcedure? _metaSync;
    private FileStream? _recFile;
    private readonly object _recLock = new();

    /// <summary>
    /// Сетевой коннект к станции (блокирует до NetTimeOut) — звать ИЗ ФОНА (Task.Run).
    /// Возвращает хэндл стрима или 0 (ошибка в LastError). Состояние бэкенда не трогает.
    /// </summary>
    public int PreconnectUrl(string url)
    {
        LastError = null;
        ManagedBass.Bass.Configure(ManagedBass.Configuration.NetPlaylist, 1);
        _downloadProc = OnDownload;
        var h = ManagedBass.Bass.CreateStream(url, 0, BassFlags.Default, _downloadProc, IntPtr.Zero);
        if (h == 0) LastError = $"Радио: {ManagedBass.Bass.LastError}";
        return h;
    }

    /// <summary>Принять предсозданный сетевой стрим (UI-поток): гасит текущий, настраивает радио.</summary>
    public void AdoptUrlStream(int handle)
    {
        FreeStream();
        _gainDb = 0;
        IsRadio = true;
        _stream = handle;
        _segStart = 0;
        _segEnd = null;
        ManagedBass.Bass.ChannelSetAttribute(handle, ChannelAttribute.Volume, (float)EffectiveVolume());
        ApplyEq(handle);
        // Смена трека в эфире + первое чтение (метаданные могли прийти до подписки).
        _metaSync = (_, _, _, _) => { try { EmitRadioMeta(); } catch { } };
        ManagedBass.Bass.ChannelSetSync(handle, SyncFlags.MetadataReceived, 0, _metaSync);
        // Поток оборвался (сеть упала) — сообщаем как конец «трека», движок покажет статус.
        _endSyncHandle = ManagedBass.Bass.ChannelSetSync(handle, SyncFlags.End | SyncFlags.Onetime, 0, _endSync);
        EmitRadioMeta();
    }

    /// <summary>Полное выключение радио: снять стрим (и запись/IsRadio вместе с ним).</summary>
    public void UnloadRadio() => FreeStream();

    private void EmitRadioMeta()
    {
        var title = ReadIcyTitle(_stream);
        if (!string.IsNullOrWhiteSpace(title))
            RadioMetaChanged?.Invoke(title!);
    }

    /// <summary>«StreamTitle='...';» из ICY-меты текущего стрима. null — меты нет.</summary>
    private static string? ReadIcyTitle(int handle)
    {
        if (handle == 0) return null;
        var ptr = ManagedBass.Bass.ChannelGetTags(handle, TagType.META);
        if (ptr == IntPtr.Zero) return null;
        var raw = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
        if (string.IsNullOrEmpty(raw)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(raw, "StreamTitle='(.*?)';");
        var title = m.Success ? m.Groups[1].Value : raw;
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    // Сырые байты сетевого потока (вызывается из сетевого потока BASS): пишем эфир как есть,
    // без перекодирования — файл в исходном качестве станции.
    private void OnDownload(IntPtr buffer, int length, IntPtr user)
    {
        if (buffer == IntPtr.Zero || length <= 0) return;
        lock (_recLock)
        {
            if (_recFile is null) return;
            try
            {
                var chunk = new byte[length];
                System.Runtime.InteropServices.Marshal.Copy(buffer, chunk, 0, length);
                _recFile.Write(chunk, 0, length);
            }
            catch
            {
                // Диск кончился/файл недоступен — запись тихо останавливается, эфир продолжает играть.
                try { _recFile?.Dispose(); } catch { }
                _recFile = null;
            }
        }
    }

    /// <summary>Расширение для файла записи по типу потока станции.</summary>
    public string RecordingExtension()
    {
        if (_stream == 0) return ".mp3";
        var info = ManagedBass.Bass.ChannelGetInfo(_stream);
        var name = info.ChannelType.ToString().ToLowerInvariant();
        if (name.Contains("ogg") || name.Contains("vorbis")) return ".ogg";
        if (name.Contains("aac")) return ".aac";
        if (name.Contains("opus")) return ".opus";
        return ".mp3";
    }

    /// <summary>Начать запись эфира в файл (сырые байты потока). false — не радио/файл не создался.</summary>
    public bool StartRecording(string path)
    {
        if (!IsRadio || _stream == 0) return false;
        lock (_recLock)
        {
            if (_recFile is not null) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _recFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Запись: {ex.Message}";
                _recFile = null;
                return false;
            }
        }
    }

    public void StopRecording()
    {
        lock (_recLock)
        {
            try { _recFile?.Dispose(); } catch { }
            _recFile = null;
        }
        if (_encHandle != 0)
        {
            try { BASS_Encode_Stop(_encHandle); } catch (Exception) { }
            _encHandle = 0;
        }
    }

    // ---- Запись в MP3 (bassenc + bassenc_mp3, встроенный LAME) ----

    private int _encHandle;

    [System.Runtime.InteropServices.DllImport("bassenc_mp3", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int BASS_Encode_MP3_StartFile(int handle, string? options, int flags, string filename);

    [System.Runtime.InteropServices.DllImport("bassenc")]
    private static extern bool BASS_Encode_Stop(int handle);

    private const int BassUnicode = unchecked((int)0x80000000);

    /// <summary>Есть ли mp3-энкодер рядом с приложением (Windows-фича, dll от un4seen).</summary>
    public static bool Mp3EncoderAvailable =>
        OperatingSystem.IsWindows()
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "bassenc_mp3.dll"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "bassenc.dll"));

    /// <summary>
    /// Запись эфира с перекодировкой в MP3 (192 кбит/с): энкодер вешается DSP-ом на играющий
    /// стрим и жуёт декодированный звук. false — не радио / энкодер не завёлся.
    /// </summary>
    public bool StartRecordingMp3(string path)
    {
        if (!IsRadio || _stream == 0 || !Mp3EncoderAvailable) return false;
        StopRecording();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _encHandle = BASS_Encode_MP3_StartFile(_stream, "-b 192", BassUnicode, path);
            if (_encHandle == 0)
            {
                LastError = $"MP3-запись: {ManagedBass.Bass.LastError}";
                return false;
            }
            return true;
        }
        catch (DllNotFoundException)
        {
            LastError = "MP3-запись: bassenc_mp3.dll не найдена";
            return false;
        }
    }

    public bool IsRecording
    {
        get
        {
            if (_encHandle != 0) return true;
            lock (_recLock) return _recFile is not null;
        }
    }

    // ---- M5: селектор устройства вывода ----

    /// <summary>Устройства вывода (без «No sound»): индекс BASS, имя, дефолтность.</summary>
    public IReadOnlyList<(int Index, string Name, bool IsDefault)> ListOutputDevices()
    {
        var list = new List<(int, string, bool)>();
        for (var i = 1; ManagedBass.Bass.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            list.Add((i, info.Name, info.IsDefault));
        }
        return list;
    }

    /// <summary>Переключить вывод на устройство (перетаскивает живые стримы без перезапуска трека).</summary>
    public bool SetOutputDevice(int deviceIndex)
    {
        // Init на новом устройстве (уже инициализировано — BASS вернёт Already, это ок).
        if (!ManagedBass.Bass.Init(deviceIndex) && ManagedBass.Bass.LastError != ManagedBass.Errors.Already)
        {
            LastError = $"Устройство: {ManagedBass.Bass.LastError}";
            return false;
        }
        var ok = true;
        if (_stream != 0) ok &= ManagedBass.Bass.ChannelSetDevice(_stream, deviceIndex);
        if (_oldFadingStream != 0) ManagedBass.Bass.ChannelSetDevice(_oldFadingStream, deviceIndex);
        ManagedBass.Bass.CurrentDevice = deviceIndex;
        if (!ok) LastError = $"Устройство: {ManagedBass.Bass.LastError}";
        return ok;
    }

    public void Dispose()
    {
        FreeStream();
        foreach (var p in _plugins) ManagedBass.Bass.PluginFree(p);
        _plugins.Clear();
        if (_initialized)
        {
            ManagedBass.Bass.Free();
            _initialized = false;
        }
    }
}
