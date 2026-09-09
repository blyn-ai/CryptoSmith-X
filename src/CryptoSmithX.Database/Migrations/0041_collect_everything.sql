-- ============================================================================
-- CryptoSmith X — миграция 0041: включить всё, что теперь есть кому писать.
--
-- Фаза 4, вторая половина. Первая (0039–0040) наполнила колонки у наборов,
-- которые уже собирались. Эта включает шесть наборов, у которых с 0032 были
-- таблицы и не было писателя вовсе: trades, book, open_interest, liquidations,
-- candles_mark, candles_index, плюс spec_versions (0031).
--
-- Требование владельца дословно: «данные, которые хотели собирать (для чего в
-- базе есть таблицы), — собираются, всё что отдаёт биржа; никаких disabled и
-- никаких not-implemented».
--
--
-- ----------------------------------------------------------------------------
-- A. Почему mode ставит миграция, хотя 0034 писала «ставит оператор»
-- ----------------------------------------------------------------------------
-- 0034 завела строки матрицы `disabled` и оставила включение оператору — это
-- было правильно тогда: писателя не существовало, и `collect` означал бы
-- петлю, которая ничего не пишет. Теперь писатель есть у каждого из шести, и
-- владелец — он же оператор — потребовал включения явно. Правило «mode ставит
-- оператор» не отменяется: этот файл ЕСТЬ его решение, записанное там же, где
-- живут остальные решения, а не тихо зашитое в код.
--
-- Включается только то, что площадка реально отдаёт, и это проверено живьём
-- 2026-09-09, а не выведено из документации:
--
--   trades          все четыре — каналы сняты живьём (Fixtures/trades).
--   book            все четыре — стакан уже строился, наружу не отдавался.
--   open_interest   все четыре: Binance /futures/data/openInterestHist и
--                   аналитика Kraken — настоящие серии; WEEX (404 на обеих
--                   генерациях API) и Hyperliquid (info отвергает запрос) серии
--                   не имеют вовсе, поэтому у них это наша собственная выборка
--                   текущего OI на сетку, source='rest'.
--   liquidations    Binance (!forceOrder@arr) и Kraken (аналитика
--                   liquidation-volume). У WEEX канала ликвидаций нет
--                   (plans/ws-coverage.md), у Hyperliquid — тоже: остаются
--                   disabled, и это факт о площадке, а не пропуск.
--   candles_mark/   только Binance: markPriceKlines/indexPriceKlines. У трёх
--   candles_index   остальных таких рядов нет ни на одном транспорте.
--   spec_versions   все четыре — пишет DiscoveryCollector из данных, которые
--                   у него и так на руках.
--
--
-- ----------------------------------------------------------------------------
-- B. source='ws' у liquidation_volume_history — новое значение CHECK
-- ----------------------------------------------------------------------------
-- 0032 разрешила там только ('analytics', 'backfill'), исходя из того, что
-- агрегат ликвидаций всегда приходит готовым от площадки. У Binance это больше
-- не так: публичного агрегата у неё нет вовсе, есть только поток событий
-- !forceOrder@arr. Сумма по этим событиям на ту же часовую сетку — это тот же
-- факт площадки, просто доставленный поштучно, и он обязан быть отличим от
-- готового агрегата Kraken: наш бакет начинается тогда, когда мы начали
-- слушать, и не знает того, что прошло мимо упавшего сокета.
--
-- Это НЕ то, от чего предостерегала шапка 0032 («не из собственных event-строк
-- trade.trade_type=liquidation»). Там речь о производной от производной — о
-- пересчёте по нашей же ленте сделок; у Binance лента ликвидации не размечает
-- вообще, так что суммировать по ней было бы нечего. Здесь суммируется
-- отдельный, специально предназначенный поток самой биржи.
--
-- NOT VALID по той же причине, что и в 0035: существующих строк в таблице ноль,
-- проверять нечего, а полная валидация — это скан ради нуля.
-- ============================================================================

alter table liquidation_volume_history drop constraint liquidation_volume_history_source_check;
alter table liquidation_volume_history
    add constraint liquidation_volume_history_source_check
    check (source in ('analytics', 'ws', 'backfill')) not valid;

comment on column liquidation_volume_history.source is
    '''analytics'' — готовый агрегат площадки (Kraken: серия liquidation-volume); ''ws'' — наша '
    'сумма по потоку отдельных событий ликвидации самой биржи (Binance: !forceOrder@arr), у которой '
    'публичного агрегата нет; ''backfill'' — догоняющий пересчёт. Разница между первым и вторым '
    'существенна для читателя: ''ws''-бакет начинается с момента, когда мы подключились, и не '
    'содержит того, что прошло мимо упавшего сокета, — ''analytics'' содержит.';


-- ----------------------------------------------------------------------------
-- C. Включение наборов
-- ----------------------------------------------------------------------------
-- Ровно те пары segment×dataset, где адаптер объявляет capability (см. §A).
-- updated_by именует эту миграцию, чтобы в UI было видно, кто именно включил.
-- dataset_setting не трогается вовсе: 0034 уже посеяла backfill_hours и
-- book.levels, и посеянные значения этой работе подходят как есть.

update segment_dataset
   set mode       = 'collect',
       updated_at = now(),
       updated_by = '0041 migration'
 where dataset_code in ('trades', 'book', 'open_interest', 'spec_versions')
   and mode <> 'collect';

update segment_dataset
   set mode       = 'collect',
       updated_at = now(),
       updated_by = '0041 migration'
 where dataset_code = 'liquidations'
   and segment_code in ('binance-usdm', 'kraken-futures')
   and mode <> 'collect';

update segment_dataset
   set mode       = 'collect',
       updated_at = now(),
       updated_by = '0041 migration'
 where dataset_code in ('candles_mark', 'candles_index')
   and segment_code = 'binance-usdm'
   and mode <> 'collect';


-- ----------------------------------------------------------------------------
-- D. Интервалы петель
-- ----------------------------------------------------------------------------
-- trades и book — не опрос площадки, а слив уже накопленного: интервал решает,
-- сколько держится в памяти между записями, а не сколько рынка видно. 5 с у
-- ленты (буфер на 200k событий) и 15 с у стакана — последнее с оглядкой на
-- посеянный 0034 book.min_write_interval_s = 10 с: чаще, чем раз в 10 с, кадр
-- одного инструмента писать не собирались, и 15 держится с той стороны черты.
--
-- open_interest и liquidations — настоящие исторические запросы к площадке, раз
-- в 5 и 15 минут: бакеты у них 5 мин и час, опрашивать чаще бакета нечего.
-- candles_mark/index — раз в минуту, как торговые свечи: бар тот же минутный.

update segment_dataset set interval_s = 5,   updated_at = now(), updated_by = '0041 migration'
 where dataset_code = 'trades';
update segment_dataset set interval_s = 15,  updated_at = now(), updated_by = '0041 migration'
 where dataset_code = 'book';
update segment_dataset set interval_s = 300, updated_at = now(), updated_by = '0041 migration'
 where dataset_code = 'open_interest';
update segment_dataset set interval_s = 900, updated_at = now(), updated_by = '0041 migration'
 where dataset_code = 'liquidations';
update segment_dataset set interval_s = 60,  updated_at = now(), updated_by = '0041 migration'
 where dataset_code in ('candles_mark', 'candles_index');
