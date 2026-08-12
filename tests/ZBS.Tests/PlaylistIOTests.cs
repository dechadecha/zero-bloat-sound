using ZBS.Core.Playlist;
using Xunit;

namespace ZBS.Tests;

public sealed class PlaylistIOTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-pl-" + Guid.NewGuid().ToString("N"));

    public PlaylistIOTests() => Directory.CreateDirectory(_dir);

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, Array.Empty<byte>());
        return p;
    }

    [Fact]
    public void M3u8_roundtrip_preserves_order_and_titles()
    {
        var a = Touch("a.mp3");
        var b = Touch("b.flac");
        var listPath = Path.Combine(_dir, "list.m3u8");
        PlaylistIO.SaveM3u8(listPath, new[] { new Track(a, "Первый"), new Track(b) });

        var loaded = PlaylistIO.Load(listPath);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Первый", loaded[0].Title);
        Assert.Equal(a, loaded[0].FilePath);
        Assert.Equal(b, loaded[1].FilePath);
    }

    [Fact]
    public void M3u_relative_paths_resolve_against_playlist_dir()
    {
        Touch("song.mp3");
        var listPath = Path.Combine(_dir, "rel.m3u");
        File.WriteAllText(listPath, "#EXTM3U\nsong.mp3\nmissing.mp3\n");

        var loaded = PlaylistIO.Load(listPath);

        Assert.Single(loaded);
        Assert.EndsWith("song.mp3", loaded[0].FilePath);
    }

    [Fact]
    public void Pls_is_loaded_with_titles_in_numeric_order()
    {
        var a = Touch("one.mp3");
        var b = Touch("two.mp3");
        var listPath = Path.Combine(_dir, "list.pls");
        File.WriteAllText(listPath, $"""
            [playlist]
            File2={b}
            Title2=Second
            File1={a}
            Title1=First
            NumberOfEntries=2
            """);

        var loaded = PlaylistIO.Load(listPath);

        Assert.Equal(new[] { "First", "Second" }, loaded.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Xspf_reads_file_uris_and_titles()
    {
        var a = Touch("x.mp3");
        var uri = new Uri(a).AbsoluteUri;
        var listPath = Path.Combine(_dir, "list.xspf");
        File.WriteAllText(listPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <playlist version="1" xmlns="http://xspf.org/ns/0/">
              <trackList>
                <track><location>{uri}</location><title>From XSPF</title></track>
              </trackList>
            </playlist>
            """);

        var loaded = PlaylistIO.Load(listPath);

        Assert.Single(loaded);
        Assert.Equal("From XSPF", loaded[0].Title);
    }

    [Fact]
    public void Scanner_expands_playlist_files_from_paths()
    {
        var a = Touch("a.mp3");
        var listPath = Path.Combine(_dir, "list.m3u8");
        PlaylistIO.SaveM3u8(listPath, new[] { new Track(a) });

        var tracks = FolderScanner.CollectFromPaths(new[] { listPath });

        Assert.Single(tracks);
        Assert.Equal(a, tracks[0].FilePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
