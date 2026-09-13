-- No venue but Kraken collects more than 45 instruments (owner, 2026-09-13). Kraken collects its whole
-- market, as policy 0029 already says; every other enabled segment keeps at most 45.
--
-- Which 45: instruments of the auto-collect assets first (asset.auto_collect — the list discovery itself
-- collects by), then the busiest by the venue's own 24h turnover, then the symbol as a stable tie-break.
-- What falls past 45 is switched off with a note and a changed-by, so discovery treats it as decided and
-- does not switch it back on; nothing is deleted and any row can be turned on again by hand.
--
-- Measured on prod before the first run: avantis-perp 51 (17 of them auto-collect assets), mexc-perp 50
-- (25 assets × USDT and USDC), bybit-perp 46 (25 USDT + 21 USDC); every other non-Kraken segment 24–25.
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/cap-collect-45.sql
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/cap-collect-45.sql

begin;

create temporary table over_cap on commit drop as
select id, segment_code, exchange_symbol
  from (select i.id, i.segment_code, i.exchange_symbol,
               row_number() over (partition by i.segment_code
                                  order by coalesce(a.auto_collect, false) desc,
                                           s.turnover_24h desc nulls last,
                                           i.exchange_symbol) as rank
          from exchange_instrument i
          join segment sg on sg.code = i.segment_code and sg.status = 'enabled'
          left join asset a on a.code = i.base_asset
          left join market_snapshot_latest s on s.exchange_instrument_id = i.id
         where i.collect and i.segment_code <> 'kraken-futures') ranked
 where rank > 45;

update exchange_instrument
   set collect = false,
       collect_note = 'over the 45-instrument cap for venues other than Kraken: auto-collect assets first, then 24h turnover',
       collect_changed_at = now(),
       collect_changed_by = 'ops/cap-collect-45.sql'
 where id in (select id from over_cap);

select segment_code, count(*) switched_off, string_agg(exchange_symbol, ', ' order by exchange_symbol) symbols
  from over_cap group by 1 order by 1;

select i.segment_code, count(*) filter (where i.collect) collected
  from exchange_instrument i join segment sg on sg.code = i.segment_code and sg.status = 'enabled'
 group by 1 order by 2 desc;

commit;
