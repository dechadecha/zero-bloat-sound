using System.Globalization;
using System.Text.Json;

namespace ZBS.UI.Desktop.Localization;

/// <summary>
/// Каркас локализации M0: словари в коде, выбор языка из настроек ("auto" — по системе).
/// К v1.0 переезжает на систему языковых пакетов (8 языков + пакеты сообщества).
/// </summary>
public static class Loc
{
    private static string _lang = "en";

    private static readonly Dictionary<string, Dictionary<string, string>> Strings = new()
    {
        ["en"] = new()
        {
            ["AppTitle"] = "Zero-Bloat Sound",
            ["OpenFolder"] = "Open folder…",
            ["EmptyHint"] = "Drop a folder here — it becomes your playlist",
            ["NoTrack"] = "Nothing playing",
            ["Play"] = "Play",
            ["Pause"] = "Pause",
            ["Stop"] = "Stop",
            ["Next"] = "Next",
            ["Previous"] = "Previous",
            ["TracksCount"] = "{0} tracks",
            ["Scanning"] = "Scanning…",
            ["PlayNext"] = "Play next",
            ["ShuffleOff"] = "Shuffle: off",
            ["ShuffleRandom"] = "Shuffle: random",
            ["ShuffleSmart"] = "Shuffle: smart",
            ["RepeatNone"] = "Repeat: off",
            ["RepeatAll"] = "Repeat: all",
            ["RepeatOne"] = "Repeat: one",
            ["SpeedTip"] = "Playback speed",
            ["AbTip"] = "A-B loop: press at A, then at B",
            ["SleepTimer"] = "Sleep timer",
            ["SleepOff"] = "Off",
            ["SleepMinutes"] = "{0} min",
            ["Exit"] = "Exit",
            ["ShowWindow"] = "Show player",
            ["PlaylistTab"] = "Playlist",
            ["LibraryTab"] = "Library",
            ["AddFolder"] = "Add folder…",
            ["Rescan"] = "Rescan",
            ["AppendAll"] = "Add all to playlist",
            ["SearchLibrary"] = "Search library…",
            ["EmptyLibraryHint"] = "Add music folders — the library will index and search them",
            ["ScanProgress"] = "Scanning… {0} files",
            ["LibraryCount"] = "In library: {0}",
            ["JumpWatermark"] = "Jump to track (Enter — play)",
            ["SavePlaylist"] = "Save playlist…",
            ["PlaylistSaved"] = "Saved: {0}",
            ["RemoveFolder"] = "Remove folder",
            ["FolderUnavailable"] = "a source folder is unavailable — cleanup skipped",
            ["Duplicates"] = "Duplicates",
            ["Broken"] = "Broken files",
            ["AllArtists"] = "— all artists —",
            ["AllGenres"] = "— all genres —",
            ["Rate"] = "Clear rating",
            ["DuplicatesFound"] = "Duplicates: {0}",
            ["BrokenFound"] = "Broken files: {0}",
            ["NowPlayingTip"] = "Now playing / browse",
            ["NavAllTracks"] = "All tracks",
            ["NavFolders"] = "Folders",
            ["NavRatings"] = "Rated",
            ["NavQueue"] = "Playlist",
            ["NavSettings"] = "Settings",
            ["SecPhonoteka"] = "Music",
            ["SecPlaylists"] = "Playlists",
            ["Back"] = "Back (Esc)",
            ["QueueTitle"] = "Queue",
            ["TextBtn"] = "Lyrics",
            ["NoLyrics"] = "No lyrics in the file tags",
            ["DetailAlbum"] = "Album",
            ["DetailGenre"] = "Genre",
            ["DetailYear"] = "Year",
            ["DetailFormat"] = "Format",
            ["DetailDuration"] = "Length",
            ["SettingsTitle"] = "Settings",
            ["TileVisualizer"] = "Visualizer",
            ["TileVisualizerHint"] = "Spectrum and beat response",
            ["TileEq"] = "Equalizer",
            ["TileEqHint"] = "10 bands, presets",
            ["TileAudio"] = "Audio engine",
            ["TileAudioHint"] = "Crossfade, ReplayGain, resume",
            ["TileKeys"] = "Hotkeys",
            ["TileKeysHint"] = "Media keys, global shortcuts",
            ["TileLook"] = "Appearance",
            ["TileLookHint"] = "Theme, accent, skins",
            ["TileUpdates"] = "Updates",
            ["TileUpdatesHint"] = "Version and channel",
            ["TileNet"] = "Network & output",
            ["TileNetHint"] = "Web remote, output device",
            ["DeviceDefault"] = "System default",
            ["RemoteOpenOnPhone"] = "Open on your phone:",
            ["RemoteOffHint"] = "Remote is off — LAN only, PIN in the link",
            ["NavRadio"] = "Radio",
            ["NavPodcasts"] = "Podcasts",
            ["ComingM32"] = "Coming in M3.2 — stay tuned.",
            ["SetCrossfade"] = "Crossfade between tracks",
            ["SetSmoothPause"] = "Smooth pause",
            ["SetReplayGain"] = "ReplayGain (even loudness)",
            ["SetResumeLong"] = "Resume long files",
            ["SetSeconds"] = "{0:0.#} s",
            ["SetMs"] = "{0} ms",
            ["SetOffShort"] = "off",
            ["KeysInfo"] = "Media keys work globally: Play/Pause, Next, Previous, Stop. Custom shortcuts — later.",
            ["LookInfo"] = "Dark theme. Accent — ZBS Voltage #00E676. Light theme and .zbs skins are coming with the skin engine.",
            ["UpdatesInfo"] = "Zero-Bloat Sound, pre-release build. Auto-updates will arrive closer to 1.0.",
            ["MiniTip"] = "Compact mode: just resize the window smaller",
            ["SearchShort"] = "Search…",
            ["GenreAll"] = "all",
            ["GenrePrefix"] = "Genre:",
            ["ColHeader"] = "TITLE · ARTIST",
            ["ColTime"] = "TIME",
            ["EqOnLbl"] = "Equalizer on",
            ["PresetsLbl"] = "Presets",
            ["PresetFlat"] = "Flat",
            ["PresetAuf"] = "AUF",
            ["PresetRock"] = "Rock",
            ["PresetPop"] = "Pop",
            ["PresetBass"] = "Bass+",
            ["PresetVoice"] = "Voice",
            ["VizOnLbl"] = "Visualizer on",
            ["VizCompactLbl"] = "Mini bars in compact mode",
            ["MagnetLbl"] = "Magnetic window snapping",
            ["MagnetHint"] = "The window sticks to screen edges while dragging",
            ["SkinsTitle"] = "Skins (.wsz / .zbs)",
            ["SkinApply"] = "Apply",
            ["SkinDisable"] = "Turn off skin",
            ["SkinRefresh"] = "Refresh",
            ["SkinFolder"] = "Skins folder…",
            ["SkinAdd"] = "Add skin…",
            ["SkinFolderReset"] = "Default folder",
            ["SkinConvert"] = "→ .zbs",
            ["SkinConverted"] = "Converted: {0}",
            ["SkinNoneHint"] = "Drop .wsz (classic Winamp) or .zbs skins into the folder",
            ["WaveformLbl"] = "Waveform seekbar",
        },
        ["ru"] = new()
        {
            ["AppTitle"] = "Zero-Bloat Sound",
            ["OpenFolder"] = "Открыть папку…",
            ["EmptyHint"] = "Кинь сюда папку — она станет плейлистом",
            ["NoTrack"] = "Ничего не играет",
            ["Play"] = "Играть",
            ["Pause"] = "Пауза",
            ["Stop"] = "Стоп",
            ["Next"] = "Следующий",
            ["Previous"] = "Предыдущий",
            ["TracksCount"] = "Треков: {0}",
            ["Scanning"] = "Сканирую…",
            ["PlayNext"] = "Сыграть следующим",
            ["ShuffleOff"] = "Шаффл: выкл",
            ["ShuffleRandom"] = "Шаффл: случайно",
            ["ShuffleSmart"] = "Шаффл: умный",
            ["RepeatNone"] = "Повтор: выкл",
            ["RepeatAll"] = "Повтор: все",
            ["RepeatOne"] = "Повтор: один",
            ["SpeedTip"] = "Скорость воспроизведения",
            ["AbTip"] = "A-B повтор: нажми в точке A, потом в точке B",
            ["SleepTimer"] = "Сон-таймер",
            ["SleepOff"] = "Выключен",
            ["SleepMinutes"] = "{0} мин",
            ["Exit"] = "Выход",
            ["ShowWindow"] = "Показать плеер",
            ["PlaylistTab"] = "Плейлист",
            ["LibraryTab"] = "Библиотека",
            ["AddFolder"] = "Добавить папку…",
            ["Rescan"] = "Пересканировать",
            ["AppendAll"] = "Всё в плейлист",
            ["SearchLibrary"] = "Поиск по библиотеке…",
            ["EmptyLibraryHint"] = "Добавь папки с музыкой — библиотека проиндексирует и будет искать",
            ["ScanProgress"] = "Сканирую… файлов: {0}",
            ["LibraryCount"] = "В библиотеке: {0}",
            ["JumpWatermark"] = "Прыжок к треку (Enter — играть)",
            ["SavePlaylist"] = "Сохранить плейлист…",
            ["PlaylistSaved"] = "Сохранено: {0}",
            ["RemoveFolder"] = "Убрать папку",
            ["FolderUnavailable"] = "папка-источник недоступна — чистка пропущена",
            ["Duplicates"] = "Дубликаты",
            ["Broken"] = "Битые файлы",
            ["AllArtists"] = "— все артисты —",
            ["AllGenres"] = "— все жанры —",
            ["Rate"] = "Снять оценку",
            ["DuplicatesFound"] = "Дубликатов: {0}",
            ["BrokenFound"] = "Битых файлов: {0}",
            ["NowPlayingTip"] = "Сейчас играет / обзор",
            ["NavAllTracks"] = "Все треки",
            ["NavFolders"] = "Папки",
            ["NavRatings"] = "Оценки",
            ["NavQueue"] = "Плейлист",
            ["NavSettings"] = "Настройки",
            ["SecPhonoteka"] = "Фонотека",
            ["SecPlaylists"] = "Плейлисты",
            ["Back"] = "Назад (Esc)",
            ["QueueTitle"] = "Очередь",
            ["TextBtn"] = "Текст",
            ["NoLyrics"] = "В тегах файла нет текста песни",
            ["DetailAlbum"] = "Альбом",
            ["DetailGenre"] = "Жанр",
            ["DetailYear"] = "Год",
            ["DetailFormat"] = "Формат",
            ["DetailDuration"] = "Длительность",
            ["SettingsTitle"] = "Настройки",
            ["TileVisualizer"] = "Визуализатор",
            ["TileVisualizerHint"] = "Спектр и реакция на бит",
            ["TileEq"] = "Эквалайзер",
            ["TileEqHint"] = "10 полос, пресеты",
            ["TileAudio"] = "Аудио-движок",
            ["TileAudioHint"] = "Кроссфейд, ReplayGain, резюме",
            ["TileKeys"] = "Горячие клавиши",
            ["TileKeysHint"] = "Медиа-кнопки, глобальные хоткеи",
            ["TileLook"] = "Внешность",
            ["TileLookHint"] = "Тема, акцент, скины",
            ["TileUpdates"] = "Обновления",
            ["TileUpdatesHint"] = "Версия и канал",
            ["TileNet"] = "Сеть и вывод",
            ["TileNetHint"] = "Веб-пульт, устройство вывода",
            ["DeviceDefault"] = "Системное по умолчанию",
            ["RemoteOpenOnPhone"] = "Открой на телефоне:",
            ["RemoteOffHint"] = "Пульт выключен — работает только в домашней сети, PIN в ссылке",
            ["NavRadio"] = "Радио",
            ["NavPodcasts"] = "Подкасты",
            ["ComingM32"] = "Появится в M3.2 — уже в плане.",
            ["SetCrossfade"] = "Кроссфейд между треками",
            ["SetSmoothPause"] = "Мягкая пауза",
            ["SetReplayGain"] = "ReplayGain (ровная громкость)",
            ["SetResumeLong"] = "Резюме длинных файлов",
            ["SetSeconds"] = "{0:0.#} с",
            ["SetMs"] = "{0} мс",
            ["SetOffShort"] = "выкл",
            ["KeysInfo"] = "Медиа-клавиши работают глобально: Play/Pause, Next, Previous, Stop. Настраиваемые сочетания — позже.",
            ["LookInfo"] = "Тёмная тема. Акцент — ZBS Voltage #00E676. Светлая тема и скины .zbs придут вместе с движком скинов.",
            ["UpdatesInfo"] = "Zero-Bloat Sound, предрелизная сборка. Автообновления появятся ближе к 1.0.",
            ["MiniTip"] = "Компакт-режим: просто уменьши окно",
            ["SearchShort"] = "Поиск…",
            ["GenreAll"] = "все",
            ["GenrePrefix"] = "Жанр:",
            ["ColHeader"] = "НАЗВАНИЕ · ИСПОЛНИТЕЛЬ",
            ["ColTime"] = "ВРЕМЯ",
            ["EqOnLbl"] = "Эквалайзер включён",
            ["PresetsLbl"] = "Пресеты",
            ["PresetFlat"] = "Плоский",
            ["PresetAuf"] = "АУФ",
            ["PresetRock"] = "Рок",
            ["PresetPop"] = "Поп",
            ["PresetBass"] = "Бас+",
            ["PresetVoice"] = "Голос",
            ["VizOnLbl"] = "Визуализатор включён",
            ["VizCompactLbl"] = "Мини-полоска в компакте",
            ["MagnetLbl"] = "Магнитное прилипание окна",
            ["MagnetHint"] = "Окно прилипает к краям экрана при перетаскивании",
            ["SkinsTitle"] = "Скины (.wsz / .zbs)",
            ["SkinApply"] = "Применить",
            ["SkinDisable"] = "Выключить скин",
            ["SkinRefresh"] = "Обновить",
            ["SkinFolder"] = "Папка скинов…",
            ["SkinAdd"] = "Добавить скин…",
            ["SkinFolderReset"] = "Стандартная папка",
            ["SkinConvert"] = "→ .zbs",
            ["SkinConverted"] = "Сконвертирован: {0}",
            ["SkinNoneHint"] = "Закинь в папку скины .wsz (классика Winamp) или .zbs",
            ["WaveformLbl"] = "Waveform-сикбар",
        },
    };

    // Человекочитаемые названия языков (для пикера). Пакеты дополняют своим "_name".
    private static readonly Dictionary<string, string> Names = new()
    {
        ["en"] = "English", ["ru"] = "Русский",
    };

    /// <summary>Язык сменился — вьюхи перечитывают все локализованные строки.</summary>
    public static event Action? Changed;

    /// <summary>Текущий код языка.</summary>
    public static string Current => _lang;

    /// <summary>Доступные языки (код, название) — встроенные + подгруженные пакеты.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available =>
        Strings.Keys.Select(c => (c, Names.TryGetValue(c, out var n) ? n : c)).ToList();

    /// <summary>
    /// Языковые пакеты: json-файлы в папке lang рядом с приложением. Формат — плоский словарь
    /// «ключ: перевод», плюс служебные "_lang" (код) и "_name" (название). Так язык добавляется
    /// без пересборки: сообщество кладёт uk.json / de.json и т.д.
    /// </summary>
    public static void LoadPacks(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (doc is null) continue;
                var code = doc.GetValueOrDefault("_lang") ?? Path.GetFileNameWithoutExtension(file);
                var dict = Strings.TryGetValue(code, out var existing) ? existing : new Dictionary<string, string>();
                foreach (var (k, v) in doc)
                    if (!k.StartsWith('_')) dict[k] = v;
                Strings[code] = dict;
                Names[code] = doc.GetValueOrDefault("_name") ?? code;
            }
            catch (Exception) { /* битый пакет — пропускаем, не роняем плеер */ }
        }
    }

    /// <summary>Применяет язык из настроек. "auto" — язык системы, фолбэк — английский.</summary>
    public static void Apply(string language)
    {
        var lang = language == "auto"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : language;
        _lang = Strings.ContainsKey(lang) ? lang : "en";
        Changed?.Invoke();
    }

    public static string T(string key) =>
        Strings[_lang].TryGetValue(key, out var v) ? v
        : Strings["en"].TryGetValue(key, out var en) ? en
        : key;

    public static string T(string key, params object[] args) => string.Format(T(key), args);
}
