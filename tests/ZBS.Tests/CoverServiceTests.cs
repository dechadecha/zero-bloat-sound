using ZBS.Core.Playlist;
using ZBS.Library;
using Xunit;

namespace ZBS.Tests;

public sealed class CoverServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-cov-" + Guid.NewGuid().ToString("N"));

    public CoverServiceTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Folder_image_is_used_when_no_embedded_cover()
    {
        var music = Path.Combine(_dir, "album");
        Directory.CreateDirectory(music);
        var trackPath = Path.Combine(music, "song.mp3");
        File.WriteAllBytes(trackPath, new byte[] { 0 }); // тегов нет — embedded не сработает
        var art = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        File.WriteAllBytes(Path.Combine(music, "cover.jpg"), art);

        var covers = new CoverService(_dir) { AutoFetch = false };
        var result = await covers.GetCoverAsync(new Track(trackPath), null, null);

        Assert.Equal(art, result);
    }

    [Fact]
    public async Task Returns_null_when_nothing_local_and_autofetch_off()
    {
        var trackPath = Path.Combine(_dir, "lonely.mp3");
        File.WriteAllBytes(trackPath, new byte[] { 0 });

        var covers = new CoverService(_dir) { AutoFetch = false };
        Assert.Null(await covers.GetCoverAsync(new Track(trackPath), "Artist", "Album"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
