-- Приёмка Binance Spot на контуре: сегмент включается, собираются все датасеты,
-- которые адаптер объявил, и ровно десять инструментов помечаются к сбору.
--
-- Десять, а не рынок, и это ПОТОЛОК, а не выборка. Глубина стоит 249 весов на
-- символ (замерено по x-mbx-used-weight-1m) при бюджете 6000 на скользящую
-- минуту, и обход выдаёт весь свой вес за несколько секунд — сколько бы ни
-- было между проходами. Значит ограничение лежит на РАЗМЕРЕ обхода: примерно
-- двадцать четыре символа, и растягивание интервала его не поднимает.
--
-- Проверено ошибкой: когда discovery открыл сегмент, 52 инструмента включились
-- сами (asset.auto_collect, 0029), и первый же проход глубины получил 429.
--
-- Поэтому скрипт не только отмечает десять, но и СНИМАЕТ отметку со всех
-- остальных в этом сегменте. Иначе автоприём тихо вернёт набор за потолок.
--
-- Снимок пишется по ВСЕМ инструментам сегмента, не только по отмеченным: это
-- один массовый вызов, и строка без котировки — всё ещё строка. Глубина, лента
-- и свечи идут по отмеченным.
--
-- HOW TO RUN IT
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-binance-spot.sql
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/enable-binance-spot.sql
--
-- Запускать ПОВТОРНО после первого прохода discovery: инструментов до него нет,
-- и отмечать к сбору нечего. Скрипт идемпотентен и сам скажет, сколько отметил.

begin;

update segment
   set status = 'enabled', updated_at = now(), updated_by = 'ops/enable-binance-spot.sql'
 where code = 'binance-spot';

create temporary table wanted (dataset_code text, interval_s int) on commit drop;
insert into wanted values
    ('discovery',    900),
    ('snapshot',      15),
    ('candles',       60),
    -- 250 весов на символ; десять инструментов раз в пять минут — 500 весов в
    -- минуту из 6000.
    ('depth',        300),
    -- Лента складывается из того же вызова, что и стакан, поэтому отдельных
    -- запросов не стоит: этот интервал только опустошает уже собранное.
    ('trades',        60),
    ('spec_versions', 3600);

update segment_dataset sd
   set mode = 'collect', interval_s = w.interval_s, transport = 'rest',
       updated_at = now(), updated_by = 'ops/enable-binance-spot.sql'
  from wanted w
 where sd.segment_code = 'binance-spot' and sd.dataset_code = w.dataset_code;

-- Сначала снять всё: автоприём новых листингов включает инструмент, чья базовая
-- валюта уже одобрена (asset.auto_collect), и без этого шага набор растёт сам.
update exchange_instrument
   set collect = false,
       collect_note = 'binance-spot: вне десятки приёмки; глубина стоит 249 весов на символ',
       collect_changed_at = now(),
       collect_changed_by = 'ops/enable-binance-spot.sql'
 where segment_code = 'binance-spot' and collect;

-- Десять инструментов. Помечаются только те, что discovery уже открыл, поэтому
-- первый запуск отметит ноль — так и задумано, см. шапку.
update exchange_instrument
   set collect = true,
       collect_note = 'приёмка binance-spot: десять инструментов, не рынок',
       collect_changed_at = now(),
       collect_changed_by = 'ops/enable-binance-spot.sql'
 where segment_code = 'binance-spot'
   and exchange_symbol in (
       'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','DOGEUSDT',
       'ADAUSDT','BNBUSDT','LINKUSDT','AVAXUSDT','LTCUSDT');

select 'помечено к сбору', count(*) from exchange_instrument
 where segment_code = 'binance-spot' and collect;

select dataset_code, mode, interval_s from segment_dataset
 where segment_code = 'binance-spot' and mode <> 'disabled' order by dataset_code;

commit;
