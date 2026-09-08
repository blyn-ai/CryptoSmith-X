# Каталог хранения и сверка со схемой

Первая часть — целевой каталог: что хранить, где, откуда приходит, в какой единице.
Вторая — что из этого база умеет **сегодня**: схема версии **0035**, снята через
`information_schema` с базы, на которую накатаны пять миграций фазы «только база»
(0030–0034, коммит 519f646) и хвост к ней (0035, fa13d8f), не по памяти и не по тексту
миграций.

Дата сверки: 2026-09-08, вечер. Первая редакция этого файла (dec87dd) сверяла с 0029 и
устарела в момент записи: 0030–0034 уже лежали в main. Вторая (852946c) — с 0034; 0035
вышла через час и закрыла один из её пунктов. Ниже — состояние на 0035.

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

## 2. Сверка со схемой 0035

Коротко: из тринадцати целевых таблиц **двенадцать есть, одной нет**. Восемь из
двенадцати совпадают с каталогом полностью или с точностью до имён. Все новые таблицы
**пусты и писателя не имеют** — фаза 0030–0034 была «только база», это её условие, а не
дыра; соответствующие датасеты стоят в `dataset` как `disabled`.

### 2.1 Что закрыла фаза 0030–0034

| Проблема из первой редакции | Состояние в 0034 |
| --- | --- |
| «NULL = не отдаёт» невозможен: 11 × NOT NULL в snapshot | **снято** (0030). Все одиннадцать полей тикера nullable; NOT NULL остался только на `received_at` |
| у snapshot нет `venue_ts`, `last_trade_at`, `funding_rate_predicted`, `next_funding_at`, `volume_24h_base`, `depth_ref`, `book_reach_*` | **все восемь добавлены** (0030), с комментариями, что каждое значит |
| спека без версий | **`instrument_spec`** (0031): `valid_from` NN, `valid_to`, `last_seen_at` ≥ `valid_from`, `venue_effective_at`, `spec_hash` NN, `raw_json`, `written_by`; CHECK на интервал |
| `funding_interval_source` нет | **есть** на обеих таблицах, `venue / measured / assumed` |
| PIT и источник нигде | **есть** на `trade`, `market_price_candle`, `funding_rate_history`, `open_interest_history`, `liquidation_volume_history`: `received_at`, `known_from`, `source` с CHECK по значениям каталога |
| `market_price_candle`, `book_topn`, `trade`, `open_interest_history`, `liquidation_volume_history` — нет | **созданы** (0032), первые три партиционированы помесячно; массивы книги с CHECK на равенство длин и `≤ levels` |
| `coverage` нет | **есть** (0033): `range_from < range_to`, `returned ≥ 0`, `reason` ровно из списка каталога, ссылка на `run_id` |
| `market_candle` — единственная историческая таблица без провенанса | **закрыто 0035**: `received_at`, `known_from`, `source` с CHECK `rest / backfill / derived`. Существующие строки не обновлялись: у них всё три NULL, и `timeframe > 1 ⇔ derived` выводится из ключа — это записано в комментарий таблицы |
| единица объёма ликвидаций «в описании площадки» | лучше каталога: **`volume_unit` на строке** |
| событийные данные как double | **новое соглашение о типах** (0032): сделка, уровень книги, бакет биржи — `numeric`; наши агрегаты (snapshot, candle) остаются `double precision` |

### 2.2 Метод — и чего он не видит

Сверка «есть колонка / нет колонки» против 95-строчного каталога по построению слепа к
решениям: всё отсутствующее для неё «открыто». Поэтому у каждого расхождения здесь три
возможных статуса, а не два:

- **закрыто** — колонка есть, совпадает с каталогом;
- **отложено** — колонки нет, причина записана в заголовке миграции (файл:раздел), и
  назван **триггер переоткрытия**. Пока триггер не сработал, пункт не открыт и в итоге
  не считается;
- **открыто** — колонки нет и причины в репозитории нет, либо причина перестала
  держаться.

Источник причин — заголовки миграций, не чат и не этот документ. Применённый файл не
правится, поэтому причины серии 0030–0035 живут в **0035 §D** («Что сознательно НЕ
вошло, и почему — дословно»); сверка обязана на него ссылаться, а не переоткрывать
пункты, которых не читала. Две первые редакции этого файла именно так и ошиблись.

### 2.3 Расхождения по статусам

**Отложено — 0035 §D п. 1.** `qty_unit`, `contract_form`, `contract_type`, `expires_at`.
Причина: скоуп V1 — линейные перпетуалы; для линейного контракта `qty_base = qty ×
contract_multiplier` верно для каждой строки каждой таблицы, «множитель 1» и «в base» —
одно утверждение, а не два, которые могли бы разойтись. Теряется только у инверсных
(множитель в quote), которых нет ни на одной подключённой площадке. Когда придут — нужен
`contract_form` плюс валюта множителя, а не `qty_unit` (тот отвечал бы не на тот вопрос).
**Триггер:** инверсные или dated контракты в скоупе.

**Отложено — 0035 §D п. 3.** `status_raw`, значения `delisting` и `unknown`. Причина:
сырой статус лежит в `raw_json` каждого прохода discovery; делистинг определяет
`delist_after_missed_discoveries` (0014), а не статус биржи; `unknown` не нужен — статус
есть у всех четырёх площадок. **Триггер:** площадка, где статус ненаблюдаем или где
`delisting` — наблюдаемое состояние, а не наша интерпретация.

**Отложено — 0035 §D п. 4.** Событийная таблица ликвидаций. Причина: единственный
наблюдаемый писатель — Binance `forceOrder`; без кода сборщика это пустая таблица без
потребителя дольше остальных пяти из 0032. Kraken закрыт через `trade.trade_type`
(`liquidation / partial_liquidation / termination`). **Триггер:** Binance `forceOrder`
в сборе.

**Переоткрыто — `volume_quote` на `market_candle`.** 0035 §D п. 2 держит причину 0001
(«нет у HL и Kraken; для ликвидности есть `turnover_24h`») и не переоткрывает. Причина
не держится по нашей же логике: по ней мы не завели бы `oi_quote` (0032) и
`volume_24h_quote` (0030), а завели — с NULL там, где не отдают. Честная причина иная:
читателя нет, свечи восстановимы, значит не срочно. Из неё следует не «никогда», а
«когда писателя всё равно трогаем»: `CandleCollector` переписывается в фазе сборщиков под
`received_at` / `known_from` / `source` — это и есть момент. **Рекомендация:**
`volume_quote numeric` nullable в миграции той фазы, с записью в её заголовке, что 0001 в
этом пункте пересмотрен и почему. Про «`volume` без единицы в имени»: переименования
запрещены правилами серии, единица записана в 0001:227 (inline-комментарий в SQL, не
`comment on column`) — это не дыра.

**Закрыто.** Всё остальное из первых двух редакций: `instrument_spec` (0031), одиннадцать
NOT NULL и восемь колонок snapshot (0030), пять событийных таблиц (0032), `coverage` и
провенанс истории (0033), провенанс `market_candle` (0035), `funding_interval_source`.
Хвосты подтверждены 0035 §C: партиции trade/book_topn/market_price_candle на текущий и
следующий месяц, `coverage.run_id` уже `on delete set null`.

**Косметика, не расхождение.** `collector_gap.cause` — свои имена (`rate_limited` ≈
throttled, `collector_down` ≈ process_down, `error` + `exchange_maintenance` ≈
venue_error, лишний `timeout`), семантика каталога покрыта.

### 2.4 Итог одной строкой

Двенадцать таблиц из тринадцати есть. **Открыт один пункт — `volume_quote`, на фазу
сборщиков.** Три отложены с причиной и триггером в 0035 §D и до триггера не считаются.
Ни одна новая таблица ещё не пишется — это следующая фаза, и она кодовая.
