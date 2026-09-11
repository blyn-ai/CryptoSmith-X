using CryptoSmithX.Database;
using CryptoSmithX.MarketData.Connectors;
using Dapper;
using Npgsql;

namespace CryptoSmithX.MarketData.Hub.Ingestion;

/// <summary>
/// Writes the two vault datasets 0044 created — the per-pair state and the pool behind the whole
/// venue. One class, two entry points, because they read the SAME catalogue response and splitting
/// them into two classes would double the call for no separation worth having.
///
/// They stay TWO datasets in the matrix regardless: an operator must be able to turn the per-pair
/// series off and keep the pool, and coverage has to be able to say which of the two is missing.
/// One class serving two cells is an implementation detail; one cell serving two datasets would be
/// a lie about what is collected.
/// </summary>
public sealed class VaultCollector
{
    /// <summary>Only what we collect: the same predicate every other collector filters on, so an
    /// instrument an operator switched off stops being written here too.</summary>
    internal const string TargetInstrumentsSql =
        "select exchange_symbol, id from exchange_instrument "
        + "where segment_code = @code and collect = true and status = 'trading'";

    private readonly IExchangeMarketData _adapter;
    private readonly Db _db;
    private readonly ILogger _logger;

    public VaultCollector(IExchangeMarketData adapter, Db db, ILogger logger)
    {
        _adapter = adapter;
        _db = db;
        _logger = logger;
    }

    /// <summary>Per-pair vault state. Rows are keyed (instrument, received_at), so two passes inside
    /// one instant collide on the key rather than multiplying rows.</summary>
    public async Task<int> PairStateAsync(CancellationToken ct)
    {
        var states = await _adapter.GetVaultPairStateAsync(ct);
        if (states.Count == 0)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);
        var ids = (await conn.QueryAsync<(string Symbol, int Id)>(new CommandDefinition(
                TargetInstrumentsSql, new { code = _adapter.SegmentCode }, cancellationToken: ct)))
            .ToDictionary(r => r.Symbol, r => r.Id, StringComparer.Ordinal);

        var id = new List<int>(states.Count);
        var at = new List<DateTimeOffset>(states.Count);
        var oiLb = new List<double?>(states.Count);
        var oiSb = new List<double?>(states.Count);
        var oiLq = new List<double?>(states.Count);
        var oiSq = new List<double?>(states.Count);
        var oiMax = new List<double?>(states.Count);
        var oiBlock = new List<double?>(states.Count);
        var dAbove = new List<double?>(states.Count);
        var dBelow = new List<double?>(states.Count);
        var liqB = new List<double?>(states.Count);
        var liqS = new List<double?>(states.Count);
        var impact = new List<double?>(states.Count);
        var skew = new List<double?>(states.Count);
        var spread = new List<double?>(states.Count);
        var vol = new List<double?>(states.Count);

        foreach (var s in states)
        {
            if (!ids.TryGetValue(s.ExchangeSymbol, out var instrumentId))
            {
                // Seen by the catalogue but not yet by discovery, or switched off by an operator.
                continue;
            }

            id.Add(instrumentId);
            at.Add(s.At);
            oiLb.Add(Figures.Num(s.OiLongBase));
            oiSb.Add(Figures.Num(s.OiShortBase));
            oiLq.Add(Figures.Num(s.OiLongQuote));
            oiSq.Add(Figures.Num(s.OiShortQuote));
            oiMax.Add(Figures.Num(s.OiMaxQuote));
            oiBlock.Add(Figures.Num(s.OiBlockLimit));
            dAbove.Add(Figures.Num(s.DepthAbove1Pct));
            dBelow.Add(Figures.Num(s.DepthBelow1Pct));
            liqB.Add(Figures.Num(s.LiquidityBuy));
            liqS.Add(Figures.Num(s.LiquiditySell));
            impact.Add(Figures.Num(s.PriceImpactMultiplier));
            skew.Add(Figures.Num(s.SkewImpactMultiplier));
            spread.Add(Figures.Num(s.SpreadPercent));
            vol.Add(Figures.Num(s.DecayedVol));
        }

        if (id.Count == 0)
        {
            return 0;
        }

        await using var cmd = new NpgsqlCommand(
            """
            insert into vault_pair_state (
                exchange_instrument_id, received_at,
                oi_long_base, oi_short_base, oi_long_quote, oi_short_quote,
                oi_max_quote, oi_block_limit,
                depth_above_1pct, depth_below_1pct, liquidity_buy, liquidity_sell,
                price_impact_mult, skew_impact_mult, spread_p, decayed_vol)
            select * from unnest(
                @ids, @at,
                @oi_lb, @oi_sb, @oi_lq, @oi_sq, @oi_max, @oi_block,
                @d_above, @d_below, @liq_b, @liq_s,
                @impact, @skew, @spread, @vol)
            on conflict (exchange_instrument_id, received_at) do nothing
            """,
            conn);

        cmd.Parameters.AddWithValue("ids", id.ToArray());
        cmd.Parameters.AddWithValue("at", at.ToArray());
        cmd.Parameters.AddWithValue("oi_lb", oiLb.ToArray());
        cmd.Parameters.AddWithValue("oi_sb", oiSb.ToArray());
        cmd.Parameters.AddWithValue("oi_lq", oiLq.ToArray());
        cmd.Parameters.AddWithValue("oi_sq", oiSq.ToArray());
        cmd.Parameters.AddWithValue("oi_max", oiMax.ToArray());
        cmd.Parameters.AddWithValue("oi_block", oiBlock.ToArray());
        cmd.Parameters.AddWithValue("d_above", dAbove.ToArray());
        cmd.Parameters.AddWithValue("d_below", dBelow.ToArray());
        cmd.Parameters.AddWithValue("liq_b", liqB.ToArray());
        cmd.Parameters.AddWithValue("liq_s", liqS.ToArray());
        cmd.Parameters.AddWithValue("impact", impact.ToArray());
        cmd.Parameters.AddWithValue("skew", skew.ToArray());
        cmd.Parameters.AddWithValue("spread", spread.ToArray());
        cmd.Parameters.AddWithValue("vol", vol.ToArray());

        await cmd.ExecuteNonQueryAsync(ct);
        return id.Count;
    }

    /// <summary>The pool behind the venue: one row, keyed on the segment, because the observation
    /// genuinely has no instrument.</summary>
    public async Task<int> VaultAsync(CancellationToken ct)
    {
        var state = await _adapter.GetVaultStateAsync(ct);
        if (state is null)
        {
            return 0;
        }

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            insert into vault_state (
                segment_code, received_at, total_assets_quote, total_supply_shares,
                share_price_quote, utilization_ratio, deposit_cap_quote, withdraw_threshold_quote)
            values (@code, @at, @assets, @supply, @share, @util, @cap, @threshold)
            on conflict (segment_code, received_at) do nothing
            """,
            conn);

        cmd.Parameters.AddWithValue("code", _adapter.SegmentCode);
        cmd.Parameters.AddWithValue("at", state.At);
        cmd.Parameters.AddWithValue("assets", (object?)Figures.Num(state.TotalAssetsQuote) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("supply", (object?)Figures.Num(state.TotalSupplyShares) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("share", (object?)Figures.Num(state.SharePriceQuote) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("util", (object?)Figures.Num(state.UtilizationRatio) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cap", (object?)Figures.Num(state.DepositCapQuote) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("threshold", (object?)Figures.Num(state.WithdrawThresholdQuote) ?? DBNull.Value);

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
