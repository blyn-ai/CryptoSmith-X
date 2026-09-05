-- ============================================================================
-- 0017 — учёт сбора первого класса (WP8).
--
-- Сегодня по базе нельзя отличить «рынок молчал» от «мы ослепли». Это не
-- придирка к интерфейсу: на этом различии стоит вся ценность данных. Ряд без
-- сделок за час и ряд, которого нет, потому что нас отключили, выглядят
-- одинаково — и стратегия, обученная на втором, будет считать тишину сигналом.
--
-- Отсюда три вещи.
--
-- 1. collector_run обрастает тем, что о проходе известно и сейчас теряется:
--    транспорт (rest/ws), HTTP-статус, потраченный вес запроса, смещение часов.
--    Плюс ссылка на инструмент — для поинструментных опросов вроде глубины и
--    свечей, где «проход» это 1005 отдельных запросов, а не один.
--
-- 2. collection_gap — явная запись о том, что интервал НЕ наблюдался, с
--    причиной. Пустота перестаёт быть умолчанием и становится фактом.
--
-- 3. Ретеншен на collector_run снимается. Он стоял 7 дней, а это ровно тот
--    слой, по которому потом восстанавливают, что происходило. Стоит копейки:
--    45 тысяч строк за двое суток против семи миллионов снапшотов.
-- ============================================================================

alter table collector_run
    add column exchange_instrument_id integer references exchange_instrument (id),
    add column transport              text check (transport in ('rest', 'ws')),
    add column http_status            integer,
    add column request_weight         integer check (request_weight >= 0),
    add column clock_offset_ms        integer;

comment on column collector_run.exchange_instrument_id is
    'Инструмент, если проход поинструментный (глубина, свечи). null у пакетных: '
    'тикер и discovery берут всю биржу одним запросом.';
comment on column collector_run.transport is
    'rest или ws. Разная стоимость, разные лимиты, разные режимы отказа — а в UI '
    'до сих пор выглядели одинаково.';
comment on column collector_run.request_weight is
    'Сколько веса стоил проход по учёту биржи. У Hyperliquid 1200/мин на IP, и '
    'без этой колонки приближение к лимиту видно только по началу 429.';
comment on column collector_run.clock_offset_ms is
    'Смещение наших часов от источника времени на момент прохода. Всё в системе '
    'опирается на UTC; уехавшие часы портят и разбиение по минутам, и watermark.';

-- ----------------------------------------------------------------------------

create table collection_gap (
    id                     bigint      generated always as identity primary key,
    exchange_code          text        not null references exchange (code),
    collector              text        not null,
    exchange_instrument_id integer              references exchange_instrument (id),
    gap_start              timestamptz not null,
    gap_end                timestamptz,
    cause                  text        not null check (cause in (
                               'rate_limited',      -- 429 от биржи
                               'timeout',
                               'ws_sequence_gap',   -- дырка в номерах сообщений
                               'ws_disconnected',
                               'resync',            -- пересобирали книгу
                               'exchange_maintenance',
                               'collector_down',    -- нас не было
                               'error')),
    detail                 text,
    created_at             timestamptz not null default now()
);

comment on table collection_gap is
    'Интервалы, которые НЕ наблюдались, с причиной. Открытая дырка — gap_end null. '
    'Существует затем, чтобы «данных нет» никогда не читалось как «значение ноль»: '
    'первое здесь записано явно, второе означало бы, что мы это измерили.';
comment on column collection_gap.gap_end is
    'null пока дырка не закрыта. Закрывает её тот же коллектор, когда снова начал '
    'получать данные, — а не таймер, потому что таймер не знает, вернулась ли биржа.';

create index collection_gap_lookup
    on collection_gap (exchange_code, collector, gap_start desc);
create index collection_gap_open
    on collection_gap (exchange_code, collector) where gap_end is null;
create index collection_gap_instrument
    on collection_gap (exchange_instrument_id, gap_start desc)
    where exchange_instrument_id is not null;

-- ----------------------------------------------------------------------------
-- Полнота часового слоя. market_metric_hour уже носит snapshot_count — сколько
-- замеров вошло в среднее. Но одного этого числа мало: 30 замеров вместо 60 это
-- либо биржа молчала, либо мы не смотрели, и без второй колонки не отличить.
alter table market_metric_hour
    add column expected_count smallint check (expected_count >= 0),
    add column gap_seconds    integer  check (gap_seconds >= 0);

comment on column market_metric_hour.expected_count is
    'Сколько замеров должно было быть при настроенном такте. Вместе с snapshot_count '
    'даёт полноту часа одним делением, без обращения к сырью.';
comment on column market_metric_hour.gap_seconds is
    'Сколько секунд часа перекрыто записями collection_gap. Отделяет «рынок молчал» '
    'от «мы ослепли»: у первого gap_seconds нулевой, у второго нет.';
