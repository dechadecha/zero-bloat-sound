namespace ZBS.Core.Playlist;

/// <summary>
/// «Кинул папку — это и есть плейлист»: рекурсивный сбор поддерживаемых файлов.
/// Порядок: сначала файлы текущей папки (по имени), затем подпапки (по имени) — как на диске.
/// CUE-листы разворачиваются в треки-сегменты, их аудиофайлы не дублируются.
/// </summary>
public static class FolderScanner
{
    /// <summary>Расширения, которые умеет играть текущий бэкенд (BASS + плагины).</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".ogg", ".wav", ".aiff", ".aif", ".flac",
        ".opus", ".aac", ".m4a",
        ".mod", ".xm", ".it", ".s3m", ".mtm", ".umx",
    };

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath)) ||
        Video.VideoFiles.IsVideo(filePath); // M4: видео живёт в общем плейлисте

    /// <summary>
    /// Собирает единый плейлист из набора путей (файлы и папки вперемешку, порядок сохраняется).
    /// Именно так «кинул несколько файлов/папок» становится ОДНИМ плейлистом, а не последним элементом.
    /// </summary>
    public static List<Track> CollectFromPaths(IEnumerable<string> paths)
    {
        var result = new List<Track>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                ScanInto(p, result);
            else if (File.Exists(p) && string.Equals(Path.GetExtension(p), ".cue", StringComparison.OrdinalIgnoreCase))
                result.AddRange(CueParser.Parse(p));
            else if (File.Exists(p) && PlaylistIO.IsPlaylist(p))
                result.AddRange(PlaylistIO.Load(p));
            else if (File.Exists(p) && IsSupported(p))
                result.Add(new Track(p));
        }
        return result;
    }

    /// <summary>Собирает треки из папки рекурсивно. Недоступные подпапки молча пропускает.</summary>
    public static List<Track> Scan(string folderPath)
    {
        var result = new List<Track>();
        ScanInto(folderPath, result);
        return result;
    }

    private static void ScanInto(string folder, List<Track> into)
    {
        string[] files;
        string[] dirs;
        try
        {
            files = Directory.GetFiles(folder);
            dirs = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        // Первый проход: разворачиваем CUE и запоминаем, какие аудиофайлы они покрывают.
        var cueTracks = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!string.Equals(Path.GetExtension(f), ".cue", StringComparison.OrdinalIgnoreCase)) continue;
            var tracks = CueParser.Parse(f);
            if (tracks.Count == 0) continue;
            cueTracks[f] = tracks;
            foreach (var t in tracks)
                covered.Add(Path.GetFullPath(t.FilePath));
        }

        foreach (var f in files)
        {
            if (cueTracks.TryGetValue(f, out var tracks))
                into.AddRange(tracks);
            else if (IsSupported(f) && !covered.Contains(Path.GetFullPath(f)))
                into.Add(new Track(f));
        }

        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
            ScanInto(d, into);
    }
}
