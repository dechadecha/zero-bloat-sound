using ZBS.Core.Playback;
using Xunit;

namespace ZBS.Tests;

public sealed class ResumeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-resume-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Roundtrip_persists_positions()
    {
        var store = new ResumeStore(_dir);
        store.Set("book.mp3", 1234.5);
        store.SaveIfDirty();

        var reloaded = new ResumeStore(_dir);
        Assert.True(reloaded.TryGet("book.mp3", out var seconds));
        Assert.Equal(1234.5, seconds, precision: 3);
    }

    [Fact]
    public void Early_positions_are_not_remembered()
    {
        var store = new ResumeStore(_dir);
        store.Set("book.mp3", 5);
        Assert.False(store.TryGet("book.mp3", out _));
    }

    [Fact]
    public void Clear_removes_entry()
    {
        var store = new ResumeStore(_dir);
        store.Set("book.mp3", 600);
        store.Clear("book.mp3");
        Assert.False(store.TryGet("book.mp3", out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
