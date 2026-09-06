using System.Data.Common;
using CryptoSmithX.Studio.Models;
using Dapper;

namespace CryptoSmithX.Studio.Data;

/// <summary>
/// Everything the public pages read. Raw SQL through Dapper, the same shape as
/// <c>WebApp.Admin/Data/PairStore.cs</c>, with three differences that are the point of the file.
///
/// <b>One.</b> It reads <c>market_snapshot_latest</c> — one current row per instrument — and never
/// the partitioned history. PairStore's lateral over <c>market_snapshot</c> is the right shape for
/// an operator asking "what was true at 14:03"; on 26 million rows it is also the query an anonymous
/// visitor can run in a loop. Migration 0025 withholds the grant on <c>market_snapshot</c> from
/// <c>studio_reader</c> precisely so this stays a permission rather than a convention.
///
/// <b>Two.</b> Every public query carries the same three guards, and they are not optional:
/// <c>and i.collect</c>, <c>and i.status &lt;&gt; 'delisted'</c>, and the exclusion of the fake
/// venue. Each is argued at the line where it appears.
///
/// <b>Three.</b> The pair is addressed by FAMILY on both sides (0024), the fold is a LEFT JOIN with
/// a coalesce onto the asset itself, and every row still reports its venue's real quote. There is no
/// inner join to <c>asset_family_member</c> anywhere in this file: one of the rejected architecture
/// proposals had one on the quote, and it silently dropped every pair that was not USD/USDT/USDC off
/// the page — no row, no dash, no mark.
///
/// The SQL is exposed as constants because those guards are tested. There is no database in the test
/// run, so the tests read the queries; a guard that can be deleted without a red test is a guard that
/// will be deleted.
/// </summary>
public static class StudioStore
{
    /// <summary>
    /// The list of pairs, folded, with the count of venues carrying each.
    ///
    /// The fold is two LEFT JOINs and two coalesces, on both sides of the pair. Base as well as
    /// quote, because nothing in 0024 makes the table quote-only and a family of WBTC/BTC is the
    /// obvious next entry an operator will make in the admin console; a fold that worked on one side
    /// only would break silently the moment they did.
    ///
    /// Absence of a membership row means "family = the asset itself", which is why this must never
    /// become an inner join and why the seeded identity row USD → USD changes no result — it is
    /// there so the composition of a family reads as data in the console rather than being inferred
    /// from a missing row (0024).
    /// </summary>
    /// <remarks>
    /// <b>The board is limited, and says so.</b> Without <c>limit</c> this query rendered every
    /// folded pair the system collects onto one anonymous page: measured at production scale it was
    /// a 14.4 MB document, and the cache in front of it then held that document. The cost grows with
    /// the instrument table, so the failure arrives on its own.
    ///
    /// <b>A limit and a count, not paging.</b> Paging was the alternative and it was rejected twice
    /// over. It multiplies the key space of a cache whose bound is a safety property — every
    /// <c>?page=</c> an anonymous caller invents is another entry — and it does not answer the
    /// question the second page is supposed to answer: this board is ordered by how many venues
    /// carry a pair, so the far end of it is the pairs one venue lists, and nobody reaches them by
    /// walking 24 pages. The filter is the tool for reaching a named pair, and it is already there.
    ///
    /// So the query takes the first <see cref="MaxPairs"/> and reports, in the same pass, how many
    /// matched. <c>count(*) over ()</c> is evaluated after the grouping and before the limit, so the
    /// figure is the number of PAIRS the filter matched, not the number shown — which is what the
    /// page prints beside the count it is showing. A page that quietly shows less than it has is the
    /// same failure as a zero standing in for a dash, and the count is what keeps this one honest.
    ///
    /// What the limit does not do is make the query cheaper: with no filter the aggregate still
    /// scans the instrument table, exactly as it did before. It bounds the DOCUMENT and the cache
    /// entry behind it. The scan is bounded elsewhere — one shape of query, a role with a connection
    /// ceiling of 30 (0025), and a cache in front.
    /// </remarks>
    public const string PairsSql =
        """
        with listing as (
            select coalesce(bm.family_code, i.base_asset)  as base_family,
                   coalesce(qm.family_code, i.quote_asset) as quote_family,
                   i.segment_code
              from exchange_instrument i
              join segment  sg on sg.code = i.segment_code
              join exchange x  on x.code  = sg.exchange_code
              -- LEFT, never INNER, and read through coalesce: an asset with no membership row is
              -- its own family. 0024 makes that the reading of an absent row, and an inner join
              -- here would drop every pair nobody has folded yet, which is most of them.
              left join asset_family_member bm on bm.asset_code = i.base_asset
              left join asset_family_member qm on qm.asset_code = i.quote_asset
             where i.collect
               -- Not `= 'trading'`. An instrument in halt, post_only or reduce_only stays in the
               -- comparison carrying its state; vanishing without a mark is the same lie as a zero
               -- standing in for a dash.
               and i.status <> 'delisted'
               -- A segment we are not collecting from would publish its last frozen observation as
               -- though the market had stopped. The instrument rule above is the opposite case on
               -- purpose: halt is a fact about the venue's book, "disabled" is a fact about us.
               and sg.status = 'enabled'
               -- 0002 seeds an in-process exchange called Fake for development, and 0005 leaves it
               -- `enabled` — it is the only venue with an adapter at that point. Filtered on the
               -- EXCHANGE rather than the segment so a later fake-spot segment is excluded by the
               -- same line. Three independent architecture proposals all forgot this and would have
               -- published a platform called "Fake" on the front page.
               and x.code <> 'fake'
        )
        select base_family                    as "BaseFamily",
               quote_family                   as "QuoteFamily",
               count(distinct segment_code)::int as "Venues",
               count(*)::int                  as "Listings",
               -- How many pairs the filter matched, on every row because a window function is the
               -- only way to carry it out of one pass. Counted over the GROUPED result, so it is
               -- pairs and not listings, and evaluated before the limit below, so it is the number
               -- the page needs in order to say what it is not showing.
               count(*) over ()::int          as "Matching"
          from listing
         where @search = ''
            or base_family  ilike @like
            or quote_family ilike @like
         group by 1, 2
         order by count(distinct segment_code) desc, count(*) desc, 1, 2
         -- Bounded, and the bound is stated on the page rather than applied behind it. See the
         -- remarks above for why this is a limit and not paging.
         limit @limit
        """;

    /// <summary>
    /// One pair across every venue that lists it.
    ///
    /// The family is expanded to asset codes BEFORE the instrument table is touched, rather than
    /// wrapping base_asset in a coalesce in the WHERE clause. Two reasons, and the second is the
    /// real one. The cheap one: 0024's partial index is on <c>(base_asset, quote_asset) where
    /// collect</c>, and a predicate on an expression cannot use it. The load-bearing one: the
    /// expansion states what "family F" means as a set — the members of F, plus F itself when F is
    /// not folded into something else — and that set is exactly the inverse of
    /// <c>coalesce(member.family_code, asset)</c>. Written any shorter it stops being the inverse:
    /// a family BTC holding only WBTC would expand to WBTC alone and drop plain BTC listings, which
    /// fold into BTC by the absent-row rule.
    ///
    /// The row keeps <c>i.base_asset</c> and <c>i.quote_asset</c> — the venue's own spelling. The
    /// heading folds; the row never does.
    /// </summary>
    public const string PairVenuesSql =
        """
        with base_codes as (
            select m.asset_code from asset_family_member m where m.family_code = @baseFamily
            union
            select @baseFamily
             where not exists (select 1 from asset_family_member where asset_code = @baseFamily)
        ),
        quote_codes as (
            select m.asset_code from asset_family_member m where m.family_code = @quoteFamily
            union
            select @quoteFamily
             where not exists (select 1 from asset_family_member where asset_code = @quoteFamily)
        )
        select i.id                             as "InstrumentId",
               i.segment_code                   as "SegmentCode",
               sg.kind                          as "SegmentKind",
               x.code                           as "ExchangeCode",
               x.name                           as "ExchangeName",
               i.exchange_symbol                as "Symbol",
               -- The venue's real assets, not the family. A row that printed the folded quote would
               -- be claiming Kraken quotes in USDT.
               i.base_asset                     as "BaseAsset",
               i.quote_asset                    as "QuoteAsset",
               i.contract_multiplier::double precision as "ContractMultiplier",
               i.price_step::double precision   as "PriceStep",
               i.qty_step::double precision     as "QtyStep",
               i.funding_interval_hours         as "FundingIntervalHours",
               i.status                         as "Status",
               i.status_changed_at              as "StatusChangedAt",
               i.first_seen_at                  as "FirstSeenAt",
               -- Absolute instants, three of them, one per call. No ages are computed in SQL: the
               -- age belongs to the request, not to the query, and a cached row must still be able
               -- to tell the truth about its own age (blueprint §5).
               s.received_at                    as "ReceivedAt",
               s.last_price                     as "LastPrice",
               s.bid_price                      as "BidPrice",
               s.ask_price                      as "AskPrice",
               s.bid_size                       as "BidSize",
               s.ask_size                       as "AskSize",
               s.mark_price                     as "MarkPrice",
               s.index_price                    as "IndexPrice",
               s.funding_rate                   as "FundingRate",
               s.turnover_24h                   as "Turnover24h",
               s.open_interest                  as "OpenInterest",
               s.open_interest_at               as "OpenInterestAt",
               s.depth_bid_10bps                as "DepthBid10",
               s.depth_ask_10bps                as "DepthAsk10",
               s.depth_bid_25bps                as "DepthBid25",
               s.depth_ask_25bps                as "DepthAsk25",
               s.depth_bid_50bps                as "DepthBid50",
               s.depth_ask_50bps                as "DepthAsk50",
               s.depth_at                       as "DepthAt"
          from exchange_instrument i
          join segment  sg on sg.code = i.segment_code
          join exchange x  on x.code  = sg.exchange_code
          -- LEFT: an instrument discovery has listed but no collector has yet observed belongs on
          -- the page saying so. An inner join would delete it, and a deleted row is a claim that the
          -- venue does not list the pair.
          left join market_snapshot_latest s on s.exchange_instrument_id = i.id
         where i.base_asset  in (select asset_code from base_codes)
           and i.quote_asset in (select asset_code from quote_codes)
           and i.collect
           -- Same three guards as the list, same arguments; see PairsSql.
           and i.status <> 'delisted'
           and sg.status = 'enabled'
           and x.code <> 'fake'
         order by x.name, i.segment_code, i.quote_asset, i.exchange_symbol
        """;

    /// <summary>
    /// How many cards one board may carry.
    ///
    /// Two hundred is chosen against the page rather than against the database: it is more pairs
    /// than anyone reads in one screenful and small enough that the document stays in the tens of
    /// kilobytes, which is what makes the cache entry behind it affordable. The board is ordered
    /// busiest-first, so what a limit cuts is always the thin end — the pairs a single venue lists —
    /// and the page names how many those are.
    /// </summary>
    public const int MaxPairs = 200;

    /// <summary>Pairs the site publishes, folded into families, busiest first, at most
    /// <see cref="MaxPairs"/> of them and always with the number that matched.</summary>
    public static async Task<PairListPage> ListPairsAsync(
        DbConnection conn, string? search, CancellationToken ct)
    {
        var term = (search ?? "").Trim();
        var rows = (await conn.QueryAsync<PairListRow>(new CommandDefinition(
            PairsSql,
            new { search = term, like = $"%{term}%", limit = MaxPairs },
            cancellationToken: ct))).ToList();

        // No rows, no window to read the count off. Zero here is an observation and not a missing
        // figure: the filter matched nothing, and the page says exactly that.
        var matching = rows.Count == 0 ? 0 : rows[0].Matching;

        return new PairListPage(
            rows.Select(r => new PairListItem(r.BaseFamily, r.QuoteFamily, r.Venues, r.Listings)).ToList(),
            matching,
            MaxPairs);
    }

    /// <summary>
    /// What Dapper materialises for the list: a card plus the total the window function repeats on
    /// every row. Private, and unpacked immediately above, so the repetition never reaches a view —
    /// a per-row copy of a page-level figure is an invitation to print the wrong one.
    /// </summary>
    private sealed record PairListRow(
        string BaseFamily, string QuoteFamily, int Venues, int Listings, int Matching);

    /// <summary>
    /// One pair across every venue, with each row's three windows attached.
    ///
    /// Null when nothing lists it — including when the caller addressed an asset that folds into
    /// some other family, because the expansion in <see cref="PairVenuesSql"/> yields the empty set
    /// for a family code that is itself a member of another. That is the right answer: the pair has
    /// exactly one address, and it is the one the fold produces.
    /// </summary>
    public static async Task<PairComparison?> GetPairAsync(
        DbConnection conn, string baseFamily, string quoteFamily, CancellationToken ct)
    {
        var rows = (await conn.QueryAsync<PairVenueRow>(new CommandDefinition(
            PairVenuesSql, new { baseFamily, quoteFamily }, cancellationToken: ct))).ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        var freshness = await SegmentFreshnessStore.ReadAsync(conn, ct);

        // A segment with no freshness row cannot be judged, so it is not judged: unknown windows,
        // no fade, no △. It is not given the neighbouring segment's clocks — that would be one
        // venue's cadence printed over another venue's data.
        var venues = rows
            .Select(r => new PairVenue(
                r,
                freshness.TryGetValue(r.SegmentCode, out var f) ? f.Windows : FreshnessWindows.Unknown))
            .ToList();

        // No verdicts here. They depend on which calls have gone degraded, which is a subtraction
        // against the time of the REQUEST, and this object is cached — see PairComparison.
        return new PairComparison(baseFamily, quoteFamily, venues);
    }
}
