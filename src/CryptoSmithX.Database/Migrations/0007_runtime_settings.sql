-- ============================================================================
-- CryptoSmith X — миграция 0007: вся настройка market-data переезжает в базу.
--
-- Решение владельца: у Hub в appsettings остаётся только строка подключения и
-- логирование; всё остальное (какие биржи, их URL, котировки, чёрные списки,
-- интервалы опроса) живёт в базе и правится через админ-UI без рестарта
-- контейнера. Цена того, что URL тоже в базе, гасится аудитом — каждая правка
-- пишет updated_by/updated_at.
--
-- Две вещи:
--   * exchange обрастает колонками адаптера и настроек (интервалы per-exchange,
--     null = взять глобальное значение из setting);
--   * setting — маленький key/value с глобальными значениями (бывшие поля
--     MarketDataOptions), с типом-подсказкой для валидации в UI.
--
-- Что НЕ вошло и почему:
--   * Отдельная таблица настроек на биржу — колонок немного и они фиксированы,
--     key/value на биржу был бы сложнее без выгоды.
--   * История правок (кто/когда/что) сверх updated_by/at — аудит-лог это
--     отдельная задача; здесь достаточно «последний, кто трогал».
--   * Секреты/ключи — hub по архитектуре без ключей, хранить нечего.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- exchange — адаптер, адреса, настройки опроса. Существующие строки получают
-- осмысленные значения, а не null: планируемым биржам адаптер = их code (их всё
-- равно не запустит статус planned), fake и kraken-futures — свои настоящие.
-- ----------------------------------------------------------------------------
alter table exchange
    add column adapter       text,
    add column base_url      text,
    add column charts_url    text,
    add column quote_assets  text[] not null default '{USD,USDT,USDC}',
    add column blacklist     text[] not null default '{}',
    -- Интервалы опроса на биржу. null = взять глобальное из setting.
    add column snapshot_interval_s   integer,
    add column candle_interval_s     integer,
    add column discovery_interval_min integer,
    add column funding_interval_min  integer,
    add column depth_interval_s      integer,
    add column updated_by            text;

comment on column exchange.adapter is
    'Какую реализацию IExchangeMarketData строить: fake, kraken-futures. Планируемым — их '
    'code как заглушка, статус planned их всё равно не запускает.';
comment on column exchange.base_url is
    'База публичного REST биржи. Свечи у некоторых на другом хосте пути — отдельная charts_url.';
comment on column exchange.charts_url is
    'База эндпоинта свечей (у Kraken Futures — futures.kraken.com/api/charts/v1).';
comment on column exchange.snapshot_interval_s is
    'Интервал снапшотов на этой бирже; null = глобальный setting snapshot_interval_s. '
    'Так же candle_interval_s, discovery_interval_min, funding_interval_min, depth_interval_s.';

update exchange
   set adapter = case code
                     when 'fake'           then 'fake'
                     when 'kraken-futures' then 'kraken-futures'
                     else code
                 end,
       base_url   = case code when 'kraken-futures' then 'https://futures.kraken.com' end,
       charts_url = case code when 'kraken-futures' then 'https://futures.kraken.com/api/charts/v1' end;

alter table exchange alter column adapter set not null;


-- ----------------------------------------------------------------------------
-- setting — глобальные значения market-data. Бывшие поля MarketDataOptions.
-- kind подсказывает UI, как валидировать value.
-- ----------------------------------------------------------------------------
create table setting (
    key         text        primary key,
    value       text        not null,
    kind        text        not null check (kind in ('int', 'text', 'int_list')),
    description text        not null,     -- показывается в UI, человеческим языком
    updated_at  timestamptz not null default now(),
    updated_by  text
);

comment on table setting is
    'Глобальные настройки market-data (бывшие MarketDataOptions). value хранится текстом, '
    'kind говорит UI и Hub, как его читать. Значение на бирже перекрывает глобальное здесь.';

insert into setting (key, value, kind, description) values
    ('snapshot_interval_s',            '10',                     'int',      'Как часто (сек) собирать снапшот тикеров с биржи.'),
    ('candle_interval_s',              '60',                     'int',      'Как часто (сек) докачивать закрытые 1-минутные свечи.'),
    ('discovery_interval_min',         '60',                     'int',      'Как часто (мин) сверять список инструментов биржи.'),
    ('funding_interval_min',           '60',                     'int',      'Как часто (мин) дописывать историю funding-ставок.'),
    ('depth_interval_s',               '60',                     'int',      'Как часто (сек) проходить по инструментам за глубиной стакана.'),
    ('derived_timeframes',             '5,15,60,240,720,1440',   'int_list', 'Производные таймфреймы свечей (мин), которые считает rollup из 1m.'),
    ('snapshot_retention_days',        '90',                     'int',      'Сколько дней хранить историю снапшотов; старше — партиции дропаются.'),
    ('candle_backfill_hours',          '3',                      'int',      'На сколько часов назад тянуть свечи при первом появлении инструмента.'),
    ('funding_backfill_hours',         '168',                    'int',      'На сколько часов назад тянуть историю funding при первом появлении инструмента.'),
    ('delist_after_missed_discoveries','3',                      'int',      'Сколько discovery-проходов подряд инструмент может отсутствовать до делистинга.')
on conflict (key) do nothing;
