# Что переводится на WS, а что нет — проверено подключением

Дата: 2026-09-09. Метод: подписка к живому сокету каждой площадки, приём 6–14 с, засчитан
только кадр нужного типа. Не документация, не память — что реально пришло. Скрипты пробы:
`ws2/ws3/ws4/ws6.py` в скретчпаде сессии; URL взяты из `segment.ws_url`, формат подписки —
из наших рабочих фидов.

## 1. Итог: покрытие по наборам

`ЕСТЬ` = канал ответил данными. `берём` = уже реализовано у нас. `—` = канала нет.

| набор | WEEX | Binance USDⓈ-M | Kraken Futures | Hyperliquid |
| --- | --- | --- | --- | --- |
| **depth** | `@depth200` **берём** | `@depth@100ms` **берём** | `book` **берём** | `l2Book` **берём** |
| **snapshot** (цена/mark/index/funding/OI) | **— нет ticker-канала** | `!ticker@arr` + `!markPrice@arr@1s` ЕСТЬ | `ticker` **берём** (несёт funding, prediction, OI) | `activeAssetCtx` ЕСТЬ (OI + funding) |
| **trades** | `@trade` ЕСТЬ | `@aggTrade` ЕСТЬ | `trade` ЕСТЬ | `trades` ЕСТЬ |
| **candles** | `@kline_1m` ЕСТЬ | `@kline_1m` ЕСТЬ | **— нет** (только REST charts) | `candle` ЕСТЬ |
| **liquidations** | `@forceOrder` — нет | `!forceOrder@arr` ЕСТЬ | через `trade.trade_type` | — нет отдельного |
| **open_interest** | `@openInterest` — нет | в `!markPrice@arr@1s`? нет — только REST | в `ticker` ЕСТЬ | в `activeAssetCtx` ЕСТЬ |
| **candles_mark / index** | — нет | `@markPrice` — поток, не свечи | — нет | — нет |
| **discovery / spec** | — нет, REST по природе | — нет | — нет | — нет |

## 2. Что это меняет по каждой площадке

**WEEX — главный выигрыш.** Из восьми проверенных каналов живы два, и оба закрывают самое
дорогое. `@trade` и `@kline_1m` (первый кадр — `klineSnapshot`, дальше обновления) снимают
`candles` с REST: 990 запросов в минуту и 198 ГБ/мес превращаются в подписку. Остаются на
REST `open_interest` (990 запросов, 27 с на проход — та самая стена §5.2) и `snapshot`:
**у WEEX ticker-канала нет вообще** — `@bookTicker`, `@markPrice`, `@miniTicker`, `@ticker`
все отвергнуты, что наш `WeexWsFeed.cs:136` уже фиксировал и проба подтвердила.

**Binance — эндпоинты разделены, и это надо знать.** Проверено на `@depth@100ms` и
`@aggTrade` по четырём путям:

| | `/public/stream` | `/market/stream` | `/ws` | `/stream` |
| --- | --- | --- | --- | --- |
| `btcusdt@depth@100ms` | **ЕСТЬ** (64) | нет | ЕСТЬ (66) | ЕСТЬ (65) |
| `btcusdt@aggTrade` | нет | **ЕСТЬ** (45) | нет | нет |

`/public/stream` — наш текущий `segment.ws_url` — для depth **работает**, и это снимает
подозрение, что «0 of 566 books live» вызвано неверным адресом. Но сделки, свечи и рыночные
потоки на нём молчат: подписка подтверждается `{"result":null}` и данных нет — ровно
ловушка, описанная в `BinanceWsFeed.cs:240`. Значит **нужен второй сокет** на
`/market/stream`, где подтверждены: `@kline_1m`, `@markPrice@1s`, `!markPrice@arr@1s`,
`!ticker@arr`, `!forceOrder@arr`, `@aggTrade`.

**Kraken — почти всё уже есть.** `ticker` (берём) несёт `funding_rate`,
`funding_rate_prediction` и открытый интерес в одном кадре — то есть `open_interest` и
`funding` у Kraken **уже приходят по WS** и не требуют REST-цикла. Новое — `trade`,
он же закрывает ликвидации через `trade_type` (0035 §D п.4). Свечей по WS нет: только
REST `charts`.

**Hyperliquid — всё четыре канала живы:** `l2Book` (берём), `trades`, `candle`,
`activeAssetCtx` (OI + funding в одном кадре), плюс `allMids`. По покрытию это самая полная
площадка из четырёх.

## 3. Что на WS не переводится ни у кого

- **discovery и spec_versions** — по природе REST: список инструментов и их параметры не
  поток событий.
- **История** — `funding` за прошлые периоды, `open_interest_history`, бэкфилл свечей,
  `coverage`. WS даёт «с этого момента», историю отдаёт только REST.
- **candles_mark / candles_index** — ни одна из четырёх не публикует свечи mark/index; у
  Binance есть поток `markPrice`, из которого их можно собирать самим, но это уже наш
  расчёт, а не данные площадки.
- **WEEX open_interest** — bulk-варианта нет ни в одном поколении API, WS-канала нет.
  Единственный набор, который остаётся упираться в стену: 990 запросов, 27 с, 37→25 req/s.

## 4. Порядок работ, по отдаче

1. **Kraken `open_interest` и `funding` — уже в `ticker`, который мы читаем.** Дешевле всего:
   данные приходят, надо их записать. Ноль новых соединений.
2. **WEEX `@kline_1m`** — снимает 990 запросов/мин и 198 ГБ/мес, канал есть, протокол тот же,
   что у уже работающего `@depth200`.
3. **`@trade` на всех четырёх** — единственный набор, живой везде; закрывает `trades` и,
   через `trade_type` у Kraken, часть ликвидаций.
4. **Второй сокет Binance на `/market/stream`** — открывает `kline`, `markPrice`,
   `!forceOrder@arr` (ликвидации) и `!ticker@arr` разом.
5. **Hyperliquid `activeAssetCtx` + `candle`** — OI, funding и свечи одной подпиской.

Не переводится и остаётся REST-ом при любом раскладе: discovery, spec, вся история,
`open_interest` у WEEX и Binance, свечи у Kraken.
