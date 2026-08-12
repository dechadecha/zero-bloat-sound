using System.IO;
using ZBS.Core.Settings;
using Xunit;

namespace ZBS.Tests;

public class SettingsBackupTests
{
    [Fact]
    public void Export_then_import_round_trips_config_files()
    {
        var src = Directory.CreateTempSubdirectory().FullName;
        var dst = Directory.CreateTempSubdirectory().FullName;
        var zip = Path.Combine(Path.GetTempPath(), $"zbs-{System.Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllText(Path.Combine(src, "settings.json"), "{\"Volume\":0.5}");
            File.WriteAllText(Path.Combine(src, "radio.json"), "[\"fav\"]");
            File.WriteAllText(Path.Combine(src, "loudness.json"), "cache"); // кэш — НЕ должен попасть в бэкап

            SettingsBackup.Export(src, zip);
            var restored = SettingsBackup.Import(dst, zip);

            Assert.Equal(2, restored); // только settings.json + radio.json
            Assert.Equal("{\"Volume\":0.5}", File.ReadAllText(Path.Combine(dst, "settings.json")));
            Assert.Equal("[\"fav\"]", File.ReadAllText(Path.Combine(dst, "radio.json")));
            Assert.False(File.Exists(Path.Combine(dst, "loudness.json"))); // кэш не восстановлен
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(dst, true);
            if (File.Exists(zip)) File.Delete(zip);
        }
    }
}
