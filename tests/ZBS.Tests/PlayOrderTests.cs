using ZBS.Core.Playback;
using Xunit;

namespace ZBS.Tests;

public sealed class PlayOrderTests
{
    [Fact]
    public void Sequential_walks_in_order_and_ends()
    {
        var order = new PlayOrder();
        order.Reset(3);
        Assert.Equal(1, order.NextAfter(0, manual: false));
        Assert.Equal(2, order.NextAfter(1, manual: false));
        Assert.Equal(-1, order.NextAfter(2, manual: false));
    }

    [Fact]
    public void Repeat_all_wraps_to_start()
    {
        var order = new PlayOrder { Repeat = RepeatMode.All };
        order.Reset(3);
        Assert.Equal(0, order.NextAfter(2, manual: false));
    }

    [Fact]
    public void Repeat_one_holds_only_on_auto_advance()
    {
        var order = new PlayOrder { Repeat = RepeatMode.One };
        order.Reset(3);
        Assert.Equal(1, order.NextAfter(1, manual: false)); // авто: тот же
        Assert.Equal(2, order.NextAfter(1, manual: true));  // ручной Next: следующий
    }

    [Fact]
    public void Queue_wins_over_everything()
    {
        var order = new PlayOrder { Repeat = RepeatMode.One, Shuffle = ShuffleMode.Smart };
        order.Reset(5);
        order.Enqueue(4);
        Assert.Equal(4, order.NextAfter(0, manual: false));
    }

    [Fact]
    public void Smart_shuffle_visits_every_track_exactly_once()
    {
        var order = new PlayOrder { Shuffle = ShuffleMode.Smart };
        order.Reset(20);
        var visited = new List<int> { order.First() };
        for (var i = 0; i < 19; i++)
            visited.Add(order.NextAfter(visited[^1], manual: false));

        Assert.Equal(-1, order.NextAfter(visited[^1], manual: false)); // круг пройден
        Assert.Equal(20, visited.Distinct().Count()); // без повторов
    }

    [Fact]
    public void Smart_shuffle_with_repeat_all_reshuffles_forever()
    {
        var order = new PlayOrder { Shuffle = ShuffleMode.Smart, Repeat = RepeatMode.All };
        order.Reset(5);
        var current = order.First();
        for (var i = 0; i < 50; i++)
        {
            current = order.NextAfter(current, manual: false);
            Assert.InRange(current, 0, 4);
        }
    }

    [Fact]
    public void Extend_adds_new_tracks_to_unplayed_part_of_smart_bag()
    {
        var order = new PlayOrder { Shuffle = ShuffleMode.Smart };
        order.Reset(3);
        var first = order.First();

        order.Extend(6);

        var seen = new List<int> { first };
        int next;
        while ((next = order.NextAfter(seen[^1], manual: false)) >= 0)
            seen.Add(next);

        Assert.Equal(6, seen.Distinct().Count()); // все шесть, без повторов
    }

    [Fact]
    public void Random_never_repeats_current_track()
    {
        var order = new PlayOrder { Shuffle = ShuffleMode.Random };
        order.Reset(2);
        for (var i = 0; i < 20; i++)
            Assert.Equal(1, order.NextAfter(0, manual: false));
    }
}
