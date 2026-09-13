-- Приёмка OKX Spot на контуре: сегмент включается, собираются все датасеты,
-- которые адаптер объявил, и ровно десять инструментов помечаются к сбору.
--
-- Десять — рамка приёмки. Гейт у спота ОБЩИЙ с перпами: www.okx.com обслуживает
-- обе поверхности, и потолок один на двоих (0058). Тот самый потолок сегодня
-- пришлось опустить с десяти запросов в секунду до пяти, когда площадка
-- ответила 50011 на первом же проходе глубины. Каждый инструмент, добавленный
-- сюда, тратит проходы перпов.
--
-- Глубина идёт через books-full на 5000 уровней — тот же вызов, что у перпов.
-- Десять символов раз в пять минут это +0.03 запроса в секунду к потолку в пять.
--
-- Снимок пишется по отмеченным инструментам; их же обходят глубина, лента и
-- свечи.
--
-- HOW TO RUN IT
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-okx-spot.sql
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/enable-okx-spot.sql
--
-- Запускать ПОВТОРНО после первого прохода discovery: инструментов до него нет,
-- и отмечать к сбору нечего. Скрипт идемпотентен и сам скажет, сколько отметил.

begin;

update segment
   set status = 'enabled', updated_at = now(), updated_by = 'ops/enable-okx-spot.sql'
 where code = 'okx-spot';

create temporary table wanted (dataset_code text, interval_s int) on commit drop;
insert into wanted values
    ('discovery',    900),
    ('snapshot',      15),
    ('candles',       60),
    ('depth',        300),
    -- Лента складывается из того же вызова, что и стакан, поэтому отдельных
    -- запросов не стоит: этот интервал только опустошает уже собранное.
    ('trades',        60),
    ('spec_versions', 3600);

update segment_dataset sd
   set mode = 'collect', interval_s = w.interval_s, transport = 'rest',
       updated_at = now(), updated_by = 'ops/enable-okx-spot.sql'
  from wanted w
 where sd.segment_code = 'okx-spot' and sd.dataset_code = w.dataset_code;

-- Сначала снять всё: автоприём новых листингов включает инструмент, чья базовая
-- валюта уже одобрена (asset.auto_collect, 0029), и без этого шага набор растёт
-- сам — на Binance он так и вырос до 52 и получил 429 на первом же проходе.
update exchange_instrument
   set collect = false,
       collect_note = 'okx-spot: вне десятки приёмки; гейт общий с перпами этой биржи',
       collect_changed_at = now(),
       collect_changed_by = 'ops/enable-okx-spot.sql'
 where segment_code = 'okx-spot' and collect;

-- Десять инструментов. Помечаются только те, что discovery уже открыл, поэтому
-- первый запуск отметит ноль — так и задумано, см. шапку.
update exchange_instrument
   set collect = true,
       collect_note = 'приёмка okx-spot: десять инструментов, не рынок',
       collect_changed_at = now(),
       collect_changed_by = 'ops/enable-okx-spot.sql'
 where segment_code = 'okx-spot'
   and exchange_symbol in (
       'BTC-USDT','ETH-USDT','SOL-USDT','XRP-USDT','DOGE-USDT',
       'ADA-USDT','BNB-USDT','LINK-USDT','AVAX-USDT','LTC-USDT');

select 'помечено к сбору', count(*) from exchange_instrument
 where segment_code = 'okx-spot' and collect;

select dataset_code, mode, interval_s from segment_dataset
 where segment_code = 'okx-spot' and mode <> 'disabled' order by dataset_code;

commit;
