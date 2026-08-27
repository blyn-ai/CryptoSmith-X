-- ============================================================================
-- CryptoSmith X — Market Data, V1 schema (PostgreSQL 14+)
-- Файл: plans/marketdata-schema.sql → миграция 0001_initial.sql, дословно.
--
-- Итог двух проходов. Что вошло из правок и почему:
--   * market_candle.updated_at + правило «rollup всегда пересчитывает»:
--     поздняя минутка чинит производный бар, а не оставляет его кривым навсегда.
--   * Свечи не ротируются: единственный источник для длинных прогонов, ~20 ГБ/год.
--   * trade_count на свече: бары «из одной сделки» давали 828 ложных движений
--     из 1292 — фильтруется на входе. null там, где биржа не отдаёт.
--   * open_interest_at, depth_at: OI и стакан приходят отдельными вызовами
--     на символ и реже, чем строка; их свежесть видна отдельно.
--   * Глубина как кумулятивный notional в 10/25/50 bps: ответ на вопрос
--     «сколько проскальзывание съест перевес». Собирается раз в 60 с.
--
-- Что НЕ вошло и почему:
--   * quote_family — склеивала бы USD/USDT/USDC, то есть ровно те контракты,
--     которые хотела разделить; в scope V1 была бы константой. Канон = base_asset,
--     разделение — по quote_asset в запросе.
--   * funding_rate_at — funding приходит с mark/index в одном вызове, всегда = received_at.
--   * next_funding_at — платежи у всех четырёх бирж на границах интервала (UTC);
--     API считает из funding_interval_hours. Вернуть NOT NULL, если появится
--     биржа с невыровненным расписанием.
--   * max_leverage, margin_tiers — на Binance только через подписной эндпоинт;
--     хаб без ключей по определению. Расстояние до ликвидации — забота бота.
--   * quote_volume на свече — нет у HL и Kraken; для ликвидности есть turnover_24h.
--   * check (ask >= bid) — пересечённый стакан бывает; CHECK превращал бы факт
--     в тихо не обновившийся latest.
--   * contract_type, is_inverse, listed_at, degraded_symbols, instruments_written —
--     константы, нет потребителя или вычислимо из latest.received_at.
--
-- Соглашения:
--   * наблюдения рынка — double precision; ограничения биржи — numeric, точно;
--   * все времена — timestamptz (UTC); статусы — text + CHECK;
--   * updated_at выставляет приложение, триггеров нет.
--
-- Scope V1: линейные perpetual, quote из USD-семейства (USD/USDT/USDC).
-- Dated и инверсные контракты discovery пропускает.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- exchange — справочник бирж
-- ----------------------------------------------------------------------------
create table exchange (
    code        text        primary key,          -- 'kraken-futures' | 'binance-usdm' | 'weex-futures' | 'hyperliquid'
    name        text        not null,
    is_enabled  boolean     not null default true, -- административное решение; наблюдаемое состояние выводится из collector_status
    note        text,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now()
);

comment on table exchange is
    'Поддерживаемые биржи. URL, лимиты, частоты опроса — в конфигурации приложения. '
    'Наблюдаемое состояние (up/degraded/down) не хранится: выводится из collector_status.';

insert into exchange (code, name, is_enabled) values
    ('kraken-futures', 'Kraken Futures',          true),
    ('binance-usdm',   'Binance USDⓈ-M Futures',  true),
    ('weex-futures',   'WEEX Futures',            true),
    ('hyperliquid',    'Hyperliquid',             true)
on conflict (code) do nothing;


-- ----------------------------------------------------------------------------
-- exchange_instrument — листинг инструмента на бирже. Ключевая сущность.
-- ----------------------------------------------------------------------------
create table exchange_instrument (
    id                      integer     generated always as identity primary key,
    exchange_code           text        not null references exchange (code),
    exchange_symbol         text        not null,       -- 'PF_XBTUSD' | 'BTCUSDT' | 'BTC' | 'cmt_btcusdt' — как на бирже

    base_asset              text        not null,       -- нормализован адаптером: XBT -> BTC, 1000PEPE -> PEPE
    quote_asset             text        not null,       -- 'USD' | 'USDT' | 'USDC'
    contract_multiplier     numeric     not null default 1 check (contract_multiplier > 0),

    price_step              numeric     not null check (price_step > 0),
    qty_step                numeric     not null check (qty_step   > 0),
    min_qty                 numeric     not null check (min_qty    > 0),
    min_notional            numeric              check (min_notional > 0),   -- NULL, если биржа не задаёт (Kraken)
    funding_interval_hours  smallint    not null check (funding_interval_hours > 0),

    status                  text        not null check (status in ('trading', 'post_only', 'reduce_only', 'halted', 'delisted')),
    status_changed_at       timestamptz not null,
    first_seen_at           timestamptz not null,
    last_seen_at            timestamptz not null,

    raw_json                jsonb       not null,       -- последний ответ discovery по этому инструменту, как есть
    updated_at              timestamptz not null default now(),

    unique (exchange_code, exchange_symbol)
);

comment on table exchange_instrument is
    'Конкретный листинг на конкретной бирже. Канонический инструмент в V1 = base_asset '
    '(все листинги BTC: where base_asset = ''BTC''; разделить по валюте котировки — where quote_asset = ...). '
    'Все различия между биржами — здесь.';

comment on column exchange_instrument.contract_multiplier is
    'Сколько единиц base_asset в одной единице количества. Цены, объёмы и OI хранятся в родных '
    'единицах биржи за одну единицу количества и НЕ пересчитываются (BTC: 1; 1000PEPE, kPEPE: 1000).';

comment on column exchange_instrument.min_qty is
    'Минимальное количество в ордере. Если биржа не задаёт отдельно — адаптер пишет qty_step.';

comment on column exchange_instrument.funding_interval_hours is
    'Интервал funding-платежей. Ближайший платёж — на границе интервала (UTC); API считает, колонки нет.';

comment on column exchange_instrument.status is
    'Переходы: появился в discovery -> trading; halted/post_only/reduce_only с биржи -> соответствующий; '
    'отсутствует N опросов подряд -> delisted. status_changed_at меняется только при СМЕНЕ статуса.';


-- ----------------------------------------------------------------------------
-- market_snapshot_latest — одна актуальная строка на инструмент, upsert ~10 с.
-- Это то, что читают боты; received_at для них = asOf.
-- ----------------------------------------------------------------------------
create table market_snapshot_latest (
    exchange_instrument_id  integer          primary key references exchange_instrument (id),
    received_at             timestamptz      not null,

    last_price              double precision not null,
    bid_price               double precision not null,
    ask_price               double precision not null,
    bid_size                double precision not null,
    ask_size                double precision not null,
    mark_price              double precision not null,
    index_price             double precision not null,
    funding_rate            double precision not null,
    turnover_24h            double precision not null,

    open_interest           double precision not null,
    open_interest_at        timestamptz      not null,

    depth_bid_10bps         double precision,
    depth_ask_10bps         double precision,
    depth_bid_25bps         double precision,
    depth_ask_25bps         double precision,
    depth_bid_50bps         double precision,
    depth_ask_50bps         double precision,
    depth_at                timestamptz
);

comment on table market_snapshot_latest is
    'Текущее состояние рынка по инструменту. Строка пишется ТОЛЬКО ЦЕЛИКОМ: если биржа не отдала '
    'обязательное поле (например, пустой стакан), строка не обновляется, и старый received_at сам '
    'сигнализирует о staleness. Пересечённый стакан (bid > ask) записывается как есть — это факт.';

comment on column market_snapshot_latest.received_at is
    'Момент сборки снапшота у нас. Bid/ask, mark/index и funding приходят одним-двумя вызовами по всем '
    'инструментам и свежи на received_at; OI и глубина — отдельными вызовами на символ и реже, '
    'у них свои *_at.';

comment on column market_snapshot_latest.funding_rate is
    'Относительная ставка (доля notional) за один funding_interval_hours, которая применится при '
    'ближайшем платеже. Положительная = лонги платят шортам. Kraken: абсолютная ставка тикера / mark_price.';

comment on column market_snapshot_latest.turnover_24h is
    'Оборот за скользящие 24 часа в quote_asset, по определению биржи.';

comment on column market_snapshot_latest.open_interest is
    'В единицах количества инструмента (как qty_step). Notional = open_interest * mark_price. '
    'На Binance приходит отдельным вызовом на символ (~60 с) — отсюда open_interest_at.';

comment on column market_snapshot_latest.depth_bid_10bps is
    'Сумма notional в quote_asset по уровням bid в пределах 10 bps от середины стакана (mid = (bid+ask)/2). '
    'Стакан — вызов на символ, собирается раз в 60 с (лимиты Binance/Hyperliquid), см. depth_at. '
    'NULL в двух случаях: биржа не отдала стакан, или последний полученный уровень ещё внутри полосы — '
    'то есть полоса не покрыта и сумма была бы заниженной. NULL всегда значит «не измерено».';


-- ----------------------------------------------------------------------------
-- market_snapshot — история снапшотов, одна строка в минуту на инструмент.
-- Те же поля, что в latest. Партиции по месяцу, retention 90 дней (drop partition).
-- ----------------------------------------------------------------------------
create table market_snapshot (
    exchange_instrument_id  integer          not null references exchange_instrument (id),
    received_at             timestamptz      not null,

    last_price              double precision not null,
    bid_price               double precision not null,
    ask_price               double precision not null,
    bid_size                double precision not null,
    ask_size                double precision not null,
    mark_price              double precision not null,
    index_price             double precision not null,
    funding_rate            double precision not null,
    turnover_24h            double precision not null,

    open_interest           double precision not null,
    open_interest_at        timestamptz      not null,

    depth_bid_10bps         double precision,
    depth_ask_10bps         double precision,
    depth_bid_25bps         double precision,
    depth_ask_25bps         double precision,
    depth_bid_50bps         double precision,
    depth_ask_50bps         double precision,
    depth_at                timestamptz,

    primary key (exchange_instrument_id, received_at)
) partition by range (received_at);

create index market_snapshot_received_at_brin on market_snapshot using brin (received_at);

comment on table market_snapshot is
    'История снапшотов с минутным шагом (latest обновляется каждые ~10 с, в историю попадает одна '
    'строка в минуту). Retention 90 дней. Spread, OI notional, next funding, high/low/change за 24 ч — '
    'считает API, не хранится.';


-- ----------------------------------------------------------------------------
-- market_candle — только закрытые бары, все таймфреймы в одной таблице.
-- 1m берётся с биржи; 5m/15m/1h/4h/12h/1d — rollup из 1m.
-- Партиции по месяцу. НЕ РОТИРУЕТСЯ.
-- ----------------------------------------------------------------------------
create table market_candle (
    exchange_instrument_id  integer          not null references exchange_instrument (id),
    timeframe               smallint         not null check (timeframe > 0),   -- в минутах: 1, 5, 15, 60, 240, 720, 1440
    open_time               timestamptz      not null,

    open                    double precision not null,
    high                    double precision not null,
    low                     double precision not null,
    close                   double precision not null,
    volume                  double precision not null,   -- в единицах количества инструмента
    trade_count             integer                   check (trade_count >= 0),
    bar_count               smallint         not null check (bar_count > 0),
    updated_at              timestamptz      not null default now(),

    primary key (exchange_instrument_id, timeframe, open_time),
    check (high >= low),
    check (bar_count <= timeframe)
) partition by range (open_time);

create index market_candle_open_time_brin on market_candle using brin (open_time);

-- Неполные производные бары — ускоритель для джоба бэкфилла, НЕ его определение:
-- джоб ограничивает выборку по времени и после одной повторной выкачки закрытого окна
-- считает бар окончательным (минуты без сделок биржа может не отдавать никогда).
create index market_candle_incomplete
    on market_candle (exchange_instrument_id, timeframe, open_time)
    where bar_count < timeframe;

comment on table market_candle is
    'Только закрытые бары; производный бар пишется, когда его окно закрыто. Набор таймфреймов — '
    'настройка rollup, не схемы (CHECK только на > 0). Все границы UTC. НЕ РОТИРУЕТСЯ: свечи — '
    'единственный источник для длинных прогонов.';

comment on column market_candle.bar_count is
    'Сколько 1m-баров вошло в бар (для 1m всегда 1). Меньше timeframe — были минуты без данных с биржи. '
    'Rollup обязан ПЕРЕСЧИТАТЬ производный бар, если после его записи пришла минутка из его окна.';

comment on column market_candle.trade_count is
    'Число сделок в баре, если биржа отдаёт (Binance, Hyperliquid — да; Kraken Futures, WEEX — нет). '
    'Rollup суммирует только когда счётчик есть у всех минуток окна, иначе null. '
    'Бар из одной сделки на неликвиде рисует движение, которого не было, — на таких сигналы не строят; '
    'где счётчика нет, признак мёртвой ленты — high = low или volume = 0.';

comment on column market_candle.updated_at is
    'Момент последней записи или пересчёта. Постоянный рост updated_at на старых барах означает, '
    'что фид отдаёт минутки с большой задержкой.';


-- ----------------------------------------------------------------------------
-- collector_status — операционное состояние коллекторов. Источник для /health
-- и для вывода состояния биржи (все коллекторы без успеха дольше N минут = down).
-- Молчащий символ при живом коллекторе — не здесь: /health считает его из
-- market_snapshot_latest.received_at по инструментам в status = 'trading'.
-- ----------------------------------------------------------------------------
create table collector_status (
    exchange_code         text        not null references exchange (code),
    collector             text        not null check (collector in ('discovery', 'snapshot', 'depth', 'candles', 'rollup')),
    last_attempt_at       timestamptz not null,
    last_success_at       timestamptz,
    last_error_at         timestamptz,
    last_error            text,
    consecutive_failures  integer     not null default 0 check (consecutive_failures >= 0),
    instruments_expected  integer,

    primary key (exchange_code, collector)
);


-- ----------------------------------------------------------------------------
-- Партиции. Управление — в retention-джобе сервиса:
--   1) перед записью (при старте и при бэкфилле) гарантировать партицию месяца:
--        select create_month_partition('market_snapshot', date '2026-08-01');
--        select create_month_partition('market_candle',   date '2025-01-01');
--   2) раз в сутки удалять старые СНАПШОТЫ (свечи не трогать):
--        drop table if exists market_snapshot_2026_05;
-- Default-партиции намеренно нет: строка без партиции — ошибка, а не тихое накопление.
-- ----------------------------------------------------------------------------
create or replace function create_month_partition(parent regclass, month date)
returns void
language plpgsql
as $$
declare
    start_date date := date_trunc('month', month)::date;
    end_date   date := (start_date + interval '1 month')::date;
    part_name  text := format('%s_%s', parent::text, to_char(start_date, 'YYYY_MM'));
begin
    execute format(
        'create table if not exists %I partition of %s for values from (%L) to (%L)',
        part_name, parent, start_date, end_date
    );
end
$$;

comment on function create_month_partition(regclass, date) is
    'Идемпотентно. Вызывать и при старте, и при бэкфилле: бэкфилл легко уходит в месяц, '
    'партиции для которого ещё нет.';
