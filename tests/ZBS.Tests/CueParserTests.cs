using ZBS.Core.Playlist;
using Xunit;

namespace ZBS.Tests;

public sealed class CueParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-cue-" + Guid.NewGuid().ToString("N"));

    public CueParserTests() => Directory.CreateDirectory(_dir);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Parses_tracks_with_titles_and_segment_bounds()
    {
        Write("album.flac", "");
        var cue = Write("album.cue", """
            PERFORMER "Someone"
            TITLE "Live Album"
            FILE "album.flac" WAVE
              TRACK 01 AUDIO
                TITLE "Intro"
                PERFORMER "Someone"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Main Song"
                INDEX 01 03:30:00
              TRACK 03 AUDIO
                TITLE "Outro"
                INDEX 01 07:45:37
            """);

        var tracks = CueParser.Parse(cue);

        Assert.Equal(3, tracks.Count);
        Assert.Equal("Someone — Intro", tracks[0].Title);
        Assert.Equal(0, tracks[0].StartSeconds);
        Assert.Equal(210, tracks[0].EndSeconds!.Value, precision: 3);
        Assert.Equal(210, tracks[1].StartSeconds, precision: 3);
        Assert.Equal(465 + 37 / 75.0, tracks[1].EndSeconds!.Value, precision: 3);
        Assert.Null(tracks[2].EndSeconds); // последний — до конца файла
        Assert.All(tracks, t => Assert.EndsWith("album.flac", t.FilePath));
    }

    [Fact]
    public void Missing_audio_file_yields_no_tracks()
    {
        var cue = Write("ghost.cue", """
            FILE "nope.flac" WAVE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            """);
        Assert.Empty(CueParser.Parse(cue));
    }

    [Fact]
    public void Scanner_replaces_covered_audio_with_cue_tracks()
    {
        Write("album.flac", "");
        Write("album.cue", """
            FILE "album.flac" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 01:00:00
            """);
        Write("loose.mp3", "");

        var tracks = FolderScanner.Scan(_dir);

        // 2 cue-трека + отдельный mp3; сам album.flac НЕ задублирован сырым файлом.
        Assert.Equal(3, tracks.Count);
        Assert.Equal(new[] { "One", "Two" }, tracks.Take(2).Select(t => t.Title).ToArray());
        Assert.EndsWith("loose.mp3", tracks[2].FilePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
