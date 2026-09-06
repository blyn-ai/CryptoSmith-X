# Порядок площадок и адаптеров

Очередь на реализацию. Порядок задан продуктом; ниже — он же, плюс честный
статус того, что уже есть, плюс место под пометки исследования (§3), которые
могут этот порядок изменить.

Термины по 0019: **exchange** — площадка (venue), **segment** — торговая
поверхность внутри неё (futures / spot / options), **dataset** — вид данных.
Уровень площадки существует потому, что бюджет запросов на IP общий у всех её
сегментов: спот и фьючерсы одной биржи делят один лимит, и планировать их надо
вместе, а не как две независимые интеграции.

## 1. Очередь

| # | Exchange | Segment | Статус | Зачем |
|---|---|---|---|---|
| 1 | Hyperliquid | Futures / Perpetuals | **есть, WS** | ключевой on-chain perp |
| 2 | Kraken | Futures | **есть, WS** | сильный EU/reference venue |
| 3 | WEEX | Futures / Perpetuals | **есть, WS** | широкий perp universe |
| 4 | Binance | Futures / Perpetuals | **есть, WS** | главный пробел — закрыт |
| 5 | OKX | Futures / Perpetuals | следующий | крупный derivatives venue |
| 6 | Bybit | Futures / Perpetuals | | закрывает основную тройку CEX derivatives |
| 7 | Binance | Spot | | spot/perp basis + главный spot reference |
| 8 | Coinbase | Spot | | USD / US / institutional reference |
| 9 | Kraken | Spot | | spot↔futures + уже знакомая экосистема |
| 10 | OKX | Spot | | spot↔perp + reuse коннектора |
| 11 | Bybit | Spot | | spot↔perp + reuse коннектора |
| 12 | Deribit | Options | | новый класс данных и клиентов, а не ещё один perp |
| 13 | Coinbase | Futures / Perpetuals | | institutional derivatives reference |
| 14 | Gate | Futures / Perpetuals | | derivatives breadth + RWA/perp |
| 15 | MEXC | Futures / Perpetuals | | long-tail, новые и мелкие инструменты |
| 16 | Bitget | Futures / Perpetuals | | последний полезный крупный derivatives venue |

## 2. Что действительно есть на сегодня (2026-09-06)

Четыре адаптера собраны в `ExchangeWorker.Build` (`ExchangeWorker.cs:533-537`) и
все четыре имеют живой WebSocket:

| адаптер | код | WS | чем питается стакан | известные оговорки |
|---|---|---|---|---|
| Kraken Futures | `kraken-futures` | да | книга по WS с контрольной суммой | — |
| Hyperliquid | `hyperliquid` | да | книга по WS | сделки не храним (`docs/datagaps.md`) |
| WEEX Futures | `weex-futures` | да, V3 | книга по WS; правило цепочки `U == prev.u`, не как у Binance | **лимит запросов под вопросом**, см. ниже |
| Binance USDⓈ-M | `binance-usdm` | да | книга по WS с REST-засевом, вес 20 на засев | пик веса 92 % бюджета после реконнекта (`0023`) |

Спота нет ни одного, и это не только вопрос адаптера: `market_snapshot_latest`
объявляет `mark_price`, `index_price`, `funding_rate`, `open_interest`,
`open_interest_at` как **not null** (`0001_initial.sql:119-140`). Спотовая
площадка не имеет ни одного из этих полей, то есть спотовую строку сегодня
физически некуда записать. Это блокер для пунктов 7-11, и он в схеме, а не в
коннекторе.

### Долг, который надо закрыть до пункта 5

**Лимит WEEX не сходится сам с собой.** `0021_venue_request_budget.sql` сеет
`('weex', 200, 8, 'documented', …)` и ссылается на поле `rateLimits` из ответа
`GET /capi/v3/market/exchangeInfo`. Живой запрос к этому эндпоинту 2026-09-06
вернул `REQUEST_WEIGHT, interval MINUTE, intervalNum 10, limit 500` — то есть
500 весов на 10 **минут**, а не 2000 на 10 секунд. Либо поле подписано неверно
на стороне WEEX (500/10 мин запретили бы даже наш текущий REST-опрос), либо
посев завышен примерно в 240 раз. Единственная площадка, помеченная у нас как
`documented`, документирована хуже всех остальных. Разбирать до того, как на
`request_budget_source = 'documented'` обопрётся что-то ещё.

## 3. Пометки исследования

_Заполняется по результатам разведки: коннективность, сложность реализации,
возможности каждой площадки. Может изменить порядок §1._
