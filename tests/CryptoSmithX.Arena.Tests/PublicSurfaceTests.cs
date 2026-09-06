using System.Text.RegularExpressions;
using CryptoSmithX.Arena;
using CryptoSmithX.Arena.Data;
using CryptoSmithX.Arena.Models;

namespace CryptoSmithX.Arena.Tests;

/// <summary>
/// The two ways an anonymous request could hurt the box, and the rules that now stand between it and
/// them. Every one of these was measured against a running container before it was a test: an
/// unbounded cache keyed on <c>?q=</c> took the process past two gigabytes on 30,000 requests, and
/// the front page rendered every folded pair the system collects into a 14.4 MB document.
///
/// None of these tests need a database. That is deliberate — they are about what the process will
/// hold and what an address is allowed to be, and both of those have to be true before a connection
/// is opened.
/// </summary>
public sealed class PublicSurfaceTests
{
    /// <summary>
    /// The application's own source, read from beside the test binary. Copied by the csproj rather
    /// than located by walking up from the working directory, so these assertions fail on the code
    /// changing and never on where the tests were run from — the convention phase one set for the
    /// two files that have to keep saying the same sentence.
    /// </summary>
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));

    /// <summary>
    /// The same file with its <c>//</c> comment lines dropped.
    ///
    /// Needed because the comments in <c>Program.cs</c> name the very strings some of these tests
    /// assert are absent — the argument for a rule has to be allowed to say what was rejected, and
    /// "UseEphemeralDataProtectionProvider does not do this" is the most useful sentence in that
    /// file. So the assertions read the code and the prose is left free to explain it.
    /// </summary>
    private static string Code(string name) =>
        string.Join(
            '\n',
            Source(name).Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ── The cache is bounded ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_flood_of_distinct_keys_cannot_grow_the_table_without_limit()
    {
        // The measured failure, in miniature: the key is "pairs:" + whatever an anonymous caller
        // typed, every distinct term is a guaranteed miss, and before this bound existed each miss
        // left a rendered result behind for the life of the process.
        var cache = new ArenaCache(new FakeClock());

        for (var i = 0; i < ArenaCache.MaxEntries * 20; i++)
        {
            Assert.Equal(i, await cache.GetAsync("pairs:" + i, _ => Task.FromResult(i), CancellationToken.None));
        }

        Assert.True(cache.Count <= ArenaCache.MaxEntries, $"held {cache.Count} entries");
    }

    [Fact]
    public async Task A_key_the_cache_refuses_still_gets_its_answer()
    {
        // Refusing to REMEMBER is not refusing to ANSWER. A caller past the ceiling gets the same
        // result they would have got without a cache at all — which is the whole reason the bound
        // is safe to have: losing the cache degrades to the site before it existed.
        var cache = new ArenaCache(new FakeClock());
        var flood = 0;

        for (var i = 0; i < ArenaCache.MaxEntries * 3; i++)
        {
            flood = await cache.GetAsync("pairs:" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        Assert.Equal(ArenaCache.MaxEntries * 3 - 1, flood);
    }

    [Fact]
    public async Task A_flood_does_not_push_out_the_page_real_visitors_are_sharing()
    {
        // The ordering that makes the ceiling a defence rather than a different vulnerability: an
        // entry already held is never evicted to make room. If a flood could evict, an attacker
        // would not need memory — they could simply take the cache away from everyone else.
        var cache = new ArenaCache(new FakeClock());
        var loads = 0;

        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref loads));

        Assert.Equal(1, await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None));

        for (var i = 0; i < ArenaCache.MaxEntries * 5; i++)
        {
            await cache.GetAsync("pairs:" + i, _ => Task.FromResult(0), CancellationToken.None);
        }

        // Still a hit, still the same answer, and the loader was never called a second time.
        Assert.Equal(1, await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task A_key_too_long_to_hold_is_answered_and_not_remembered()
    {
        // The term is never trimmed to fit the key: a filter that answers a long question with the
        // results of its first sixty-four characters is the page quietly searching for something
        // else. So the answer is real and the memory is not kept.
        var cache = new ArenaCache(new FakeClock());
        var key = "pairs:" + new string('A', ArenaCache.MaxKeyLength);
        var loads = 0;

        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref loads));

        Assert.True(key.Length > ArenaCache.MaxKeyLength);
        Assert.Equal(1, await cache.GetAsync(key, Load, CancellationToken.None));
        Assert.Equal(0, cache.Count);

        // No clock advance: the same request in the same millisecond runs again, because nothing
        // was kept. That is the cost, and it is paid only by the caller who wrote the long key.
        Assert.Equal(2, await cache.GetAsync(key, Load, CancellationToken.None));
    }

    [Fact]
    public async Task The_ceiling_is_not_a_ratchet()
    {
        // A bound that filled once and stayed full would answer the memory problem by breaking the
        // cache permanently. Dead entries — finished AND past the one-second window — are swept, so
        // a process that survived a flood is a process with a working cache a second later.
        var clock = new FakeClock();
        var cache = new ArenaCache(clock);

        for (var i = 0; i < ArenaCache.MaxEntries; i++)
        {
            await cache.GetAsync("pairs:" + i, _ => Task.FromResult(i), CancellationToken.None);
        }

        Assert.Equal(ArenaCache.MaxEntries, cache.Count);
        clock.Advance(ArenaCache.Ttl);

        var loads = 0;
        Task<int> Load(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref loads));

        Assert.Equal(1, await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None));
        Assert.Equal(1, await cache.GetAsync("pair:BTC/USD", Load, CancellationToken.None));
        Assert.Equal(1, loads); // it was admitted, so the second visitor is a hit
    }

    // ── An address is not an arbitrary string ───────────────────────────────────────────────────

    [Theory]
    [InlineData("BTC")]
    [InlineData("USD")]
    [InlineData("1000PEPE")]
    [InlineData("USDT")]
    [InlineData("wbtc")]           // case is significant and never folded (0024), but it is allowed
    [InlineData("A_B-C")]
    [InlineData("ABCDEFGHIJKLMNOP")] // exactly MaxLength
    public void An_asset_code_is_an_address(string code) => Assert.True(PairAddress.IsFamily(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-BTC")]                // a code starts with a letter or a digit
    [InlineData("styles.css")]          // the dot, which is what kept the route off the design system
    [InlineData("ds/styles.css")]
    [InlineData("BTC USD")]
    [InlineData("BTC'; drop table")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]   // one past MaxLength
    public void Anything_else_is_a_404_before_a_connection_is_opened(string? code) =>
        Assert.False(PairAddress.IsFamily(code));

    [Fact]
    public void A_long_string_is_refused_by_length_and_not_by_luck()
    {
        // The measured failure: a 200-character base family ran the full family-expansion query and
        // only then returned 404, through /arena/Pairs/Pair?baseFamily=… where no route constraint
        // applies. Both halves matter — the characters and the length.
        Assert.False(PairAddress.IsFamily(new string('A', 200)));
        Assert.False(PairAddress.IsFamily(new string('A', PairAddress.MaxLength + 1)));
        Assert.True(PairAddress.IsFamily(new string('A', PairAddress.MaxLength)));
    }

    [Theory]
    [InlineData("BTC\n")]
    [InlineData("BTC\r")]
    [InlineData("BTC\r\n")]
    [InlineData("\nBTC")]
    [InlineData("BTC\n\n")]
    public void A_newline_is_not_in_the_character_class_and_never_was(string code)
    {
        // .NET's `$` matches at the end of the string OR immediately before a single trailing
        // newline, so the rule as written accepted "BTC\n" — one character outside its own class,
        // and every family code on the site gained a second spelling that reached the family
        // expansion and its own cache entry before answering 404. `\z` is the end of the string.
        Assert.False(PairAddress.IsFamily(code));
    }

    [Fact]
    public void The_pattern_closes_at_the_end_of_the_string()
    {
        // The property above, said about the constant itself, so the four route templates that
        // carry this pattern verbatim cannot be left anchored the old way by an edit here.
        Assert.EndsWith(@"\z", PairAddress.Pattern, StringComparison.Ordinal);
        Assert.DoesNotContain("$", PairAddress.Pattern, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_routes_and_the_action_check_carry_the_same_rule()
    {
        // Two copies of a security rule are one rule and one decoration, and the decoration is the
        // one nobody notices going stale. The route templates keep the two-segment route off
        // /arena/ds/styles.css; PairAddress keeps the query-string address off the database. They
        // have to say the same thing, so this reads the templates and checks that they do.
        var program = Code("Program.cs");

        Assert.Equal(4, Regex.Matches(program, Regex.Escape($"regex({PairAddress.Pattern})")).Count);
        Assert.Equal(4, Regex.Matches(program, Regex.Escape($"maxlength({PairAddress.MaxLength})")).Count);
    }

    [Fact]
    public void The_page_links_to_the_live_stream_by_the_address_we_mean()
    {
        // Url.Action resolves to /Pairs/Live?baseFamily=… because the default route is registered
        // first. That address works, and it is the one the constraints do not cover; a page that
        // emitted it taught every reader the URL we did not mean.
        var view = Source("Pair.cshtml");

        Assert.Contains("Url.RouteUrl(\"pair-live\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Url.Action(\"Live\"", view, StringComparison.Ordinal);
    }

    // ── The board is bounded, and says what it did not show ─────────────────────────────────────

    [Fact]
    public void The_pair_list_is_limited()
    {
        // Without this the front page rendered every folded pair the system collects — 14.4 MB at
        // production scale, and the cache then held the document.
        Assert.Contains("limit @limit", ArenaStore.PairsSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_limit_comes_with_the_count_it_cut()
    {
        // A limit on its own is a silent truncation, which is the same failure as a zero standing in
        // for a dash. The window function is counted over the grouped result and before the limit,
        // so what the page prints is the number of PAIRS that matched.
        Assert.Contains("count(*) over ()::int          as \"Matching\"", ArenaStore.PairsSql, StringComparison.Ordinal);
        Assert.True(
            ArenaStore.PairsSql.IndexOf("count(*) over ()", StringComparison.Ordinal)
            < ArenaStore.PairsSql.IndexOf("limit @limit", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_board_knows_that_it_is_full()
    {
        var cards = Enumerable.Range(0, ArenaStore.MaxPairs)
            .Select(i => new PairListItem("A" + i, "USD", 1, 1))
            .ToList();

        var full = new PairListModel(cards, 4812, ArenaStore.MaxPairs, "", DateTimeOffset.UnixEpoch);
        var complete = new PairListModel(cards, cards.Count, ArenaStore.MaxPairs, "", DateTimeOffset.UnixEpoch);

        Assert.True(full.Truncated);
        Assert.False(complete.Truncated);
    }

    [Fact]
    public void The_board_says_out_loud_when_it_is_showing_less_than_it_has()
    {
        // The rule this guards is a house rule, not a layout preference: a page that quietly shows
        // less than it has is the same failure as a zero standing in for a dash. Someone could keep
        // the limit and drop the sentence and no other test here would notice.
        var view = Source("Index.cshtml");

        Assert.Contains("Model.Truncated", view, StringComparison.Ordinal);
        Assert.Contains("Model.Matching", view, StringComparison.Ordinal);
    }

    // ── Keys that live and die with the process ─────────────────────────────────────────────────

    [Fact]
    public void The_key_ring_keeps_what_it_is_given_and_shares_it_with_nobody()
    {
        var a = new ProcessKeyRing();
        var b = new ProcessKeyRing();

        Assert.Empty(a.GetAllElements());
        a.StoreElement(new System.Xml.Linq.XElement("key"), "key-1");

        Assert.Single(a.GetAllElements());
        Assert.Empty(b.GetAllElements()); // a second process starts with nothing, which is the point
    }

    [Fact]
    public void The_call_that_did_not_do_what_it_claimed_is_not_back()
    {
        // Measured, not assumed: with AddDataProtection().UseEphemeralDataProtectionProvider() the
        // process still wrote key-<guid>.xml under ~/.aspnet/DataProtection-Keys and still logged
        // "No XML encryptor configured" on every start, because that call replaces the provider and
        // leaves the file-backed key ring and its startup hosted service registered. It reads like
        // the fix, which is exactly why it is worth a test.
        var program = Code("Program.cs");

        Assert.DoesNotContain("UseEphemeralDataProtectionProvider", program, StringComparison.Ordinal);
        Assert.Contains("new ProcessKeyRing()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_response_is_compressed_and_the_live_stream_is_not()
    {
        // Every anonymous first visit downloaded ~1.3 MB uncompressed, and nothing in front of the
        // app compresses. text/event-stream stays off the list deliberately: compressing the stream
        // would buffer the one thing on this site whose point is arriving immediately.
        var program = Code("Program.cs");

        Assert.Contains("AddResponseCompression", program, StringComparison.Ordinal);
        Assert.Contains("app.UseResponseCompression();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("text/event-stream", program, StringComparison.Ordinal);

        // Before the static files it compresses, and after the forwarded headers EnableForHttps
        // reads: an order that is only correct while all three stay in it.
        var forwarded = program.IndexOf("app.UseForwardedHeaders();", StringComparison.Ordinal);
        var compression = program.IndexOf("app.UseResponseCompression();", StringComparison.Ordinal);
        var statics = program.IndexOf("app.UseStaticFiles();", StringComparison.Ordinal);
        Assert.True(forwarded < compression && compression < statics);
    }

    /// <summary>
    /// A clock that does not move on its own. <c>FakeTimeProvider</c> would do as well; this keeps
    /// the flood tests from depending on the timer machinery inside it while thousands of entries
    /// are inserted.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
