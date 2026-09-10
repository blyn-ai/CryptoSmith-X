using CryptoSmithX.MarketData.Connectors.Streaming;

namespace CryptoSmithX.MarketData.Connectors.Tests;

/// <summary>
/// The fan-out on <see cref="EventBuffer{T}"/>, and above all what it must not disturb.
///
/// <see cref="EventBuffer{T}.Drain"/> is the path into the database and it is the ONLY one that is
/// allowed to be lossless. A second consumer calling Drain would steal events from it outright —
/// which is why an observer gets its own queue instead, and why the first test here is not about
/// observers at all but about the drain being untouched by them.
///
/// The taps are LOSSY on purpose, with the same discipline the buffer itself has: a bound per
/// subscriber, the oldest shed first, and a counter that says the loss happened. A live view that
/// fell behind must cost the viewer a gap in the tape, never the recorder a gap in the record.
/// </summary>
public sealed class EventTapTests
{
    [Fact]
    public void Drain_returns_the_same_events_no_matter_how_many_observers_watch()
    {
        var buffer = new EventBuffer<int>();
        using var one = buffer.Observe(10);
        using var two = buffer.Observe(10);
        using var three = buffer.Observe(10);

        for (var i = 0; i < 5; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal([0, 1, 2, 3, 4], buffer.Drain());
    }

    [Fact]
    public void Drain_loses_nothing_when_an_observer_is_overflowing()
    {
        // The whole point of the split. The tap's bound is two; a hundred events arrive; the
        // recorder still gets all one hundred, in order.
        var buffer = new EventBuffer<int>();
        using var tap = buffer.Observe(capacity: 2);

        for (var i = 0; i < 100; i++)
        {
            buffer.Add(i);
        }

        var drained = buffer.Drain();

        Assert.Equal(100, drained.Count);
        Assert.Equal(Enumerable.Range(0, 100), drained);
        Assert.Equal(98, tap.Dropped);
    }

    [Fact]
    public void An_observer_is_bounded_by_its_own_capacity_and_sheds_the_oldest()
    {
        // Same rule as the buffer's own: keep the most recent market, because that is the half a
        // late reader can still act on, and count what went so the loss is never silent.
        var buffer = new EventBuffer<int>();
        using var tap = buffer.Observe(capacity: 3);

        for (var i = 0; i < 6; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal([3, 4, 5], tap.Drain());
        Assert.Equal(3, tap.Dropped);
    }

    [Fact]
    public void An_observers_drain_is_its_own_and_empties_only_itself()
    {
        var buffer = new EventBuffer<int>();
        using var one = buffer.Observe(10);
        using var two = buffer.Observe(10);

        buffer.Add(1);
        buffer.Add(2);

        Assert.Equal([1, 2], one.Drain());
        Assert.Empty(one.Drain());
        Assert.Equal([1, 2], two.Drain());   // untouched by the first one's drain
        Assert.Equal([1, 2], buffer.Drain());
    }

    [Fact]
    public void Disposing_an_observer_stops_delivery_to_it()
    {
        var buffer = new EventBuffer<int>();
        var tap = buffer.Observe(10);

        buffer.Add(1);
        tap.Dispose();
        buffer.Add(2);

        // Whatever it already held stays readable; nothing new arrives. A room that closed must not
        // keep a queue growing behind it for the life of the process.
        Assert.Equal([1], tap.Drain());
    }

    [Fact]
    public void Disposing_one_observer_leaves_the_others_receiving()
    {
        var buffer = new EventBuffer<int>();
        var going = buffer.Observe(10);
        using var staying = buffer.Observe(10);

        buffer.Add(1);
        going.Dispose();
        buffer.Add(2);

        Assert.Equal([1, 2], staying.Drain());
    }

    [Fact]
    public void Disposing_twice_is_harmless() =>
        // Rooms are disposed by whoever leaves last, and "last" is a race worth losing quietly.
        new EventBuffer<int>().Observe(10).Dispose();

    [Fact]
    public void An_observer_added_later_gets_what_comes_after_it_and_not_the_backlog()
    {
        // A tap is a live view, not a history: it starts from the moment someone began watching.
        // The backlog belongs to the recorder, and the recorder still has it.
        var buffer = new EventBuffer<int>();
        buffer.Add(1);

        using var tap = buffer.Observe(10);
        buffer.Add(2);

        Assert.Equal([2], tap.Drain());
        Assert.Equal([1, 2], buffer.Drain());
    }

    [Fact]
    public async Task Add_stays_lock_free_and_correct_with_observers_under_concurrent_writers()
    {
        // Add runs on the socket's own read loop. It reads the observer list once and hands the
        // event to each queue — no lock, so a slow or stuck reader can never stall the socket that
        // feeds every other subscriber. What is asserted is the consequence: nothing the writers
        // produced is lost by the recorder, however many of them ran at once.
        var buffer = new EventBuffer<int>();
        using var tap = buffer.Observe(100_000);

        const int writers = 8;
        const int each = 2_000;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < each; i++)
            {
                buffer.Add(w * each + i);
            }
        })));

        Assert.Equal(writers * each, buffer.Drain().Count);
        Assert.Equal(writers * each, tap.Drain().Count);
        Assert.Equal(0, tap.Dropped);
    }

    [Fact]
    public async Task Observing_while_events_are_flowing_neither_throws_nor_loses_the_record()
    {
        // Subscribing swaps an immutable array under Interlocked; a writer mid-Add is holding the
        // previous one and finishes against it. The new tap simply starts a moment later, which is
        // exactly what "a live view starts when you start watching" means.
        var buffer = new EventBuffer<int>();
        var stop = false;
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!Volatile.Read(ref stop))
            {
                buffer.Add(i++);
            }

            return i;
        });

        var taps = new List<EventTap<int>>();
        for (var i = 0; i < 20; i++)
        {
            taps.Add(buffer.Observe(1_000));
            await Task.Delay(1);
        }

        Volatile.Write(ref stop, true);
        var written = await writer;

        Assert.Equal(written, buffer.Drain().Count);
        foreach (var tap in taps)
        {
            tap.Dispose();
        }
    }
}
