using CryptoSmithX.WebApp.Studio.Data;
using CryptoSmithX.WebApp.Studio.Live;
using CryptoSmithX.WebApp.Studio.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The room: one computation per tick however many people are watching, a viewer who falls behind
/// losing frames rather than accumulating them, and a room that actually stops when the last tab
/// closes.
///
/// The tick is driven by hand here — <see cref="LiveRoom.TickAsync"/> called directly — so these
/// assert what a tick DECIDES rather than racing its timer. The timer itself is one
/// <see cref="PeriodicTimer"/> and is not the interesting part; what a tick costs and what it sends
/// are.
/// </summary>
public sealed class LiveRoomTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 30, TimeSpan.Zero);
    private static readonly DateTime Written = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task One_tick_computes_once_for_many_viewers()
    {
        // The whole reason a room is per asset. Fifty tabs on BTC must cost what one tab costs;
        // per-viewer this would be fifty identical computations five times a second.
        using var room = Room(out _);
        var readers = Enumerable.Range(0, 5).Select(_ => room.Join()).ToList();

        await room.TickAsync(CancellationToken.None);

        Assert.Equal(1, room.Computations);
        Assert.Equal(5, room.Viewers);

        // And every one of them was written to.
        foreach (var reader in readers)
        {
            Assert.True(reader.TryRead(out _));
        }
    }

    [Fact]
    public async Task A_slow_viewer_sees_the_newest_frame_and_not_the_backlog()
    {
        // Capacity 1, drop oldest — that IS "skip a frame rather than queue", and there is no other
        // queue behind it. A reader on a slow connection is late, never wrong.
        using var room = Room(out var hub);
        var reader = room.Join();

        await room.TickAsync(CancellationToken.None);
        hub.Push(Quote(1, bid: 101));
        await room.TickAsync(CancellationToken.None);
        hub.Push(Quote(1, bid: 102));
        await room.TickAsync(CancellationToken.None);

        Assert.True(reader.TryRead(out var frame));
        Assert.False(reader.TryRead(out _));   // one frame held, not three

        // And it is the newest one, not the oldest.
        Assert.Contains(frame!.Slots, s => s.Text.Contains("102", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tick_with_nothing_new_sends_nothing()
    {
        // The ages on screen keep counting on their own; a frame carrying only a re-rendered age
        // would be five writes a second for a number the browser is already advancing.
        using var room = Room(out _);
        var reader = room.Join();

        await room.TickAsync(CancellationToken.None);
        Assert.True(reader.TryRead(out _));

        await room.TickAsync(CancellationToken.None);
        Assert.False(reader.TryRead(out _));
        Assert.Equal(1, room.Computations);
    }

    [Fact]
    public async Task Only_the_slots_that_moved_are_in_the_second_frame()
    {
        using var room = Room(out var hub);
        var reader = room.Join();

        await room.TickAsync(CancellationToken.None);
        reader.TryRead(out var opening);

        hub.Push(Quote(1, bid: 101));
        await room.TickAsync(CancellationToken.None);
        Assert.True(reader.TryRead(out var second));

        // Smaller than the opening picture, and carrying the venue that moved. Row 1 travels WHOLE
        // here and that is right: a quote arriving flips its every cell from "latest" to "ws" and
        // gives it a live age it did not have.
        Assert.True(second!.Slots.Count < opening!.Slots.Count);
        Assert.Contains(second.Slots, s => s.InstrumentId == 1);

        // The OTHER venue can be in here too, and that is not a leak: a mark is a comparison, so one
        // venue's bid moving changes where the spread's MIN sits on the whole page. What must not
        // appear is a slot whose content did not change at all.
        Assert.All(second.Slots, s => Assert.NotEqual(opening.Slots.Single(o => o.Key == s.Key), s));
    }

    [Fact]
    public async Task Hub_down_keeps_baseline_and_says_degraded()
    {
        // The room lives on: a page that stopped showing the market because the live feed went away
        // would be worse than one showing what the collectors recorded. But it must SAY so — a
        // frozen figure and a calm market look identical.
        using var room = Room(out var hub);
        var reader = room.Join();
        hub.State = HubStreamState.Down;

        await room.TickAsync(CancellationToken.None);

        Assert.True(reader.TryRead(out var frame));
        Assert.Equal("degraded", frame!.Signal);
        Assert.NotEmpty(frame.Slots);   // still the baseline, not an empty table
    }

    [Fact]
    public async Task The_hub_going_down_is_reported_even_though_no_figure_moved()
    {
        // This one is here because it shipped broken and the test host caught it. With the hub gone
        // no quotes arrive, so the "is there anything to say" check passed and the reader was told
        // NOTHING — and a page that goes quiet when its feed dies is indistinguishable from a page
        // watching a quiet market, which is the single confusion this signal exists to prevent.
        using var room = Room(out var hub);
        var reader = room.Join();

        await room.TickAsync(CancellationToken.None);
        Assert.True(reader.TryRead(out _));

        // A tick with nothing new says nothing...
        await room.TickAsync(CancellationToken.None);
        Assert.False(reader.TryRead(out _));

        // ...but a tick where the FEED changed says so, with no slots at all if no figure moved.
        hub.State = HubStreamState.Down;
        await room.TickAsync(CancellationToken.None);

        Assert.True(reader.TryRead(out var frame));
        Assert.Equal("degraded", frame!.Signal);
    }

    [Fact]
    public async Task A_recovered_hub_resends_the_whole_picture()
    {
        // While the hub was gone this room was told nothing, so the reader's page is as old as the
        // outage. The first thing a recovered feed owes them is the current state, not a diff
        // against a picture that has since moved.
        using var room = Room(out var hub);
        var reader = room.Join();

        await room.TickAsync(CancellationToken.None);
        reader.TryRead(out var opening);

        room.Resend();
        hub.Push(Quote(1, bid: 101));
        await room.TickAsync(CancellationToken.None);

        Assert.True(reader.TryRead(out var whole));
        Assert.Equal(opening!.Slots.Count, whole!.Slots.Count);
    }

    [Fact]
    public void Last_leave_disposes_room_and_unsubscribes()
    {
        // A room left running for a tab that is gone is a computation five times a second for
        // nobody, and a hub subscription asking for figures no one will read.
        var hub = new FakeHub();
        using var rooms = new LiveRooms(Load, hub.Stream, Notifier(), TimeProvider.System, NullLoggerFactory.Instance);

        var room = rooms.Room("BTC");
        var reader = room.Join();
        Assert.Equal(1, rooms.Count);

        rooms.Leave(room, reader);

        Assert.Equal(0, rooms.Count);
        Assert.Equal(0, room.Viewers);
    }

    [Fact]
    public void One_room_serves_every_viewer_of_the_same_asset()
    {
        var hub = new FakeHub();
        using var rooms = new LiveRooms(Load, hub.Stream, Notifier(), TimeProvider.System, NullLoggerFactory.Instance);

        var a = rooms.Room("BTC");
        var b = rooms.Room("btc");   // the address is not case sensitive and neither is the room

        Assert.Same(a, b);
        Assert.Equal(1, rooms.Count);
    }

    [Fact]
    public void A_room_survives_one_of_several_viewers_leaving()
    {
        var hub = new FakeHub();
        using var rooms = new LiveRooms(Load, hub.Stream, Notifier(), TimeProvider.System, NullLoggerFactory.Instance);

        var room = rooms.Room("BTC");
        var first = room.Join();
        var second = room.Join();

        rooms.Leave(room, first);

        Assert.Equal(1, rooms.Count);
        Assert.Equal(1, room.Viewers);
        rooms.Leave(room, second);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>A stopped clock, so a test about what MOVED is not also a test about time passing:
    /// under a running clock every row's written age re-renders about once a second and every slot
    /// on the page legitimately differs.</summary>
    private static LiveRoom Room(out FakeHub hub)
    {
        hub = new FakeHub();
        return new LiveRoom("BTC", Load, hub.Stream, Notifier(), new FakeTimeProvider(Now), NullLogger.Instance);
    }

    private static LiveNotifier Notifier() =>
        // Never connected: a room subscribes to the collectors' signal, and these tests drive the
        // tick by hand rather than waiting for one.
        new(new Microsoft.Extensions.Configuration.ConfigurationManager
            {
                ["ConnectionStrings:Database"] = "Host=nowhere.test;Database=x;Username=x;Password=x",
            },
            NullLogger<LiveNotifier>.Instance);

    private static Task<PairPageModel?> Load(string baseFamily, CancellationToken ct)
    {
        var rows = Rows.Live(
            Rows.Venue(1, bid: 100, ask: 101) with { ReceivedAt = Written },
            Rows.Venue(2, bid: 200, ask: 201) with { ReceivedAt = Written });

        return Task.FromResult<PairPageModel?>(new PairPageModel(
            baseFamily, rows, Verdicts.Compute(rows), ColumnScales.Empty, Written, Written, Now));
    }

    private static HubQuote Quote(int id, double bid) =>
        new(id, "kraken-futures", new DateTimeOffset(Written.AddSeconds(10), TimeSpan.Zero),
            bid, null, null, null, null, null, null, null, null, null);

    /// <summary>A <see cref="HubStream"/> nothing is connected to, driven by hand: the room only
    /// needs it to raise quotes and to report a state.</summary>
    private sealed class FakeHub
    {
        public FakeHub() =>
            Stream = new HubStream(new HttpClient(new DeadHandler()) { BaseAddress = new Uri("http://hub.test") },
                NullLogger<HubStream>.Instance);

        public HubStream Stream { get; }

        public HubStreamState State
        {
            get => Stream.State;
            set => Stream.ForceState(value);
        }

        public void Push(params HubQuote[] quotes) => Stream.RaiseQuotes(new HubQuotesFrame(quotes));
    }

    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("no hub in this test"));
    }
}
