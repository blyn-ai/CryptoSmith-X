-- ============================================================================
-- csx-proto: сиды справочников + Kraken Futures засеян руками.
-- Значения — не выдуманные: это то, что мы установили за время работы
-- (дамп на 300 млн свечей, вердикты адаптеров, WS-заход).
-- ============================================================================

-- ── справочник видов данных ──────────────────────────────────────────────
insert into collection (code, name, description, kind, default_mode, default_interval_s, default_retention_days, sort_order) values
 ('snapshot',      'Snapshot',       'Срез рынка: цена, спред, mark/index, funding, открытый интерес. Не восстанавливается задним числом.', 'feed',    'collect',   10,   90, 10),
 ('depth',         'Depth',          'Глубина стакана в полосах 10/25/50 bps — во сколько обойдётся проскальзывание. Истории не существует нигде.', 'feed', 'collect',   60,   90, 20),
 ('candles',       'Candles',        'Минутные бары OHLCV. Не ротируются никогда: единственный источник для длинных прогонов.', 'feed',       'collect',   60, null, 30),
 ('funding',       'Funding',        'Ставка funding и её история.',                                                              'feed',    'collect', 3600, null, 40),
 ('discovery',     'Discovery',      'Какие инструменты торгуются: появления, делистинги, смена статуса.',                        'feed',    'collect', 3600, null, 50),
 ('rollup',        'Rollup',         'Производные таймфреймы из минуток. Считаем сами, с биржи не берётся.',                      'derived', 'collect',   60, null, 60),
 ('trades',        'Trades',         'Лента сделок. Объём другого порядка — требует отдельного решения по хранилищу.',            'feed',    'disabled', null, null, 70),
 ('open_interest', 'Open interest',  'Открытый интерес. У части бирж приходит внутри тикера, отдельной петлёй не собирается.',    'feed',    'disabled', null, null, 80),
 ('liquidations',  'Liquidations',   'Ликвидации. Почти везде только через сокет, истории нет ни у кого.',                        'feed',    'disabled', null, null, 90);

-- ── справочник ключей capability ─────────────────────────────────────────
insert into capability_key (key, kind, description, human_editable, loss_relevant, sort_order) values
 ('venue_supports',     'bool', 'Биржа предоставляет этот вид данных',                              true,  false, 10),
 ('we_implement',       'bool', 'У нас написан код для него',                                       false, false, 20),
 ('transports_venue',   'list', 'Какие транспорты предлагает биржа',                                true,  false, 30),
 ('transports_us',      'list', 'Какие транспорты реализованы у нас',                               false, false, 40),
 ('api_versions_venue', 'list', 'Версии API у биржи',                                               true,  false, 50),
 ('api_versions_us',    'list', 'Версии, которые использует наш адаптер',                           false, false, 60),
 ('history_depth',      'text', 'Насколько назад биржа отдаёт историю: none / limited(...) / full', true,  true,  70),
 ('auth',               'text', 'public или private (нужны ключи)',                                 false, false, 80),
 ('rate_limit',         'text', 'Ограничение частоты, как его описывает биржа',                     true,  false, 90);

-- ── биржа ────────────────────────────────────────────────────────────────
insert into exchange (code, name, status, adapter, base_url, ws_url) values
 ('kraken-futures', 'Kraken Futures', 'enabled', 'kraken-futures',
  'https://futures.kraken.com', 'wss://futures.kraken.com/ws/v1');

-- ── политика: строка на КАЖДУЮ коллекцию, полная матрица ─────────────────
insert into exchange_collection (exchange_code, collection_code, mode, transport, note, updated_by)
select 'kraken-futures', c.code,
       case c.code when 'trades' then 'disabled'
                   when 'open_interest' then 'disabled'
                   when 'liquidations' then 'disabled'
                   else 'collect' end,
       case c.code when 'snapshot' then 'ws'
                   when 'depth' then 'ws'
                   when 'candles' then 'rest'
                   when 'funding' then 'rest'
                   when 'discovery' then 'rest'
                   else null end,
       case c.code when 'open_interest' then 'Приходит внутри snapshot: отдельной петли нет и не нужно'
                   when 'trades' then 'Биржа отдаёт, у нас не реализовано — это бэклог, а не отказ'
                   else null end,
       'denis'
  from collection c;

-- ── capability: полный крест, включая «ещё не установлено» ───────────────
insert into exchange_collection_capability (exchange_code, collection_code, capability_key)
select ec.exchange_code, ec.collection_code, k.key
  from exchange_collection ec cross join capability_key k;

-- Значения, которые мы установили сами. Каждое — с источником и датой.
update exchange_collection_capability c set
    value = v.value, source = v.source, filled_at = v.at, filled_by = v.by, valid_since = v.since
from (values
  -- snapshot: тикеры батчатся одним вызовом, WS-фид написан
  ('snapshot','venue_supports','true','declared',   '2026-08-31'::timestamptz,'kraken adapter','2026-08-31'::timestamptz),
  ('snapshot','we_implement','true','declared',     '2026-08-31','kraken adapter','2026-08-31'),
  ('snapshot','transports_venue','rest,ws','manual','2026-08-31','denis','2026-08-31'),
  ('snapshot','transports_us','rest,ws','declared', '2026-09-01','kraken adapter','2026-09-01'),
  ('snapshot','history_depth','none','manual',      '2026-08-31','denis',null),
  ('snapshot','auth','public','declared',           '2026-08-31','kraken adapter',null),
  ('snapshot','api_versions_venue','v3','manual',   '2026-08-31','denis',null),
  ('snapshot','api_versions_us','v3','declared',    '2026-08-31','kraken adapter',null),
  -- depth: книга собирается из WS снапшот+дельты с проверкой seq
  ('depth','venue_supports','true','declared',      '2026-08-31','kraken adapter',null),
  ('depth','we_implement','true','declared',        '2026-09-01','kraken adapter','2026-09-01'),
  ('depth','transports_venue','rest,ws','manual',   '2026-08-31','denis',null),
  ('depth','transports_us','rest,ws','declared',    '2026-09-01','kraken adapter','2026-09-01'),
  ('depth','history_depth','none','manual',         '2026-08-31','denis',null),
  ('depth','auth','public','declared',              '2026-08-31','kraken adapter',null),
  -- candles: глубину измерили дампом — 20 месяцев, 300 млн баров
  ('candles','venue_supports','true','declared',    '2026-08-31','kraken adapter',null),
  ('candles','we_implement','true','declared',      '2026-08-31','kraken adapter',null),
  ('candles','transports_venue','rest','manual',    '2026-08-31','denis',null),
  ('candles','transports_us','rest','declared',     '2026-08-31','kraken adapter',null),
  ('candles','history_depth','full','probed',       '2026-08-22','dump probe','2026-08-22'),
  ('candles','auth','public','declared',            '2026-08-31','kraken adapter',null),
  -- funding: у Кракена полноценный historicalfundingrates
  ('funding','venue_supports','true','declared',    '2026-08-31','kraken adapter',null),
  ('funding','we_implement','true','declared',      '2026-08-31','kraken adapter',null),
  ('funding','transports_venue','rest','manual',    '2026-08-31','denis',null),
  ('funding','transports_us','rest','declared',     '2026-08-31','kraken adapter',null),
  ('funding','history_depth','full','probed',       '2026-09-01','funding backfill','2026-09-01'),
  ('funding','auth','public','declared',            '2026-08-31','kraken adapter',null),
  -- discovery: биржа показывает только сегодняшний список
  ('discovery','venue_supports','true','declared',  '2026-08-31','kraken adapter',null),
  ('discovery','we_implement','true','declared',    '2026-08-31','kraken adapter',null),
  ('discovery','transports_venue','rest','manual',  '2026-08-31','denis',null),
  ('discovery','transports_us','rest','declared',   '2026-08-31','kraken adapter',null),
  ('discovery','history_depth','none','manual',     '2026-08-31','denis',null),
  ('discovery','auth','public','declared',          '2026-08-31','kraken adapter',null),
  -- rollup: derived, биржа тут ни при чём
  ('rollup','venue_supports','false','declared',    '2026-08-31','hub',null),
  ('rollup','we_implement','true','declared',       '2026-08-31','hub',null),
  -- trades: биржа отдаёт, мы нет — это бэклог
  ('trades','venue_supports','true','manual',       '2026-09-02','denis',null),
  ('trades','we_implement','false','declared',      '2026-09-02','kraken adapter',null),
  ('trades','transports_venue','rest,ws','manual',  '2026-09-02','denis',null),
  ('trades','transports_us','','declared',          '2026-09-02','kraken adapter',null),
  -- open_interest: биржа отдаёт внутри тикера, отдельной петли у нас нет
  ('open_interest','venue_supports','true','manual','2026-09-02','denis',null),
  ('open_interest','we_implement','false','declared','2026-09-02','kraken adapter',null),
  ('open_interest','history_depth','none','manual', '2026-09-02','denis',null),
  -- liquidations: НЕ ПРОВЕРЯЛИ. Значение остаётся null — это честнее догадки.
  ('liquidations','we_implement','false','declared','2026-09-02','kraken adapter',null)
) as v(coll, key, value, source, at, by, since)
where c.exchange_code = 'kraken-futures'
  and c.collection_code = v.coll and c.capability_key = v.key;

-- ── журнал: реальное событие, а не выдуманное ────────────────────────────
insert into capability_log (exchange_code, collection_code, capability_key, old_value, new_value, source, changed_at, changed_by, note) values
 ('kraken-futures','depth','we_implement','false','true','declared','2026-09-01 12:28:00+00','deploy 6424c23',
  'Появился WS-сборщик книги: снапшот + дельты с проверкой seq. Свежесть глубины по всем 274 парам упала с ~60 с до 7 с.'),
 ('kraken-futures','depth','transports_us','rest','rest,ws','declared','2026-09-01 12:28:00+00','deploy 6424c23',
  'REST остался деградацией и кросс-чеком, не удалён.'),
 ('kraken-futures','candles','history_depth',null,'full','probed','2026-08-22 08:20:00+00','dump probe',
  'Выкачано 300 231 107 баров за 2025-01-01…2026-08-22 без единой ошибки.');
