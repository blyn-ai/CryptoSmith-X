# Avantis: полное покрытие 14/14 — инвентаризация источников

Промежуточный артефакт задачи «полное покрытие Avantis». Всё ниже — **замерено
живьём на mainnet, анонимно, без ключа**, командой, которая приведена рядом.
Отменяет вывод прежнего аудита о том, что часть колонок обязана остаться пустой.

## Главное: внешние источники не нужны ни в одной из 14 клеток

Прежний аудит смотрел `/v2/trading` и один вывод распространил на площадку.
Полный обход SDK нашёл три поверхности, которых он не видел, и все три — **нативные
Avantis**. Приоритет 1 из постановки побеждает везде; ветка «внешний fallback»
не понадобилась.

---

## Найденные поверхности

### A. Исполняемая котировка по размеру

```
POST https://prod-api.avantisfi.com/risk/v2/spread
{"pairIndex":0,"trader":"0x00…00","coinSize10":"<size×1e10>","isLong":true,"isOpen":true,"orderType":0}
```

`trader` = нулевой адрес — штатный анонимный режим их же UI (SDK: «the zero
address matches the UI's anonymous-quote fallback»). Ответ: `spreadPctWithoutFlow10`,
`estimatedSpreadPctWithFlow10`, `spreadMechanism` (SM001–SM006), `byPass`, `flowParams`.

Измерено на ETH, `HTTP 201`, 0.19 с:

| размер | long | short |
|---|---|---|
| 0.1 / 1 / 10 ETH | 0.010000 % | 0.010000 % |
| 100 ETH | 0.016952 % | 0.018355 % |
| 1 000 ETH | 0.048176 % | 0.043772 % |
| 2 000 ETH | 0.0839 % | — |
| 5 000 ETH | **0.2596 % = 26.0 bps** | — |
| 20 000 ETH | 2.0802 % | — |
| 50 000 ETH | `SM004 — No spread available` | — |

**Нагрузочный замер** (32 запроса, разные пары и стороны):

```
conc= 1   32 req in 6.19s  ( 5.2 req/s)  p50=0.191s p95=0.208s  {201:17, 403:11, 503:4}
conc= 8   32 req in 1.12s  (28.6 req/s)  p50=0.185s p95=0.556s  {201:17, 403:11, 503:4}
conc=16   32 req in 0.60s  (53.6 req/s)  p50=0.182s p95=0.575s  {201:17, 403:11, 503:4}
```

Раскладка кодов **одинакова на всех трёх уровнях** — значит, это свойство ПАР
(403 = закрытый/делистнутый рынок, 503 = SM004), а не троттлинг. Лимита не
наблюдается до 53.6 req/s. Бюджет: 51 пара × 2 стороны = 102 запроса ≈ 2 с при
concurrency 16.

**Локальной формулы спреда в SDK нет** — `compute` покрывает PnL, ликвидацию,
комиссии и ликвидность, но не спред: механизмы SM001–SM006 и `flowParams` живут
на сервере. По правилу постановки (п. 4) — значит, endpoint с ограниченным
бинарным поиском.

### B. Нативная лента сделок — то, чего прежний аудит не нашёл

```
GET https://api.avantisfi.com/v1/history/recent-trades/{pairIndex}
```

`HTTP 200`, 0.65 с, анонимно. Поля записи:

```
_id, hash, timestamp, price, openPrice, positionSize,
buy, isLong, isOpen, isLiquidation, isPartialClose,
isTp, isSl, isPartialTp, isPartialSl, isPnl, txnType
```

Замер глубины и охвата:

| пара | записей | охват |
|---|---|---|
| ETH (idx 0) | 10 | 4 ч 01 мин |
| BTC (idx 1) | 10 | 1 ч 47 мин |
| idx 20 | 10 | 5 ч 51 мин |

Десять записей покрывают часы, то есть поток сделок ~2.5/час на ETH. При опросе
раз в минуту с буфером в десять записей пропустить сделку невозможно.

**Отсюда нативно берутся четыре клетки**: `Last` (`price`), `Last trade`
(`timestamp`), `Turnover 24h` (скользящая сумма `positionSize`, дедуп по `hash`),
`Liquidations 24h` (то же с фильтром `isLiquidation`). Внешний источник для них
**не нужен** — приоритет 1.

### C. Расширенный OI

```
GET https://prod-api.avantisfi.com/core/v2/open-interests
```

`HTTP 200`, 27.8 КБ. Даёт `longOI`, `shortOI`, `longCoinOI`, `shortCoinOI` и —
чего нет в `/v2/trading` — **`pendingLongOI`, `pendingShortOI`,
`pendingLongCoinOI`, `pendingShortCoinOI`**.

### D. Чужие книги риск-движка (reference, НЕ книга Avantis)

```
GET https://prod-api.avantisfi.com/risk/v2/orderbook/snapshots
```

`HTTP 200`, 28 КБ, 194 строки, источники `hyperliquid`, `lighter`,
`binance_futures_depth`, `binance_spot_depth`, с `ageMs` у каждой. По ETH:

```
hyperliquid            bid 120 994  ask 114 103   age  7.1 s
lighter                bid  29 909  ask  27 049   age  3.7 s
binance_futures_depth  bid   7 683  ask   5 739   age 17.1 s
binance_spot_depth     bid   1 771  ask   1 778   age 17.1 s
```

Это НЕ книга Avantis. Хранится отдельным reference-набором с `source` на строке;
в клетки глубины Avantis не попадает — там стоит собственная quote curve.

### E. Локальная формула направленной ёмкости

`compute/liquidity.py::available_liquidity` — «Mirrors avantis-ui-v2 lib/trade.ts
availableLiquidity». Чистая функция от полей, которые мы **уже собираем** из
`/v2/trading`: `maxOpenInterest`, `totalOi`, `groupInfo.{groupMaxOI,groupOI}`,
`maxWalletOI`, `values.{groupOpenInterestPercentageP,maxLongOiP,maxShortOiP}`,
`pairMaxOI`, `openInterest.{long,short}`, `liquidity.{buy,sell}`. Отдаёт
максимальный дополнительный ноционал (USDC) по каждой стороне.

Это снимает вопрос о семантике: **`liquidity.buy` → long/ask, `liquidity.sell` →
short/bid**. HTTP не нужен вовсе.

### F. Единица фандинга

`data/markets.md`: `funding_fee_per_hour_p` — **процент в час**. Живое значение
по `fundingRate{long,short}` двустороннее и несимметричное (ETH: long 0.00109998,
short −0.00114364) — обе стороны показываются, среднее не берётся.

### G. Oracle

Pyth Lazer SSE, `PriceUpdate`: `feed_id`, `price`, `timestamp_ms`, `best_bid`,
`best_ask`, `raw`. Фид оракула, которым пользуется сама Avantis. Provenance `PYTH`.

---

## Таблица клеток: 14 из 14

Конвенция котировки — **10 000 USD ноционала на сторону**, базовое количество из
актуального index × multiplier. Если рынок столько не принимает — максимальный
реально котируемый размер, фактический ноционал печатается в клетке.

| клетка | показываемая величина | класс | источник | единица | каденс | badge |
|---|---|---|---|---|---|---|
| Bid | `index × (1 − spread_short)` | derived | A + oracle | цена | 1 мин | `Q10K` |
| Ask | `index × (1 + spread_long)` | derived | A + oracle | цена | 1 мин | `Q10K` |
| Spread | `(ask − bid) / mid`, subline long/short | derived | A | bps | 1 мин | `Q10K` |
| Last | `history[0].price` | **native** | B | цена | 1 мин | — |
| Last trade | `history[0].timestamp` | **native** | B | время | 1 мин | — |
| Mark | oracle, которым Avantis маркирует | native/ref | G | цена | 1 мин | `PYTH` |
| Index | oracle index | native/ref | G | цена | 1 мин | `PYTH` |
| Funding /day | long и short × 24, обе стороны | **native** | `/v2/trading` | %/сут | 1 мин | — |
| Venue rate | исходные часовые long/short | **native** | `/v2/trading` | %/ч | 1 мин | — |
| Interval | `1 h` | **native** | F | — | — | — |
| Turnover 24h | скользящая сумма `positionSize` | **native** | B | quote | 1 мин | — |
| Liquidations 24h | то же, `isLiquidation` | **native** | B | quote | 1 мин | — |
| Open interest | `coinOI` total; subline long/short + pending | **native** | `/v2/trading` + C | base | 1 мин | — |
| Bid / Ask size | `available_liquidity` по сторонам | derived | E | quote→base | 1 мин | `CAP` |
| Depth 10/25/50 | размер, котируемый не дороже порога, по сторонам | derived | A, бинпоиск | base | 5 мин | `Q` |
| Book reach | максимальный котируемый размер до SM004/hard cap | derived | A, бинпоиск | base | 5 мин | `QUOTE` |

Внешних источников — **ноль**. Ветка `EXT <source>` / `MKT Σ` из постановки не
задействована, потому что приоритет 1 нашёлся везде.

---

## Матрица паритета наборов

Снято с прода: `select dataset_code, mode from segment_dataset where segment_code=…`.

| набор | другие perp-венью | Avantis сейчас | что делаем |
|---|---|---|---|
| `snapshot` | collect | collect | остаётся; клеток в строке станет 14 |
| `discovery` | collect | collect | остаётся |
| `spec_versions` | collect | collect | остаётся |
| `candles` (trade OHLC) | collect | **disabled** | включаем из ленты B |
| `candles_index` | collect (Binance) | collect | остаётся; своднёй грани дала 0050 |
| `candles_mark` | collect (Binance) | **disabled** | включаем после подтверждения модели маркировки |
| `funding` | collect | **disabled** | включаем, обе стороны |
| `open_interest` | collect | **disabled** | включаем |
| `depth` | collect | **disabled** | включаем как quote-curve depth |
| `book` | collect | disabled | reference-набор из D, отдельным именем |
| `trades` | collect | **disabled** | включаем из ленты B |
| `liquidations` | collect | **disabled** | включаем из ленты B |
| `rollup` | collect | disabled | включаем |
| `vault_pair_state` | n/a | collect | остаётся |
| `vault_state` | n/a | collect | остаётся |

Интервалы свечей, которые ведёт `RollupJob`: 1, 5, 15, 60, 240, 720, 1440 —
Avantis обязан иметь их все, на всех сериях, что даёт 0050 для index/mark и
общий каскад для trade.

---

## Что это меняет в прежних решениях

Формулировки, которые больше не абсолютны и подлежат правке в
`plans/avantis-connection.md`, комментариях у `VaultPairRow` и тестах:

- «bid/ask нет по модели» → **нет resting book**, но есть исполняемая quote curve;
  клетки заполняются ею с badge.
- «нет публичной ленты исполнений» → **неверно**, лента B нативная и публичная.
- «turnover существует экономически; через API — нет» → **неверно**, считается из B.
- «нет дискретного платежа по модели» → верно про дискретность, но часовая ставка
  публикуется и нормализуется в сутки; обе стороны.

Различать надо четыре вещи и не стирать их: resting book · executable quote curve
· oracle/reference quote · external market. Но ни одна из них не является
основанием оставить клетку пустой.
