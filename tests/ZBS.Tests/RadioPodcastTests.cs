using ZBS.Core.Podcasts;
using ZBS.Core.Radio;
using Xunit;

namespace ZBS.Tests;

public class RadioPodcastTests
{
    [Fact]
    public void Fm_Nearest_Snaps_Within_Range()
    {
        Assert.Equal("Европа Плюс", FmStations.Nearest(106.2)!.Name);
        Assert.Equal("Европа Плюс", FmStations.Nearest(106.1)!.Name);
        Assert.Null(FmStations.Nearest(87.9)); // между станциями — шипение
    }

    [Fact]
    public void Favorites_Roundtrip_And_Toggle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zbs-radio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = new RadioStation("uuid1", "Тест", "http://x/stream", "Russia", "rock", 128, "MP3");
            var fav = new RadioFavorites(dir);
            fav.Toggle(s);
            Assert.True(fav.Contains(s));
            var reloaded = new RadioFavorites(dir);
            Assert.True(reloaded.Contains(s));
            reloaded.Toggle(s);
            Assert.False(reloaded.Contains(s));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PodcastStore_Positions_And_Finished()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zbs-pod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ep = new PodcastEpisode("http://feed", "guid1", "Эпизод", "http://a.mp3", null, 3600, null);
            var store = new PodcastStore(dir);
            store.SavePosition(ep, 500, 3600);
            Assert.Equal(500, new PodcastStore(dir).PositionOf(ep));
            store.SavePosition(ep, 3590, 3600); // доиграл — позиция сброшена, помечен прослушанным
            var reloaded = new PodcastStore(dir);
            Assert.Equal(0, reloaded.PositionOf(ep));
            Assert.True(reloaded.IsFinished(ep));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PodcastStore_Short_Position_Not_Saved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zbs-pod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ep = new PodcastEpisode("http://feed", "g2", "Эпизод", "http://a.mp3", null, 3600, null);
            var store = new PodcastStore(dir);
            store.SavePosition(ep, 5, 3600); // первые секунды не запоминаем
            Assert.Equal(0, new PodcastStore(dir).PositionOf(ep));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
