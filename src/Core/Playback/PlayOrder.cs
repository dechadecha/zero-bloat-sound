namespace ZBS.Core.Playback;

public enum ShuffleMode
{
    Off,
    /// <summary>Честный рандом: любой трек, кроме текущего.</summary>
    Random,
    /// <summary>Умный: перемешанный обход без повторов, пока не пройдёт вся библиотека.</summary>
    Smart,
}

public enum RepeatMode
{
    None,
    All,
    One,
}

/// <summary>
/// Кто играет следующим: очередь «сыграть следующим» → повтор трека → шаффл/по порядку.
/// Чистая логика без звука — легко тестируется.
/// </summary>
public sealed class PlayOrder
{
    private readonly Queue<int> _queue = new();
    private readonly List<int> _bag = new();
    private readonly Random _rng = new();
    private int _bagPos = -1;
    private int _count;

    public ShuffleMode Shuffle { get; set; } = ShuffleMode.Off;
    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    public int QueueLength => _queue.Count;

    /// <summary>Новый плейлист: очередь и мешок сбрасываются.</summary>
    public void Reset(int trackCount)
    {
        _count = trackCount;
        _queue.Clear();
        RebuildBag();
    }

    public void Enqueue(int index)
    {
        if (index >= 0 && index < _count)
            _queue.Enqueue(index);
    }

    /// <summary>Плейлист дорос (Append): новые индексы попадают в ещё не сыгранную часть мешка.</summary>
    public void Extend(int newCount)
    {
        if (newCount <= _count) return;
        for (var i = _count; i < newCount; i++)
            _bag.Add(i);
        _count = newCount;
        // Перемешиваем только несыгранный хвост — история проигранного не трогается.
        for (var i = _bag.Count - 1; i > _bagPos + 1; i--)
        {
            var j = _rng.Next(_bagPos + 1, i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
    }

    /// <summary>С какого индекса начинать воспроизведение нового плейлиста.</summary>
    public int First()
    {
        if (_count == 0) return -1;
        return Shuffle switch
        {
            ShuffleMode.Off => 0,
            ShuffleMode.Random => _rng.Next(_count),
            _ => BagNext(),
        };
    }

    /// <summary>Пользователь сам выбрал трек — синхронизируем позицию умного шаффла.</summary>
    public void SyncTo(int index)
    {
        if (Shuffle != ShuffleMode.Smart) return;
        var pos = _bag.IndexOf(index);
        if (pos >= 0) _bagPos = pos;
    }

    /// <summary>
    /// Smart включили посреди сессии: мешок перестраивается с текущего трека,
    /// иначе _bagPos телепортируется в случайное место и часть плейлиста не сыграет.
    /// </summary>
    public void ActivateSmart(int currentIndex)
    {
        RebuildBag();
        if (currentIndex >= 0)
        {
            var pos = _bag.IndexOf(currentIndex);
            if (pos > 0)
                (_bag[0], _bag[pos]) = (_bag[pos], _bag[0]);
            _bagPos = 0;
        }
    }

    /// <summary>Вернуть индекс в начало очереди (кроссфейд не удался — элемент не должен пропасть).</summary>
    public void PushFront(int index)
    {
        if (index < 0 || index >= _count) return;
        var rest = _queue.ToArray();
        _queue.Clear();
        _queue.Enqueue(index);
        foreach (var i in rest) _queue.Enqueue(i);
    }

    /// <summary>
    /// Следующий индекс после current. manual=true — пользователь нажал Next (Repeat One не держит).
    /// -1 — играть дальше нечего.
    /// </summary>
    public int NextAfter(int current, bool manual)
    {
        if (_count == 0) return -1;
        if (_queue.Count > 0)
        {
            var fromQueue = _queue.Dequeue();
            return fromQueue < _count ? fromQueue : NextAfter(current, manual);
        }
        if (Repeat == RepeatMode.One && !manual)
            return current;

        return Shuffle switch
        {
            ShuffleMode.Off => NextSequential(current),
            ShuffleMode.Random => NextRandom(current),
            _ => NextSmart(),
        };
    }

    private int NextSequential(int current)
    {
        var next = current + 1;
        if (next < _count) return next;
        return Repeat == RepeatMode.All ? 0 : -1;
    }

    private int NextRandom(int current)
    {
        if (_count == 1) return Repeat == RepeatMode.None ? -1 : 0;
        int candidate;
        do { candidate = _rng.Next(_count); } while (candidate == current);
        return candidate;
    }

    private int NextSmart()
    {
        var next = BagNext();
        if (next >= 0) return next;
        if (Repeat != RepeatMode.All) return -1;
        RebuildBag();
        return BagNext();
    }

    private int BagNext()
    {
        _bagPos++;
        return _bagPos < _bag.Count ? _bag[_bagPos] : -1;
    }

    private void RebuildBag()
    {
        var last = _bag.Count > 0 && _bagPos >= 0 && _bagPos < _bag.Count ? _bag[_bagPos] : -1;
        _bag.Clear();
        _bag.AddRange(Enumerable.Range(0, _count));
        for (var i = _bag.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
        // Не начинать новый круг с только что игравшего трека.
        if (_count > 1 && _bag.Count > 0 && _bag[0] == last)
            (_bag[0], _bag[^1]) = (_bag[^1], _bag[0]);
        _bagPos = -1;
    }
}
