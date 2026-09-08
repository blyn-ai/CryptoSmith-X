# Каталог хранения и сверка со схемой

Первая часть — целевой каталог: что хранить, где, откуда приходит, в какой единице.
Вторая — что из этого база умеет **сегодня** (схема версии 0029, снята с живой базы
через `information_schema`, не по памяти).

Дата сверки: 2026-09-08.

---

## 1. Целевой каталог

| Метрика | Лежит в | Приходит из | Единица | Что это |
| --- | --- | --- | --- | --- |
| symbol | instrument | discovery | текст | символ биржи как есть; новый символ = новый инструмент |
| base, quote | instrument | discovery | текст | активы, ключ в реестр алиасов |
| contract_form | instrument | discovery | enum | linear / inverse |
| contract_type | instrument | discovery | enum | perpetual / future |
| expires_at | instrument | discovery | ts | экспирация; NULL у perpetual |
| qty_unit | instrument | discovery | enum | base / contract — в чём биржа считает количество |
| first_seen_at | instrument | discovery | ts | впервые в списке |
| valid_from | instrument_spec | discovery | ts | когда впервые увидели эти значения; часть ключа |
| valid_to | instrument_spec | discovery | ts | NULL = действует; ставится при следующей версии |
| last_seen_at | instrument_spec | discovery | ts | последнее подтверждение тех же значений; у действующей версии = когда инструмент вообще видели; «пропал» = старше интервала discovery |
| venue_effective_at | instrument_spec | человек | ts | когда биржа реально поменяла; NULL = не знаем |
| status | instrument_spec | discovery | enum | наш: active / halted / post_only / delisting / delisted / unknown |
| status_raw | instrument_spec | discovery | текст | статус биржи дословно |
| price_tick | instrument_spec | discovery | price | шаг цены |
| qty_step | instrument_spec | discovery | qty | шаг количества |
| min_qty | instrument_spec | discovery | qty | минимальная заявка |
| multiplier | instrument_spec | discovery | число | base в одном contract; NULL при qty_unit = base, CHECK |
| funding_interval | instrument_spec | discovery | interval | период начисления |
| funding_interval_source | instrument_spec | discovery | enum | venue / measured / assumed |
| spec_hash | instrument_spec | discovery | текст | отпечаток нормализованных полей; новая версия при изменении хэша |
| raw_json | instrument_spec | discovery | json | ответ биржи по инструменту как есть |
| received_at | snapshot | ticker | ts | время всех колонок из ticker |
| venue_ts | snapshot | ticker | ts | время биржи на тикере; NULL = не отдаёт |
| last | snapshot | ticker | price | последняя сделка; HL тикером не отдаёт → NULL, из mid не выводить |
| last_trade_at | snapshot | ticker | ts | время этой сделки по бирже; NULL = не отдаёт |
| bid, ask | snapshot | ticker | price | лучшие котировки тикера |
| bid_size, ask_size | snapshot | ticker | qty | объём на лучших |
| mark | snapshot | ticker | price | mark по формуле биржи |
| index | snapshot | ticker | price | индекс; у HL — oracle, определение своё |
| funding_rate | snapshot | ticker | rate | ставка текущего периода, как показывает биржа; rate за funding_interval |
| funding_rate_predicted | snapshot | ticker | rate | прогноз следующего периода; Kraken отдаёт, остальные NULL |
| next_funding_at | snapshot | ticker | ts | момент следующего расчёта по бирже; NULL = не отдаёт |
| volume_24h_base | snapshot | ticker | qty | скользящие 24 ч в base; NULL, где не отдают |
| volume_24h_quote | snapshot | ticker | quote | скользящие 24 ч в quote; NULL, где не отдают |
| open_interest_at | snapshot | oi | ts | время измерения OI |
| open_interest | snapshot | oi | qty | raw OI в qty_unit инструмента |
| depth_at | snapshot | depth | ts | время состояния книги, из которой считаны полосы |
| depth_ref | snapshot | depth | price | mid книги на depth_at; база расчёта полос |
| depth_10_bid … depth_50_ask | snapshot | depth | quote | накопленный ноционал в полосе от depth_ref |
| book_reach_bid, book_reach_ask | snapshot | depth | bps | докуда видна книга; reach ≥ band = полная полоса; пустая сторона = 0; книги нет = NULL |
| open_time | candle | candles | ts | начало минуты [t, t+1m); только закрытые |
| received_at | candle | candles | ts | когда получили |
| known_from | candle | candles | ts | с какого момента строка известна PIT; NULL = с received_at |
| source | candle | candles | enum | live / backfill |
| open, high, low, close | candle | candles | price | OHLC минуты |
| volume_base | candle | candles | qty | объём в base |
| volume_quote | candle | candles | quote | объём в quote; NULL, где не отдают |
| trade_count | candle | candles | count | число сделок; NULL, где не отдают |
| timeframe | candle_derived | rollup | min | 5 … 1440 |
| open_time, OHLCV, trade_count | candle_derived | rollup | как в candle | композиция минуток |
| minutes_known | candle_derived | rollup | count | минут из timeframe, о которых знаем; меньше timeframe = неполное измерение |
| series | market_price_candle | candles_mark / candles_index | enum | mark / index |
| timeframe, open_time | market_price_candle | candles_mark / candles_index | min, ts | таймфрейм и начало; только закрытые свечи |
| open, high, low, close | market_price_candle | candles_mark / candles_index | price | OHLC mark/index; объёма намеренно нет |
| received_at, known_from, source | market_price_candle | candles_mark / candles_index | ts, ts, enum | получение, PIT и live/backfill |
| observed_at | book_topn | book | ts | время биржи из кадра, не наш час |
| received_at | book_topn | book | ts | когда получили |
| seq | book_topn | book | число | номер состояния книги; строка пишется только при смене |
| is_snapshot | book_topn | book | bool | первая строка после ресинка |
| levels | book_topn | book | count | сколько уровней сохранено |
| bid_px[], bid_qty[], ask_px[], ask_qty[] | book_topn | book | price, qty | верхние N уровней; длины px/qty совпадают и не превышают levels |
| bid_n[], ask_n[] | book_topn | book | count | ордеров на уровне; NULL, где не отдают |
| venue_uid | trade | trades | текст | id биржи; aggTrade или raw — свойство площадки |
| seq | trade | trades | число | номер в ленте; NULL, где нет |
| event_time | trade | trades | ts | время биржи |
| received_at, known_from | trade | trades | ts | время получения и PIT |
| source | trade | trades | enum | ws / rest_recent / backfill |
| price | trade | trades | price | цена сделки |
| qty | trade | trades | qty | размер сделки; raw в qty_unit инструмента |
| taker_side | trade | trades | enum | buy / sell по тейкеру; маппинг по площадке |
| trade_type | trade | trades | enum | fill / liquidation / partial_liquidation / termination / block; NULL = неизвестно, не fill |
| venue_time, received_at | liquidation | liquidations | ts | только площадки с отдельным потоком; у Kraken ликвидации через trade.trade_type |
| side | liquidation | liquidations | enum | сторона ликвидируемой позиции; у Binance противоположна стороне ордера |
| price, qty | liquidation | liquidations | price, qty | цена и размер ликвидации |
| funding_time | funding | funding | ts | момент расчёта по бирже |
| funding_rate | funding | funding | rate | расчётная ставка за funding_interval |
| funding_interval | funding | funding | interval | семантический период ставки на этой строке; не выводить только из разницы funding_time |
| received_at, known_from, source | funding | funding | ts, ts, enum | время получения, PIT и live/backfill |
| bucket_time | open_interest_history | open_interest | ts | сетка биржи |
| interval_s | open_interest_history | open_interest | count | ширина бакета |
| oi_open, oi_high, oi_low, oi_close | open_interest_history | open_interest | qty | raw OI в qty_unit; Kraken даёт OHLC, Binance только oi_close |
| oi_quote | open_interest_history | open_interest | quote | OI в quote; Binance отдаёт, остальные NULL |
| received_at, known_from, source | open_interest_history | open_interest | ts, ts, enum | время получения, PIT и analytics/backfill |
| bucket_time, interval_s | liquidation_volume_history | liquidations | ts, count | сетка биржи и ширина бакета |
| volume | liquidation_volume_history | liquidations | qty/quote | объём ликвидаций; конкретная единица фиксируется в описании площадки |
| received_at, known_from, source | liquidation_volume_history | liquidations | ts, ts, enum | время получения, PIT и analytics/backfill |
| feed | coverage | любой исторический | enum | candles / candles_mark / candles_index / funding / trades / open_interest / liquidations |
| range_from, range_to | coverage | — | ts | реально покрытый диапазон; при limit_hit range_to = последняя возвращённая запись |
| returned | coverage | — | count | сколько записей вернула биржа |
| requested_at | coverage | — | ts | когда запросили |
| reason | coverage | — | enum | NULL = полностью; throttled / error / timeout / limit_hit / beyond_history |
| feed | gap | любой | enum | какой поток |
| instrument | gap | — | ссылка | NULL = весь канал |
| gap_from, gap_to | gap | — | ts | интервал гэпа; gap_to NULL = открыт |
| cause | gap | — | enum | ws_disconnected / ws_sequence_gap / resync / throttled / venue_error / process_down; process_down определяется по session/heartbeat при следующем старте |

---

## 2. Сверка со схемой 0029

Коротко: из тринадцати целевых таблиц **пять есть в каком-то виде, восемь отсутствуют
целиком**. Из того, что есть, ни одна таблица не совпадает с каталогом полностью.

### 2.1 Сквозные расхождения — важнее любой отдельной колонки

1. **PIT и происхождение отсутствуют везде.** Ни в одной таблице нет ни `known_from`, ни
   `source` (live / backfill / ws / rest_recent). База не отличает строку, полученную в
   момент события, от строки, дозаполненной задним числом. Это то, о чём говорит
   `collection-policy.md` §8: ось D нигде не записана.
2. **«NULL = не отдаёт» невозможен.** В `market_snapshot` двенадцать колонок с
   `NOT NULL` там, где каталог требует NULL: `last_price`, `bid/ask`, `bid_size/ask_size`,
   `mark`, `index`, `funding_rate`, `turnover_24h`, `open_interest`, `open_interest_at`.
   Площадка, которая чего-то не отдаёт (HL — `last`; Kraken — объём в quote), вынуждает
   писать подставное значение. Правило дизайн-системы «— значит не измерено, а не ноль»
   из этой таблицы вывести нельзя.
3. **Единицы не записаны.** Каталог различает price / qty / quote / rate / bps; в базе всё
   рыночное — `double precision` без указания единицы, спека — `numeric`. `market_candle.volume`
   вообще без единицы в имени: base или quote — по договорённости в коде. Точность
   `double` для цен и объёмов — отдельное решение, принятое раньше; каталог его не
   пересматривает, но фиксирует, что тик цены (`price_step numeric`) и сама цена
   (`double`) живут в разных типах.
4. **`received_at` есть только у snapshot.** У свечей — `updated_at` (это не то же:
   перезапись при повторной загрузке двигает его), у фандинга нет ничего.
5. **Enum'ов в Postgres нет — везде `text` + CHECK.** Это не расхождение, а стиль; но
   списки значений в CHECK расходятся с каталогом (см. status и gap.cause ниже).

### 2.2 По таблицам

**instrument → `exchange_instrument`.** Есть: `exchange_symbol`, `base_asset`/`quote_asset`
(+ `_raw`), `first_seen_at`. Нет: `contract_form` (linear/inverse), `contract_type`
(perpetual/future — есть только `segment.kind` на уровне площадки, не инструмента),
`expires_at`, `qty_unit`. Без `qty_unit` при `contract_multiplier NOT NULL CHECK > 0`
множитель 1 неотличим от «количество в base».

**instrument_spec → нет.** Спека лежит плоско на `exchange_instrument` и перезаписывается
на месте: `price_step`, `qty_step`, `min_qty`, `contract_multiplier`, `funding_interval_hours`,
`status`, `raw_json`. Нет версий (`valid_from/valid_to`), нет `venue_effective_at`,
`status_raw`, `spec_hash`, `funding_interval_source`. `funding_interval_hours smallint`
не выразит период короче часа. Статус: у нас `trading / post_only / reduce_only / halted /
delisted`; в каталоге `active / halted / post_only / delisting / delisted / unknown` — нет
`delisting` и `unknown`, есть лишний `reduce_only`. Единственная история — `status_changed_at`
и `last_seen_at`, то есть меняющийся тик цены прошлого не хранит.

**snapshot → `market_snapshot`.** Есть: `received_at`, `last/bid/ask/bid_size/ask_size/
mark/index/funding_rate`, `turnover_24h` (= volume_24h_quote), `open_interest(_at)`,
`depth_at`, шесть полос 10/25/50. Нет: `venue_ts`, `last_trade_at`, `funding_rate_predicted`,
`next_funding_at`, `volume_24h_base`, `depth_ref`, `book_reach_bid/ask`. Без `depth_ref`
полоса не воспроизводима; без `book_reach` пустая полоса неотличима от книги, которая
кончилась раньше полосы. Плюс NOT NULL из п. 2.1.

**candle, candle_derived → `market_candle`** (одна таблица, `timeframe` 1 и >1, помесячные
партиции). Есть: `open_time`, OHLC, `volume` (единица не названа), `trade_count`,
`bar_count` = minutes_known с CHECK `≤ timeframe` — это совпадает с каталогом. Нет:
`received_at`, `known_from`, `source`, `volume_quote`.

**market_price_candle (mark/index) → нет.**

**book_topn → нет.** Датасет `book` не зарегистрирован даже в `dataset`.

**trade → нет.** Датасет `trades` зарегистрирован (`disabled`), таблицы нет.

**liquidation → нет.** Датасет `liquidations` зарегистрирован (`disabled`), таблицы нет.

**funding → `funding_rate_history`.** Три колонки: `exchange_instrument_id`, `funding_time`,
`rate`. Нет `funding_interval` на строке — период приходится выводить из соседних строк,
что каталог прямо запрещает. Нет `received_at`, `known_from`, `source`.

**open_interest_history → нет.** Датасет `open_interest` зарегистрирован (`disabled`).
OI сегодня живёт только внутри snapshot (наша сетка, наш час) и в
`market_metric_hour.open_interest_last` (наш собственный часовой rollup) — сетки биржи и
OHLC по OI нет нигде.

**liquidation_volume_history → нет.**

**coverage → нет.** `collector_run` пишет прогон (`started_at`, `items`, `ok`, `error`,
`http_status`, `request_weight`, `transport`), но не покрытый диапазон: нет
`range_from/range_to`, `returned`, `reason` (`limit_hit` / `beyond_history`). Ответить
«за какой период у нас есть история и где она кончилась потому, что биржа больше не даёт»
база не может.

**gap → `collector_gap`.** Совпадает по смыслу: `collector` (= feed, свободный текст),
`exchange_instrument_id` NULL = весь канал, `gap_start/gap_end` с открытым концом, `cause`.
Списки причин разные по именам, семантика покрыта: `rate_limited` ≈ throttled,
`error` + `exchange_maintenance` ≈ venue_error, `collector_down` ≈ process_down; лишний
`timeout`. Единственная таблица, которую можно считать готовой с точностью до переименований.

### 2.3 Что есть в базе и чего нет в каталоге

`market_metric_hour` (наш часовой rollup OI / фандинга / спреда / глубины с `expected_count`
и `gap_seconds`), `market_snapshot_latest`, `min_notional`, `listed_at`, `collect*` на
инструменте, весь блок `collector_run` / `collector_status`. Всё это операционное или
производное; каталог про сырьё, и противоречия тут нет.

### 2.4 Итог одной строкой

Готова одна таблица из тринадцати (gap). Четыре существуют, но без PIT, без источника,
без единиц и с NOT NULL там, где нужен NULL. Восемь надо создавать с нуля — и три из
них (trades, liquidations, open_interest) уже числятся в `dataset` как выключенные
датасеты, у которых нет места, куда писать.
