using System.Text.RegularExpressions;

namespace ZBS.Library;

/// <summary>
/// Достраивает артиста/название/альбом при сканировании, когда теги неполные.
/// Приоритет: тег → разбор «Артист - Название» из имени файла → структура папок.
/// Ничего не пишет в файлы — только то, что попадёт в медиатеку и отображение.
/// </summary>
public static class MetadataResolver
{
    // Пробельные разделители — самые надёжные («Артист - Название»). Порядок: длинные тире раньше.
    private static readonly string[] SpacedSeps = { " — ", " – ", " - " };
    private static readonly Regex SepRun = new(@"^[\s\-–—_.:]+", RegexOptions.Compiled);

    // Заглушки-альбомы: в тег не берём (папка ничего не говорит о конкретном альбоме).
    private static readonly HashSet<string> AlbumPlaceholders = new(StringComparer.OrdinalIgnoreCase)
        { "без альбома", "unknown", "unknown album", "разное", "misc", "singles", "синглы" };

    public static (string? Artist, string? Title, string? Album) Resolve(
        string filePath, string? sourceRoot, FileTags tags)
    {
        var artist = Nz(tags.Artist);
        var title = Nz(tags.Title);
        var album = Nz(tags.Album);
        var fileName = TagReader.CleanFileName(filePath);

        if (artist is null)
        {
            var (pArtist, pTitle) = SplitArtistTitle(fileName);
            if (pArtist is not null)
            {
                artist = pArtist;
                title ??= pTitle;
            }
            else
            {
                artist = FolderArtist(filePath, sourceRoot);
                title ??= fileName;
            }
        }
        else if (title is null)
        {
            // Тег артиста есть, названия нет: если имя файла «Артист - Название» и артист совпал —
            // берём правую часть; иначе — очищенное имя файла целиком.
            var (pArtist, pTitle) = SplitArtistTitle(fileName);
            title = pArtist is not null && SameArtist(pArtist, artist) ? pTitle : fileName;
        }
        else
        {
            // И тег артиста, и название есть: срезаем дублирующий префикс «Артист - » из названия.
            title = StripArtistPrefix(title, artist) ?? title;
        }

        album ??= FolderAlbum(filePath, sourceRoot, artist);
        return (artist, title, album);
    }

    /// <summary>Разбить «Артист - Название» / «Артист-Название». null-артист — разделитель не найден/ненадёжен.</summary>
    public static (string? Artist, string? Title) SplitArtistTitle(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);

        foreach (var sep in SpacedSeps)
        {
            var i = name.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0)
            {
                var left = name[..i].Trim();
                var right = name[(i + sep.Length)..].Trim();
                if (LooksLikeArtist(left) && right.Length > 0)
                    return (left, right);
            }
        }

        // Голый дефис без пробелов («Nik CHernikov-Frendzona») — только если слева есть пробел:
        // так ловим имя-с-пробелом и не рвём «Rock-n-Roll»/«well-known».
        var h = name.IndexOf('-');
        if (h > 0 && h < name.Length - 1)
        {
            var left = name[..h].Trim();
            var right = name[(h + 1)..].Trim();
            if (left.Contains(' ') && LooksLikeArtist(left) && right.Length > 0)
                return (left, right);
        }
        return (null, null);
    }

    // Похоже на имя артиста: 2..60 символов, есть буква, не одни цифры (не «01», не номер трека).
    private static bool LooksLikeArtist(string s) =>
        s.Length is >= 2 and <= 60 && s.Any(char.IsLetter) && !s.All(c => char.IsDigit(c) || c == '.');

    private static string? StripArtistPrefix(string title, string artist)
    {
        var nt = Norm(title);
        var na = Norm(artist);
        if (na.Length < 2 || !nt.StartsWith(na, StringComparison.Ordinal) || nt.Length <= na.Length)
            return null;
        var rest = title[Math.Min(artist.Length, title.Length)..];
        var m = SepRun.Match(rest);
        if (!m.Success || m.Length == 0) return null; // «Потапа» после «Потап» без разделителя — не префикс
        var stripped = rest[m.Length..].Trim();
        return stripped.Length > 0 ? stripped : null;
    }

    /// <summary>Верхняя папка под корнем-источником — обычно имя артиста.</summary>
    private static string? FolderArtist(string filePath, string? sourceRoot)
    {
        var rel = RelativeParts(filePath, sourceRoot);
        return rel is { Length: >= 2 } ? rel[0] : null; // [артист, …, файл]
    }

    /// <summary>Непосредственная папка трека — кандидат в альбом (кроме заглушек и «== артист»).</summary>
    private static string? FolderAlbum(string filePath, string? sourceRoot, string? artist)
    {
        var rel = RelativeParts(filePath, sourceRoot);
        if (rel is not { Length: >= 3 }) return null; // нужен уровень альбома: [артист, альбом, …, файл]
        var album = rel[^2];
        if (AlbumPlaceholders.Contains(album)) return null;
        if (artist is not null && SameArtist(album, artist)) return null;
        return album;
    }

    private static string[]? RelativeParts(string filePath, string? sourceRoot)
    {
        if (string.IsNullOrEmpty(sourceRoot)) return null;
        var p = filePath.Replace('/', '\\');
        var root = sourceRoot.Replace('/', '\\').TrimEnd('\\');
        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return p[root.Length..].Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool SameArtist(string a, string b) =>
        Norm(a).Equals(Norm(b), StringComparison.Ordinal);

    private static string Norm(string s) =>
        Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
