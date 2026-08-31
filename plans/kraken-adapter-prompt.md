# Задание: конфигурация в базе + адаптер Kraken Futures

Репо: `blyn-ai/CryptoSmith-X`, ветка `main`. Прочти перед началом:
`plans/architecture.mermaid`, миграции `0001`, `0005`, `0006`,
`src/CryptoSmithX.MarketData.Hub/Ingestion/` (весь), `Options/MarketDataOptions.cs`,
`src/CryptoSmithX.MarketData.Connectors/Fake/FakeExchangeMarketData.cs`,
`Areas/Admin/` в WebApp (Exchanges — образец страниц и форм).

Правила репо: .NET 10, raw SQL + Npgsql + Dapper, без EF/репозиториев/MediatR.
`TreatWarningsAsErrors`. Комментарии миграций по-русски, в стиле 0001 (шапка: что
вошло / что НЕ вошло и почему). Два коммита — по одному на фазу. После каждой
фазы: локальный стек (`deploy/docker-compose.yml`) поднимается, все тесты зелёные.
Прод доезжает сам: push в main → GitHub Actions → GHCR → VPS.

Принятое решение владельца: ВСЕ настройки market-data живут в базе и редактируются
через админ-UI. appsettings у Hub остаётся только ConnectionStrings + Logging.
URL тоже в базе; цена дрейфа гасится аудитом — каждая правка пишет updated_by/at.

## Фаза 1 — конфигурация переезжает в базу, hub становится супервизором

### Миграция 0007_runtime_settings.sql

exchange дополняется (существующие строки — осмысленные сиды, не null):
- `adapter text not null` — 'fake' у fake, 'kraken-futures' у kraken-futures,
  остальным их code (адаптеров нет — статус planned их и так не запустит);
- `base_url text` — сид для kraken-futures: 'https://futures.kraken.com';
- `charts_url text` — сид: 'https://futures.kraken.com/api/charts/v1'
  (свечи у Кракена на другом хосте пути — потому отдельная колонка);
- `quote_assets text[] not null default '{USD,USDT,USDC}'`;
- `blacklist text[] not null default '{}'`;
- интервалы, null = взять глобальный из setting:
  `snapshot_interval_s int`, `candle_interval_s int`, `discovery_interval_min int`,
  `funding_interval_min int`, `depth_interval_s int`;
- `updated_by text` — если 0005 её ещё не добавил.

Новая таблица `setting` — глобальные значения, key/value с типом-подсказкой:
```sql
create table setting (
    key         text primary key,
    value       text not null,
    kind        text not null check (kind in ('int','text','int_list')),
    description text not null,      -- показывается в UI, пиши по-человечески
    updated_at  timestamptz not null default now(),
    updated_by  text
);
```
Сиды — текущие значения MarketDataOptions: snapshot_interval_s=10,
candle_interval_s=60, discovery_interval_min=60, funding_interval_min=60,
depth_interval_s=60, derived_timeframes=5,15,60,240,720,1440,
snapshot_retention_days=90, candle_backfill_hours=3, funding_backfill_hours=168,
delist_after_missed_discoveries=3.

### Hub: конфиг из базы, живой

- Новый `DbSettings` (Hub): читает setting + exchange одним-двумя запросами,
  кэш ~30 с. Никакого IOptions поверх — простой класс, как Db.
- `MarketDataOptions`/`ExchangeOptions` и секция MarketData в appsettings.json
  УДАЛЯЮТСЯ. Всё, что их читало, переходит на DbSettings.
- `ExchangeWorker` становится супервизором: каждые ~30 с сверяет петли с базой —
  `status='enabled'` и петель нет → построить адаптер и запустить;
  статус ушёл из enabled → отменить CancellationToken этой биржи, петли гаснут.
  Рестарт контейнера при переключении биржи в UI больше НЕ нужен — это цель фазы.
- Петли читают свой интервал через DbSettings на каждой итерации: правка
  интервала в UI применяется максимум через старый интервал + 30 с кэша.
- rollup/retention остаются одни на сервис (не на биржу) — как сейчас.

### UI (веб-приложение уже несёт дизайн-систему — смотри app.css, не выдумывай стилей)

- Exchanges/Details, форма Lifecycle расширяется: adapter (read-only текст),
  base_url, charts_url, quote_assets (строка через запятую), blacklist,
  пять интервалов (пусто = глобальный, покажи глобальное значение плейсхолдером).
  Сохранение пишет updated_by = User.Identity.Name.
- System → Settings: пункт меню уже есть с бейджем soon
  (SideNavViewComponent.cs:76) — снять soon, сделать страницу: таблица setting,
  value редактируется инлайн-формой, description показывается, после сохранения
  подпись "применится в течение минуты" (кэш DbSettings + интервал петли).
- Валидация по kind: int — целое > 0; int_list — целые через запятую. Кривое
  значение не сохраняется, alert-error (класс уже есть).

## Фаза 2 — адаптер Kraken Futures

Структура (зеркально Fake, всё новое — в Kraken/):
```
Connectors/Kraken/KrakenFuturesMarketData.cs   — 4 метода IExchangeMarketData
Connectors/Kraken/KrakenFuturesClient.cs       — тонкий HTTP, base_url/charts_url из ctor
Connectors/Kraken/KrakenDtos.cs                — internal record-ы под JSON Кракена
Connectors.Tests/KrakenFuturesMarketDataTests.cs
```

- Эндпоинты (публичные, ключей НЕТ — hub по архитектуре без ключей):
  instruments `GET {base_url}/derivatives/api/v3/instruments`;
  tickers `GET {base_url}/derivatives/api/v3/tickers`;
  свечи `GET {charts_url}/trade/{symbol}/1m?from=&to=` (unix-секунды);
  funding `GET {base_url}/derivatives/api/v3/historicalfundingrates?symbol=`;
  стакан `GET {base_url}/derivatives/api/v3/orderbook?symbol=`.
- Адаптер ТУПОЙ: не нормализует тикеры (BaseAssetRaw как отдал Кракен — 'XBT';
  справочник 0006 разрулит, алиас XBT→BTC уже засижен), не ретраит, не логирует,
  не спит. Ошибка HTTP = исключение наружу, петля сама посчитает и запишет.
- Scope V1 (из 0001): только линейные перпы из quote_assets биржи; symbol
  начинается с 'PF_'; dated/инверсные пропускаются в discovery.
- Семантика полей — по комментариям схемы 0001, они закон:
  funding_rate относительный за интервал (у Кракена абсолютный → делить на mark);
  open_interest в единицах количества; volume свечей в единицах количества;
  turnover_24h в quote; trade_count у Кракена НЕТ → null, не 0.
- Глубина: вызов на символ. Собирать ТОЛЬКО для status='trading', проходами по
  depth_interval_s, и уважать лимит: между вызовами пауза, чтобы полный проход
  316 символов не превышал публичный rate limit Кракена — вычисли из документации
  и зафиксируй числом в комментарии. Не успели все символы за проход — у
  неуспевших depth_* остаются null и depth_at честно старый (схема так задумана).
- Тесты: канонические JSON-ответы Кракена фикстурами (сохранить в
  Connectors.Tests/Fixtures/kraken/*.json, взять реальные ответы public API),
  тестируется маппинг: XBT остаётся raw, funding делится на mark, PF_-фильтр,
  null trade_count, парсинг свечей. Без сети в тестах.
- `ExchangeWorker.Build()`: ветка по exchange.adapter из базы —
  'kraken-futures' → new KrakenFuturesMarketData(new KrakenFuturesClient(baseUrl, chartsUrl)).

## Definition of done

1. Миграция 0007 применяется на существующую базу И на пустую.
2. Секции MarketData в appsettings больше нет; hub конфигурируется только базой.
3. В админке: биржа включается/выключается БЕЗ рестарта hub (проверь на fake:
   выключил в UI → петли гаснут за ≤ интервал+30с, включил → поднялись).
4. Страница System → Settings работает, значения меняются, аудит пишется.
5. Kraken: на локальном стеке переключи kraken-futures в enabled — discovery
   находит инструменты (XBT в base_asset_raw, BTC в base_asset), снапшоты со
   спредом/OI/глубиной, свечи, funding-история пишутся. Покажи селектами.
6. Все тесты зелёные, включая новые кракен-фикстуры.
7. Два коммита в стиле репо, push в main (CI сам довезёт до прода; прод-биржу
   kraken-futures в enabled НЕ переключать — это делает владелец в UI).
