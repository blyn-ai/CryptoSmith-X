# Промпт: три режима свежести — исполнение `plans/live-three-modes.md`

Это бриф исполнителю. План — там; здесь — то, чего в плане нет: точные места в коде,
сигнатуры, инварианты, тесты до кода и приёмка цифрами. Там, где план говорит «сделать
веер», здесь написано, какой класс, какие поля, что нельзя, и какой тест это ловит.

Работа режется на пять слайсов, **каждый — свой PR**, в порядке A → B → C → D → E.
A и B независимы друг от друга. C ждёт A. D ждёт C. E — после всего.

## Что уже установлено (не перепроверять)

- Режимы **Latest** и **History** существуют. Latest — это `LivePageController.Live`
  (`Controllers/LivePageController.cs`), `LISTEN csx_live` через `Live/LiveNotifier.cs`, пол
  5 с (`PairsV2Controller.MinPushInterval`), потолок 100 потоков (`Live/LiveStreamGate.cs`).
  История — обычный рендер. **Ни строки в этом цикле не менять** — см. «Что сохранить дословно».
- По проводу сейчас едет **отрендеренная разметка** (`event: panel`, `data:` — HTML), клиент
  `wwwroot/studio-live.js` вживляет её функцией `morph()`, которая сопоставляет детей
  **по индексу**. На такте 200 мс это не работает: Razor на каждого зрителя пять раз в
  секунду и обход поддерева `morph()` — та пара, на которой таблица уже один раз разъехалась.
- Hub — `Microsoft.NET.Sdk.Worker` без HTTP (`src/CryptoSmithX.MarketData.Hub/Program.cs`,
  `Host.CreateApplicationBuilder`). Образ в `deploy/Dockerfile` — `dotnet/runtime`, не `aspnet`.
- Кэши фидов — `Connectors/Streaming/MarketCache.cs`: `Set/TryGet(maxAge)/FresherThan(maxAge)`,
  **событий нет**. Поэтому выход хаба **опрашивает** кэши на своём такте; в фидах не меняется
  ни строки.
- `Connectors/Streaming/EventBuffer.cs` — однопотребительский: `Drain()` забирает всё.
  `TradeCollector` его единственный потребитель. Второй `Drain()` украдёт события у базы.
- Что реально живое по площадкам (по коду, не по докам):

  | сегмент | котировка (bid/ask) | mark/index/funding/OI | глубина / книга | сделки |
  |---|---|---|---|---|
  | `kraken-futures` | `KrakenWsFeed.TryGetFreshTickers` — целые `Ticker` | там же | `TryGetDepth`, `TryGetBookFrame` | `_trades` |
  | `hyperliquid` | `HyperliquidWsFeed.TryGetTop` (`BookTop`) | `TryGetFreshContexts` (`AssetContext`) | `TryGetDepth`, `TryGetBookFrame` | `_trades` |
  | `binance-usdm` | **только как верх книги**: `BinanceWsFeed.TryGetBookFrame(symbol, 1)` — `!bookTicker` на REST | `BinanceMarketWsFeed.TryGetFreshContexts` (`BinanceContext`) | `BinanceWsFeed.TryGetDepth` | `_trades`, `_liquidations` |
  | `weex-futures` | только как верх книги: `WeexWsFeed.TryGetBookFrame(symbol, 1)` | **нет** — тикер на REST; OI — фоновый REST-фид | `TryGetDepth` | `_trades` |

  Где в таблице «нет» — в живом кадре поля `null`, строка остаётся на базе. **Не заполнять из REST.**
- Модель страницы: `PairPageLoader.LoadAsync` → `PairPageModel(Rows: VenueRowModel(Row: PairVenueRow,
  Windows, Ages: CallAges, …), Verdicts: Verdicts.Compute(rows), …)`. Возраст строки — по
  `Row.ReceivedAt` (`PairPageLoader.cs:82`). Клетки форматирует `V2Cells.Field(r, field, stress,
  book)` → `V2Cell(Value, Text, Sub)`; марки — `V2Ranks.Mark/ToneWord`; колонки — `V2Columns.All`.
- Сеть: `hub` и `studio` в одном compose (`deploy/docker-compose.prod.yml`, сеть по умолчанию);
  у `hub` нет `ports:`. Значит `http://hub:8080` из студии — и ниоткуда больше. В
  `deploy/traefik/cryptosmithx.yml` для хаба маршрута нет и не будет.
- Тестовые проекты: `tests/CryptoSmithX.MarketData.Connectors.Tests`, `…Hub.Tests`,
  `…WebApp.Studio.Tests`. Studio-тесты читают копии файлов из `surface/` — список `Content
  Include` в `CryptoSmithX.WebApp.Studio.Tests.csproj`; новый файл, который тест читает,
  добавляется туда.

---

## Слайс A — хаб отдаёт живое

**Где именно.** `src/CryptoSmithX.MarketData.Hub/` (csproj, `Program.cs`, новая папка `Live/`),
`src/CryptoSmithX.MarketData.Connectors/` (`IExchangeMarketData.cs`, `Market/`, четыре адаптера),
`deploy/Dockerfile` (stage `hub`).

**Что сделать.**

1. `CryptoSmithX.MarketData.Hub.csproj`: `Sdk="Microsoft.NET.Sdk.Web"`. `Program.cs`:
   `WebApplication.CreateBuilder(args)`; `ExchangeWorker` остаётся `AddHostedService`;
   `Migrator.VerifyAsync` остаётся до `RunAsync`; Sentry остаётся через logging (как сейчас).
   Kestrel: `http://0.0.0.0:8080`, никакого HTTPS, никакой авторизации. `deploy/Dockerfile`:
   stage `hub` → `dotnet/aspnet:10.0` (сейчас `runtime`, комментарий там прямо говорит почему —
   его переписать).
2. `Connectors/Market/LiveQuote.cs`:
   ```csharp
   public sealed record LiveQuote(
       string ExchangeSymbol, DateTimeOffset At,
       double? BidPrice, double? BidSize, double? AskPrice, double? AskSize,
       double? LastPrice, double? MarkPrice, double? IndexPrice, double? FundingRate,
       double? OpenInterest, double? Turnover24h, Depth? Depth);
   ```
   `At` — время из кэша фида (`MarketCache.Entry.At`), не «сейчас». `null` — «сокет этого не
   держит», никогда `0` и никогда REST.
3. `IExchangeMarketData` — default-член, зеркально `DrainTrades()`:
   ```csharp
   /// Only what the venue's own socket holds and is younger than maxAge. Never REST — an adapter
   /// with no socket returns nothing, and that is the answer.
   IReadOnlyList<LiveQuote> LiveQuotes(TimeSpan maxAge) => [];
   ```
   Реализации — по таблице выше; для Binance/WEEX bid/ask — верх `TryGetBookFrame(symbol, 1)`.
   `FakeExchangeMarketData` — не реализует (пусто).
4. `Hub/Live/InstrumentMap.cs`: `(segmentCode, exchangeSymbol) → instrumentId` и обратно, тем же
   предикатом, что `ExchangeWorker.CollectedSymbols` (`collect = true and status = 'trading'`),
   перечитывается раз в 60 с. `SnapshotCollector` в этом слайсе **не трогать** — его SQL
   остаётся, объединение позже.
5. `Hub/Live/LiveEgress.cs` — `GET /live?instruments=1,2,3`, `text/event-stream`,
   `X-Accel-Buffering: no`, `: ping` раз в 25 с (как в `LivePageController`). На соединение:
   `HashSet<int> wanted`, `Dictionary<int, LiveQuote> lastSent`. Цикл — `PeriodicTimer(200 ms)`:
   для каждого сегмента, у которого есть `wanted`, `adapter.LiveQuotes(settings.WsStaleAfter)`
   → в id через `InstrumentMap` → отобрать те, что не `Equals` последнему отправленному →
   один `event: quotes` с JSON-массивом `{"id":…, "seg":"…", "at":<unix ms>, "bid":…, …}`.
   Пустой массив не шлётся. Запись — в том же цикле, последовательно: если запись не уложилась
   в такт, следующий такт просто позже — очередь не копится **по построению**.
6. Адаптеры хабу доступны через `ExchangeWorker` — он их строит в `Build`. Отдать наружу
   словарь `segmentCode → IExchangeMarketData` через небольшой `IAdapterRegistry`, который
   `ExchangeWorker` заполняет при старте сегмента и чистит при остановке. Выход читает только
   его; `VenueGate` не берёт.

**Тесты (Hub.Tests, Connectors.Tests).**
- `LiveQuotes` у адаптера с пустым фидом → `[]`, и **ни одного HTTP-вызова** (fake `HttpMessageHandler`,
  считающий запросы, — образец есть в тестах коннекторов).
- Конфлятор: три `Set` одного символа между двумя тиками → один элемент в кадре, последнее значение.
- Кадр не содержит инструментов, чьё значение не изменилось.
- `InstrumentMap` не отдаёт инструменты с `collect = false`.

**Приёмка.** На тесте, из контейнера студии: `curl -N http://hub:8080/live?instruments=<id>` —
кадры идут, интервал между кадрами ≥ 200 мс, `at` в кадре — время фида, не время запроса.
`curl https://cryptosmithx-test.blynai.eu/live` — **404 от Traefik**, не ответ хаба.

---

## Слайс B — наблюдатели в `EventBuffer`

**Где именно.** `Connectors/Streaming/EventBuffer.cs`, `IExchangeMarketData.cs`, четыре фида
(только проброс).

**Инварианты — записать тестами ДО кода.**
1. `Drain()` возвращает ровно то же, что и без наблюдателей, при любом числе наблюдателей и
   при переполненном наблюдателе. Это единственный путь в базу, и он не теряет.
2. Наблюдатель ограничен своим `capacity`; при переполнении выбрасывает **старейшее** и считает
   `Dropped` — та же дисциплина, что у самого буфера.
3. `Dispose()` наблюдателя прекращает доставку; `Add` после этого его не видит.
4. `Add` остаётся O(1) и без блокировок при N наблюдателях.

**Что сделать.**
```csharp
public sealed class EventTap<T> : IDisposable
{
    public IReadOnlyList<T> Drain();   // своё, независимое от EventBuffer.Drain()
    public long Dropped { get; }
}
// на EventBuffer<T>:
public EventTap<T> Observe(int capacity);
```
Список наблюдателей — иммутабельный массив, заменяемый через `Interlocked.CompareExchange`
(copy-on-write): `Add` читает ссылку один раз и раскладывает событие по очередям без lock.
`IExchangeMarketData`: `EventTap<TradeEvent>? ObserveTrades(int capacity) => null;` и
`ObserveLiquidations` — default null; фиды пробрасывают в свои `_trades`/`_liquidations`.
Выход хаба (слайс A) на каждом тике дренирует свои `EventTap` и шлёт `event: trades`.

**Чего не делать.** Не трогать `Drain()`. Не заводить второй `ConcurrentQueue` «на всякий
случай». Не звать наблюдателей синхронно колбэком из `Add` — это отдаёт поток сокета чужому коду.

---

## Слайс C — веер в студии

Здесь исполнитель без присмотра ошибается тихо. Поэтому — интерфейсы, инварианты и тесты
**до** реализации, и реализация только в этих рамках.

**Где именно.** `src/CryptoSmithX.WebApp.Studio/Live/` (новые `HubStream.cs`, `LiveRoom.cs`,
`LiveRooms.cs`, `LiveFrameGate.cs`, `LiveFrame.cs`), `Controllers/LivePageController.cs` (новая
ветка, старая не меняется), `Controllers/PairsV2Controller.cs`, `Program.cs` (регистрации),
`appsettings*.json` (`Hub:BaseUrl`, на тесте `http://hub:8080`).

### C.1 `HubStream` — одно соединение вверх на процесс

```csharp
public sealed class HubStream : IDisposable
{
    public HubStreamState State { get; }                 // Down | Opening | Up
    public event Action<HubStreamState>? StateChanged;
    public event Action<HubQuotesFrame>? Quotes;         // распарсенный event: quotes
    public event Action<HubTradesFrame>? Trades;
    public IDisposable Subscribe(IReadOnlyCollection<int> instrumentIds);
}
```
Инварианты:
- В любой момент **не более одного** открытого HTTP-соединения к хабу. Подписка = объединение
  всех живых `Subscribe`. Изменение объединения → debounce 250 мс → закрыть и открыть заново
  с новым `?instruments=`. Пустое объединение → соединение закрыто, `State = Down` не
  выставляется (это не отказ, это «никто не смотрит»).
- Переподключение с backoff 1 с → 30 с, бесконечно, пока есть подписчики. Любой обрыв →
  `State = Down` и `StateChanged` **один раз**, не на каждую попытку.
- Обработчики событий **не бросают** — правило `LiveNotifier`, дословно: исключение в
  обработчике логируется и не роняет читающий цикл.
- Одна регистрация в DI: `AddSingleton<HubStream>()`; никакого `AddHostedService` — по той же
  причине, по какой её нет у `LiveNotifier` (комментарий в `Program.cs:60`).

Тесты (`Studio.Tests`, с фейковым `HttpMessageHandler`, отдающим SSE-поток из строки):
`Two_subscriptions_open_one_connection`; `Union_changes_reconnect_once_after_debounce`;
`Last_subscriber_gone_closes_the_connection`; `A_throwing_handler_does_not_stop_the_reader`;
`A_drop_is_reported_once_and_recovery_once`.

### C.2 `LiveRoom` — одна комната на актив

```csharp
public sealed class LiveRoom : IDisposable
{
    public string BaseFamily { get; }
    public ChannelReader<LiveFrame> Join();            // Channel(capacity: 1, DropOldest) на зрителя
    public void Leave(ChannelReader<LiveFrame> reader);
    public int Viewers { get; }
}
```
Что держит: `PairPageModel baseline` — загружен через `PairPageLoader.LoadAsync` **тем же
кешом**, и перезагружается по `LiveNotifier` ровно как режим Latest (проходы коллекторов
меняют OI, глубину, покрытие — живой кадр их не несёт); `Dictionary<int, LiveQuote> latest`;
`LiveFrame lastFrame`; `PeriodicTimer(200 ms)`.

Тик:
1. Если с прошлого тика не пришло ни одной котировки и baseline не менялся — ничего.
2. Наложить котировки на строки **только** там, где `quote.At >= row.ReceivedAt` (правило
   «побеждает более позднее время наблюдения» из плана §6): `PairVenueRow with { BidPrice = …,
   … }`; `CallAges.PriceSeconds` = `now - quote.At`; отдельно `written = now - row.ReceivedAt`.
3. `Verdicts.Compute(rows)` — марки считаются в кадре, иначе живые цифры со старыми метками.
4. Слоты: для каждой строки и каждой `V2Columns.All` — `V2Cells.Field(...)` → текст, подпись;
   `V2Ranks.Mark/ToneWord` → марка; возраст живой и записанный — `Format.SpacedAge`.
   Это **тот же код, что рендерит Razor**; в JS формул нет.
5. Диф против `lastFrame` по слотам → `LiveFrame(seq, slots)`; пустой диф не шлётся.
6. Записать во **все** каналы зрителей. Канал `BoundedChannelFullMode.DropOldest`, ёмкость 1 —
   это и есть «пропустить кадр, а не копить», и никакой другой очереди нет.

Инварианты:
- Один расчёт на тик на комнату, **не × зрителей**. Счётчик расчётов — в лог раз в минуту.
- Комната создаётся первым `Join`, уничтожается последним `Leave` — таймер остановлен,
  подписка `HubStream` снята, подписка `LiveNotifier` снята. Утечка комнаты = провал приёмки.
- `HubStream.State == Down` → комната **живёт** на baseline, кадры несут `signal: degraded`;
  восстановление → `signal: up` и полный (не дифф) кадр.

Тесты: `One_tick_computes_once_for_many_viewers`; `A_slow_viewer_sees_the_newest_frame_and_not_the_backlog`;
`A_quote_older_than_the_written_row_does_not_overlay_it`; `Marks_move_when_one_venue_moves`
(двинулась одна площадка — MAX переехал на неё в кадре); `Last_leave_disposes_room_and_unsubscribes`;
`Hub_down_keeps_baseline_and_says_degraded`.

### C.3 Гейт и режим потока

- `LiveFrameGate`: свой потолок **50** и **не больше 3 с адреса**. Адрес — `CF-Connecting-IP`,
  иначе первый из `X-Forwarded-For`, иначе `RemoteIpAddress`; одна функция, один тест на
  порядок. Существующий `LiveStreamGate` (100) — для Latest, не трогать.
- `LivePageController.Live(baseFamily, mode, ct)`: `mode` ∈ {`latest`, `live`}, по умолчанию
  `latest`. `latest` → **существующий цикл без изменений**. `live` → новая приватная ветка:
  гейт `LiveFrameGate`, `Join` комнаты, цикл `await foreach (var frame in reader.ReadAllAsync(ct))`
  → `event: slots`, `id: seq`, `data: <json>`; `: ping` раз в 25 с; `event: signal`
  (`up`/`degraded`) при смене состояния `HubStream`; `finally { Leave; gate.Exit }`.
- В `live` **`panel` для региона `table` не шлётся** — таблицу несут слоты; `panel` для `now`
  (книги) остаётся до слайса D-2. Иначе `morph()` перезапишет живую клетку значением из базы
  и через 200 мс она вернётся — мерцание.

**Приёмка C (на тесте, через `?probe=`, см. «Дисциплина»).**
`curl -N '…/studio/PairsV2/Live?baseFamily=BTC&mode=live'` — первый `slots` ≤ 1 с; далее
кадры не чаще 200 мс; во втором окне тот же адрес — кадры совпадают по `seq`; четвёртое
соединение с одного IP → `notice: full`. `docker stop cryptosmithx-marketdata-hub` → в
потоке `signal: degraded` ≤ 5 с, слоты перестают приходить, `panel: now` продолжает;
`docker start` → `signal: up` и полный кадр. Лог студии: одна строка расчётов в минуту, число
расчётов при 5 вкладках BTC равно числу при 1.

---

## Слайс D — кадр, клиент, два возраста, переключатель

**Где именно.** `Views/PairsV2/_V2Table.cshtml`, `Views/PairsV2/Asset.cshtml`,
`wwwroot/studio-live.js`, `wwwroot/studio-v2.css`, `Live/LiveFrame.cs`,
`tests/…Studio.Tests/DesignSystemTests.cs` (InlineData версий).

### D.1 Слот

```json
{"i":123,"g":"bid","p":0,"t":"77,342.000000","s":null,"m":"max","tone":"good",
 "a":"2 s","w":"14 s","src":"ws"}
```
`i` — instrument id, `g` — `V2Field` в нижнем регистре (как `data-group` на клетке), `p` — 0/1
для парных полей, `t`/`s` — текст и подпись из `V2Cell`, `m`/`tone` — марка или `null`, `a` —
живой возраст, `w` — записанный, `src` — `ws` или `latest`. Все строки готовы: клиент
**ничего не форматирует**.

### D.2 Разметка

- `.v2-row` получает `data-instrument="@r.Row.InstrumentId"`. Клетка уже несёт `data-group`.
- `.v2-cellage` становится **двумя** span'ами, оба всегда в DOM в обоих режимах:
  `<span data-age-live>` и `<span data-age-written>`. В Latest живой печатает `—`. Место под
  обе строки **зарезервировано** — переключение режима не двигает строки; здесь уже ловили
  CLS 0.12 на тикающих возрастах. Проверка: высота `.v2-row` в обоих режимах равна (тест
  через `getBoundingClientRect` на живой странице — в приёмку).
- Источник на клетке: `data-src="ws|latest"`; площадка без сокета в живом режиме остаётся
  `latest` и **так и подписана**.

### D.3 `studio-live.js`

- Добавить `source.addEventListener('slots', …)`: найти
  `[data-instrument="i"] .v2-cell[data-group="g"]`, внутри — `.v2-part:nth-of-type(p+1) > span`
  для `t`, `.v2-figsub` для `s`, `.v2-part i` для марки (создать/удалить `<i>`, выставить
  классы `v2-part--best|worst` и `v2-part--good|bad` — те же имена, что в Razor), два span'а
  возраста, `data-src`. Только `textContent` и атрибуты. **`morph()` для слотов не зовётся и
  не переписывается.**
- `panel` остаётся как есть. Правило «не трогать регион под курсором» на слоты не
  распространяется: замена текста в клетке не двигает разметку.
- `mode` — в `data-live-url` (`?mode=live`), переключатель перезапускает поток через
  существующий `open()`/`close()` — один путь старта потока, как и сейчас.

### D.4 Переключатель — дисциплина UI, а не дизайн

**Ничего не изобретать.** Два состояния рядом с существующим тумблером LIVE
(`.a-liverow`, `#a-live`): кнопки в стиле `.v2-cols-btn` с `aria-pressed` — это уже
существующий стиль нажатой кнопки на этой странице. Надписи: `live` и `latest`, без пояснений
в интерфейсе; пояснение — в `title`. History переключателем не является: это адрес с
диапазоном, как сейчас.

- В `studio-v2.css` — **ноль новых токенов**: только `var(--…)` из `ds/tokens/*`.
  `PaletteDisciplineTests` это ловит; не обходить.
- Никакой анимации, никаких новых цветов, никаких рамок. Если кажется, что «так лучше» —
  не делать; записать в отчёт как предложение.

### D.5 Версии статики

Бампнуть `studio-v2.css?v=`, `studio-live.js?v=`, и если тронут — `studio-ages.js?v=`, в
`Views/PairsV2/Asset.cshtml` **и** в `Views/Shared/_Layout.cshtml` где есть; в
`DesignSystemTests.A_script_tag_carries_the_version_its_file_has_earned` — InlineData на те же
номера. Тест падает — значит, номер забыт. **Номер, который хоть раз запросили до деплоя, —
сожжён**; см. «Дисциплина».

---

## Слайс E — деградация и замеры

Всё ниже — **ворота приёмки**, не пожелания. Цифры — в отчёт.

- **h2 до Kestrel.** `curl -sI --http2 https://cryptosmithx-test.blynai.eu/studio/v2/BTC | head -1`
  → `HTTP/2`. Если `HTTP/1.1` — шесть соединений на origin, седьмая вкладка встанет; это
  блокер, не замечание.
- **Буферизация на 200 мс.** `curl -N --no-buffer` на живой поток с `ts` (или `while read`
  с `date +%s%N`): разница между `at` в кадре и временем прихода ≤ 500 мс на протяжении
  минуты. Больше — Cloudflare буферизует; проверить `X-Accel-Buffering` и `Cache-Control`
  на пути.
- **Потолок.** 51-е соединение → `notice: full` сразу; 4-е с одного IP → то же.
- **Деградация.** Сценарий из приёмки C, с временем в секундах.
- **Стоимость.** CPU контейнера студии (`docker stats`) при 1, 5, 20 вкладках BTC — три числа.
  Ожидание: рост не линейный по вкладкам (комната считает один раз).
- **Разметка.** Высота `.v2-row` в `latest` и `live` — равна; ни одна `.v2-part` не шире своей
  клетки (скрипт замера уже есть в истории этого репозитория — воспроизвести).

---

## Что сохранить дословно

- Цикл Latest в `LivePageController.Live`: гейт, `pending`, ожидание `Notifier.Listening`,
  `Signal()`, debounce 400 мс, `PushAsync`, `MinPushInterval`. Новая ветка — рядом, не внутри.
- `studio-live.js`: `morph()`, `morphChildren()`, `held`, `SAY`, `MAX_ATTEMPTS`, логика
  `visibilitychange`. Добавляется один обработчик события и один параметр в URL.
- `EventBuffer.Drain()` и `TradeCollector` — байт в байт.
- `IExchangeMarketData.GetTickersAsync` и его WS→REST откат — не трогать; живой путь идёт
  мимо него.
- `V2Cells`, `Format`, `V2Ranks`, `Verdicts` — только вызываются. Ни одной формулы
  форматирования, ранга или возраста в JS.

## Чего не делать

- Не открывать сокеты к биржам из студии. Их держит хаб, и только он.
- Не звать `GetTickersAsync` из живого пути. REST в живом пути = провал слайса A.
- Не делать второй `Drain()`.
- Не слать `panel: table` в режиме `live`.
- Не копить очередь кадров зрителю. Канал ёмкостью 1 с `DropOldest` — и всё.
- Не заводить SignalR, брокер, WebSocket. Не добавлять маршрут хаба в Traefik.
- Не бампать такт: 200 мс — бюджет, не настройка. В конфиг не выносить.
- Не «улучшать» разметку и цвета. Ноль новых токенов. Ноль новых рамок.
- Не проверять деплой реальным `?v=`.

## Дисциплина проекта

- Сборка: `~/.dotnet/dotnet build`; `TreatWarningsAsErrors` — предупреждение = красная сборка.
- Тесты: **только по проекту** — `~/.dotnet/dotnet test tests/<Project>/<Project>.csproj`;
  запуск на решении виснет.
- `?v=N` — ключ кэша Cloudflare, тратится **первым запросом**, а не деплоем. Деплой
  проверять одноразовой строкой: `curl 'https://…/studio/studio-live.js?probe=$RANDOM'`.
  Сожжённый номер — перепрыгнуть на следующий, не ждать TTL.
- `[hidden]` проигрывает любому авторскому `display`. Прятать — через `hidden` и не задавать
  `display` тому же селектору; `HiddenAttributeTests` это ловит.
- `morph()` сопоставляет по индексу: никакого `appendChild` для перестановки строк — только
  CSS `order`.
- Dapper: позиционные записи только с ctor точных типов; массивы из Npgsql приходят как
  `System.Array` — через `reader.GetFieldValueAsync<double[]>`; `IEnumerable`-параметр
  разворачивается в `@p1,@p2,…` и ломает `unnest`.
- CSS страницы объявляет **ноль** токенов — только `ds/tokens/*`.
- Свежесть — по вызову, не по строке; окно — из `SegmentFreshness`, не константа.
- Деплой: push в `main` → CI → тест-хост сходится сам; прод — только `workflow_dispatch`.
  ssh-алиас `csx-prod` ведёт на **тест**.
- Комментарии в коде — как в соседних файлах: почему, а не что; с числами замера, где они есть.

## Отчёт

По каждому слайсу: что сделано, что **измерено** (команда и цифра), какие тесты добавлены
(имена), что не удалось и почему. Отдельной строкой — всё, что хотелось «улучшить» и не было
сделано. Без «должно работать»: если не измерено — не сделано.
