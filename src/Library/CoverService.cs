using System.Security.Cryptography;
using System.Text;
using ZBS.Core.Playlist;

namespace ZBS.Library;

/// <summary>
/// Обложки: встроенная в теги → картинка в папке (folder/cover/front) → автодотяжка
/// из Cover Art Archive (открытая база, без ключей; отключаемо). Скачанное кэшируется
/// на диск и НИКОГДА не пишется в теги и не перезаписывает пользовательские файлы.
/// </summary>
public sealed class CoverService
{
    private static readonly string[] FolderNames =
        { "folder.jpg", "folder.png", "cover.jpg", "cover.png", "front.jpg", "front.png" };

    private readonly string _cacheDir;
    private readonly CoverFetcher _fetcher = new();

    /// <summary>Тумблер автодотяжки из интернета (настройка CoverAutoFetch).</summary>
    public bool AutoFetch { get; set; } = true;

    public CoverService(string settingsDirectory)
    {
        _cacheDir = Path.Combine(settingsDirectory, "covers");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Обложка трека (байты картинки) или null. Сеть — только если локально пусто и AutoFetch включён.</summary>
    public async Task<byte[]?> GetCoverAsync(Track track, string? artist, string? album, CancellationToken ct = default)
    {
        var local = ReadEmbedded(track.FilePath) ?? ReadFolderImage(track.FilePath);
        if (local is not null) return local;
        if (!AutoFetch || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return null;
        return await GetOrFetchCachedAsync(artist!, album!, ct);
    }

    private static byte[]? ReadEmbedded(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var pic = file.Tag.Pictures.FirstOrDefault();
            return pic?.Data.Count > 0 ? pic.Data.Data : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? ReadFolderImage(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null) return null;
            foreach (var name in FolderNames)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    // In-flight дедуп: играющий трек и открытая деталь того же альбома не должны
    // бежать в Cover Art Archive параллельно и толкаться за один файл кэша.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();

    private Task<byte[]?> GetOrFetchCachedAsync(string artist, string album, CancellationToken ct)
    {
        var key = CacheKey(artist, album);
        var task = _inflight.GetOrAdd(key, _ => FetchCoreAsync(key, artist, album, ct));
        if (task.IsCompleted) _inflight.TryRemove(key, out Task<byte[]?>? _);
        else _ = task.ContinueWith(_ => _inflight.TryRemove(key, out Task<byte[]?>? _), TaskScheduler.Default);
        return task;
    }

    private async Task<byte[]?> FetchCoreAsync(string key, string artist, string album, CancellationToken ct)
    {
        var hit = Path.Combine(_cacheDir, key + ".jpg");
        var miss = Path.Combine(_cacheDir, key + ".none"); // негативный кэш: не долбим API впустую
        try
        {
            if (File.Exists(hit)) return await File.ReadAllBytesAsync(hit, ct);
            if (File.Exists(miss)) return null;

            var (bytes, definitive) = await _fetcher.FetchFrontCoverAsync(artist, album, ct);
            if (bytes is null)
            {
                // .none — только на честный отказ базы; сетевой сбой не должен
                // навсегда лишать альбом автодотяжки.
                if (definitive)
                    await File.WriteAllBytesAsync(miss, Array.Empty<byte>(), ct);
                return null;
            }
            await File.WriteAllBytesAsync(hit, bytes, ct);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CacheKey(string artist, string album)
    {
        var raw = $"{artist.ToLowerInvariant()}|{album.ToLowerInvariant()}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }
}
