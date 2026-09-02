-- ============================================================================
-- CryptoSmith X — миграция 0014: коллекция как сущность (фаза 1 — модель и хаб).
--
-- «Какие данные собираем» было размазано по четырём местам: CHECK на
-- collector_status.collector, пять колонок интервалов на exchange, жёсткий
-- массив петель в ExchangeWorker, хардкод в разметке. Добавить новый вид
-- данных значило ALTER TABLE + CHECK + колонка + ветка супервизора + правка
-- cshtml. Теперь это строка в справочнике.
--
-- Форма — из прототипа plans/csx-proto.sql/csx-proto-seed.sql (согласованная
-- владельцем модель, не черновик): collection, capability_key,
-- exchange_collection, exchange_collection_capability, capability_log —
-- имена, ключи и ограничения совпадают. Два расхождения, оба сознательные:
--
--   * collection_setting — которого в прототипе НЕТ. Прототип покрывал только
--     mode/interval/retention; реальный setting несёт четыре знания, которые
--     не ложатся ни в один из этих трёх столбцов (candle/funding backfill —
--     это не интервал и не retention; derived_timeframes — список, а не
--     число; delist_after_missed_discoveries — счётчик проходов). Плодить
--     под каждое отдельную nullable-колонку на collection было бы ровно тем
--     analog антипаттерном, который сама модель и лечит; вместо этого —
--     key/value/kind точно как у setting, только на уровне коллекции, а не
--     глобально. Централизованно фиксирую как найденное расхождение с
--     прототипом, а не тихо обхожу.
--   * collection.description — на английском, не на русском, как в
--     csx-proto-seed.sql: это копия для admin UI, а он весь на английском
--     (Details.cshtml и соседи). Прототип обсуждался по-русски, продукт — нет.
--
-- Порядок именно такой: справочник -> сиды -> ПОЛНЫЙ бэкфилл матрицы для
-- каждой существующей биржи -> и только потом FK collector_status.collector
-- -> collection.code вместо CHECK. Иначе работающий хаб упадёт на первой же
-- записи статуса в промежутке между созданием таблиц и бэкфиллом.
--
-- Что остаётся на exchange: adapter, base_url, charts_url, ws_url,
-- quote_assets, blacklist, status, name/description, updated_by/at — это НЕ
-- про то, что собираем, а про то, как достучаться и включена ли биржа
-- вообще. Пять интервальных колонок отсюда уезжают в exchange_collection.
--
-- Что НЕ вошло и почему:
--   * Per-exchange override retention_days для 'snapshot' физически не может
--     быть соблюдён: market_snapshot партиционирована по МЕСЯЦУ на ВСЕ биржи
--     разом — дроп партиции не умеет пощадить одну биржу. Колонка
--     exchange_collection.retention_days для 'snapshot' поэтому пишется (это
--     решение оператора, оно должно быть видно), но RetentionJob её не
--     читает — только collection.default_retention_days. Задокументировано
--     на самой колонке и в RetentionJob; это найденное противоречие модели с
--     физикой партиций, не забытая недоделка.
--   * capability_key.rate_limit и api_versions_* не заполняются кодом в этой
--     миграции/фазе — это ручные/probed факты (human_editable=true в
--     прототипе), а не то, что адаптер может честно продекларировать сам.
--     Строки в exchange_collection_capability под них создаются (полный
--     крест, как в прототипе) со значением null — «ещё не установлено»,
--     честнее прочерка в коде.
-- ============================================================================

-- ── справочник видов данных ──────────────────────────────────────────────
create table collection (
    code                    text     primary key,
    name                    text     not null,
    description             text     not null,
    kind                    text     not null check (kind in ('feed', 'derived')),
    default_mode            text     not null check (default_mode in ('disabled', 'on_demand', 'collect')),
    default_interval_s      integer  check (default_interval_s > 0),
    default_retention_days  integer  check (default_retention_days > 0),
    sort_order              smallint not null default 100
);

comment on table collection is
    'Справочник видов собираемых данных (~десяток строк). Новый вид данных — вставка строки '
    'сюда, не ALTER TABLE. kind=derived — считается хабом из уже собранного (rollup), '
    'ось «биржа» к нему не применима как к фиду с биржи, но матрица его всё равно держит '
    '(строка на каждую биржу) ради единообразия — реально исполняется только против ServiceExchange.';
comment on column collection.default_retention_days is
    'null = не ротируется никогда. Для candles/rollup это ЗАКОН (0001: единственный источник '
    'для длинных прогонов; свечи делистнутых инструментов не удаляются — иначе ошибка '
    'выжившего в бэктестах), для funding — тоже (0006: funding_rate_history не ротируется). '
    'Для depth — 90: физически отдельного хранилища нет, глубина живёт колонками в '
    'market_snapshot(_latest) и ротируется вместе со snapshot, это не независимое правило.';

-- ── коллекция-специфичные настройки, которых нет в mode/interval/retention ─
create table collection_setting (
    collection_code text        not null references collection (code),
    key             text        not null,
    value           text        not null,
    kind            text        not null check (kind in ('int', 'text', 'int_list')),
    description     text        not null,
    updated_at      timestamptz not null default now(),
    updated_by      text,
    primary key (collection_code, key)
);

comment on table collection_setting is
    'Тот же приём, что у глобального setting (key/value/kind), но на уровне коллекции — для '
    'параметров, которые не являются ни интервалом, ни retention (окно бэкфилла, список '
    'таймфреймов). Нет в прототипе csx-proto.sql: он не покрывал эти четыре значения. UI для '
    'правки — фаза 2, здесь только хранение и чтение хабом.';

-- ── справочник ключей capability ─────────────────────────────────────────
create table capability_key (
    key            text     primary key,
    kind           text     not null check (kind in ('bool', 'int', 'text', 'list')),
    description    text     not null,
    human_editable boolean  not null default false,
    loss_relevant  boolean  not null default false,
    sort_order     smallint not null default 100
);

comment on table capability_key is
    'Тот же приём, что в setting: key/value/kind/description. Новый вид capability — строка, '
    'не ALTER TABLE.';

-- ── матрица: политика (решение человека) — ВСЕГДА полная ─────────────────
create table exchange_collection (
    exchange_code   text        not null references exchange (code),
    collection_code text        not null references collection (code),
    mode            text        not null check (mode in ('disabled', 'on_demand', 'collect')),
    interval_s      integer     check (interval_s > 0),
    retention_days  integer     check (retention_days > 0),
    transport       text        check (transport in ('rest', 'ws')),
    note            text,
    updated_at      timestamptz not null default now(),
    updated_by      text,
    primary key (exchange_code, collection_code)
);

comment on table exchange_collection is
    'Матрица биржа×коллекция, ВСЕГДА полная: строка на каждую пару, даже там, где биржа не '
    'умеет. Отсутствие строки не должно значить «выключено» — иначе «не умеет» и «выключили '
    'руками» неразличимы. interval_s/retention_days null = взять из collection (каскад '
    'exchange_collection -> collection; дальше в setting только для того, что явно из setting '
    'не уехало — сегодня из интервалов/retention это никто, они все теперь терминальные здесь).';
comment on column exchange_collection.retention_days is
    'Для collection_code=''snapshot'' это решение оператора ВИДНО, но RetentionJob его не '
    'применяет — партиции market_snapshot общие на все биржи, точечно одну пощадить нельзя '
    '(см. шапку миграции). Для остальных коллекций поле сейчас декоративно тем же способом, '
    'каким decorative retention_days на collection: реального ротатора для candles/funding/'
    'discovery/rollup нет и не должно быть.';

-- ── матрица: значения capability (EAV, полный крест с capability_key) ────
create table exchange_collection_capability (
    exchange_code   text        not null,
    collection_code text        not null,
    capability_key  text        not null references capability_key (key),
    value           text,
    source          text        check (source in ('declared', 'probed', 'manual')),
    valid_since     timestamptz,
    filled_at       timestamptz,
    filled_by       text,
    primary key (exchange_code, collection_code, capability_key),
    foreign key (exchange_code, collection_code)
        references exchange_collection (exchange_code, collection_code) on delete cascade
);

comment on column exchange_collection_capability.source is
    'declared — объявлено кодом адаптера при сборке (ExchangeWorker.Build); probed — измерено '
    'опросом биржи; manual — поставлено человеком. value null = ещё не установлено — честный '
    'ответ, не прочерк.';

-- ── журнал изменений capability (append-only) ────────────────────────────
create table capability_log (
    id              bigint      generated always as identity primary key,
    exchange_code   text        not null,
    collection_code text        not null,
    capability_key  text        not null,
    old_value       text,
    new_value       text,
    source          text        not null check (source in ('declared', 'probed', 'manual')),
    changed_at      timestamptz not null default now(),
    changed_by      text,
    note            text
);

comment on table capability_log is
    'Единственное, что через год отличит «мы сломали» от «биржа выключила». Reconcile пишет '
    'строку только когда объявленное значение реально ИЗМЕНИЛОСЬ (не на каждый цикл/рестарт).';

create index capability_log_lookup on capability_log (exchange_code, collection_code, changed_at desc);

-- ── сид справочника: коды, соответствующие реальным петлям ───────────────
-- Значения — законы, а не забытое знание (см. комментарий на default_retention_days).
-- instruments/prices отдельными строками не заводятся: это ровно discovery/snapshot.
insert into collection (code, name, description, kind, default_mode, default_interval_s, default_retention_days, sort_order) values
    ('discovery',      'Discovery',      'Which instruments are listed: appearances, delistings, status changes.', 'feed', 'collect', 3600, null, 10),
    ('snapshot',       'Snapshot',       'Market snapshot: price, spread, mark/index, funding rate, open interest. Not recoverable after the fact.', 'feed', 'collect', 10, 90, 20),
    ('depth',          'Depth',          'Order book depth in 10/25/50 bps bands — how much slippage a size would cost. No venue keeps history of this.', 'feed', 'collect', 60, 90, 30),
    ('candles',        'Candles',        '1-minute OHLCV bars. Never rotated: the only source for long backtests.', 'feed', 'collect', 60, null, 40),
    ('funding',        'Funding',        'Funding rate and its history.', 'feed', 'collect', 3600, null, 50),
    ('rollup',         'Rollup',         'Derived timeframes computed from 1m bars. We compute this ourselves; no venue is involved.', 'derived', 'collect', 60, null, 60),
    ('trades',         'Trades',         'Trade tape. A different order of volume — needs its own storage decision before it can be turned on.', 'feed', 'disabled', null, null, 70),
    ('open_interest',  'Open interest',  'Open interest as its own feed. Most venues already carry it inline in the ticker; not collected separately.', 'feed', 'disabled', null, null, 80),
    ('liquidations',   'Liquidations',   'Liquidations. Almost everywhere this is socket-only, and no venue keeps history of it.', 'feed', 'disabled', null, null, 90);

-- ── сид collection_setting: то, что не является интервалом/retention ─────
insert into collection_setting (collection_code, key, value, kind, description) values
    ('candles',   'backfill_hours',                  '3',                        'int',      'How many hours back to pull candles when an instrument first appears.'),
    ('funding',   'backfill_hours',                  '168',                      'int',      'How many hours back to pull funding history when an instrument first appears.'),
    ('discovery', 'delist_after_missed_discoveries', '3',                        'int',      'How many consecutive discovery passes an instrument may be missing before it is delisted.'),
    ('rollup',    'derived_timeframes',              '5,15,60,240,720,1440',     'int_list', 'Derived candle timeframes (minutes) rollup computes from 1m.');

-- ── сид capability_key ────────────────────────────────────────────────────
insert into capability_key (key, kind, description, human_editable, loss_relevant, sort_order) values
    ('venue_supports',     'bool', 'The venue offers this kind of data at all',                   true,  false, 10),
    ('we_implement',       'bool', 'Our adapter has code for it',                                 false, false, 20),
    ('transports_venue',   'list', 'Transports the venue offers for it',                          true,  false, 30),
    ('transports_us',      'list', 'Transports our adapter actually uses',                        false, false, 40),
    ('api_versions_venue', 'list', 'API versions the venue exposes',                               true,  false, 50),
    ('api_versions_us',    'list', 'API version our adapter targets',                              false, false, 60),
    ('history_depth',      'text', 'How far back the venue serves history: none / limited(...) / full', true, true, 70),
    ('auth',               'text', 'public, or private (needs keys we do not hold)',               false, false, 80),
    ('rate_limit',         'text', 'Rate limit as the venue documents it',                         true,  false, 90);

-- ── бэкфилл: строка на каждую пару (существующая биржа) × (коллекция) ────
-- Policy: то, что каждая реально работающая биржа делает СЕГОДНЯ — все пять
-- рабочих петель включены безусловно (текущее поведение — фиксированный
-- массив из пяти, без какого-либо per-collection выключателя). rollup
-- заводится строкой для каждой биржи ради полноты матрицы, хотя исполняется
-- только против ServiceExchange ('fake') — см. комментарий на collection.kind.
insert into exchange_collection (exchange_code, collection_code, mode, note, updated_by)
select e.code, c.code,
       case c.code when 'trades' then 'disabled'
                   when 'open_interest' then 'disabled'
                   when 'liquidations' then 'disabled'
                   else 'collect' end,
       case c.code when 'open_interest' then 'Carried inline in the snapshot ticker on every venue we run today; no separate loop needed'
                   else null end,
       '0014 migration'
  from exchange e cross join collection c;

-- ── бэкфилл: значения из старых колонок exchange, где они были выставлены ─
-- (сегодня ни у одной существующей биржи override не выставлен вручную —
-- проверено на локальном стеке и на копии эксплуатационного дампа — но
-- миграция переносит их честно, а не полагается на это совпадение).
update exchange_collection ec set interval_s = e.snapshot_interval_s
  from exchange e where e.code = ec.exchange_code and ec.collection_code = 'snapshot' and e.snapshot_interval_s is not null;
update exchange_collection ec set interval_s = e.candle_interval_s
  from exchange e where e.code = ec.exchange_code and ec.collection_code = 'candles' and e.candle_interval_s is not null;
update exchange_collection ec set interval_s = e.depth_interval_s
  from exchange e where e.code = ec.exchange_code and ec.collection_code = 'depth' and e.depth_interval_s is not null;
update exchange_collection ec set interval_s = e.discovery_interval_min * 60
  from exchange e where e.code = ec.exchange_code and ec.collection_code = 'discovery' and e.discovery_interval_min is not null;
update exchange_collection ec set interval_s = e.funding_interval_min * 60
  from exchange e where e.code = ec.exchange_code and ec.collection_code = 'funding' and e.funding_interval_min is not null;

alter table exchange
    drop column snapshot_interval_s,
    drop column candle_interval_s,
    drop column discovery_interval_min,
    drop column funding_interval_min,
    drop column depth_interval_s;

-- ── бэкфилл: значения capability для все пары (полный крест, честное null) ─
insert into exchange_collection_capability (exchange_code, collection_code, capability_key)
select ec.exchange_code, ec.collection_code, k.key
  from exchange_collection ec cross join capability_key k;

-- rollup — hub-decided, не про адаптер конкретной биржи: биржа тут ни при чём,
-- мы это считаем сами. Тот же факт для всех бирж (совпадает с прототипом).
update exchange_collection_capability c set
    value = v.value, source = 'declared', filled_at = now(), filled_by = 'hub'
   from (values ('venue_supports', 'false'), ('we_implement', 'true')) as v(key, value)
 where c.collection_code = 'rollup' and c.capability_key = v.key;

-- ── переезд шести значений из setting в дефолты коллекции: сверка ────────
-- (значения уже вписаны буквально в сид collection выше; здесь только удаление
-- источника, чтобы не осталось двух разных мест правды для одного числа)
delete from setting
 where key in (
    'snapshot_interval_s', 'candle_interval_s', 'depth_interval_s',
    'discovery_interval_min', 'funding_interval_min',
    'candle_backfill_hours', 'funding_backfill_hours',
    'snapshot_retention_days', 'derived_timeframes', 'delist_after_missed_discoveries'
 );

-- ── только теперь: FK collector_status.collector -> collection.code ──────
-- Матрица уже полная (включая derived rollup), так что любой корректный
-- collector-код из существующего кода уже есть в collection.code.
alter table collector_status drop constraint collector_status_collector_check;
alter table collector_status
    add constraint collector_status_collector_fkey
        foreign key (collector) references collection (code);
