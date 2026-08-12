using ZBS.Core.Audio;
using ZBS.Core.Playback;
using ZBS.Core.Playlist;
using Xunit;

namespace ZBS.Tests;

/// <summary>Логика движка на фейковом бэкенде — без BASS и звука.</summary>
public sealed class PlaybackEngineTests
{
    internal sealed class FakeBackend : IAudioBackend
    {
        public HashSet<string> FailingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LoadedPath { get; private set; }
        public double LoadedStart { get; private set; }
        public double? LoadedEnd { get; private set; }
        public PlaybackState State { get; private set; } = PlaybackState.Stopped;
        public double DurationSeconds { get; set; } = 10;
        public double PositionSeconds { get; set; }
        public double Volume { get; set; } = 1;
        public double Speed { get; set; } = 1;
        public bool SpeedSupported => true;
        public string? LastError { get; private set; }
        public event Action? PlaybackEnded;

        public bool Init(int deviceIndex = -1) => true;
        public bool CanDecode(string filePath) => !FailingPaths.Contains(filePath);
        public float[]? EqGains { get; private set; }
        public void SetEqualizer(float[]? gains) => EqGains = gains;
        public bool GetSpectrum(float[] buffer) => false;

        public double TrackGainDb { get; set; }

        public bool Load(string filePath, double startSeconds = 0, double? endSeconds = null)
        {
            LastError = null;
            TrackGainDb = 0;
            if (FailingPaths.Contains(filePath))
            {
                LastError = "fake load error";
                State = PlaybackState.Stopped;
                return false;
            }
            LoadedPath = filePath;
            LoadedStart = startSeconds;
            LoadedEnd = endSeconds;
            PositionSeconds = 0;
            return true;
        }

        public bool CrossfadeTo(string filePath, double startSeconds, double? endSeconds, double fadeSeconds, double gainDb = 0)
        {
            var ok = Load(filePath, startSeconds, endSeconds);
            if (ok) TrackGainDb = gainDb;
            return ok;
        }

        public void Play() => State = PlaybackState.Playing;
        public void Pause() => State = PlaybackState.Paused;
        public void Stop() { State = PlaybackState.Stopped; PositionSeconds = 0; }
        public void FadePause(int fadeMs) => Pause();
        public void FadeResume(int fadeMs) => Play();
        public void FadeOutAndStop(int fadeMs) => Stop();
        /// <summary>Как реальный BASS при конце файла: стрим останавливается, позиция — на конце.</summary>
        public void RaiseEnded()
        {
            State = PlaybackState.Stopped;
            PositionSeconds = DurationSeconds;
            PlaybackEnded?.Invoke();
        }
        public void Dispose() { }
    }

    private static List<Track> Tracks(params string[] names) =>
        names.Select(n => new Track(n)).ToList();

    [Fact]
    public void Auto_advance_skips_broken_tracks()
    {
        var backend = new FakeBackend();
        backend.FailingPaths.Add("bad.mp3");
        using var engine = new PlaybackEngine(backend);

        engine.SetPlaylist(Tracks("a.mp3", "bad.mp3", "c.mp3"));
        Assert.Equal(0, engine.CurrentIndex);

        backend.RaiseEnded(); // конец a.mp3 → bad.mp3 не грузится → должен заиграть c.mp3

        Assert.Equal(2, engine.CurrentIndex);
        Assert.Equal("c.mp3", backend.LoadedPath);
        Assert.Equal(PlaybackState.Playing, backend.State);
    }

    [Fact]
    public void Auto_advance_stops_gracefully_when_rest_is_broken()
    {
        var backend = new FakeBackend();
        backend.FailingPaths.Add("bad1.mp3");
        backend.FailingPaths.Add("bad2.mp3");
        using var engine = new PlaybackEngine(backend);

        engine.SetPlaylist(Tracks("a.mp3", "bad1.mp3", "bad2.mp3"));
        backend.RaiseEnded(); // всё, что дальше, битое — движок не падает и не зацикливается

        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

    [Fact]
    public void Ended_event_is_marshalled_through_dispatch()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend);
        var dispatched = 0;
        engine.Dispatch = a => { dispatched++; a(); };

        engine.SetPlaylist(Tracks("a.mp3", "b.mp3"));
        backend.RaiseEnded();

        Assert.Equal(1, dispatched);
        Assert.Equal(1, engine.CurrentIndex);
    }

    [Fact]
    public void Queue_jumps_ahead_of_order_then_playlist_continues()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend);
        engine.SetPlaylist(Tracks("a.mp3", "b.mp3", "c.mp3", "d.mp3"));

        engine.Enqueue(3); // «сыграть следующим» — d
        backend.RaiseEnded();
        Assert.Equal("d.mp3", backend.LoadedPath);
        Assert.Equal(3, engine.CurrentIndex);

        backend.RaiseEnded(); // очередь пуста, продолжаем по порядку после d — а он последний
        Assert.Equal(3, engine.CurrentIndex);      // никуда не прыгнули
        Assert.Equal("d.mp3", backend.LoadedPath); // и ничего нового не загрузили
    }

    [Fact]
    public void Repeat_one_replays_same_track_on_auto_advance()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend) { Repeat = RepeatMode.One };
        engine.SetPlaylist(Tracks("a.mp3", "b.mp3"));

        backend.RaiseEnded();

        Assert.Equal(0, engine.CurrentIndex);
        Assert.Equal("a.mp3", backend.LoadedPath);
    }

    [Fact]
    public void Cue_segment_bounds_are_passed_to_backend()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend);
        engine.SetPlaylist(new List<Track> { new("album.flac", "Song 2", 120, 240) });

        Assert.Equal(120, backend.LoadedStart);
        Assert.Equal(240, backend.LoadedEnd);
    }

    [Fact]
    public void Replay_gain_from_provider_is_applied_on_load()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend)
        {
            ReplayGainEnabled = true,
            GainProvider = _ => -7.5,
        };
        engine.SetPlaylist(Tracks("a.mp3"));
        Assert.Equal(-7.5, backend.TrackGainDb, precision: 3);

        engine.ReplayGainEnabled = false;
        engine.PlayAt(0);
        Assert.Equal(0, backend.TrackGainDb, precision: 3);
    }

    [Fact]
    public void Append_grows_playlist_without_restarting_playback()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend);
        engine.SetPlaylist(Tracks("a.mp3", "b.mp3"));

        engine.AppendTracks(Tracks("c.mp3"));

        Assert.Equal(3, engine.Tracks.Count);
        Assert.Equal("a.mp3", backend.LoadedPath); // играющий трек не тронут
        backend.RaiseEnded();
        backend.RaiseEnded();
        Assert.Equal("c.mp3", backend.LoadedPath); // добавленный доступен в порядке
    }

    [Fact]
    public void Played_enough_fires_once_after_half_of_track()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend);
        var played = new List<string>();
        engine.TrackPlayedEnough += t => played.Add(Path.GetFileName(t.FilePath));

        engine.SetPlaylist(Tracks("a.mp3", "b.mp3"));
        backend.PositionSeconds = 6; // 60% из 10 секунд
        backend.RaiseEnded();        // конец a → засчитан, играет b

        Assert.Equal(new[] { "a.mp3" }, played);

        backend.PositionSeconds = 1; // b брошен в начале
        engine.PlayAt(0);
        Assert.Equal(new[] { "a.mp3" }, played); // b не засчитан
    }

    [Fact]
    public void Ab_loop_rewinds_position_on_poll()
    {
        var backend = new FakeBackend();
        using var engine = new PlaybackEngine(backend);
        engine.SetPlaylist(Tracks("a.mp3"));

        backend.PositionSeconds = 2;
        engine.AbLoopToggle(); // A = 2
        backend.PositionSeconds = 6;
        engine.AbLoopToggle(); // B = 6, петля активна
        Assert.True(engine.AbLoopActive);

        backend.PositionSeconds = 6.5;
        engine.Poll();

        Assert.Equal(2, backend.PositionSeconds, precision: 3);
    }
}
