-- ============================================================================
-- csx-proto — прототип модели «коллекции и capability». Отдельная база,
-- боевую marketdata не трогает. Db-first: сначала таблицы и справочники,
-- потом UI, потом код, который это оживит.
--
-- Три оси, которые нельзя смешивать:
--   * capability — что биржа физически умеет и что умеем мы (факт);
--   * policy     — что решил человек (режим, частота, хранение);
--   * health     — что наблюдаем (в боевой схеме это collector_status,
--                  в прототип не тащим: он про конфигурацию).
-- ============================================================================

drop table if exists capability_log, exchange_collection_capability,
    exchange_collection, capability_key, collection, exchange cascade;

-- ── биржа: урезанная копия боевой, только нужное прототипу ───────────────
create table exchange (
    code     text primary key,
    name     text not null,
    status   text not null check (status in ('planned','enabled','disabled','maintenance','abandoned')),
    adapter  text,
    base_url text,
    ws_url   text
);

-- ── справочник видов данных ──────────────────────────────────────────────
create table collection (
    code                   text     primary key,
    name                   text     not null,
    description            text     not null,   -- человеческим языком, в карточку UI
    kind                   text     not null check (kind in ('feed','derived')),
    default_mode           text     not null check (default_mode in ('disabled','on_demand','collect')),
    default_interval_s     integer  check (default_interval_s > 0),
    default_retention_days integer  check (default_retention_days > 0),  -- null = хранить вечно
    sort_order             smallint not null default 100
);

comment on column collection.default_retention_days is
    'null = не ротируется никогда. У candles это ЗАКОН, а не пропуск: свечи — '
    'единственный источник для длинных прогонов, а свечи делистнутых инструментов '
    'защищают бэктест от ошибки выжившего.';

-- ── справочник ключей capability ─────────────────────────────────────────
create table capability_key (
    key            text     primary key,
    kind           text     not null check (kind in ('bool','int','text','list')),
    description    text     not null,
    human_editable boolean  not null default false,  -- правится ли руками через UI
    loss_relevant  boolean  not null default false,  -- влияет ли на «потеряем навсегда»
    sort_order     smallint not null default 100
);

comment on table capability_key is
    'Тот же приём, что в setting: key/value/kind/description. Новый вид capability '
    'добавляется строкой, а не ALTER TABLE — иначе версии API и время в колонки не влезут.';

-- ── матрица: политика (решение человека) ─────────────────────────────────
create table exchange_collection (
    exchange_code   text        not null references exchange (code),
    collection_code text        not null references collection (code),
    mode            text        not null check (mode in ('disabled','on_demand','collect')),
    interval_s      integer     check (interval_s > 0),      -- null = из collection, затем глобально
    retention_days  integer     check (retention_days > 0),  -- null = из collection
    transport       text        check (transport in ('rest','ws')),
    note            text,
    updated_at      timestamptz not null default now(),
    updated_by      text,
    primary key (exchange_code, collection_code)
);

comment on table exchange_collection is
    'Матрица ВСЕГДА полная: строка на каждую пару биржа×коллекция, даже там, где '
    'биржа не умеет. Отсутствие строки не должно значить «выключено» — иначе '
    '«не умеет» и «выключили руками» становятся неразличимы.';

-- ── матрица: значения capability ─────────────────────────────────────────
create table exchange_collection_capability (
    exchange_code   text        not null,
    collection_code text        not null,
    capability_key  text        not null references capability_key (key),
    value           text,                    -- null = ещё не установлено (это тоже честный ответ)
    source          text        check (source in ('declared','probed','manual')),
    valid_since     timestamptz,             -- с какого момента значение таково
    filled_at       timestamptz,
    filled_by       text,
    primary key (exchange_code, collection_code, capability_key),
    foreign key (exchange_code, collection_code)
        references exchange_collection (exchange_code, collection_code) on delete cascade
);

comment on column exchange_collection_capability.source is
    'declared — объявлено кодом при сборке; probed — измерено опросом биржи; '
    'manual — поставлено человеком. Без источника значение бесполезно: '
    '«история 2 года» без «кем и когда проверено» — это мнение, а не факт.';

-- ── журнал изменений capability (append-only) ────────────────────────────
create table capability_log (
    id              bigint      generated always as identity primary key,
    exchange_code   text        not null,
    collection_code text        not null,
    capability_key  text        not null,
    old_value       text,
    new_value       text,
    source          text        not null check (source in ('declared','probed','manual')),
    changed_at      timestamptz not null default now(),
    changed_by      text,
    note            text
);

comment on table capability_log is
    'Единственное, что через год отличит «мы сломали» от «биржа выключила». '
    'Без него дыра в архиве необъяснима.';

create index capability_log_lookup on capability_log (exchange_code, collection_code, changed_at desc);
