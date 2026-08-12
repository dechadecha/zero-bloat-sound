namespace ZBS.Core.Video;

/// <summary>Видео-форматы для общего плейлиста (M4). Играются через libmpv, аудио — как обычно.</summary>
public static class VideoFiles
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".wmv", ".flv", ".ts", ".mpg", ".mpeg",
    };

    public static bool IsVideo(string filePath) => Extensions.Contains(Path.GetExtension(filePath));
}
