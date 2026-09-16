-- Enabling Aster (aster-perp) on TEST, in stages — plans/aster-venue-blueprint.md §9.
--
-- HOW TO RUN IT. Each stage below is its own `begin; ... commit;` block. Run ONE stage per sitting:
-- delete (or comment out) every stage after the one you are about to run, execute this file, watch
-- what §9 says to watch, and only then come back for the next stage. This file does not — cannot —
-- pause itself between stages; that discipline is the operator's, the same way ops/enable-gmx-perp.sql
-- is a single stage because GMX needed only one. Re-running an earlier stage is harmless: every
-- UPDATE here is idempotent (same values, same WHERE), and stage 1's segment.status = 'enabled' does
-- not re-fire capability_log noise on a repeat run.
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-aster-perp.sql
--
-- PROD is not in scope for this script. §9's own step 10 gates a PROD decision on 24 h of TEST
-- observation and a re-measured gate; nothing here enables Aster on PROD.

-- ============================================================================
-- STAGE 1 — segment.status = 'enabled'; discovery 900, spec_versions 3600.
-- ============================================================================
-- Watch before stage 2: instruments — 441 TRADING in scope, NO symbolType-1 rows at all; 25 rows
-- with collect = true (the auto_collect policy, 0029) — ASTERUSDT is a 26th by hand, stage 1b below;
-- one log line for contractType ""; no status exception; asset gains only the expected new codes
-- (plus NEX and WOJAK via the 0065 aliases); listed_at filled on all rows; funding interval read
-- from fundingInfo, not the 8 h default on every row; no collector_gap.
begin;

update segment set status = 'enabled', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
 where code = 'aster-perp';

update segment_dataset sd
   set mode = 'collect', transport = 'rest', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
 where sd.segment_code = 'aster-perp' and sd.dataset_code in ('discovery', 'spec_versions');

select dataset_code, mode, interval_s from segment_dataset
 where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;

commit;

-- ----------------------------------------------------------------------------------------------
-- STAGE 1b — ASTERUSDT, by operator decision (blueprint §6): 90 % of Binance's ASTERUSDT turnover,
-- the one market where Aster is not a small satellite, and therefore the sharpest subject for the
-- correlation question §10 exists to answer. Run only AFTER stage 1's first discovery pass has
-- written the exchange_instrument row — before that, this UPDATE matches nothing and does nothing.
-- ----------------------------------------------------------------------------------------------
-- begin;
--
-- update exchange_instrument
--    set collect = true, updated_at = now(),
--        collect_changed_at = now(), collect_changed_by = 'ops/enable-aster-perp.sql',
--        collect_note = 'Operator decision, plans/aster-venue-blueprint.md §6: the venue token, 90 % of Binance turnover.'
--  where segment_code = 'aster-perp' and exchange_symbol = 'ASTERUSDT';
--
-- select exchange_symbol, collect, status from exchange_instrument
--  where segment_code = 'aster-perp' and exchange_symbol = 'ASTERUSDT';
--
-- commit;

-- ============================================================================
-- STAGE 2 — snapshot 15 (REST path; BinanceUsdmProfile.Aster.TickersFromMarketFeed = false).
-- ============================================================================
-- Watch before stage 3: 4 rows/min per instrument, INCLUDING TRX, DOT and BCH (the thin markets the
-- change-only ticker array would have starved under the Binance profile's WS branch); open_interest
-- and open_interest_at filled (the OI cycle started); venue_ts lag < 2 s; no 429 in collector_run;
-- weight headers not needed to confirm this (208 weight/min at 15 s is 9 % of the 2400 budget).
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'rest', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'snapshot';
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 3 — depth 300.
-- ============================================================================
-- Watch before stage 4: log line says the depth feed subscribed the COLLECTED set (26 streams on
-- the approved universe, not 441 — BinanceUsdmProfile.Aster.FeedSymbols = Collected); seed walk done
-- within ~1 min (26 symbols at one seed per 2 s); book_reach_* >= 50 bps on BTC and ETH; depth_at
-- fresh; no reseed loop; REST fallback, if seen at all, at limit 500 (BinanceUsdmProfile.Aster.RestDepthLimit).
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'ws', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'depth';
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 4 — candles 60, candles_mark 60, candles_index 60.
-- ============================================================================
-- Watch before stage 5: log line says the market feed subscribed 3 + 2*26 = 55 streams; closed bars
-- only; coverage rows without limit_hit loops; mark/index present for all 26 collected instruments.
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'ws', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'candles';
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'rest', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code in ('candles_mark', 'candles_index');
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 5 — funding 3600.
-- ============================================================================
-- Watch before stage 6: first pass backfills to the funding dataset_setting's backfill_hours;
-- funding_time spacing matches each instrument's own interval (1 h / 4 h / 8 h, read from fundingInfo).
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'rest', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'funding';
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 6 — trades 5.
-- ============================================================================
-- Watch before stage 7: trade rows track the aggTrade rate (BTC ~0.9/s, ETH ~0.6/s on the blueprint's
-- own probe); source = 'ws'; after a forced reconnect a gap is RECORDED, not papered over; no
-- duplicate-key errors.
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'ws', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'trades';
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 7 — book 15.
-- ============================================================================
-- Watch before stage 8: book_topn rows with seq monotonic per instrument; is_snapshot = true where
-- expected; row size recorded (plans/capacity-sizing.md §8 marks book_topn as the disk driver here).
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'ws', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'book';
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 8 — open_interest 300.
-- ============================================================================
-- Watch before stage 9: open_interest_history.source = 'rest', 12 buckets/h; NO 404s in
-- collector_run — the proof that BinanceUsdmProfile.Aster.OpenInterestHistory = Sampled makes
-- GetOpenInterestHistoryAsync return [] without ever calling /futures/data/openInterestHist.
-- begin;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'rest', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'open_interest';
--
-- select dataset_code, mode, interval_s from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 9 — liquidations 900. ONLY once the segment_dataset.note and the history_depth capability
-- row (both written by migration 0065) are confirmed in place and Studio shows the "sampled (<= 1
-- event/symbol/s) - lower bound" label wherever it draws this segment's liquidation series.
-- ============================================================================
-- Watch before stage 10: hourly buckets appear only for symbols that actually had a forceOrder event
-- in that hour (AKEUSDT-shaped rarity, blueprint §1.4: 2 events in a 10-minute capture); Studio shows
-- the sampled label, not a bare volume.
-- begin;
--
-- do $$
-- begin
--     if not exists (
--         select 1 from segment_dataset_capability
--          where segment_code = 'aster-perp' and dataset_code = 'liquidations'
--            and capability_key = 'history_depth' and value = 'sampled_1s_per_symbol'
--     ) then
--         raise exception 'aster-perp: the liquidations sample capability row is missing; run migration 0065 first';
--     end if;
-- end $$;
--
-- update segment_dataset sd
--    set mode = 'collect', transport = 'ws', updated_at = now(), updated_by = 'ops/enable-aster-perp.sql'
--  where sd.segment_code = 'aster-perp' and sd.dataset_code = 'liquidations';
--
-- select dataset_code, mode, interval_s, note from segment_dataset
--  where segment_code = 'aster-perp' and mode <> 'disabled' order by 1;
--
-- commit;

-- ============================================================================
-- STAGE 10 — +24 h. Not an enable step: a checkpoint.
-- ============================================================================
-- By now the venue's 24 h server-side disconnect should have happened on both feeds, been
-- reconnected and reseeded, and any gap it caused should be recorded, not silently absorbed.
-- Re-measure the gate from collector_run (429 count, actual request rate) and, once the numbers are
-- in hand, record them — do not run this templated UPDATE with guessed numbers:
--
-- update exchange
--    set request_budget_source = 'measured',
--        request_budget_note = '<replace with the real 24h numbers: 429 count, measured req/s, and the date>',
--        updated_at = now(), updated_by = 'ops/enable-aster-perp.sql (24h checkpoint)'
--  where code = 'aster';
--
-- Compare disk growth per table against plans/aster-venue-blueprint.md §8's estimates
-- (pg_total_relation_size() per partition), replace every ≈ in that section with a measured number,
-- and only then decide on PROD — a separate, later, deliberate step, not part of this file.
