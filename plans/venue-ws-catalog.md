# Что биржи умеют по WS — против того, что мы берём

Проверено 2026-09-08: официальные доки всех четырёх бирж (ссылки внизу) + живые
пробы. Этот файл фиксирует ВНЕШНИЕ факты — их из нашего кода не вывести, и они
меняются только когда биржа меняет API. Что берём МЫ — не дублируется здесь
подробно: это выводимо из кода (Capabilities каждого адаптера говорят
`"rest"` / `"rest,ws"` по каждому датасету; сверено с кодом в тот же день).

Контекст: обсуждение «собирать больше данных» — событийную гранулярность вместо
минутных семплов. Эта матрица отвечает на вопрос «а всё через WS нельзя?».

## Матрица: датасет × биржа

|                      | Kraken Futures | Binance USDⓈ-M | Hyperliquid | WEEX |
|----------------------|----------------|-----------------|-------------|------|
| Книга (depth)        | WS есть, берём | WS есть, берём  | WS есть, берём | WS есть, берём |
| Тикер (bid/ask/last) | WS есть, берём | WS есть (`@bookTicker`), **не берём** | WS нет батчем; per-coin ctx есть | **WS нет вовсе** |
| Mark/index/funding   | в WS-тикере, берём | WS есть (`!markPrice@arr`), **не берём** | WS есть (`activeAssetCtx`), **не берём** | **WS нет** |
| Open interest        | в WS-тикере, берём | **WS нет вовсе** — только REST по символу | WS есть (`activeAssetCtx`), **не берём** | **WS нет** — только REST по символу |
| Свечи 1m             | **WS нет вовсе** (только spot-сокет; фьючерсы — Charts REST) | WS есть (`@kline_1m`), **не берём** | WS есть (`candle`, 1m…1M), **не берём** | **WS нет** |
| Трейды               | WS есть (`trade`), не берём | WS есть (`@aggTrade`), не берём | WS есть (`trades`), не берём | **WS нет** |
| Каталог инструментов | **WS нет** | дельты есть (`!contractInfo`), полный каталог — REST | **WS нет** (meta только POST /info) | **WS нет** |

Полные каталоги, дословно из доков:
- **Kraken Futures WS** (wss://futures.kraken.com/ws/v1), публичные фиды — весь
  список: `book, ticker, ticker_lite, trade, heartbeat`. OHLC-фида на
  ФЬЮЧЕРСНОМ сокете нет (фид «Candles (OHLC)» в доках — это spot-WS); минутки —
  `GET /api/charts/v1/{tick_type}/{symbol}/1m` (проверено живьём на PI_XBTUSD).
- **Binance USDⓈ-M WS** (wss://fstream.binance.com): 20 стримов, среди них
  bookTicker, markPrice (mark+index+funding, 1s/3s), kline, ticker/miniTicker,
  aggTrade, depth, contractInfo. Open-interest-стрима НЕТ ни в одном виде
  (у Options есть, у фьючерсов — нет); OI только REST `/fapi/v1/openInterest`
  по одному символу (батч-вызов без символа отвечает 400 -1102, проверено).
- **Hyperliquid WS**: `l2Book` (полный снапшот каждым пушем), `candle`
  (1m…1M), `trades`, `activeAssetCtx` (markPx, oraclePx, funding,
  openInterest per-coin), bulk-варианты `allDexsAssetCtxs` / `fastAssetCtxs`.
  Каталог (universe, szDecimals, marginTables) — только REST POST /info.
- **WEEX** (wss://ws-contract.weex.com/v3/ws/public): единственный рабочий
  публичный канал — `@depth200`. Тикер-каналы (`@bookTicker`, `@markPrice`,
  `@miniTicker`) отвергаются сокетом — проверено живьём при постройке
  адаптера (комментарии в WeexWsFeed.cs это фиксируют); funding и OI в
  сокете отсутствуют.

## Следствия (зафиксированы после обсуждения 2026-09-08)

1. «Всё через WS» не существует ни на одной бирже: каталог инструментов не
   пушит никто, а история (свечи/funding назад) — это запросы о прошлом,
   которые чинят дыры после обрывов; REST-контур обязателен в любом случае.
2. WS доступен ровно там, где REST уже дёшев (батчи Binance — 55 weight/проход
   на ~570 символов; Hyperliquid — один metaAndAssetCtxs на всю вселенную), и
   отсутствует ровно там, где REST болит (per-symbol OI-циклы Binance/WEEX).
   Переезд «дорогого» на WS невозможен, переезд «дешёвого» — малоценен, пока
   персист — минутный семпл.
3. Кандидаты, если понадобится тиковая история: Hyperliquid `activeAssetCtx`
   (funding/OI пушем — единственная биржа, где это возможно) и Binance
   `@kline_1m` (быстрее закрытие бара; REST-backfill всё равно остаётся).
4. План «собирать больше» (dual-write): писать события должен НЕ адаптер и не
   коллектор по тику, а отдельный рекордер (фид → ограниченная очередь →
   батч-вставка), старый путь не меняется ни на байт; тот же Postgres-неймспейс,
   новый префикс таблиц; ротация и партиции — до первого INSERT; приёмка —
   «минутные семплы выводимы из событий, diff чистый N суток», и только потом
   старый пайплайн заменяется деривацией. До любого DDL — снять фактические
   event-rates с живых WS-фидов (они уже считают кадры) и посчитать GB/день:
   хост уже ловил LA 5.5 от одного плохого запроса.

## Источники

- Kraken: docs.kraken.com/exchange/api-reference/futures-websocket/ (индекс с
  полным каталогом фидов), …/futures-websocket/{ticker,ticker_lite,book,trade},
  docs.kraken.com/api/docs/futures-api/websocket/ticker/ (схема с funding/OI),
  docs.kraken.com/exchange/guides/general/historical-data (Charts REST).
- Binance: developers.binance.com/…/usd-s-m-futures/api/ws-streams/market
  (полный список стримов), …/rest-api/market-data#open-interest,
  …/websocket-market-streams/{Mark-Price-Stream,Contract-Info-Stream,…}.
- Hyperliquid: hyperliquid.gitbook.io/…/api/websocket/subscriptions,
  …/api/info-endpoint/perpetuals.
- WEEX: живые пробы каналов, записанные в комментариях WeexWsFeed.cs /
  WeexFuturesMarketData.cs этого репозитория.
