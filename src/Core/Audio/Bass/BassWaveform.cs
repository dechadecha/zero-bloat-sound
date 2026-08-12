using ManagedBass;

namespace ZBS.Core.Audio.Bass;

/// <summary>
/// Форма волны для сикбара (M3.4): decode-стрим по файлу → пики по бакетам.
/// Не трогает воспроизведение (отдельный decode-канал), безопасно звать из фона.
/// </summary>
public static class BassWaveform
{
    /// <summary>
    /// Пики 0..1 по buckets корзинам. null — файл не открылся/не аудио/отменено.
    /// isStale — кооперативная отмена (трек уже сменился — не дожёвываем весь файл).
    /// </summary>
    public static float[]? ComputePeaks(string filePath, int buckets = 400, Func<bool>? isStale = null)
    {
        var stream = ManagedBass.Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (stream == 0) return null;
        try
        {
            var totalBytes = ManagedBass.Bass.ChannelGetLength(stream);
            if (totalBytes <= 0) return null;

            var peaks = new float[buckets];
            var buffer = new float[16384];
            long readTotal = 0;
            while (true)
            {
                if (isStale?.Invoke() == true) return null;
                var got = ManagedBass.Bass.ChannelGetData(stream, buffer, buffer.Length * sizeof(float));
                if (got <= 0) break;
                var samples = got / sizeof(float);
                for (var i = 0; i < samples; i++)
                {
                    var bucket = (int)((readTotal + (long)i * sizeof(float)) * buckets / totalBytes);
                    if (bucket >= buckets) bucket = buckets - 1;
                    var v = Math.Abs(buffer[i]);
                    if (v > peaks[bucket]) peaks[bucket] = v;
                }
                readTotal += got;
            }

            // VBR: длина у BASS оценочная. Реально прочитали меньше — растягиваем
            // заполненную часть на всю ширину, иначе хвост «ложная тишина».
            if (readTotal > 0 && readTotal < totalBytes * 97 / 100)
            {
                var filled = Math.Max(1, (int)((long)buckets * readTotal / totalBytes));
                var stretched = new float[buckets];
                for (var i = 0; i < buckets; i++)
                    stretched[i] = peaks[(int)((long)i * filled / buckets)];
                peaks = stretched;
            }

            // Нормализация к самому громкому месту — иначе тихие записи дают плоскую линию.
            var max = 0f;
            foreach (var p in peaks)
                if (p > max) max = p;
            if (max > 0.001f)
                for (var i = 0; i < peaks.Length; i++)
                    peaks[i] = Math.Min(1f, peaks[i] / max);
            return peaks;
        }
        finally
        {
            ManagedBass.Bass.StreamFree(stream);
        }
    }
}
