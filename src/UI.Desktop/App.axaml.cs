using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ZBS.Core.Audio.Bass;
using ZBS.Core.Playback;
using ZBS.Core.Settings;
using ZBS.Library;
using ZBS.UI.Desktop.Localization;
using ZBS.UI.Desktop.ViewModels;
using ZBS.UI.Desktop.Views;

namespace ZBS.UI.Desktop;

public partial class App : Application
{
    private MediaKeysHook? _mediaKeys;
    private TrayIcon? _tray;
    private LibraryService? _library;
    private LoudnessAnalyzer? _loudness;
    private LibraryViewModel? _libraryVm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore();
            var settings = store.Load();
            Loc.Apply(settings.Language);

            var backend = new BassAudioBackend
            {
                // Хвосты фейдов возвращаются на UI-поток — иначе гонки за переиспользованные хэндлы BASS.
                Dispatch = a => Dispatcher.UIThread.Post(a),
            };
            // Нет аудиоустройства (сервер/CI) — падать нельзя: инициализируемся в «no sound».
            if (!backend.Init(-1))
                backend.Init(0);

            var resume = new ResumeStore(SettingsStore.ResolveDirectory());
            var engine = new PlaybackEngine(backend, resume);
            var vm = new MainViewModel(engine, store, settings);

            // M4: видео через libmpv — только если dll реально лежит рядом (иначе плеер как раньше).
            if (ZBS.Core.Video.Mpv.MpvVideoBackend.IsAvailable)
            {
                var video = new ZBS.Core.Video.Mpv.MpvVideoBackend();
                engine.AttachVideoBackend(video, ZBS.Core.Video.VideoFiles.IsVideo);
                vm.VideoSurfaceSetter = video.AttachWindow;
                vm.VideoDecodingSetter = video.SetVideoDecoding;
            }

            var library = new LibraryService(SettingsStore.ResolveDirectory());
            var libraryVm = new LibraryViewModel(library, settings)
            {
                FileProber = backend.CanDecode,
            };
            vm.AttachLibrary(libraryVm);
            // Выравнивание громкости: RG-тег из медиатеки, а для файлов без тегов — свой
            // фоновый RMS-анализ (записи/рипы играли заметно тише остальных).
            var loudness = new ZBS.Core.Audio.Bass.LoudnessAnalyzer(SettingsStore.ResolveDirectory());
            _loudness = loudness;
            engine.GainProvider = path => library.GetGainDb(path) ?? loudness.GetOrQueue(path);
            loudness.Computed += (path, gainDb) => Dispatcher.UIThread.Post(() =>
            {
                // Файл дообсчитался, пока играл — применяем сразу, не дожидаясь перезапуска.
                if (settings.ReplayGain && engine.CurrentTrack?.FilePath == path && !engine.CurrentIsRadio)
                    engine.ApplyGainNow(gainDb);
            });

            var covers = new CoverService(SettingsStore.ResolveDirectory())
            {
                AutoFetch = settings.CoverAutoFetch,
            };
            vm.CoverProvider = track => Task.Run(async () =>
            {
                // Целиком в пуле: чтение тегов и файлов обложек (TagLib + диск) не должно
                // фризить UI-поток на медленных дисках.
                var tags = TagReader.Read(track.FilePath);
                return await covers.GetCoverAsync(track, tags.Artist, tags.Album);
            });
            vm.RateHandler = (track, rating) => _ = Task.Run(() =>
            {
                try { library.RateTrack(track, rating); } catch { }
            });
            engine.TrackPlayedEnough += t => _ = Task.Run(() =>
            {
                // fire-and-forget: ошибка счётчика (включая гонку с выходом) не должна никого ронять
                try { library.MarkPlayed(t); } catch { }
            });
            _library = library;
            _libraryVm = libraryVm;

            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            SetupTray(vm, window, desktop);
            SetupMediaKeys(vm, settings);

            if (Program.Instance is { } si)
                si.ArgsReceived += vm.HandleExternalArgs;

            if (desktop.Args is { Length: > 0 } args)
                vm.HandleExternalArgs(args);

            // Exit (в отличие от ShutdownRequested) стреляет ВСЕГДА — и при закрытии окна,
            // и при forced Shutdown из трея, и при логофф ОС. Иначе выход через трей
            // терял настройки, резюме и не закрывал BASS/SQLite.
            desktop.Exit += (_, _) =>
            {
                // Единая логика с Closing: компакт/фулскрин не портят сохранённый размер.
                window.PersistWindowSize();
                _libraryVm?.CancelScan();
                _mediaKeys?.Dispose();
                _loudness?.Dispose();
                _tray?.Dispose();
                vm.Dispose();
                _library?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(MainViewModel vm, MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var show = new NativeMenuItem(Loc.T("ShowWindow"));
        show.Click += (_, _) => Dispatcher.UIThread.Post(() => { window.Show(); window.Activate(); });
        menu.Add(show);
        menu.Add(new NativeMenuItemSeparator());

        var playPause = new NativeMenuItem("⏯");
        playPause.Click += (_, _) => Dispatcher.UIThread.Post(() => vm.PlayPauseCommand.Execute(null));
        menu.Add(playPause);
        var next = new NativeMenuItem("⏭");
        next.Click += (_, _) => Dispatcher.UIThread.Post(() => vm.NextCommand.Execute(null));
        menu.Add(next);
        var prev = new NativeMenuItem("⏮");
        prev.Click += (_, _) => Dispatcher.UIThread.Post(() => vm.PreviousCommand.Execute(null));
        menu.Add(prev);
        menu.Add(new NativeMenuItemSeparator());

        var sleep = new NativeMenuItem(Loc.T("SleepTimer")) { Menu = new NativeMenu() };
        foreach (var minutes in new[] { 15, 30, 60, 90 })
        {
            var item = new NativeMenuItem(Loc.T("SleepMinutes", minutes));
            var m = minutes;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => vm.SetSleepTimer(m));
            sleep.Menu.Add(item);
        }
        var sleepOff = new NativeMenuItem(Loc.T("SleepOff"));
        sleepOff.Click += (_, _) => Dispatcher.UIThread.Post(() => vm.SetSleepTimer(0));
        sleep.Menu.Add(sleepOff);
        menu.Add(sleep);
        menu.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem(Loc.T("Exit"));
        exit.Click += (_, _) => Dispatcher.UIThread.Post(() => desktop.TryShutdown());
        menu.Add(exit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ZeroBloatSound/Assets/zbs.ico"))),
            ToolTipText = Loc.T("AppTitle"),
            Menu = menu,
        };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(() => { window.Show(); window.Activate(); });
    }

    private void SetupMediaKeys(MainViewModel vm, AppSettings settings)
    {
        if (!settings.MediaKeys) return;
        _mediaKeys = new MediaKeysHook();
        _mediaKeys.PlayPausePressed += () => Dispatcher.UIThread.Post(() => vm.PlayPauseCommand.Execute(null));
        _mediaKeys.NextPressed += () => Dispatcher.UIThread.Post(() => vm.NextCommand.Execute(null));
        _mediaKeys.PreviousPressed += () => Dispatcher.UIThread.Post(() => vm.PreviousCommand.Execute(null));
        _mediaKeys.StopPressed += () => Dispatcher.UIThread.Post(() => vm.StopCommand.Execute(null));
        _mediaKeys.Install();
    }
}
