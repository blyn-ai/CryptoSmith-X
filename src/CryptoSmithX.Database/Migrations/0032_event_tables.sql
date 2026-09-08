-- ============================================================================
-- CryptoSmith X — миграция 0032: пять таблиц под событийную гранулярность —
-- сделки, топ книги по кадрам, свечи mark/index, история OI и ликвидаций.
--
-- Часть фазы «только база» (см. 0030). Таблицы заводятся ПУСТЫМИ: писателя у
-- них нет ни здесь, ни в этой фазе вообще — это код следующих фаз (см. план
-- дальнейших шагов в конце 0034). Партиции текущего и следующего месяца
-- создаются здесь же, чтобы таблицы были пригодны к записи сразу, а не
-- ждали рестарта хаба — расширение `Partitions.EnsureCurrentAndNextAsync` на
-- них самих остаётся кодовой задачей следующей фазы.
--
--
-- ----------------------------------------------------------------------------
-- Дополнение к соглашению о типах (0001)
-- ----------------------------------------------------------------------------
-- 0001 говорит: наблюдения-сэмплы (тикер, свеча из REST-агрегата биржи) —
-- double precision; ограничения биржи (price_step, qty_step) — numeric. Этим
-- файлом соглашение расширяется на третий случай, которого в 0001 не было,
-- потому что событийных таблиц тогда не было:
--
--   СОБЫТИЙНЫЕ ДАННЫЕ (отдельная сделка, отдельный уровень книги, отдельный
--   бакет аналитики биржи) — NUMERIC, не double precision.
--
-- Разница не косметическая. double precision — это то, что получается,
-- когда МЫ агрегируем много измерений в одно число (среднее, сумма, mid) —
-- там точность двоичной дроби ниже точности биржи не страшна, потому что
-- итоговое число уже приближение по построению. Одна сделка, один уровень
-- книги, один бакет OI — это ПЕРВИЧНОЕ измерение биржи, часто присланное как
-- десятичная строка в JSON; конвертация в double и обратно способна дать
-- 100.10000000000001 там, где биржа прислала ровно 100.1, и это разница,
-- которую человек, сверяющий строку с логом сделок на бирже, обязательно
-- заметит и не обязан прощать. market_snapshot и market_candle остаются
-- double precision (0001, менять не будем — см. 0030 «чего здесь нет»):
-- это уже существующие 26M строк агрегатов, где перезапись ради точности
-- ниже точности биржи не окупает переписывание истории.
--
--
-- ----------------------------------------------------------------------------
-- Общие поля пяти таблиц — что значат
-- ----------------------------------------------------------------------------
--   received_at  — НАШИ часы: когда строка получена (WS-фрейм, REST-ответ).
--   known_from   — PIT (point-in-time): с какого момента строка ДОСТУПНА
--                  читателю бэктеста, если это позже received_at (например,
--                  бэкфилл дозаписал прошлое сегодня — сама сделка старая,
--                  а видимость новая). NULL = совпадает с received_at,
--                  задавать отдельно только когда есть разница.
--   source       — как строка попала в таблицу: 'ws' / 'rest_recent' /
--                  'backfill' (у candles/oi — 'rest' вместо 'ws', потому что
--                  ни на одной площадке этих данных на сокете сегодня не
--                  берём — см. plans/venue-ws-catalog.md).
--
-- Единица количества — единица ИНСТРУМЕНТА (как qty_step в exchange_instrument
-- / contract_multiplier), поэтому qty_unit на строке НЕ заводится нигде,
-- кроме liquidation_volume_history — там единица агрегата определяет сама
-- площадка, а не инструмент, см. комментарий у volume_unit ниже.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- 1. trade — лента сделок
-- ----------------------------------------------------------------------------
create table trade (
    exchange_instrument_id integer     not null references exchange_instrument (id),
    event_time             timestamptz not null,
    venue_uid              text        not null,
    seq                    bigint,
    received_at            timestamptz not null,
    known_from             timestamptz,
    source                 text        not null check (source in ('ws', 'rest_recent', 'backfill')),
    price                  numeric     not null check (price > 0),
    qty                    numeric     not null check (qty > 0),
    taker_side             text        not null check (taker_side in ('buy', 'sell')),
    trade_type             text                check (trade_type in ('fill', 'liquidation', 'partial_liquidation', 'termination', 'block')),
    primary key (exchange_instrument_id, event_time, venue_uid)
) partition by range (event_time);

create index trade_event_time_brin on trade using brin (event_time);

comment on table trade is
    'Лента сделок, событийная гранулярность. Партиции по месяцу. Пустая на момент создания — '
    'писателя у этой фазы нет (см. шапку 0032). primary key на (instrument, event_time, venue_uid): '
    'сама биржа гарантирует уникальность venue_uid только в своих часах, отсюда event_time в ключе, '
    'а не только uid.';
comment on column trade.event_time is
    'Время сделки ПО БИРЖЕ — часы venue, не наши. Ключ партиции.';
comment on column trade.venue_uid is
    'Идентификатор сделки, как его даёт биржа (может быть числом, hash, строкой — хранится как text, '
    'не парсится).';
comment on column trade.qty is
    'В единицах инструмента (как qty_step у exchange_instrument / instrument_spec) — единицы на '
    'строке нет: сделка привязана к instrument_id, который её и определяет.';
comment on column trade.trade_type is
    'NULL = неизвестно (бэкфилл без пометки типа) — НЕ то же самое, что ''fill''. Площадка, которая '
    'размечает ликвидации отдельно (liquidation / partial_liquidation / termination), даёт это поле '
    'явно; там, где не размечает, NULL — не подставлять fill по умолчанию.';


-- ----------------------------------------------------------------------------
-- 2. book_topn — топ книги по кадрам (снимок или дельта)
-- ----------------------------------------------------------------------------
create table book_topn (
    exchange_instrument_id integer     not null references exchange_instrument (id),
    observed_at             timestamptz not null,
    seq                     bigint      not null,
    received_at             timestamptz not null,
    is_snapshot             boolean     not null,
    levels                  smallint    not null check (levels > 0),
    bid_px  numeric[] not null, bid_qty numeric[] not null,
    ask_px  numeric[] not null, ask_qty numeric[] not null,
    bid_n   integer[],          ask_n   integer[],
    primary key (exchange_instrument_id, observed_at, seq),
    check (cardinality(bid_px) = cardinality(bid_qty) and cardinality(ask_px) = cardinality(ask_qty)),
    check (cardinality(bid_px) <= levels and cardinality(ask_px) <= levels)
) partition by range (observed_at);

create index book_topn_observed_at_brin on book_topn using brin (observed_at);

comment on table book_topn is
    'Топ N уровней книги по кадрам (снимок целиком или дельта — is_snapshot различает). Партиции '
    'по месяцу, retention 90 дней (как у снапшота: объём того же порядка — см. 0034). Пустая на '
    'момент создания — писателя у этой фазы нет.';
comment on column book_topn.observed_at is
    'Время кадра ПО БИРЖЕ, из самого кадра (не received_at — то наши часы получения). Ключ партиции.';
comment on column book_topn.seq is
    'Порядковый номер кадра площадки (sequence number из WS-протокола биржи) — не наш счётчик. '
    'Часть первичного ключа: у одного observed_at несколько последовательных кадров возможны.';
comment on column book_topn.levels is
    'Сколько уровней запрошено/подписано (глубина подписки), не сколько реально пришло — bid_n/ask_n '
    'могут быть короче, если книга на этой стороне тоньше levels.';
comment on column book_topn.bid_n is
    'Число ордеров на уровне, если площадка его отдаёт (не все отдают) — NULL поэлементно и/или '
    'массив целиком NULL, если биржа этого не публикует.';


-- ----------------------------------------------------------------------------
-- 3. market_price_candle — свечи mark / index
-- ----------------------------------------------------------------------------
create table market_price_candle (
    exchange_instrument_id integer     not null references exchange_instrument (id),
    series                  text        not null check (series in ('mark', 'index')),
    timeframe               smallint    not null check (timeframe > 0),
    open_time               timestamptz not null,
    open numeric not null, high numeric not null, low numeric not null, close numeric not null,
    received_at             timestamptz not null,
    known_from              timestamptz,
    source                  text        not null check (source in ('rest', 'backfill')),
    primary key (exchange_instrument_id, series, timeframe, open_time),
    check (high >= low)
) partition by range (open_time);

create index market_price_candle_open_time_brin on market_price_candle using brin (open_time);

comment on table market_price_candle is
    'Свечи mark-price и index-price отдельно от торговой цены (market_candle — только по сделкам). '
    'Только ЗАКРЫТЫЕ бары — is_final не заводится: он был бы константой true на каждой строке, '
    'потому что незакрытые здесь не пишутся вовсе. volume не заводится: mark/index не торгуются '
    'напрямую, объём на них — не измерение, а то, что биржа обычно шлёт нулём. Пустая на момент '
    'создания — писателя у этой фазы нет.';
comment on column market_price_candle.series is
    '''mark'' — mark-price (расчётная цена для маржи/ликвидации), ''index'' — index-price '
    '(референс от внешних площадок). Разные ряды одного инструмента, не варианты одного значения.';


-- ----------------------------------------------------------------------------
-- 4. open_interest_history — история OI по бакетам
-- ----------------------------------------------------------------------------
create table open_interest_history (
    exchange_instrument_id integer     not null references exchange_instrument (id),
    interval_s              integer     not null check (interval_s > 0),
    bucket_time              timestamptz not null,
    oi_open numeric, oi_high numeric, oi_low numeric,
    oi_close                 numeric     not null,
    oi_quote                 numeric,
    received_at              timestamptz not null,
    known_from                timestamptz,
    source                    text        not null check (source in ('analytics', 'rest', 'backfill')),
    primary key (exchange_instrument_id, interval_s, bucket_time)
);

comment on table open_interest_history is
    'История open interest по бакетам времени (bucket_time — сетка биржи, не наша). НЕ ротируется '
    '(как funding_rate_history) и НЕ партиционирована — это агрегат, объём на порядки меньше сделок '
    'или снапшотов. oi_open/high/low NULL у площадок без OHLC-агрегата OI: тогда единственное '
    'значение — oi_close. Пустая на момент создания — писателя у этой фазы нет.';
comment on column open_interest_history.oi_close is
    'Единственное обязательное значение бакета — последнее наблюдение OI внутри interval_s. У '
    'площадок без встроенного OHLC-агрегата это единственное, что вообще есть.';
comment on column open_interest_history.oi_quote is
    'OI в quote-нотионале, если площадка отдаёт готовым (Binance: sumOpenInterestValue) — не наш '
    'расчёт oi_close * mark_price, тот читатель может посчитать сам при необходимости, имея обе цифры.';


-- ----------------------------------------------------------------------------
-- 5. liquidation_volume_history — объём ликвидаций по бакетам
-- ----------------------------------------------------------------------------
create table liquidation_volume_history (
    exchange_instrument_id integer     not null references exchange_instrument (id),
    interval_s              integer     not null check (interval_s > 0),
    bucket_time              timestamptz not null,
    volume                    numeric     not null check (volume >= 0),
    volume_unit               text        not null,
    received_at                timestamptz not null,
    known_from                  timestamptz,
    source                      text        not null check (source in ('analytics', 'backfill')),
    primary key (exchange_instrument_id, interval_s, bucket_time)
);

comment on table liquidation_volume_history is
    'Агрегированный объём ликвидаций по бакетам — почти везде это аналитический эндпоинт биржи '
    '(сокет с отдельными событиями ликвидации не держит истории нигде), поэтому строится из готового '
    'агрегата площадки, не из собственных event-строк trade.trade_type=liquidation. Не ротируется, '
    'не партиционирована — тот же класс объёма, что open_interest_history. Пустая на момент создания.';
comment on column liquidation_volume_history.volume_unit is
    'Единица АГРЕГАТА, как определяет площадка (quote-notional, base qty, число событий — по бирже '
    'разное) — на этой таблице единица на строке уместна, в отличие от trade/book_topn/oi_history, '
    'где единица выводится из инструмента: это готовое число аналитики биржи, а не наше измерение '
    'в единицах инструмента, и из instrument_id её не вывести.';


-- ----------------------------------------------------------------------------
-- 6. Партиции — текущий и следующий месяц, для трёх партиционированных таблиц
-- ----------------------------------------------------------------------------
select create_month_partition('trade'::regclass,               date_trunc('month', now())::date);
select create_month_partition('trade'::regclass,               date_trunc('month', now() + interval '1 month')::date);
select create_month_partition('book_topn'::regclass,            date_trunc('month', now())::date);
select create_month_partition('book_topn'::regclass,            date_trunc('month', now() + interval '1 month')::date);
select create_month_partition('market_price_candle'::regclass,  date_trunc('month', now())::date);
select create_month_partition('market_price_candle'::regclass,  date_trunc('month', now() + interval '1 month')::date);


-- ----------------------------------------------------------------------------
-- 7. Гранты
-- ----------------------------------------------------------------------------
grant select on trade                        to studio_reader;
grant select on book_topn                     to studio_reader;
grant select on market_price_candle           to studio_reader;
grant select on open_interest_history         to studio_reader;
grant select on liquidation_volume_history    to studio_reader;
