using ZBS.Core.Settings;
using Xunit;

namespace ZBS.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zbs-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Roundtrip_preserves_values()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { Volume = 0.42, Language = "ru", LastFolder = "D:\\Music" });

        var loaded = new SettingsStore(_dir).Load();

        Assert.Equal(0.42, loaded.Volume, precision: 5);
        Assert.Equal("ru", loaded.Language);
        Assert.Equal("D:\\Music", loaded.LastFolder);
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ this is not json");

        var loaded = new SettingsStore(_dir).Load();

        Assert.Equal(0.8, loaded.Volume, precision: 5);
        Assert.Equal("auto", loaded.Language);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
