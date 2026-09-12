-- The ten MEXC contracts that settle in the base coin.
--
-- They are inverse: a contract is worth contractSize of the QUOTE, not of the base. Read as base
-- units — which is what every other contract on that venue means — BTC_USD's contractSize of 100
-- became a hundred bitcoin a contract, and the grid showed 2.8 trillion dollars of depth at 50 bps
-- on a venue whose whole book is a few million.
--
-- Discovery no longer returns them, so nothing refreshes these rows; they are taken out of
-- collection explicitly as well, because "no collector happens to ask for it" and "this instrument
-- is not collected" are different statements and only the second one is recorded.
--
-- The readings already stored under the wrong contract model are left where they are. They stop
-- being refreshed from here, so the page ages them as it ages any other stale row, and every
-- instant this application prints is measured against the request rather than asserted. Removing
-- observations that were genuinely taken is a separate decision from stopping collection, and not
-- one this script makes.

begin;

create temporary table retired on commit drop as
select id, exchange_symbol
  from exchange_instrument
 where segment_code = 'mexc-perp'
   and quote_asset_raw = 'USD';

update exchange_instrument
   set collect = false,
       collect_note = 'inverse: settles in the base coin, so its sizes are quote units — out of a linear segment',
       collect_changed_at = now(),
       collect_changed_by = 'ops/retire-mexc-inverse-instruments.sql'
 where id in (select id from retired);

select 'retired', count(*) from retired;
select exchange_symbol, collect from exchange_instrument
 where segment_code = 'mexc-perp' and quote_asset_raw = 'USD' order by 1;

commit;
