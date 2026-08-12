using System.Text;

namespace ZBS.Core.Playlist;

/// <summary>
/// Разбор .cue: один большой FLAC/WAV + разметка → отдельные треки-сегменты.
/// Понимает несколько FILE в одном листе. Ошибки листа не роняют скан — просто пусто.
/// </summary>
public static class CueParser
{
    public static List<Track> Parse(string cuePath)
    {
        try
        {
            return ParseLines(TextFileReader.ReadAllLinesSmart(cuePath), Path.GetDirectoryName(cuePath) ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Мусорный лист (NUL в имени, кривые пути) не должен ронять скан папки целиком.
            return new List<Track>();
        }
    }

    private static List<Track> ParseLines(string[] lines, string baseDir)
    {
        var result = new List<Track>();
        string? currentFile = null;
        string? pendingTitle = null;
        string? pendingPerformer = null;
        double? pendingStart = null;
        double? pendingStart00 = null; // INDEX 00 — фолбэк для листов без INDEX 01
        var trackOpen = false;

        void FlushPending()
        {
            pendingStart ??= pendingStart00;
            pendingStart00 = null;
            if (!trackOpen || currentFile is null || pendingStart is null) return;
            var title = (pendingPerformer, pendingTitle) switch
            {
                (not null, not null) => $"{pendingPerformer} — {pendingTitle}",
                (null, not null) => pendingTitle,
                _ => null,
            };
            result.Add(new Track(currentFile, title, pendingStart.Value));
            trackOpen = false;
            pendingTitle = null;
            pendingPerformer = null;
            pendingStart = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                FlushPending();
                var name = Unquote(line[5..], stripTrailingWord: true);
                var full = Path.GetFullPath(Path.Combine(baseDir, name));
                currentFile = File.Exists(full) ? full : null; // нет файла — треки этого FILE пропустятся
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
            {
                FlushPending();
                trackOpen = line.EndsWith("AUDIO", StringComparison.OrdinalIgnoreCase);
            }
            else if (trackOpen && line.StartsWith("TITLE ", StringComparison.OrdinalIgnoreCase))
            {
                pendingTitle = Unquote(line[6..]);
            }
            else if (trackOpen && line.StartsWith("PERFORMER ", StringComparison.OrdinalIgnoreCase))
            {
                pendingPerformer = Unquote(line[10..]);
            }
            else if (trackOpen && line.StartsWith("INDEX 01 ", StringComparison.OrdinalIgnoreCase))
            {
                pendingStart = ParseCueTime(line[9..].Trim());
            }
            else if (trackOpen && line.StartsWith("INDEX 00 ", StringComparison.OrdinalIgnoreCase))
            {
                pendingStart00 = ParseCueTime(line[9..].Trim());
            }
        }
        FlushPending();

        // Конец каждого трека = начало следующего В ТОМ ЖЕ файле; последний играет до конца файла.
        for (var i = 0; i < result.Count - 1; i++)
        {
            if (result[i].FilePath == result[i + 1].FilePath)
                result[i] = result[i] with { EndSeconds = result[i + 1].StartSeconds };
        }
        return result;
    }

    /// <summary>mm:ss:ff, где ff — кадры по 1/75 секунды. Отрицательные значения — мусор.</summary>
    private static double? ParseCueTime(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var m) ||
            !int.TryParse(parts[1], out var s) ||
            !int.TryParse(parts[2], out var f)) return null;
        if (m < 0 || s < 0 || f < 0) return null;
        return m * 60 + s + f / 75.0;
    }

    /// <summary>Достаёт значение из кавычек; stripTrailingWord — отрезать тип после значения (FILE ... WAVE).</summary>
    private static string Unquote(string value, bool stripTrailingWord = false)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            return close > 0 ? value[1..close] : value.Trim('"');
        }
        if (stripTrailingWord)
        {
            var lastWord = value.LastIndexOf(' ');
            if (lastWord > 0) value = value[..lastWord];
        }
        return value.Trim();
    }
}
