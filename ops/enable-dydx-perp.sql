-- Включение dYdX v4 на тесте (plans/prompt-dex-five-venues.md, шаг 5).
--
-- Наборы — ровно те, что адаптер объявил. Инструменты к сбору отбирает сам discovery по политике
-- 0029: базовая монета в 25 авто-одобренных активах (asset.auto_collect). Промпт просит «collect у
-- всех торгующихся, как у остальных перпов», но у остальных перпов именно так и есть — по 25–50, а
-- весь рынок собирается только у Kraken Futures; прод приведён к этому сегодня же. BTC и ETH в
-- списке, так что приёмка на /studio/v2/BTC и ETH от этого не зависит.
--
-- depth раз в 60 с, а не 300: с него адаптер берёт bid/ask и размеры топа для снимка, и это их
-- свежесть. ~25 рынков × (книга + лента) = ~50 запросов в минуту при бюджете 8 в секунду.
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-dydx-perp.sql

begin;

update segment set status = 'enabled', updated_at = now(), updated_by = 'ops/enable-dydx-perp.sql'
 where code = 'dydx-perp';

create temporary table wanted (dataset_code text, interval_s int) on commit drop;
insert into wanted values
    ('discovery', 900), ('snapshot', 60), ('candles', 60), ('funding', 3600),
    ('depth', 60), ('trades', 60), ('liquidations', 900), ('open_interest', 3600),
    ('spec_versions', 3600);

update segment_dataset sd
   set mode = 'collect', interval_s = w.interval_s, transport = 'rest',
       updated_at = now(), updated_by = 'ops/enable-dydx-perp.sql'
  from wanted w
 where sd.segment_code = 'dydx-perp' and sd.dataset_code = w.dataset_code;

select dataset_code, mode, interval_s from segment_dataset
 where segment_code = 'dydx-perp' and mode <> 'disabled' order by 1;

commit;
