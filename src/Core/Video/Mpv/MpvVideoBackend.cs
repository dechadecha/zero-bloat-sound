using System.Runtime.InteropServices;
using ZBS.Core.Audio;

namespace ZBS.Core.Video.Mpv;

/// <summary>
/// Видео-бэкенд на libmpv (M4). Реализует тот же IAudioBackend — PlaybackEngine
/// переключается между BASS (аудио) и mpv (видео) прозрачно для UI.
/// Рендер — в чужое окно (wid), которое отдаёт вьюха ДО первой загрузки;
/// пока окна нет, Load буферизуется и стартует при появлении поверхности.
/// Без libmpv-2.dll класс просто не создаётся (IsAvailable = false) — плеер живёт как раньше.
/// </summary>
public sealed class MpvVideoBackend : IAudioBackend
{
    private const string Lib = "libmpv-2";

    // --- P/Invoke (минимум клиентского API mpv) ---
    [DllImport(Lib)] private static extern IntPtr mpv_create();
    [DllImport(Lib)] private static extern int mpv_initialize(IntPtr handle);
    [DllImport(Lib)] private static extern void mpv_terminate_destroy(IntPtr handle);
    [DllImport(Lib)] private static extern int mpv_set_option_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] private static extern int mpv_set_property_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] private static extern int mpv_get_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);
    [DllImport(Lib)] private static extern int mpv_set_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref double data);
    [DllImport(Lib)] private static extern int mpv_command(IntPtr handle, IntPtr args);
    [DllImport(Lib)] private static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);
    [DllImport(Lib)] private static extern void mpv_wakeup(IntPtr handle);

    private const int FormatDouble = 5; // MPV_FORMAT_DOUBLE
    private const int EventShutdown = 1;
    private const int EventEndFile = 7;
    private const int EndReasonEof = 0;
    private const int EndReasonError = 4;

    public static bool IsAvailable =>
        OperatingSystem.IsWindows() &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"));

    private IntPtr _handle;
    private IntPtr _wid;
    private Thread? _eventThread;
    private volatile bool _running;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private double _volume = 1.0;
    private double _speed = 1.0;
    private (string Path, double Start, double? End)? _pendingLoad;
    private bool _pendingPlay;

    public event Action? PlaybackEnded;
    public string? LastError { get; private set; }
    public bool SpeedSupported => true;
    public double TrackGainDb { get; set; } // v1: видео без ReplayGain

    public bool Init(int deviceIndex = -1) => true; // реальная инициализация — при первом Load с окном

    /// <summary>Окно для рендера (HWND дочернего хоста). Зовёт вьюха; wid в mpv — init-only.</summary>
    public void AttachWindow(IntPtr wid)
    {
        _wid = wid;
        if (_handle == IntPtr.Zero && _pendingLoad is not null)
            SignalIfStartFailed(StartPending());
    }

    /// <summary>Отложенный старт провалился — нельзя молча висеть «на паузе»: сигналим концом, движок перейдёт дальше.
    /// «Поверхности ещё нет» — не провал: AttachWindow доиграет позже.</summary>
    private void SignalIfStartFailed(bool ok)
    {
        if (ok || _wid == IntPtr.Zero) return;
        _state = PlaybackState.Stopped;
        _pendingLoad = null;
        try { PlaybackEnded?.Invoke(); }
        catch { /* подписчик маршалит и обрабатывает сам */ }
    }

    /// <summary>Свернули окно — видео не декодим (звук продолжается). Развернули — вернуть.</summary>
    public void SetVideoDecoding(bool on)
    {
        if (_handle != IntPtr.Zero)
            mpv_set_property_string(_handle, "vid", on ? "auto" : "no");
    }

    private bool InitMpv()
    {
        if (_handle != IntPtr.Zero) return true;
        if (_wid == IntPtr.Zero)
        {
            LastError = "video surface not ready";
            return false;
        }
        var h = mpv_create();
        if (h == IntPtr.Zero)
        {
            LastError = "mpv_create failed";
            return false;
        }
        mpv_set_option_string(h, "wid", _wid.ToInt64().ToString());
        mpv_set_option_string(h, "input-default-bindings", "no");
        mpv_set_option_string(h, "input-vo-keyboard", "no");
        mpv_set_option_string(h, "osc", "no");
        mpv_set_option_string(h, "osd-level", "0");
        mpv_set_option_string(h, "keep-open", "no");
        mpv_set_option_string(h, "hwdec", "auto-safe");
        mpv_set_option_string(h, "terminal", "no");
        mpv_set_option_string(h, "audio-display", "no");
        if (mpv_initialize(h) < 0)
        {
            LastError = "mpv_initialize failed";
            mpv_terminate_destroy(h);
            return false;
        }
        _handle = h;
        SetVolumeProperty();
        SetSpeedProperty();
        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
        return true;
    }

    private void EventLoop()
    {
        // mpv_event: { event_id:int, error:int, reply_userdata:ulong, data:void* }
        while (_running && _handle != IntPtr.Zero)
        {
            var ev = mpv_wait_event(_handle, 0.5);
            if (ev == IntPtr.Zero) continue;
            var id = Marshal.ReadInt32(ev);
            if (id == EventShutdown) break;
            if (id != EventEndFile) continue;
            var data = Marshal.ReadIntPtr(ev, 16);
            var reason = data != IntPtr.Zero ? Marshal.ReadInt32(data) : EndReasonEof;
            if (reason is EndReasonEof or EndReasonError)
            {
                _state = PlaybackState.Stopped;
                try { PlaybackEnded?.Invoke(); }
                catch { /* подписчик маршалит и обрабатывает сам */ }
            }
        }
    }

    public bool Load(string filePath, double startSeconds = 0, double? endSeconds = null)
    {
        LastError = null;
        TrackGainDb = 0;
        _pendingLoad = (filePath, startSeconds, endSeconds);
        _pendingPlay = false;
        if (_handle == IntPtr.Zero)
        {
            // Поверхности ещё нет (Обзор только открывается) — прикинемся успешными,
            // старт случится в AttachWindow. Плеер не видит разницы.
            if (_wid == IntPtr.Zero) { _state = PlaybackState.Paused; return true; }
            if (!InitMpv()) return false;
        }
        return StartPending();
    }

    private bool StartPending()
    {
        if (_pendingLoad is not { } load) return true;
        if (!InitMpv()) return false;
        // start/end — опциями loadfile: немедленный сет time-pos mpv отбрасывает
        // (файл ещё не загружен, MPV_ERROR_PROPERTY_UNAVAILABLE) и сегмент терялся.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var opts = new List<string>(2);
        if (load.Start > 0) opts.Add($"start={load.Start.ToString(inv)}");
        if (load.End is { } end && end > load.Start) opts.Add($"end={end.ToString(inv)}");
        var ok = opts.Count > 0
            ? Command("loadfile", load.Path, "replace", "-1", string.Join(',', opts))
            : Command("loadfile", load.Path, "replace");
        if (!ok)
        {
            LastError = $"mpv loadfile: {Path.GetFileName(load.Path)}";
            return false;
        }
        mpv_set_property_string(_handle, "pause", _pendingPlay ? "no" : "yes");
        _state = _pendingPlay ? PlaybackState.Playing : PlaybackState.Paused;
        _pendingLoad = null;
        return true;
    }

    /// <summary>mpv_command через массив аргументов — никакой возни с кавычками в путях.</summary>
    private bool Command(params string[] args)
    {
        if (_handle == IntPtr.Zero) return false;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                ptrs[i] = Utf8(args[i]);
            var array = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            try
            {
                Marshal.Copy(ptrs, 0, array, ptrs.Length);
                return mpv_command(_handle, array) >= 0;
            }
            finally
            {
                Marshal.FreeHGlobal(array);
            }
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero)
                    Marshal.FreeHGlobal(p);
        }
    }

    private static IntPtr Utf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        var p = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    public bool CrossfadeTo(string filePath, double startSeconds, double? endSeconds, double fadeSeconds, double gainDb = 0)
    {
        // Кроссфейда у видео нет — обычный переход (движок сюда и так не заходит).
        var ok = Load(filePath, startSeconds, endSeconds);
        if (ok) Play();
        return ok;
    }

    public void Play()
    {
        _pendingPlay = true;
        if (_handle == IntPtr.Zero) { SignalIfStartFailed(StartPending()); return; }
        if (_pendingLoad is not null) { SignalIfStartFailed(StartPending()); if (_handle == IntPtr.Zero) return; }
        mpv_set_property_string(_handle, "pause", "no");
        _state = PlaybackState.Playing;
    }

    public void Pause()
    {
        _pendingPlay = false;
        if (_handle == IntPtr.Zero) { _state = PlaybackState.Paused; return; }
        mpv_set_property_string(_handle, "pause", "yes");
        _state = PlaybackState.Paused;
    }

    public void Stop()
    {
        _pendingPlay = false;
        _pendingLoad = null;
        if (_handle != IntPtr.Zero) Command("stop");
        _state = PlaybackState.Stopped;
    }

    // Видео v1 — без фейдов: маппим на обычные действия.
    public void FadePause(int fadeMs) => Pause();
    public void FadeResume(int fadeMs) => Play();
    public void FadeOutAndStop(int fadeMs) => Stop();

    public PlaybackState State => _state;

    public double DurationSeconds
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            return mpv_get_property(_handle, "duration", FormatDouble, out var d) >= 0 ? d : 0;
        }
    }

    public double PositionSeconds
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            return mpv_get_property(_handle, "time-pos", FormatDouble, out var p) >= 0 ? Math.Max(0, p) : 0;
        }
        set
        {
            if (_handle == IntPtr.Zero) return;
            var pos = Math.Max(0, value);
            mpv_set_property(_handle, "time-pos", FormatDouble, ref pos);
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            SetVolumeProperty();
        }
    }

    private void SetVolumeProperty()
    {
        if (_handle == IntPtr.Zero) return;
        var v = _volume * 100.0;
        mpv_set_property(_handle, "volume", FormatDouble, ref v);
    }

    public double Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 0.5, 2.0);
            SetSpeedProperty();
        }
    }

    private void SetSpeedProperty()
    {
        if (_handle == IntPtr.Zero) return;
        var s = _speed;
        mpv_set_property(_handle, "speed", FormatDouble, ref s);
    }

    public bool CanDecode(string filePath) => VideoFiles.IsVideo(filePath);

    public void SetEqualizer(float[]? gains) { } // EQ — только у аудио-тракта
    public bool GetSpectrum(float[] buffer) => false;

    public void Dispose()
    {
        // Контракт libmpv: terminate_destroy НЕЛЬЗЯ звать, пока другой поток сидит в
        // mpv_wait_event на том же handle. Порядок: разбудить → дождаться выхода → уничтожить.
        _running = false;
        var h = _handle;
        if (h != IntPtr.Zero) mpv_wakeup(h);
        _eventThread?.Join(2000);
        _handle = IntPtr.Zero;
        if (h != IntPtr.Zero) mpv_terminate_destroy(h);
    }
}
