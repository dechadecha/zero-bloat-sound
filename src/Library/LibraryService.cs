using ZBS.Core.Playlist;

namespace ZBS.Library;

/// <summary>
/// Фасад медиатеки для UI: папки-источники, инкрементальный скан, поиск, счётчики.
/// Скан идёт в фоне (вызывающий запускает через Task.Run) и безопасен к повторному запуску.
/// CUE-альбомы хранятся посегментно (id = Track.ResumeKey), теги файла читаются один раз.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private const int BatchSize = 500;

    /// <summary>
    /// Версия логики чтения тегов. Меняй при любой правке TagReader (кодировки, нормализация):
    /// при несовпадении с сохранённой в базе скан ПРИНУДИТЕЛЬНО перечитывает все файлы,
    /// игнорируя mtime — иначе исправления парсинга не применятся к уже просканированному.
    /// </summary>
    private const string TagReaderVersion = "5";

    private readonly LibraryDb _db;
    private readonly object _scanLock = new();
    private bool _scanning;

    public LibraryService(string directory) => _db = new LibraryDb(directory);

    public IReadOnlyList<string> Folders => _db.GetFolders();
    public int TrackCount => _db.TrackCount();
    public bool IsScanning { get { lock (_scanLock) return _scanning; } }

    /// <summary>Последний скан пропустил чистку пропавших: какая-то папка-источник была недоступна.</summary>
    public bool LastScanSkippedCleanup { get; private set; }

    public void AddFolder(string path) => _db.AddFolder(path);
    public void RemoveFolder(string path) => _db.RemoveFolder(path);

    /// <summary>
    /// Полный проход по папкам: новые/изменённые файлы читаются (по mtime), пропавшие удаляются.
    /// Если папка-источник недоступна (выключенный внешний диск) — чистка ПРОПУСКАЕТСЯ,
    /// иначе один клик «пересканировать» без диска F: стёр бы рейтинги и счётчики навсегда.
    /// </summary>
    public void Scan(Action<int>? progress = null, CancellationToken ct = default)
    {
        lock (_scanLock)
        {
            if (_scanning) return;
            _scanning = true;
        }
        try
        {
            var folders = _db.GetFolders();
            var missingFolder = folders.Any(f => !Directory.Exists(f));
            // Логика парсинга тегов изменилась — перечитать всё, не доверяя mtime.
            var forceReread = _db.GetMeta("reader_ver") != TagReaderVersion;
            var knownMtimes = _db.GetAllMtimes();
            var fileTagsCache = new Dictionary<string, FileTags>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var batch = new List<TrackRow>(BatchSize);
            var processed = 0;

            foreach (var folder in folders)
            {
                foreach (var track in FolderScanner.Scan(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    var id = track.ResumeKey;
                    if (!seen.Add(id)) continue;

                    long mtime;
                    try { mtime = new FileInfo(track.FilePath).LastWriteTimeUtc.Ticks; }
                    catch (IOException) { continue; }

                    if (forceReread || !knownMtimes.TryGetValue(id, out var known) || known != mtime)
                    {
                        if (!fileTagsCache.TryGetValue(track.FilePath, out var tags))
                        {
                            tags = TagReader.Read(track.FilePath);
                            fileTagsCache[track.FilePath] = tags;
                        }
                        batch.Add(BuildRow(track, tags, mtime, folder));
                        if (batch.Count >= BatchSize)
                        {
                            _db.UpsertMany(batch);
                            batch.Clear();
                        }
                    }
                    if (++processed % 50 == 0)
                        progress?.Invoke(processed);
                }
            }
            _db.UpsertMany(batch);

            LastScanSkippedCleanup = missingFolder;
            if (!missingFolder)
                _db.RemoveMissing(seen);
            _db.SetMeta("reader_ver", TagReaderVersion); // теги перечитаны текущей логикой
            progress?.Invoke(processed);
        }
        finally
        {
            lock (_scanLock) _scanning = false;
        }
    }

    private static TrackRow BuildRow(Track track, FileTags tags, long mtime, string sourceRoot)
    {
        var isSegment = track.IsCueSegment;
        var duration = isSegment
            ? Math.Max(0, (track.EndSeconds ?? tags.DurationSeconds) - track.StartSeconds)
            : tags.DurationSeconds;
        // Достраиваем артиста/название/альбом из имени файла и структуры папок, когда теги пустые.
        var (artist, title, album) = MetadataResolver.Resolve(track.FilePath, sourceRoot, tags);
        // CUE-сегмент несёт собственный заголовок из листа — он приоритетнее выведенного.
        if (isSegment && track.Title is not null) title = track.Title;
        return new TrackRow(
            Id: track.ResumeKey,
            FilePath: track.FilePath,
            SegStart: track.StartSeconds,
            SegEnd: track.EndSeconds,
            Title: title,
            Artist: artist,
            Album: album ?? (isSegment ? Path.GetFileNameWithoutExtension(track.FilePath) : null),
            Genre: tags.Genre,
            Year: tags.Year,
            DurationSeconds: duration,
            GainDb: tags.ReplayGainTrackDb,
            Mtime: mtime);
    }

    public List<LibraryTrack> Search(string query, string? artist = null, string? genre = null, int limit = 500) =>
        _db.Search(query, artist, genre, limit);

    public IReadOnlyList<string> GetArtists() => _db.GetArtists();
    public IReadOnlyList<string> GetGenres() => _db.GetGenres();

    /// <summary>
    /// Дубликаты: одинаковые артист+название+длительность (±2 сек). Только показ —
    /// удаление файлов руками пользователя, мы ничего не сносим.
    /// </summary>
    public List<LibraryTrack> FindDuplicates()
    {
        var all = _db.Search("", limit: int.MaxValue);
        return all
            .Where(t => t.Title is not null)
            .GroupBy(t => (
                Artist: t.Artist?.ToLowerInvariant() ?? "",
                Title: t.Title!.ToLowerInvariant(),
                Bucket: Math.Round(t.DurationSeconds / 2)))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();
    }

    /// <summary>
    /// Сканер битых: пробуем открыть каждый файл декодером (prober приходит от аудио-бэкенда).
    /// Возвращает треки, чьи файлы пропали или не открываются.
    /// </summary>
    public List<LibraryTrack> FindBroken(Func<string, bool> prober, Action<int>? progress = null, CancellationToken ct = default)
    {
        var all = _db.Search("", limit: int.MaxValue);
        var broken = new List<LibraryTrack>();
        var checkedFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        foreach (var t in all)
        {
            ct.ThrowIfCancellationRequested();
            if (!checkedFiles.TryGetValue(t.FilePath, out var ok))
            {
                ok = File.Exists(t.FilePath) && prober(t.FilePath);
                checkedFiles[t.FilePath] = ok;
            }
            if (!ok) broken.Add(t);
            if (++processed % 100 == 0) progress?.Invoke(processed);
        }
        return broken;
    }

    /// <summary>RG-поправка для движка (GainProvider): у сегментов и файла она общая.</summary>
    public double? GetGainDb(string filePath) => _db.GetGainDbByFile(filePath);

    /// <summary>Счётчик прослушиваний: у CUE-сегмента — своя строка (id = ResumeKey трека).</summary>
    public void MarkPlayed(Track track) => _db.IncrementPlayCount(track.ResumeKey);

    public void SetRating(string id, int rating) => _db.SetRating(id, rating);

    /// <summary>
    /// Оценка трека из ПЛЕЙЛИСТА: если файла ещё нет в медиатеке (играли папку напрямую) —
    /// он добавляется в базу, чтобы звезда не улетала в пустоту.
    /// </summary>
    public void RateTrack(Track track, int rating)
    {
        var id = track.ResumeKey;
        if (!_db.HasTrack(id))
        {
            long mtime;
            try { mtime = new FileInfo(track.FilePath).LastWriteTimeUtc.Ticks; }
            catch (IOException) { return; }
            var tags = TagReader.Read(track.FilePath);
            var root = _db.GetFolders().FirstOrDefault(
                f => track.FilePath.StartsWith(f, StringComparison.OrdinalIgnoreCase)) ?? "";
            _db.UpsertMany(new[] { BuildRow(track, tags, mtime, root) });
        }
        _db.SetRating(id, rating);
    }

    /// <summary>Строка медиатеки → трек плейлиста (сегменты CUE восстанавливаются с границами).</summary>
    public static Track ToTrack(LibraryTrack t)
    {
        var title = (t.Artist, t.Title) switch
        {
            (not null, not null) => $"{t.Artist} — {t.Title}",
            (null, not null) => t.Title,
            _ => null,
        };
        return new Track(t.FilePath, title, t.SegStart, t.SegEnd);
    }

    public void Dispose() => _db.Dispose();
}
