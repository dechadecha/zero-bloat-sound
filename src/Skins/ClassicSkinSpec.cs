namespace ZBS.Skins;

/// <summary>
/// Классическая спецификация Winamp 2 (.wsz): главное окно 275×116, спрайт-листы с
/// фиксированными координатами. Строит SkinModel из набора имеющихся листов —
/// чего в скине нет, того в модели нет (рендерер не заметит).
/// </summary>
public static class ClassicSkinSpec
{
    public static SkinModel Build(string skinName, ISet<string> sheets)
    {
        var m = new SkinModel
        {
            Manifest = new SkinManifest { Name = skinName, Size = new[] { 275, 116 } },
        };
        var e = m.Elements;

        void Add(SkinElement el)
        {
            if (sheets.Contains(el.Sheet) &&
                (el.ThumbSheet is null || sheets.Contains(el.ThumbSheet)))
                e.Add(el);
        }

        Add(new SkinElement { Type = SkinElementType.Background, Id = "bg", Sheet = "main.bmp", X = 0, Y = 0, W = 275, H = 116 });

        // Титлбар: полоса 275×14 (активная — y0 в titlebar.bmp начинается с x27? нет: активная (27,0), неактивная (27,15)).
        Add(new SkinElement
        {
            Type = SkinElementType.Background, Id = "titlebar", Sheet = "titlebar.bmp",
            X = 0, Y = 0, W = 275, H = 14, Src = new[] { 27, 0 },
        });

        // Транспорт: cbuttons.bmp — 5 кнопок 23×18 (normal y0, pressed y18) + eject 22×16.
        var actions = new[] { "Prev", "PlayPause", "Pause", "Stop", "Next" };
        for (var i = 0; i < 5; i++)
        {
            Add(new SkinElement
            {
                Type = SkinElementType.Button, Id = actions[i].ToLowerInvariant(), Sheet = "cbuttons.bmp",
                X = 16 + i * 23, Y = 88, W = 23, H = 18,
                Src = new[] { i * 23, 0 }, SrcPressed = new[] { i * 23, 18 },
                Action = actions[i],
            });
        }
        Add(new SkinElement
        {
            Type = SkinElementType.Button, Id = "eject", Sheet = "cbuttons.bmp",
            X = 136, Y = 89, W = 22, H = 16,
            Src = new[] { 114, 0 }, SrcPressed = new[] { 114, 16 },
            Action = "Eject",
        });

        // Шаффл/повтор: shufrep.bmp (repeat 28×15 @x0, shuffle 47×15 @x28; off-n y0, off-p y15, on-n y30, on-p y45).
        Add(new SkinElement
        {
            Type = SkinElementType.Toggle, Id = "shuffle", Sheet = "shufrep.bmp",
            X = 164, Y = 89, W = 47, H = 15,
            Src = new[] { 28, 0 }, SrcPressed = new[] { 28, 15 },
            SrcOn = new[] { 28, 30 }, SrcOnPressed = new[] { 28, 45 },
            Action = "Shuffle",
        });
        Add(new SkinElement
        {
            Type = SkinElementType.Toggle, Id = "repeat", Sheet = "shufrep.bmp",
            X = 210, Y = 89, W = 28, H = 15,
            Src = new[] { 0, 0 }, SrcPressed = new[] { 0, 15 },
            SrcOn = new[] { 0, 30 }, SrcOnPressed = new[] { 0, 45 },
            Action = "Repeat",
        });

        // Кнопки титлбара: 9×9, в titlebar.bmp (close (18,0), minimize (9,0)).
        Add(new SkinElement
        {
            Type = SkinElementType.Button, Id = "close", Sheet = "titlebar.bmp",
            X = 264, Y = 3, W = 9, H = 9,
            Src = new[] { 18, 0 }, SrcPressed = new[] { 18, 9 },
            Action = "Close",
        });
        Add(new SkinElement
        {
            Type = SkinElementType.Button, Id = "minimize", Sheet = "titlebar.bmp",
            X = 244, Y = 3, W = 9, H = 9,
            Src = new[] { 9, 0 }, SrcPressed = new[] { 9, 9 },
            Action = "Minimize",
        });

        // Посбар: posbar.bmp — фон 248×10, бегунок 29×10 (x248 normal / x278 pressed).
        Add(new SkinElement
        {
            Type = SkinElementType.SliderH, Id = "posbar", Sheet = "posbar.bmp",
            X = 16, Y = 72, W = 248, H = 10,
            SrcBg = new[] { 0, 0 },
            ThumbSheet = "posbar.bmp", Thumb = new[] { 248, 0, 29, 10 }, ThumbPressed = new[] { 278, 0 },
            Action = "Seek",
        });

        // Громкость: volume.bmp — 28 кадров 68×13 с шагом 15 по Y, бегунок 14×11 (15,422)/(0,422).
        Add(new SkinElement
        {
            Type = SkinElementType.SliderH, Id = "volume", Sheet = "volume.bmp",
            X = 107, Y = 57, W = 68, H = 13,
            Frames = 28, FrameStride = 15, SrcBg = new[] { 0, 0 },
            ThumbSheet = "volume.bmp", Thumb = new[] { 15, 422, 14, 11 }, ThumbPressed = new[] { 0, 422 },
            Action = "Volume",
        });

        // Время mm:ss: цифры 9×13. Часть скинов кладёт их в nums_ex.bmp вместо numbers.bmp.
        Add(new SkinElement
        {
            Type = SkinElementType.Digits, Id = "time",
            Sheet = sheets.Contains("numbers.bmp") ? "numbers.bmp" : "nums_ex.bmp",
            X = 48, Y = 26, DigitW = 9, DigitH = 13,
            DigitsX = new[] { 48, 60, 78, 90 },
        });

        // Название трека: text.bmp — шрифт 5×6, зона 111..264 на y27.
        Add(new SkinElement
        {
            Type = SkinElementType.Marquee, Id = "title", Sheet = "text.bmp",
            X = 111, Y = 27, W = 153, H = 6, CharW = 5, CharH = 6,
        });

        // Индикатор play/pause/stop: playpaus.bmp — кадры 9×9.
        Add(new SkinElement
        {
            Type = SkinElementType.Indicator, Id = "playpaus", Sheet = "playpaus.bmp",
            X = 26, Y = 28, W = 9, H = 9,
            Src = new[] { 0, 0 },        // play
            SrcPressed = new[] { 9, 0 }, // pause
            SrcOn = new[] { 18, 0 },     // stop
        });

        return m;
    }
}
