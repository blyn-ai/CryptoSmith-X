using System.Text.Json;
using CryptoSmithX.WebApp.Studio.Live;

namespace CryptoSmithX.WebApp.Studio.Tests;

/// <summary>
/// The three parties to a live update saying the same thing: the view that writes the address, the
/// server that sends it, and the script that looks it up.
///
/// Nothing here is reachable from a method call, which is exactly why it is read from the files.
/// A slot whose group does not match the cell's <c>data-group</c> lands NOWHERE — no error, no
/// console message, just a table that never moves while a stream reports itself perfectly healthy.
/// Sewn to the view the way <c>studio-ages.js</c> already is, and for the same reason.
/// </summary>
public sealed class SlotAddressTests
{
    private static readonly string Table = Read("_V2Table.cshtml");
    private static readonly string Script = Read("studio-live.js");

    [Fact]
    public void The_row_carries_the_instrument_the_wire_addresses_it_by()
    {
        Assert.Contains("""data-instrument="@r.Row.InstrumentId" """.TrimEnd(), Table);
        Assert.Contains("""'.v2-row[data-instrument="' + slot.i + '"]'""", Script);
    }

    [Fact]
    public void The_group_on_the_cell_and_the_group_on_the_wire_are_the_same_spelling()
    {
        // data-group renders the V2Field's own name — "Bid", not "bid". A CSS attribute selector
        // compares values case-sensitively, so lowercasing one side would silently orphan every
        // slot on the page.
        Assert.Contains("""data-group="@c.Field" """.TrimEnd(), Table);
        Assert.Contains("""'.v2-cell[data-group="' + slot.g + '"]'""", Script);

        var slot = Slot();
        Assert.Equal(Models.V2Field.Bid.ToString(), slot.Group);
        Assert.NotEqual("bid", slot.Group);
    }

    [Fact]
    public void Both_ages_are_in_the_markup_in_both_modes()
    {
        // Reserved, not added when live: switching modes must not move a row. This table has
        // already cost a CLS of 0.12 on ages that merely ticked.
        Assert.Contains("data-age-live", Table);
        Assert.Contains("data-age-written", Table);
        Assert.Contains("[data-age-live]", Script);
        Assert.Contains("[data-age-written]", Script);
    }

    [Fact]
    public void The_source_is_marked_on_the_cell_and_starts_at_latest()
    {
        // On the CELL, never on the page: a feed only starts where the exchange has a ws_url, so
        // live is always partial and a page-wide flag would mix a two-second figure with a
        // ten-second one in silence.
        Assert.Contains("""data-src="latest" """.TrimEnd(), Table);
        Assert.Contains("setAttribute('data-src', slot.src)", Script);
    }

    [Fact]
    public void Every_name_the_script_reads_off_a_slot_is_a_name_the_server_writes()
    {
        // The wire's field names are short because this rides five times a second; short names are
        // also the ones a typo hides in best, so they are checked against the serialiser itself.
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(Slot()))!;

        foreach (var name in new[] { "i", "g", "p", "t", "s", "m", "tone", "a", "w", "src" })
        {
            Assert.True(json.ContainsKey(name), $"the wire has no '{name}', and studio-live.js reads it");
            Assert.Contains("slot." + name, Script);
        }
    }

    [Fact]
    public void The_frame_carries_its_sequence_and_its_signal()
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new LiveFrame(1, [Slot()], "up")))!;

        Assert.True(json.ContainsKey("seq"));
        Assert.True(json.ContainsKey("slots"));
        Assert.True(json.ContainsKey("signal"));
        Assert.Contains("frame.slots", Script);
    }

    [Fact]
    public void The_mark_classes_the_script_writes_are_the_views_own()
    {
        // Two spellings of the same state is how a chip ends up styled on one path and not the
        // other. These four are what _V2Table.cshtml itself emits.
        foreach (var css in new[] { "v2-part--best", "v2-part--worst", "v2-part--good", "v2-part--bad" })
        {
            Assert.Contains(css, Script);
        }

        Assert.Contains("v2-part--", Table);
    }

    [Fact]
    public void The_mode_is_a_parameter_of_the_one_stream_and_not_a_second_endpoint()
    {
        // One address, one gate, one place a reader's connection is accounted for.
        Assert.Contains("'mode=' + mode", Script);
        Assert.Contains("v2-mode-btn", Read("AssetV2.cshtml"));
    }

    private static LiveSlot Slot()
    {
        var rows = Rows.Live(Rows.Venue(1, bid: 100, ask: 101));
        return LiveFrames
            .Slots(rows, Data.Verdicts.Compute(rows), new Dictionary<int, HubQuote>(), DateTimeOffset.UnixEpoch)
            .First(s => s.Group == Models.V2Field.Bid.ToString());
    }

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface", name));
}
