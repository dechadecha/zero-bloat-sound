using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZBS.Library;

/// <summary>Строка синхронного текста: момент появления + текст.</summary>
public sealed record LrcLine(TimeSpan Time, string Text);

/// <summary>
/// Парсер .lrc (караоке-титры): [mm:ss.xx]текст, несколько таймкодов на строку,
/// тег [offset:±ms]. Источник — .lrc-файл рядом с треком или LRC-таймкоды прямо
/// в теге USLT (так пишут многие теггеры).
/// </summary>
public static class LrcParser
{
    private static readonly Regex TimeRe = new(
        @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
        RegexOptions.Compiled);

    private static readonly Regex OffsetRe = new(
        @"^\[offset:\s*([+-]?\d+)\s*\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Разбор LRC-текста. null — если синхронных строк нет (тогда показываем как простой текст).
    /// Строки сортируются по времени, пустые «паузы» сохраняются (гасят подсветку в проигрыше).
    /// </summary>
    // Enhanced LRC (Lyricsify/Walaoke): пословные таймкоды <mm:ss.xx> внутри строки —
    // для нашей построчной подсветки это мусор, вырезаем.
    private static readonly Regex WordTimeRe = new(
        @"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>",
        RegexOptions.Compiled);

    private static IEnumerable<string> SplitLines(string content)
    {
        // \n, \r\n и одиночные \r (старые Mac-файлы) — всё считается переводом строки.
        foreach (var l in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            yield return l;
    }

    public static IReadOnlyList<LrcLine>? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        content = content.TrimStart('﻿'); // BOM ломает ^-якорные регексы

        // [offset] относится ко ВСЕМУ файлу, где бы ни стоял тег — собираем заранее.
        var offset = TimeSpan.Zero;
        foreach (var raw in SplitLines(content))
        {
            var off = OffsetRe.Match(raw);
            if (!off.Success) continue;
            // Положительный offset в LRC = текст должен появляться РАНЬШЕ.
            if (long.TryParse(off.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                offset = TimeSpan.FromMilliseconds(-ms);
        }

        var lines = new List<LrcLine>();
        foreach (var raw in SplitLines(content))
        {
            var line = raw;
            if (OffsetRe.IsMatch(line)) continue;

            var matches = TimeRe.Matches(line);
            if (matches.Count == 0) continue;

            var text = WordTimeRe.Replace(TimeRe.Replace(line, ""), "").Trim();
            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (sec > 59) continue; // [12:87] — не таймкод, скорее счёт в тексте
                double frac = 0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    frac = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
                }
                var t = TimeSpan.FromSeconds(min * 60 + sec + frac) + offset;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                lines.Add(new LrcLine(t, text));
            }
        }

        if (lines.Count == 0) return null;
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>.lrc-файл рядом с треком (то же имя). null — нет или нечитаем.</summary>
    public static string? ReadSidecar(string trackPath)
    {
        try
        {
            var lrc = Path.ChangeExtension(trackPath, ".lrc");
            if (!File.Exists(lrc)) return null;
            var bytes = File.ReadAllBytes(lrc);
            return Decode(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>UTF-16 по BOM/NUL-байтам → строгий UTF-8 → cp1251 (старые .lrc из рунета).</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        // UTF-16 LE без BOM (Notepad-«Юникод» без заголовка): латиница даёт валидный UTF-8
        // с NUL после каждого символа — по ним и распознаём.
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0)
            return Encoding.Unicode.GetString(bytes);
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return text.TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static readonly Regex MetaRe = new(@"^\[[a-zA-Z]+:[^\]]*\]\s*$", RegexOptions.Compiled);

    /// <summary>Плоский текст из LRC: без таймкодов и мета-тегов ([ar:]/[ti:]/…).</summary>
    public static string StripTimestamps(string content)
    {
        var sb = new StringBuilder();
        foreach (var line in SplitLines(content.TrimStart('﻿')))
        {
            if (MetaRe.IsMatch(line)) continue;
            sb.AppendLine(WordTimeRe.Replace(TimeRe.Replace(line, ""), "").TrimEnd());
        }
        return sb.ToString().Trim();
    }

    /// <summary>Индекс текущей строки для позиции (последняя с Time &lt;= pos), -1 до первой.</summary>
    public static int IndexFor(IReadOnlyList<LrcLine> lines, TimeSpan pos)
    {
        var lo = 0;
        var hi = lines.Count - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= pos) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return result;
    }
}
