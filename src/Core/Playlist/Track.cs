namespace ZBS.Core.Playlist;

/// <summary>
/// Единица плейлиста. Обычный файл — только путь; CUE-трек — путь к аудиофайлу
/// плюс границы сегмента и заголовок из листа. Полные теги приедут с медиатекой (M2).
/// </summary>
public sealed record Track(
    string FilePath,
    string? Title = null,
    double StartSeconds = 0,
    double? EndSeconds = null)
{
    public bool IsCueSegment => EndSeconds.HasValue || StartSeconds > 0;

    public string DisplayName => Title ?? Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>Ключ для запоминания позиции (резюме): у CUE-сегментов свой на каждый трек.</summary>
    public string ResumeKey => IsCueSegment ? $"{FilePath}#{StartSeconds:F0}" : FilePath;
}
