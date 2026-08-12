using ZBS.Core.Audio;
using ZBS.Core.Audio.Bass;
using Xunit;

namespace ZBS.Tests;

/// <summary>
/// Смоук BASS на устройстве «no sound» (индекс 0) — работает без аудиокарты, в т.ч. в CI.
/// </summary>
public sealed class BassBackendTests : IDisposable
{
    private readonly BassAudioBackend _backend = new();
    private readonly string _wav = Path.Combine(Path.GetTempPath(), "zbs-tone-" + Guid.NewGuid().ToString("N") + ".wav");

    [Fact]
    public void Init_load_and_length_work_on_nosound_device()
    {
        Assert.True(_backend.Init(0), _backend.LastError);

        WriteTestWav(_wav, seconds: 2);
        Assert.True(_backend.Load(_wav), _backend.LastError);

        Assert.InRange(_backend.DurationSeconds, 1.9, 2.1);
        Assert.Equal(PlaybackState.Stopped, _backend.State);

        _backend.PositionSeconds = 1.0;
        Assert.InRange(_backend.PositionSeconds, 0.9, 1.1);
    }

    [Fact]
    public void Load_of_missing_file_reports_error()
    {
        Assert.True(_backend.Init(0), _backend.LastError);
        Assert.False(_backend.Load("Z:\\no\\such\\file.mp3"));
        Assert.NotNull(_backend.LastError);
    }

    /// <summary>Пишет валидный WAV (PCM 16-бит, моно, 44100 Гц) с тишиной заданной длины.</summary>
    private static void WriteTestWav(string path, int seconds)
    {
        const int rate = 44100;
        const short bits = 16;
        const short channels = 1;
        int dataSize = rate * seconds * (bits / 8) * channels;

        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8); w.Write(36 + dataSize); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(rate); w.Write(rate * channels * (bits / 8));
        w.Write((short)(channels * (bits / 8))); w.Write(bits);
        w.Write("data"u8); w.Write(dataSize);
        w.Write(new byte[dataSize]);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { File.Delete(_wav); } catch (IOException) { }
    }
}
