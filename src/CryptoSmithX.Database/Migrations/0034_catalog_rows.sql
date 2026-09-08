-- ============================================================================
-- CryptoSmith X — миграция 0034: каталог под четыре новых набора данных
-- (book, candles_mark, candles_index, spec_versions) — те же таблицы, тот же
-- приём, что 0014 уже применил к первым девяти. Все новые ячейки матрицы
-- заводятся `disabled` — как и таблицы 0032, каталог описывает то, чего пока
-- никто не собирает: включение — код и решение оператора, следующая фаза.
--
-- Часть фазы «только база» (см. 0030). Последний файл фазы.
--
--
-- ----------------------------------------------------------------------------
-- Четыре новых dataset — зачем каждый
-- ----------------------------------------------------------------------------
--   book           — book_topn (0032): топ книги по кадрам. Отдельно от
--                     depth (полосы 10/25/50bps, уже собираются) — это
--                     другая форма того же наблюдения: полосы это сумма,
--                     book — сырые уровни, из которых сумму можно
--                     пересчитать, а не наоборот.
--   candles_mark,
--   candles_index  — market_price_candle (0032), два ряда одной таблицы —
--                     ДВЕ строки каталога, а не одна, потому что режим
--                     сбора у mark и index может быть разным (например,
--                     площадка отдаёт mark по WS, а index только REST) —
--                     одна ячейка на два разных транспорта не выразить.
--   spec_versions  — запись новых версий в instrument_spec (0031) самим
--                     discovery, когда spec_hash меняется. `kind = feed`,
--                     хотя источник — то же дискавери, что уже собирается:
--                     это отдельное РЕШЕНИЕ (писать версию при изменении
--                     или только текущее состояние в exchange_instrument,
--                     как сегодня), которое матрица должна уметь включать
--                     и выключать независимо от discovery.
--
-- retention: book = 90 — единственное решение объёма ЗДЕСЬ, а не перенос
-- готового значения из задачи. Он же интервал у depth (0014): полосы и топ
-- книги — снимки одного и того же стакана, тот же порядок объёма, то же
-- обоснование retention, что depth унаследовал от snapshot.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- 1. dataset — четыре новые строки
-- ----------------------------------------------------------------------------
insert into dataset (code, name, description, kind, default_mode, default_interval_s, default_retention_days, sort_order) values
    ('book',           'Book (top-N)',    'Top-of-book snapshots and deltas, raw levels rather than the summed 10/25/50bps bands depth already stores.', 'feed', 'disabled', 10,   90,   100),
    ('candles_mark',   'Mark candles',    'OHLC candles of the mark price, separate from trade-price candles.',                                          'feed', 'disabled', 60,   null, 110),
    ('candles_index',  'Index candles',   'OHLC candles of the index price.',                                                                              'feed', 'disabled', 60,   null, 120),
    ('spec_versions',  'Spec versions',   'New instrument_spec rows written when discovery sees the spec hash change, instead of only the current row.', 'feed', 'disabled', 3600, null, 130);

comment on column dataset.default_retention_days is
    'null = не ротируется никогда. Для candles/rollup это ЗАКОН (0001: единственный источник '
    'для длинных прогонов; свечи делистнутых инструментов не удаляются — иначе ошибка '
    'выжившего в бэктестах), для funding — тоже (0006: funding_rate_history не ротируется). '
    'Для depth — 90: физически отдельного хранилища нет, глубина живёт колонками в '
    'market_snapshot(_latest) и ротируется вместе со snapshot, это не независимое правило. '
    'С 0034: для book — тоже 90, тем же обоснованием, что у depth: топ книги и полосы — '
    'снимки одного и того же стакана, тот же порядок объёма.';


-- Существующим trades/open_interest/liquidations — недостающий default_interval_s (0014
-- завёл их с null: они были disabled с самого начала и такт не задавался). Значения — из
-- уже принятого решения по этим наборам (dataset_setting ниже дублирует часть их же смысла
-- под другими ключами — namespace/backfill, не интервал).
update dataset set default_interval_s = 5    where code = 'trades'         and default_interval_s is null;
update dataset set default_interval_s = 60   where code = 'open_interest'  and default_interval_s is null;
update dataset set default_interval_s = 3600 where code = 'liquidations'   and default_interval_s is null;


-- ----------------------------------------------------------------------------
-- 2. segment_dataset — полный крест новых наборов × все сегменты, disabled
-- ----------------------------------------------------------------------------
-- Тот же приём, что 0014:218 применил к первым девяти датасетам: матрица ВСЕГДА полная —
-- строка на каждую пару (сегмент, датасет), даже там, где сегмент не умеет. Отсутствие
-- строки не должно значить «выключено», иначе «не умеет» и «выключили руками» неразличимы.
insert into segment_dataset (segment_code, dataset_code, mode, note, updated_by)
select s.code, d.code, 'disabled', null, '0034 migration'
  from segment s
 cross join (values ('book'), ('candles_mark'), ('candles_index'), ('spec_versions')) as d(code)
 on conflict (segment_code, dataset_code) do nothing;


-- ----------------------------------------------------------------------------
-- 3. segment_dataset_capability — крест новых ячеек × capability_key, NULL
-- ----------------------------------------------------------------------------
-- Тот же приём, что 0014:252. we_implement НЕ проставляется здесь: адаптеры объявят его
-- сами при сборке (ExchangeWorker.Build, как в 0029 «чего здесь нет и почему» — это код
-- следующей фазы), а не эта миграция от их имени. Честное «не знаем» до тех пор.
insert into segment_dataset_capability (segment_code, dataset_code, capability_key)
select sd.segment_code, sd.dataset_code, k.key
  from segment_dataset sd
 cross join capability_key k
 where sd.dataset_code in ('book', 'candles_mark', 'candles_index', 'spec_versions')
 on conflict (segment_code, dataset_code, capability_key) do nothing;


-- ----------------------------------------------------------------------------
-- 4. dataset_setting — параметры новых наборов, которых нет в mode/interval/retention
-- ----------------------------------------------------------------------------
insert into dataset_setting (dataset_code, key, value, kind, description) values
    ('trades',        'universe_top_n',      '50',  'int', 'How many top instruments (by turnover) to collect trades for, when the dataset is enabled.'),
    ('trades',        'backfill_hours',      '24',  'int', 'How many hours back to pull trades when an instrument first appears.'),
    ('trades',        'deep_backfill',        '0',  'int', 'Whether a deeper, one-off historical backfill beyond backfill_hours is requested (0 = no).'),
    ('book',          'levels',              '25',  'int', 'How many top-of-book levels to request per side.'),
    ('book',          'universe_top_n',      '50',  'int', 'How many top instruments (by turnover) to collect the book for.'),
    ('book',          'min_write_interval_s','10',  'int', 'Minimum seconds between two written book_topn rows for the same instrument, even if deltas arrive faster.'),
    ('open_interest',  'interval_s',         '60',  'int', 'Bucket size for open_interest_history when collected as its own feed (distinct from the inline OI already carried in the snapshot ticker).'),
    ('open_interest',  'backfill_hours',    '720',  'int', 'How many hours back to pull open interest history when an instrument first appears.'),
    ('candles_mark',   'backfill_hours',     '24',  'int', 'How many hours back to pull mark-price candles when an instrument first appears.'),
    ('candles_index',  'backfill_hours',     '24',  'int', 'How many hours back to pull index-price candles when an instrument first appears.'),
    ('liquidations',   'interval_s',       '3600',  'int', 'Bucket size for liquidation_volume_history.'),
    ('liquidations',   'backfill_hours',    '720',  'int', 'How many hours back to pull liquidation history when an instrument first appears.');
