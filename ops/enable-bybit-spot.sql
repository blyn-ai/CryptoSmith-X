-- Приёмка Bybit Spot на контуре: сегмент включается, собираются все датасеты,
-- которые адаптер объявил, и ровно десять инструментов помечаются к сбору.
--
-- Десять — рамка приёмки, а не потолок площадки. Лимит здесь считается в
-- ЗАПРОСАХ, а не в весах, и гейт у спота ОБЩИЙ с перпами этой же биржи:
-- api.bybit.com обслуживает обе поверхности (0056). Значит каждый инструмент,
-- добавленный сюда, тратит проходы перпов — расширять набор нужно с оглядкой на
-- них, а не на отдельный бюджет, которого у спота нет.
--
-- Десять символов раз в пять минут — 0.03 запроса в секунду из общего потолка.
--
-- Снимок пишется по отмеченным инструментам; их же обходят глубина, лента и
-- свечи.
--
-- HOW TO RUN IT
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-bybit-spot.sql
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/enable-bybit-spot.sql
--
-- Запускать ПОВТОРНО после первого прохода discovery: инструментов до него нет,
-- и отмечать к сбору нечего. Скрипт идемпотентен и сам скажет, сколько отметил.

begin;

update segment
   set status = 'enabled', updated_at = now(), updated_by = 'ops/enable-bybit-spot.sql'
 where code = 'bybit-spot';

create temporary table wanted (dataset_code text, interval_s int) on commit drop;
insert into wanted values
    ('discovery',    900),
    ('snapshot',      60),
    ('candles',       60),
    ('depth',        300),
    -- Лента складывается из того же вызова, что и стакан, поэтому отдельных
    -- запросов не стоит: этот интервал только опустошает уже собранное.
    ('trades',        60),
    ('spec_versions', 3600);

update segment_dataset sd
   set mode = 'collect', interval_s = w.interval_s, transport = 'rest',
       updated_at = now(), updated_by = 'ops/enable-bybit-spot.sql'
  from wanted w
 where sd.segment_code = 'bybit-spot' and sd.dataset_code = w.dataset_code;

-- Сначала снять всё: автоприём новых листингов включает инструмент, чья базовая
-- валюта уже одобрена (asset.auto_collect, 0029), и без этого шага набор растёт
-- сам — на Binance он так и вырос до 52 и получил 429 на первом же проходе.
update exchange_instrument
   set collect = false,
       collect_note = 'bybit-spot: вне десятки приёмки; гейт общий с перпами этой биржи',
       collect_changed_at = now(),
       collect_changed_by = 'ops/enable-bybit-spot.sql'
 where segment_code = 'bybit-spot' and collect;

-- Десять инструментов. Помечаются только те, что discovery уже открыл, поэтому
-- первый запуск отметит ноль — так и задумано, см. шапку.
update exchange_instrument
   set collect = true,
       collect_note = 'приёмка bybit-spot: десять инструментов, не рынок',
       collect_changed_at = now(),
       collect_changed_by = 'ops/enable-bybit-spot.sql'
 where segment_code = 'bybit-spot'
   and exchange_symbol in (
       'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','DOGEUSDT',
       'ADAUSDT','BNBUSDT','LINKUSDT','AVAXUSDT','LTCUSDT');

select 'помечено к сбору', count(*) from exchange_instrument
 where segment_code = 'bybit-spot' and collect;

select dataset_code, mode, interval_s from segment_dataset
 where segment_code = 'bybit-spot' and mode <> 'disabled' order by dataset_code;

commit;
