using Microsoft.Data.Sqlite;

namespace ZBS.Library;

/// <summary>Строка медиатеки. Id: путь файла, для CUE-сегмента — «путь#старт» (совпадает с Track.ResumeKey).</summary>
public sealed record LibraryTrack(
    string Id,
    string FilePath,
    double SegStart,
    double? SegEnd,
    string? Title,
    string? Artist,
    string? Album,
    string? Genre,
    double DurationSeconds,
    double? GainDb,
    int Rating,
    int PlayCount)
{
    /// <summary>«★★★» для списка; пусто, если оценки нет.</summary>
    public string RatingStars => Rating > 0 ? new string('★', Rating) : "";

    /// <summary>«3:44» для колонки времени; пусто, если длительность неизвестна.</summary>
    public string DurationText
    {
        get
        {
            if (DurationSeconds <= 0 || double.IsNaN(DurationSeconds)) return "";
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
    }

    /// <summary>Что показать в списке: «Артист — Название», иначе что есть, иначе имя файла.</summary>
    public string Display => (Artist, Title) switch
    {
        (not null, not null) => $"{Artist} — {Title}",
        (null, not null) => Title,
        (not null, null) => Artist,
        _ => TagReader.CleanFileName(FilePath),
    };

    /// <summary>Двухстрочная строка списка (канон): жирное название…</summary>
    public string DisplayTitle
    {
        get
        {
            if (Title is not null) return Title;
            var clean = TagReader.CleanFileName(FilePath);
            var sep = clean.IndexOf(" - ", StringComparison.Ordinal);
            return sep > 0 ? clean[(sep + 3)..].Trim() : clean;
        }
    }

    /// <summary>…и исполнитель мелким muted под ним. Без тегов — левая часть «Артист - Название» из имени файла.</summary>
    public string DisplayArtist
    {
        get
        {
            if (Artist is not null) return Artist;
            if (Title is not null) return "";
            var clean = TagReader.CleanFileName(FilePath);
            var sep = clean.IndexOf(" - ", StringComparison.Ordinal);
            return sep > 0 ? clean[..sep].Trim() : "";
        }
    }
}

/// <summary>Что кладём в базу при скане (собирает LibraryService).</summary>
public sealed record TrackRow(
    string Id, string FilePath, double SegStart, double? SegEnd,
    string? Title, string? Artist, string? Album, string? Genre,
    int Year, double DurationSeconds, double? GainDb, long Mtime);

/// <summary>
/// SQLite-хранилище медиатеки: library.db рядом с настройками, WAL-режим.
/// Одно соединение на всех потребителей — каждый вход сериализуется локом.
/// Поиск — по search_text (ToLowerInvariant в C#): LIKE в SQLite регистронезависим
/// только для ASCII, а библиотека у нас русская.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    private const int SchemaVersion = 4;
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public LibraryDb(string directory)
    {
        Directory.CreateDirectory(directory);
        var dbPath = System.IO.Path.Combine(directory, "library.db");
        _conn = Open(dbPath);
    }

    /// <summary>
    /// Открывает базу-кэш. Если файл повреждён (это всего лишь производный кэш поверх
    /// музыкальных файлов — например, после жёсткого убийства процесса во время записи),
    /// сносит его и создаёт заново: пользователь просто пересканирует, ничего ценного не теряется.
    /// </summary>
    private SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        try
        {
            ExecOn(conn, "PRAGMA journal_mode=WAL;");
            var integrity = ScalarOn(conn, "PRAGMA quick_check;") as string;
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new SqliteException("integrity check failed", 11);
            Migrate(conn);
            ExecOn(conn, "CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);");
            return conn;
        }
        catch (SqliteException)
        {
            conn.Dispose();
            DeleteDbFiles(dbPath);
            var fresh = new SqliteConnection($"Data Source={dbPath}");
            fresh.Open();
            ExecOn(fresh, "PRAGMA journal_mode=WAL;");
            Migrate(fresh);
            ExecOn(fresh, "CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);");
            return fresh;
        }
    }

    private static void DeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch (IOException) { }
        }
    }

    private static void ExecOn(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? ScalarOn(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void Migrate(SqliteConnection conn)
    {
        // Выполняется в конструкторе (однопоточно), поэтому без лока и на переданном соединении.
        var version = Convert.ToInt32(ScalarOn(conn, "PRAGMA user_version;"));
        if (version == SchemaVersion) return;
        if (version is 2 or 3)
        {
            // v2/v3 → v4: та же схема, лишь пере-нормализуем жанры на месте
            // (рейтинги и счётчики сохраняются).
            NormalizeGenresInPlace(conn);
            ExecOn(conn, $"PRAGMA user_version={SchemaVersion};");
            return;
        }
        // Данных, достойных миграции, до v2 не существовало — пересоздаём начисто.
        ExecOn(conn, "DROP TABLE IF EXISTS tracks; DROP TABLE IF EXISTS folders;");
        ExecOn(conn, $"""
            CREATE TABLE tracks(
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                seg_start REAL DEFAULT 0,
                seg_end REAL,
                title TEXT, artist TEXT, album TEXT, genre TEXT,
                year INTEGER DEFAULT 0,
                duration REAL DEFAULT 0,
                gain_db REAL,
                rating INTEGER DEFAULT 0,
                play_count INTEGER DEFAULT 0,
                last_played TEXT,
                file_mtime INTEGER DEFAULT 0,
                search_text TEXT DEFAULT '',
                added TEXT DEFAULT (datetime('now'))
            );
            CREATE TABLE folders(path TEXT PRIMARY KEY);
            CREATE INDEX idx_tracks_file ON tracks(file_path);
            CREATE INDEX idx_tracks_search ON tracks(search_text);
            PRAGMA user_version={SchemaVersion};
            """);
    }

    private void NormalizeGenresInPlace(SqliteConnection conn)
    {
        var rows = new List<(string Id, string Genre)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, genre FROM tracks WHERE genre IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }
        using var tx = conn.BeginTransaction();
        foreach (var (id, genre) in rows)
        {
            var normalized = TagReader.NormalizeGenre(genre);
            if (normalized == genre) continue;
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE tracks SET genre=@g WHERE id=@id";
            upd.Parameters.AddWithValue("@g", (object?)normalized ?? DBNull.Value);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock)
            ExecInner(sql, args);
    }

    private void ExecInner(string sql, params (string, object?)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T Query<T>(Func<SqliteConnection, T> read)
    {
        lock (_lock)
            return read(_conn);
    }

    /// <summary>Пакет операций одной транзакцией — скан 30к файлов не делает 30к fsync-коммитов.</summary>
    public void RunInTransaction(Action<Action<string, (string, object?)[]>> body)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            body((sql, args) => ExecInner(sql, args));
            tx.Commit();
        }
    }

    // ---- Папки-источники ----

    public IReadOnlyList<string> GetFolders() => Query(conn =>
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM folders ORDER BY path";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    });

    public void AddFolder(string path) =>
        Exec("INSERT OR IGNORE INTO folders(path) VALUES(@p)", ("@p", path));

    /// <summary>
    /// Удаляет папку-источник и её треки. Сравнение префикса — в C#, а не LIKE:
    /// иначе D:\Music удалял бы и D:\Music2, а «_» в пути матчил бы что угодно.
    /// </summary>
    public void RemoveFolder(string path)
    {
        Exec("DELETE FROM folders WHERE path=@p", ("@p", path));
        var prefix = path.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var doomed = Query(conn =>
        {
            var list = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, file_path FROM tracks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (r.GetString(1).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(r.GetString(0));
            return list;
        });
        RunInTransaction(exec =>
        {
            foreach (var id in doomed)
                exec("DELETE FROM tracks WHERE id=@id", new[] { ("@id", (object?)id) });
        });
    }

    // ---- Треки ----

    public static string BuildSearchText(TrackRow row) =>
        string.Join(' ', new[] { row.Title, row.Artist, row.Album, row.FilePath }
            .Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();

    public void UpsertMany(IReadOnlyList<TrackRow> rows)
    {
        if (rows.Count == 0) return;
        RunInTransaction(exec =>
        {
            foreach (var row in rows)
                exec("""
                    INSERT INTO tracks(id,file_path,seg_start,seg_end,title,artist,album,genre,year,duration,gain_db,file_mtime,search_text)
                    VALUES(@id,@fp,@ss,@se,@t,@a,@al,@g,@y,@d,@rg,@m,@st)
                    ON CONFLICT(id) DO UPDATE SET
                        file_path=@fp, seg_start=@ss, seg_end=@se, title=@t, artist=@a, album=@al,
                        genre=@g, year=@y, duration=@d, gain_db=@rg, file_mtime=@m, search_text=@st
                    """, new[]
                    {
                        ("@id", (object?)row.Id), ("@fp", (object?)row.FilePath),
                        ("@ss", (object?)row.SegStart), ("@se", (object?)row.SegEnd),
                        ("@t", (object?)row.Title), ("@a", (object?)row.Artist),
                        ("@al", (object?)row.Album), ("@g", (object?)row.Genre),
                        ("@y", (object?)row.Year), ("@d", (object?)row.DurationSeconds),
                        ("@rg", (object?)row.GainDb), ("@m", (object?)row.Mtime),
                        ("@st", (object?)BuildSearchText(row)),
                    });
        });
    }

    /// <summary>Все известные mtime одним запросом — скан не делает 30к раунд-трипов.</summary>
    public Dictionary<string, long> GetAllMtimes() => Query(conn =>
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, file_mtime FROM tracks";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = r.GetInt64(1);
        return result;
    });

    public void RemoveMissing(IReadOnlySet<string> existingIds)
    {
        var doomed = Query(conn =>
        {
            var list = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM tracks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                if (!existingIds.Contains(id)) list.Add(id);
            }
            return list;
        });
        RunInTransaction(exec =>
        {
            foreach (var id in doomed)
                exec("DELETE FROM tracks WHERE id=@id", new[] { ("@id", (object?)id) });
        });
    }

    public IReadOnlyList<string> GetArtists() => Query(conn =>
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        // GROUP BY NOCASE: «Dino Mc47» и «Dino MC47» — один артист (представитель — любой из вариантов).
        cmd.CommandText = "SELECT artist FROM tracks WHERE artist IS NOT NULL GROUP BY artist COLLATE NOCASE ORDER BY artist COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    });

    public IReadOnlyList<string> GetGenres() => Query(conn =>
    {
        var result = new List<string>();
        using var cmd = conn.CreateCommand();
        // NOCASE-группировка симметрично артистам: «Rock»/«rock» — один пункт дропдауна.
        cmd.CommandText = "SELECT genre FROM tracks WHERE genre IS NOT NULL GROUP BY genre COLLATE NOCASE ORDER BY genre COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    });

    public List<LibraryTrack> Search(string query, string? artist = null, string? genre = null, int limit = 500)
    {
        // Запрос в lowercase + экранирование LIKE-спецсимволов: «100%» ищет «100%», а не всё подряд.
        var escaped = query.ToLowerInvariant()
            .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id,file_path,seg_start,seg_end,title,artist,album,genre,duration,gain_db,rating,play_count
                FROM tracks
                WHERE (@q = '' OR search_text LIKE @like ESCAPE '\')
                  AND (@artist IS NULL OR artist = @artist COLLATE NOCASE)
                  AND (@genre IS NULL OR genre = @genre COLLATE NOCASE)
                ORDER BY artist, album, seg_start, file_path LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@q", query);
            cmd.Parameters.AddWithValue("@like", $"%{escaped}%");
            cmd.Parameters.AddWithValue("@artist", (object?)artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@genre", (object?)genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lim", limit);
            var result = new List<LibraryTrack>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new LibraryTrack(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetDouble(2),
                    r.IsDBNull(3) ? null : r.GetDouble(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetDouble(8),
                    r.IsDBNull(9) ? null : r.GetDouble(9),
                    r.GetInt32(10),
                    r.GetInt32(11)));
            }
            return result;
        });
    }

    public double? GetGainDbByFile(string filePath) => Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT gain_db FROM tracks WHERE file_path=@p LIMIT 1";
        cmd.Parameters.AddWithValue("@p", filePath);
        var v = cmd.ExecuteScalar();
        return v is double d ? (double?)d : null;
    });

    public bool HasTrack(string id) => Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tracks WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is not null;
    });

    public void IncrementPlayCount(string id) =>
        Exec("UPDATE tracks SET play_count=play_count+1, last_played=datetime('now') WHERE id=@id", ("@id", id));

    public void SetRating(string id, int rating) =>
        Exec("UPDATE tracks SET rating=@r WHERE id=@id", ("@r", Math.Clamp(rating, 0, 5)), ("@id", id));

    public string? GetMeta(string key) => Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k=@k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    });

    public void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v",
            ("@k", key), ("@v", value));

    public int TrackCount() => Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    public void Dispose()
    {
        lock (_lock)
            _conn.Dispose();
    }
}
