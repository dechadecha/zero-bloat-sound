using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZBS.Skins;

/// <summary>Типы элементов скина. Один рендерер на все форматы (.wsz конвертится в эту же модель).</summary>
public enum SkinElementType
{
    Background,
    Button,   // обычная кнопка: normal/pressed
    Toggle,   // кнопка-состояние: off/on × normal/pressed (шаффл/повтор)
    SliderH,  // горизонтальный слайдер (посбар/громкость)
    Digits,   // цифры времени mm:ss из numbers.bmp
    Marquee,  // бегущая строка названия шрифтом text.bmp
    Indicator, // play/pause/stop индикатор
}

/// <summary>
/// Элемент скина: плоская структура (одна на все типы — незадействованные поля пустые),
/// чтобы layout.json оставался простым и правился руками.
/// Координаты — в «базовых» пикселях скина (классика: окно 275×116), масштаб — дело рендерера.
/// </summary>
public sealed class SkinElement
{
    public SkinElementType Type { get; set; }
    public string Id { get; set; } = "";
    /// <summary>Файл спрайт-листа внутри пакета (lowercase, например "cbuttons.bmp").</summary>
    public string Sheet { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    // Button/Toggle/Indicator: координаты кадров в листе.
    public int[]? Src { get; set; }          // normal [x,y]
    public int[]? SrcPressed { get; set; }   // pressed [x,y]
    public int[]? SrcOn { get; set; }        // toggle on-normal [x,y]
    public int[]? SrcOnPressed { get; set; } // toggle on-pressed [x,y]

    /// <summary>Что делает: PlayPause/Stop/Next/Prev/Eject/Shuffle/Repeat/Close/Minimize/Seek/Volume.</summary>
    public string? Action { get; set; }

    // SliderH: фоновые кадры (volume: 28 кадров с шагом FrameStride по Y) + бегунок.
    public int Frames { get; set; } = 1;
    public int FrameStride { get; set; }
    public int[]? SrcBg { get; set; }        // [x,y] кадра 0
    public string? ThumbSheet { get; set; }
    public int[]? Thumb { get; set; }        // [x,y,w,h]
    public int[]? ThumbPressed { get; set; } // [x,y]

    // Digits.
    public int DigitW { get; set; }
    public int DigitH { get; set; }
    public int[]? DigitsX { get; set; }      // X-позиции разрядов (m m s s), Y общий

    // Marquee.
    public int CharW { get; set; }
    public int CharH { get; set; }
}

public sealed class SkinManifest
{
    public string Format { get; set; } = "zbs/1";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public int[] Size { get; set; } = { 275, 116 };

    /// <summary>
    /// Тема современного UI (zbs/2): токен → hex. Ключи: bg, surface1..3, accent,
    /// text, textMuted, faint, line, ghost (все опциональны — не задан → дефолт).
    /// Пакет ТОЛЬКО с темой (без элементов) перекрашивает основной интерфейс;
    /// пакет с элементами — классическое ретро-окно.
    /// </summary>
    public Dictionary<string, string>? Theme { get; set; }
}

public sealed class SkinModel
{
    public SkinManifest Manifest { get; set; } = new();
    public List<SkinElement> Elements { get; set; } = new();

    // layout.json правится руками — читаем толерантно: любой регистр имён, хвостовые запятые, комментарии.
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}
