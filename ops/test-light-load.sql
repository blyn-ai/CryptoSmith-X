-- TEST only: a collection scope and cadence a 3-core box can carry.
--
-- 2026-09-16 the test VPS ran at load 15-20 on 3 cores, the hub alone at ~235 % CPU, and Binance
-- closed its depth socket ~50 times an hour — the signature of a consumer that cannot keep up.
-- The owner's decision: five bases per venue, the 25 auto-collect bases on Kraken, and slower
-- polling. Production is not touched by this file.
--
--   test   ssh root@10.11.12.29 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/test-light-load.sql
--
-- NOT fixed here: the Binance USD-M WS feeds subscribe the venue's whole in-scope listing (~566
-- depth streams, ~1 135 market streams) whatever is collected. That is code, not configuration.

begin;

-- 1. Scope. Five bases per venue, ONE listing each: USDT first, then USDC, then USD, then whatever
--    the venue quotes (GMX's pools, oldest id first). Kraken keeps its perpetuals on the
--    auto-collect list.
with ranked as (
    select i.id, i.segment_code, i.exchange_symbol, i.base_asset,
           row_number() over (
               partition by i.segment_code, i.base_asset
               order by case i.quote_asset when 'USDT' then 0 when 'USDC' then 1 when 'USD' then 2 else 3 end,
                        (i.status <> 'trading'), i.id) as rn
      from exchange_instrument i
      join segment s on s.code = i.segment_code and s.status = 'enabled'
),
wanted as (
    select r.id,
           case
               when r.segment_code = 'kraken-futures'
                   then r.exchange_symbol like 'PF\_%' and coalesce(a.auto_collect, false)
               else r.base_asset in ('BTC', 'ETH', 'SOL', 'XRP', 'DOGE') and r.rn = 1
           end as keep
      from ranked r
      left join asset a on a.code = r.base_asset
)
update exchange_instrument i
   set collect = w.keep,
       updated_at = now(),
       collect_changed_at = now(),
       collect_changed_by = 'ops/test-light-load.sql',
       collect_note = case when w.keep
                           then 'TEST light load: kept (five bases, one listing each; Kraken the auto-collect 25).'
                           else 'TEST light load: switched off to keep the 3-core test VPS breathing.' end
  from wanted w
 where w.id = i.id and i.collect is distinct from w.keep;

-- 2. Cadence. Explicit on every collecting row, so no segment falls back to dataset defaults
--    (snapshot's default is 2 s).
update segment_dataset sd
   set interval_s = v.interval_s, updated_at = now(), updated_by = 'ops/test-light-load.sql'
  from (values ('snapshot', 60), ('book', 60), ('depth', 600), ('trades', 15),
               ('candles', 60), ('candles_mark', 300), ('candles_index', 300),
               ('open_interest', 300), ('funding', 3600)) as v(dataset_code, interval_s)
  join segment s on true
 where sd.segment_code = s.code and s.status = 'enabled'
   and sd.dataset_code = v.dataset_code and sd.mode = 'collect'
   and sd.interval_s is distinct from v.interval_s;

select i.segment_code, count(*) filter (where i.collect) as collected
  from exchange_instrument i join segment s on s.code = i.segment_code and s.status = 'enabled'
 group by 1 order by 2 desc, 1;

commit;
