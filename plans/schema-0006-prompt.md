# Задание: миграция 0006 — справочник активов + недостающие рыночные данные

Репо: `blyn-ai/CryptoSmith-X`, ветка `main`. Прочти перед началом:
`plans/architecture.mermaid`, `src/CryptoSmithX.Database/Migrations/0001_initial.sql`
(целиком, включая шапку — там перечислено, что НЕ вошло и почему), `0005_exchange_admin.sql`,
`src/CryptoSmithX.MarketData.Hub/Ingestion/DiscoveryCollector.cs`,
`src/CryptoSmithX.MarketData.Connectors/Market/Instrument.cs`.

Правила репо: .NET 10, raw SQL + Npgsql + Dapper, никакого EF/репозиториев/MediatR.
`TreatWarningsAsErrors`. Миграция — один файл `0006_assets_and_metrics.sql`, идемпотентный
не нужен (migrator ведёт schema_version), но комментарии в стиле 0001: у каждой таблицы
`comment on table/column` по-русски, в шапке файла — что вошло, что НЕ вошло и почему.
Локальный стек: `deploy/docker-compose.yml` (`docker compose up -d --build`), проверka —
migrator должен выйти 0, hub/webapp подняться, все тесты зелёные (`dotnet test`).

## Зачем (контекст, не переписывай его в код)

Рядом лежит исторический дамп Kraken Futures (300 млн OHLCV-свечей, отдельная база).
Сравнение с ним показало: свечи добираются задним числом всегда, funding — частично,
а OI / глубина стакана / bid-ask НЕ добираются никак — они существуют только с момента,
когда мы начали их писать, и сейчас умирают вместе с ротацией market_snapshot (90 дней).
Плюс нормализация тикеров (XBT→BTC, 1000PEPE→PEPE) зашита в код адаптера — при 4 биржах
это разъедется. Отсюда две задачи.

## Задача A — справочник активов и нормализация

Цель: хранить И оригинал биржи, И каноническое имя; маппинг — данные, не код.
Межбиржевые выборки вида «все листинги BTC» должны быть одним простым запросом.

### Схема

```sql
-- asset — канонические активы. Строка появляется автоматически при первом
-- discovery неизвестного алиаса (code = алиас как есть) — админ потом может
-- слить дубль правкой alias, discovery починит инструменты следующим проходом.
create table asset (
    code        text primary key,          -- 'BTC', 'PEPE', 'SOL' — канон
    name        text,
    note        text,
    created_at  timestamptz not null default now()
);

-- asset_alias — как биржи называют актив. exchange_code null = глобальный алиас.
-- multiplier: '1000PEPE' -> PEPE с множителем 1000; вливается в contract_multiplier
-- инструмента при резолве (см. ниже), в ценах/объёмах ничего не пересчитываем.
create table asset_alias (
    exchange_code text references exchange (code),   -- null = для всех бирж
    alias         text not null,
    asset_code    text not null references asset (code),
    multiplier    numeric not null default 1 check (multiplier > 0),
    note          text,
    unique nulls not distinct (exchange_code, alias)  -- PG16
);
```

Сиды: `asset` — BTC, ETH (и активы фейка); `asset_alias` — глобальные `XBT→BTC`,
`1000PEPE→PEPE (multiplier 1000)`, `kPEPE→PEPE (1000)`. Больше не выдумывай —
остальное само зарегистрируется при discovery реальных бирж.

### exchange_instrument

- добавить `base_asset_raw text`, `quote_asset_raw text` — строки биржи КАК ЕСТЬ;
  backfill существующих строк: `raw = текущему значению`;  затем `not null`;
- `base_asset` и `quote_asset` остаются каноном, добавить FK на `asset(code)`
  (значит: сиды asset обязаны покрыть всё, что уже в таблице — проверь селектом);
- добавить `listed_at timestamptz` (null) — дата листинга ПО ДАННЫМ БИРЖИ
  (Kraken отдаёт openingDate). Это НЕ first_seen_at (когда увидели мы).
  В 0001 это поле сознательно не вошло («нет потребителя») — потребитель появился:
  фильтр «контракту меньше N дней — не торгуем, истории мало». Отрази это в шапке 0006.

### Перенос резолва из адаптера в Hub

Сейчас `Instrument.BaseAsset` — «Normalised by the adapter». Меняем контракт:

- `Instrument` (Connectors): `BaseAsset`/`QuoteAsset` переименовать в
  `BaseAssetRaw`/`QuoteAssetRaw` — адаптер отдаёт строки биржи как есть и
  НИЧЕГО не нормализует (адаптеры тупеют — это цель). Обнови Fake и его тесты.
- `DiscoveryCollector` перед upsert-ом инструментов резолвит каждый raw:
  1) алиас этой биржи → 2) глобальный алиас → 3) identity (canon = raw).
  На identity-промахе — upsert в `asset` (code = raw, note = 'auto-registered')
  и НИЧЕГО в asset_alias (identity не нуждается в строке-алиасе).
  Multiplier из сработавшего алиаса ПЕРЕМНОЖАЕТСЯ с multiplier-ом инструмента
  от адаптера, итог — в contract_multiplier.
  Резолв — одним запросом на пачку (посмотри, как discovery уже пишет пачками),
  не по строке на инструмент.
- Один прогон discovery должен чинить канон после правки алиаса админом:
  проверь, что upsert обновляет base_asset/quote_asset/contract_multiplier.

### Удобство выборок

`create view instrument_v` — instrument + exchange.name + канон, чтобы
«все листинги BTC по биржам» были `select * from instrument_v where base_asset='BTC'`.
API `/v1/instruments` уже отдаёт base_asset — поведение не меняется, но проверь
и Scalar-страницей, и curl-ом, что контракт ответа не поехал.

## Задача B — данные, которых нет, но которые не добрать задним числом

### funding_rate_history — НЕ ротируется

```sql
create table funding_rate_history (
    exchange_instrument_id integer not null references exchange_instrument (id),
    funding_time           timestamptz not null,      -- момент платежа (граница интервала)
    rate                   double precision not null, -- та же семантика, что в snapshot
    primary key (exchange_instrument_id, funding_time)
);
```

Коллектор `funding` (расширь CHECK в collector_status — это alter, не пересоздание):
раз в час тянет исторические ставки и дописывает недостающее (on conflict do nothing).
У фейка — синтетические ставки, чтобы петля жила и тестировалась. Интервал — в
MarketDataOptions рядом с остальными. Kraken-адаптера ещё нет — метод в
IExchangeMarketData добавь (`GetFundingHistoryAsync`), Fake реализует, живой Kraken
подключит его позже.

### market_metric_hour — часовой срез микроструктуры, НЕ ротируется

Снапшоты умирают через 90 дней; OI/глубина/спред должны жить дольше.
Одна строка на инструмент-час, пишет существующий rollup-джоб последним шагом
(отдельный джоб не заводи), из market_snapshot за закрытый час:

```sql
create table market_metric_hour (
    exchange_instrument_id integer not null references exchange_instrument (id),
    hour_time              timestamptz not null,      -- начало часа, UTC
    open_interest_last     double precision not null, -- последнее наблюдение часа
    funding_rate_last      double precision not null,
    spread_bps_avg         double precision,          -- avg((ask-bid)/mid*1e4), null если стакан кривой
    depth_bid_25bps_avg    double precision,          -- avg по ненулевым, null если измерений не было
    depth_ask_25bps_avg    double precision,
    snapshot_count         smallint not null,         -- сколько снапшотов вошло — мера доверия
    primary key (exchange_instrument_id, hour_time)
);
```

Пересчёт закрытого часа при повторном прогоне — upsert целиком (как rollup свечей).
25 bps достаточно (10/50 остаются в снапшотах свои 90 дней) — зафиксируй выбор в комментарии.

### Что НЕ делать (впиши в шапку 0006 как «не вошло»)

- `feed` (mark/index/premium свечи) — бот торгует по trade; mark/index текущие есть
  в снапшоте; колонка без потребителя.
- `tags` Кракена — нет потребителя.
- Никаких новых сервисов/проектов; всё в существующих Hub-джобах.
- Бэкфилл дампа Кракена — отдельная задача, сюда не тащить. Но проверь мысленно:
  дамп маппится через новый справочник (symbol дампа = exchange_symbol) без правок схемы.

## Definition of done

1. `0006_assets_and_metrics.sql` применяется миgratorом на существующую базу
   (не только на пустую!) — прогони оба сценария локально.
2. Fake-биржа: discovery регистрирует активы, funding-петля пишет историю,
   rollup наполняет market_metric_hour. Покажи селектами.
3. `instrument_v` возвращает канон; выборка по base_asset между биржами — один where.
4. Все тесты зелёные, новые — на резолв алиасов (биржевой > глобальный > identity,
   multiplier перемножается) и на пересчёт metric_hour.
5. `/v1/instruments` и `/scalar/v1` не изменились по контракту.
6. Один коммит, сообщение в стиле репо, push в main.
