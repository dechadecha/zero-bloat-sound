namespace ZBS.Core.Settings;

/// <summary>Настройки приложения. Каждая фича обязана быть выключаемой — тумблеры живут здесь.</summary>
public sealed class AppSettings
{
    public double Volume { get; set; } = 0.8;
    /// <summary>"auto" — по языку системы; иначе код языка ("en", "ru", ...).</summary>
    public string Language { get; set; } = "auto";
    public string? LastFolder { get; set; }
    public double WindowWidth { get; set; } = 520;
    public double WindowHeight { get; set; } = 640;

    /// <summary>Кроссфейд между треками, сек. 0 — выключен.</summary>
    public double CrossfadeSeconds { get; set; }

    /// <summary>Мягкая пауза (фейд), мс. 0 — обычная жёсткая пауза.</summary>
    public int SmoothPauseMs { get; set; } = 200;

    /// <summary>Резюме длинных файлов (аудиокниги/миксы).</summary>
    public bool ResumeLongFiles { get; set; } = true;

    /// <summary>"off" | "random" | "smart".</summary>
    public string Shuffle { get; set; } = "off";

    /// <summary>"none" | "all" | "one".</summary>
    public string Repeat { get; set; } = "none";

    /// <summary>Глобальные мультимедиа-клавиши клавиатуры.</summary>
    public bool MediaKeys { get; set; } = true;

    /// <summary>ReplayGain-выравнивание громкости по тегам.</summary>
    public bool ReplayGain { get; set; } = true;

    /// <summary>Автодотяжка обложек из Cover Art Archive, когда локально их нет.</summary>
    public bool CoverAutoFetch { get; set; } = true;

    /// <summary>Эквалайзер включён.</summary>
    public bool EqEnabled { get; set; }

    /// <summary>Гейны 10 полос EQ в дБ, через точку с запятой («0;2;-1;…»). Пусто — плоско.</summary>
    public string EqGains { get; set; } = "";

    /// <summary>Визуализатор (спектр) включён.</summary>
    public bool VisualizerEnabled { get; set; } = true;

    /// <summary>Показывать мини-визуализатор в компакт-режиме.</summary>
    public bool VisualizerInCompact { get; set; } = true;

    /// <summary>Режим визуализатора: 0 столбики, 1 волна, 2 сглаж. пики, 3 радиальный.</summary>
    public int VisualizerMode { get; set; }

    /// <summary>Магнитное прилипание окна к краям экрана (вкл. AIMP-стайл верх).</summary>
    public bool MagneticSnap { get; set; } = true;

    /// <summary>Имя файла активного скина в папке skins ("" — скин выключен, обычный UI).</summary>
    public string SkinFile { get; set; } = "";

    /// <summary>Пользовательская папка скинов ("" — стандартная в данных приложения).</summary>
    public string SkinsFolder { get; set; } = "";

    /// <summary>Waveform-подложка под сикбаром.</summary>
    public bool WaveformSeekbar { get; set; } = true;

    // ---- M5 «Сеть и выводы» ----

    /// <summary>Секция «Радио» в сайдбаре.</summary>
    public bool RadioEnabled { get; set; } = true;

    /// <summary>Режим экрана радио: "net" (каталог) | "fm" (частотная шкала).</summary>
    public string RadioMode { get; set; } = "net";

    /// <summary>Последняя частота FM-тюнера, МГц.</summary>
    public double FmTuner { get; set; } = 100.0;

    /// <summary>Секция «Подкасты» в сайдбаре.</summary>
    public bool PodcastsEnabled { get; set; } = true;

    /// <summary>Устройство вывода BASS (-1 — системное по умолчанию).</summary>
    public int OutputDevice { get; set; } = -1;

    /// <summary>Веб-пульт (LAN + PIN). По умолчанию ВЫКЛ.</summary>
    public bool WebRemoteEnabled { get; set; }

    /// <summary>Запись эфира с перекодировкой в MP3 (при наличии энкодера); выкл — сырой поток.</summary>
    public bool RecordMp3 { get; set; } = true;

    /// <summary>Discord Rich Presence («слушает …»). По умолчанию ВЫКЛ.</summary>
    public bool DiscordPresence { get; set; }

    // Last.fm: скробблинг. Пароль НЕ хранится — только бессрочный session key.
    public bool LastfmEnabled { get; set; }
    public string LastfmApiKey { get; set; } = "";
    public string LastfmApiSecret { get; set; } = "";
    public string LastfmSessionKey { get; set; } = "";
    public string LastfmUser { get; set; } = "";
}
