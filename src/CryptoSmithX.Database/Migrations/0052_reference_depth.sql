-- ============================================================================
-- CryptoSmith X — миграция 0052: чужие книги, за которыми следит риск-движок
-- Avantis, получают собственный набор — и не притворяются книгой Avantis.
--
-- НАЙДЕНО ЗАМЕРОМ. GET https://prod-api.avantisfi.com/risk/v2/orderbook/snapshots
-- отвечает анонимно, HTTP 200, ~28 КБ, 194 строки по четырём источникам:
-- hyperliquid, lighter, binance_futures_depth, binance_spot_depth — с
-- накопленной ликвидностью на своей стороне и собственным возрастом чтения
-- (ageMs) у каждой строки.
--
-- ЭТО НЕ КНИГА AVANTIS. У площадки книги нет вовсе (0044). Это то, за чем
-- следит ЕЁ риск-движок, чтобы оценивать собственную кривую котировки —
-- AvantisQuotes уже отвечает на «сколько возьмёт Avantis» через
-- /risk/v2/spread, а это отвечает на другой вопрос: «что показывают книги,
-- на которые площадка смотрит». Смешивать их в одну таблицу значило бы
-- выдать чужую ликвидность за собственную — ровно то, что план запретил
-- дословно.
--
-- Поэтому отдельная таблица, source на каждой строке, и не book_topn: тот
-- набор существует ТОЛЬКО там, где сокет площадки сам держит уровни
-- (TryGetBookFrame), и остаётся честно disabled для Avantis.
--
-- Полный крест по сегментам — тот же приём и та же причина, что в 0044:
-- «не умеет» и «выключили руками» должны различаться.

create table vault_reference_depth (
    exchange_instrument_id integer     not null references exchange_instrument (id),
    source                  text        not null,
    received_at             timestamptz not null,
    cumulative_bid_qty      double precision,
    cumulative_ask_qty      double precision,
    venue_age_seconds       double precision,
    primary key (exchange_instrument_id, source, received_at)
);

comment on table vault_reference_depth is
    'Чужие книги (hyperliquid/lighter/binance...), за которыми следит риск-движок '
    'vault-backed площадки, чтобы оценивать собственную кривую котировки. НЕ книга '
    'этой площадки — та отсутствует по модели (0044). source называет, о какой '
    'площадке на самом деле эта строка, чтобы её нельзя было прочитать как '
    'собственную ликвидность наблюдаемой.';
comment on column vault_reference_depth.venue_age_seconds is
    'Возраст чтения, КАК ЕГО ЗАЯВЛЯЕТ риск-движок в ageMs у каждой строки — не '
    'received_at минус момент наблюдения, потому что каденс опроса чужих книг '
    'нам неизвестен и не наш, чтобы его предполагать.';

insert into dataset (code, name, kind, description, default_mode, default_interval_s, default_retention_days)
values
    ('reference_depth', 'Reference depth', 'feed',
     'Чужие книги, за которыми следит риск-движок vault-backed площадки — не собственная книга',
     'disabled', 300, 30)
on conflict (code) do nothing;

insert into segment_dataset (segment_code, dataset_code, mode, note, updated_by)
select s.code, 'reference_depth', 'disabled', null, '0052 migration'
  from segment s
 on conflict (segment_code, dataset_code) do nothing;

-- we_implement НЕ проставляется здесь — тем же приёмом, что 0044: адаптер
-- объявит сам при сборке, не миграция от его имени.
insert into segment_dataset_capability (segment_code, dataset_code, capability_key)
select sd.segment_code, sd.dataset_code, k.key
  from segment_dataset sd
 cross join capability_key k
 where sd.dataset_code = 'reference_depth'
 on conflict (segment_code, dataset_code, capability_key) do nothing;
