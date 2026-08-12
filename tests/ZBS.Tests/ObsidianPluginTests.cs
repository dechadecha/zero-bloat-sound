using System.IO;
using ZBS.Plugins.Api;
using ZBS.Plugins.Obsidian;
using Xunit;

namespace ZBS.Tests;

public class ObsidianPluginTests
{
    // Хост, умеющий поднять событие трека (для end-to-end проверки плагина).
    private sealed class RaisableHost : IPluginHost
    {
        public void Log(string message) { }
        public string HostVersion => "test";
        public PluginTrackInfo? CurrentTrack { get; private set; }
        public event System.Action<PluginTrackInfo?>? TrackChanged;
        public event System.Action<bool>? PlayingChanged;
        public void Fire(PluginTrackInfo? t) { CurrentTrack = t; TrackChanged?.Invoke(t); }
        public void FirePlaying(bool p) => PlayingChanged?.Invoke(p);
    }

    [Fact]
    public void Writes_listened_track_to_note_and_dedupes()
    {
        var note = Path.Combine(Path.GetTempPath(), $"zbs-obsidian-{System.Guid.NewGuid():N}.md");
        try
        {
            var plugin = new ObsidianPlugin(new ObsidianConfig
            {
                Note = note, Dedupe = true, Format = "- {artist} — {title}"
            });
            var host = new RaisableHost();
            plugin.OnLoad(host);

            host.Fire(new PluginTrackInfo("Химера", "Ария", "Химера", 240, @"C:\m\a.mp3"));
            host.Fire(new PluginTrackInfo("Химера", "Ария", "Химера", 240, @"C:\m\a.mp3")); // тот же — дедуп
            host.Fire(new PluginTrackInfo("Серебро", "Би-2", null, 200, @"C:\m\b.mp3"));
            host.Fire(null); // стоп — не пишем

            var lines = File.ReadAllLines(note);
            Assert.Equal(2, lines.Length); // дубль и стоп отфильтрованы
            Assert.Equal("- Ария — Химера", lines[0]);
            Assert.Equal("- Би-2 — Серебро", lines[1]);

            plugin.OnUnload(); // отписка — дальше событие не пишет
            host.Fire(new PluginTrackInfo("X", "Y", null, 10, @"C:\m\c.mp3"));
            Assert.Equal(2, File.ReadAllLines(note).Length);
        }
        finally
        {
            if (File.Exists(note)) File.Delete(note);
        }
    }
}
