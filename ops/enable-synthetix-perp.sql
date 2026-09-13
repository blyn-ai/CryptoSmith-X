-- Включение Synthetix на тесте (plans/prompt-dex-five-venues.md, шаг 5). Инструменты к сбору отбирает
-- discovery по политике 0029 (asset.auto_collect). depth раз в 15 с — это свежесть bid/ask и размеров.
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-synthetix-perp.sql

begin;

update segment set status = 'enabled', updated_at = now(), updated_by = 'ops/enable-synthetix-perp.sql'
 where code = 'synthetix-perp';

create temporary table wanted (dataset_code text, interval_s int) on commit drop;
insert into wanted values
    ('discovery', 900), ('snapshot', 15), ('candles', 60), ('funding', 3600),
    ('depth', 15), ('trades', 60), ('spec_versions', 3600);

update segment_dataset sd
   set mode = 'collect', interval_s = w.interval_s, transport = 'rest',
       updated_at = now(), updated_by = 'ops/enable-synthetix-perp.sql'
  from wanted w
 where sd.segment_code = 'synthetix-perp' and sd.dataset_code = w.dataset_code;

select dataset_code, mode, interval_s from segment_dataset
 where segment_code = 'synthetix-perp' and mode <> 'disabled' order by 1;

commit;
