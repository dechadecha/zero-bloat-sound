using System.Text;
using System.Xml.Linq;

namespace ZBS.Core.Playlist;

/// <summary>
/// Импорт/экспорт файлов плейлистов. Читаем: m3u/m3u8, pls, xspf, asx, wpl.
/// Пишем: m3u8 (унив. стандарт) и pls. Пути в файлах — относительные к плейлисту или абсолютные.
/// foobar .fpl не поддержан: закрытый бинарный формат.
/// </summary>
public static class PlaylistIO
{
    public static readonly IReadOnlySet<string> PlaylistExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls", ".xspf", ".asx", ".wpl",
    };

    public static bool IsPlaylist(string path) => PlaylistExtensions.Contains(Path.GetExtension(path));

    public static List<Track> Load(string path) => Load(path, depth: 0);

    private static List<Track> Load(string path, int depth)
    {
        try
        {
            var baseDir = Path.GetDirectoryName(path) ?? "";
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".m3u" or ".m3u8" => LoadM3u(path, baseDir, depth),
                ".pls" => LoadPls(path, baseDir, depth),
                ".xspf" => LoadXspf(path, baseDir, depth),
                ".asx" => LoadXmlRefs(path, baseDir, "ref", "href", depth),
                ".wpl" => LoadXmlRefs(path, baseDir, "media", "src", depth),
                _ => new List<Track>(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new List<Track>();
        }
    }

    private static List<Track> LoadM3u(string path, string baseDir, int depth)
    {
        var result = new List<Track>();
        string? pendingTitle = null;
        foreach (var raw in TextFileReader.ReadAllLinesSmart(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                pendingTitle = comma > 0 && comma < line.Length - 1 ? line[(comma + 1)..].Trim() : null;
            }
            else if (!line.StartsWith('#'))
            {
                AddIfExists(result, line, baseDir, pendingTitle, depth);
                pendingTitle = null;
            }
        }
        return result;
    }

    private static List<Track> LoadPls(string path, string baseDir, int depth)
    {
        var files = new SortedDictionary<int, string>();
        var titles = new Dictionary<int, string>();
        foreach (var raw in TextFileReader.ReadAllLinesSmart(path))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[4..], out var fn))
                files[fn] = value;
            else if (key.StartsWith("Title", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[5..], out var tn))
                titles[tn] = value;
        }
        var result = new List<Track>();
        foreach (var (n, file) in files)
            AddIfExists(result, file, baseDir, titles.GetValueOrDefault(n), depth);
        return result;
    }

    private static List<Track> LoadXspf(string path, string baseDir, int depth)
    {
        var result = new List<Track>();
        var doc = XDocument.Load(path);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        foreach (var trackEl in doc.Descendants(ns + "track"))
        {
            var location = trackEl.Element(ns + "location")?.Value.Trim();
            if (string.IsNullOrEmpty(location)) continue;
            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile)
                location = uri.LocalPath;
            else
            {
                // XSPF location — URI: относительные пути percent-encoded («My%20Song.mp3», кириллица).
                var decoded = Uri.UnescapeDataString(location);
                if (decoded != location && !File.Exists(Path.Combine(baseDir, location)))
                    location = decoded;
            }
            var title = trackEl.Element(ns + "title")?.Value.Trim();
            AddIfExists(result, location, baseDir, string.IsNullOrEmpty(title) ? null : title, depth);
        }
        return result;
    }

    private static List<Track> LoadXmlRefs(string path, string baseDir, string element, string attribute, int depth)
    {
        var result = new List<Track>();
        var doc = XDocument.Load(path);
        foreach (var el in doc.Descendants())
        {
            if (!string.Equals(el.Name.LocalName, element, StringComparison.OrdinalIgnoreCase)) continue;
            var src = el.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, attribute, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
            if (!string.IsNullOrEmpty(src))
                AddIfExists(result, src, baseDir, null, depth);
        }
        return result;
    }

    private static void AddIfExists(List<Track> into, string location, string baseDir, string? title, int depth)
    {
        if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return; // стримы приедут с M5 — пока только локальные файлы
        try
        {
            var full = Path.IsPathRooted(location) ? location : Path.GetFullPath(Path.Combine(baseDir, location));
            if (!File.Exists(full)) return;

            // Вложенный плейлист/cue-лист разворачиваем (на один уровень, без самоссылок),
            // мусор (jpg/txt/сам плейлист) треком не становится.
            if (IsPlaylist(full))
            {
                if (depth < 1)
                    into.AddRange(Load(full, depth + 1));
                return;
            }
            if (string.Equals(Path.GetExtension(full), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                into.AddRange(CueParser.Parse(full));
                return;
            }
            if (FolderScanner.IsSupported(full))
                into.Add(new Track(full, title));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Кривой путь в чужом плейлисте — пропускаем строку, не файл целиком.
        }
    }

    /// <summary>
    /// Сохраняет m3u8 (UTF-8, #EXTM3U + #EXTINF с названиями).
    /// CUE-сегменты в m3u непредставимы — пишем их общий файл один раз, не N дублей.
    /// </summary>
    public static void SaveM3u8(string path, IEnumerable<Track> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        var writtenCueFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (t.IsCueSegment)
            {
                if (!writtenCueFiles.Add(t.FilePath)) continue;
                sb.AppendLine($"#EXTINF:-1,{Path.GetFileNameWithoutExtension(t.FilePath)}");
            }
            else
            {
                sb.AppendLine($"#EXTINF:-1,{t.DisplayName}");
            }
            sb.AppendLine(t.FilePath);
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
