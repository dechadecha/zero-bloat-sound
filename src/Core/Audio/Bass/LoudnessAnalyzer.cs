using System.Collections.Concurrent;
using System.Text.Json;
using ManagedBass;

namespace ZBS.Core.Audio.Bass;

/// <summary>
/// Своё выравнивание громкости для файлов БЕЗ ReplayGain-тегов (записи, рипы, старые mp3):
/// decode-проход по файлу → RMS → поправка к эталону, кэш в loudness.json.
/// Считается в фоне по одному файлу; готовая поправка применяется со следующего запуска трека
/// (или сразу, если владелец подпишется на Computed).
/// </summary>
public sealed class LoudnessAnalyzer : IDisposable
{
    // Эталон ≈ −14 dBFS RMS (примерно уровень современного мастеринга/стримингов).
    private const double TargetRmsDb = -14.0;
    private const double MaxBoostDb = 12.0; // тихую запись тянем максимум на +12 — без перегруза шумов

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, double> _cache = new(); // путь → gain dB
    private readonly BlockingCollection<string> _queue = new();
    private readonly HashSet<string> _queued = new();
    private readonly object _queuedLock = new();
    private readonly Thread _worker;
    private volatile bool _disposed;

    /// <summary>Файл проанализирован (из фонового потока — подписчик маршалит сам).</summary>
    public event Action<string, double>? Computed;

    public LoudnessAnalyzer(string settingsDir)
    {
        _cachePath = Path.Combine(settingsDir, "loudness.json");
        try
        {
            if (File.Exists(_cachePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_cachePath));
                if (loaded is not null)
                    foreach (var (k, v) in loaded) _cache[k] = v;
            }
        }
        catch (Exception) { }
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "zbs-loudness", Priority = ThreadPriority.Lowest };
        _worker.Start();
    }

    /// <summary>Поправка из кэша; нет — ставит файл в очередь анализа и возвращает null.</summary>
    public double? GetOrQueue(string path)
    {
        if (_cache.TryGetValue(path, out var db)) return db;
        if (_disposed) return null; // Add в закрытую очередь кидает — на выходе просто без поправки
        lock (_queuedLock)
        {
            if (!_queued.Add(path)) return null;
            try { _queue.Add(path); }
            catch (InvalidOperationException) { _queued.Remove(path); } // гонка с Dispose
        }
        return null;
    }

    private void WorkerLoop()
    {
        foreach (var path in _queue.GetConsumingEnumerable())
        {
            if (_disposed) return;
            try
            {
                var gain = Compute(path);
                if (gain.HasValue)
                {
                    _cache[path] = gain.Value;
                    Save();
                    Computed?.Invoke(path, gain.Value);
                }
            }
            catch (Exception) { /* битый файл — просто без поправки */ }
            finally
            {
                lock (_queuedLock) _queued.Remove(path);
            }
        }
    }

    /// <summary>RMS всего файла decode-стримом (устройство не занимает, играющему не мешает).</summary>
    private double? Compute(string path)
    {
        var h = ManagedBass.Bass.CreateStream(path, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (h == 0) return null;
        try
        {
            var buffer = new float[65536];
            double sumSquares = 0;
            long samples = 0;
            while (true)
            {
                if (_disposed) return null; // выход из приложения: не трогать BASS после Bass.Free
                var got = ManagedBass.Bass.ChannelGetData(h, buffer, buffer.Length * 4);
                if (got <= 0) break;
                var n = got / 4;
                for (var i = 0; i < n; i++)
                {
                    var v = buffer[i];
                    sumSquares += v * v;
                }
                samples += n;
            }
            if (samples < 44100) return null; // меньше секунды звука — не о чем говорить
            var rms = Math.Sqrt(sumSquares / samples);
            if (rms <= 1e-6) return null;     // тишина/битый декод
            var rmsDb = 20 * Math.Log10(rms);
            return Math.Clamp(TargetRmsDb - rmsDb, -MaxBoostDb, MaxBoostDb);
        }
        finally
        {
            ManagedBass.Bass.StreamFree(h);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, double>(_cache)));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        // Дождаться воркера: следом App зовёт Bass.Free, а конкурентный ChannelGetData
        // по освобождаемому нативному стриму — access violation на выходе.
        _worker.Join(3000);
    }
}
