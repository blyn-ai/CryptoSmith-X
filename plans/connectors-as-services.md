# План: коннекторы как сервисы — архитектура v2 и первый слайс на Kraken Futures

Код пишет владелец. Этот файл — архитектура, границы и спецификация первого слайса, достаточно
подробная, чтобы по ней писать: проекты, контракты, темы шины, ключи Redis, стадии Docker,
сервисы compose, роли и миграции, тесты и приёмка числами.

Сведено из двух разборов (Claude и GPT) и из того, что показал сам репозиторий. Где мнения
расходились — ниже решение и довод, а не список вариантов.

---

## Границы

```
Connector (контейнер на домен лимита; владеет WS/REST, sequence, нормализацией, временем получения)
    │  envelope
    ▼
NATS JetStream
    ├── marketdata-state   → Redis      = что верно СЕЙЧАС
    ├── marketdata-writer  → PostgreSQL = что мы НАБЛЮДАЛИ, батчами, с пульсом живости
    └── Studio live (HubStream → LiveRoom → SSE) — уже построено, в слайсе 2 меняется только источник
Maintenance (partitions, retention, rollups) — отдельно от ingestion, слайс 3
```

**Коннектор владеет биржей. Шина — транспортом. Redis — «сейчас». Postgres — историей.
API — запросами. Studio live — браузером.** Redis никогда не промежуточная база, из которой
потом восстанавливают историю.

## Решения

**Шина — NATS JetStream.** Выбирают возможности, а не пропускная способность: весь наш
ingestion сегодня — полядра и 463 МБ на пятнадцать сегментов, и до потолка упрётся любой
брокер. Решают три вещи, которых у Redis Streams нет в готовом виде:
- **last-value stream** (`max_msgs_per_subject: 1`) — после рестарта Redis состояние
  восстанавливается из шины за секунды, не спрашивая биржи;
- **request/reply** — команды коннектору (`md.cmd.*`) без самодельного протокола;
- **подписки по шаблону** — `md.state.*.PF_XBTUSD.>` для страницы актива.

Бенчмарк на тест-хосте в шаге 3 не выбирает, а **размеряет**: объём потоков, задержки, память.

**Граница контейнера — домен лимита = хост `base_url`.** Не коннектор и не сегмент.
`okx-perp` и `okx-spot` делят измеренный общий бюджет, `bybit-perp` и `bybit-spot` — тоже;
разнесённые по контейнерам, они будут каждый считать лимит своим целиком. Лимитер остаётся в
процессе (`VenueGates`, ключ на хост с 0054) — и верен по построению, потому что весь домен в
одном контейнере. Когда появится исходящий прокси, ключ станет «хост + маршрут».

**Один образ `connector`, площадка выбирается переменной окружения.** Одна запись в матрице CI,
а не пятнадцать. Цена названа честно: любое изменение кода коннекторов пересоздаёт все
контейнеры-коннекторы на деплое. Изоляция от падения, утечки памяти и бана — главное, ради
чего это делается, — сохраняется целиком; изоляция деплоя — нет. Пересмотреть, если рестарты на
деплое станут измеренной проблемой: история лежит в шине, писатель догоняет.

**Коннектор не пишет в Postgres.** Никогда. Конфигурацию читает ролью `connector_reader` —
только `select`. Это граница: всё, что сохраняется, идёт через шину и писателя.

**Браузер не подписывается на шину.** Studio публичная и анонимная; форматирование и ранги
живут в C# (`Format`, `V2Cells`, `Verdicts`), формул в JS нет по решению живого режима.
Транспорт в браузер остаётся SSE — выбран в этой сессии с доводами, SignalR отвергнут.

**Состояние и события — разные потоки с разной ценой потери.**

| что | потеря | поток |
|---|---|---|
| тикер, топ книги, полосы глубины, OI, фандинг | допустима — следующее значение перекроет | last-value |
| сделки, ликвидации, кадры книги, факты пробелов | **нет** — каждое событие важно | durable, с повтором |

---

## Слайс 1 — Kraken Futures в тени, рядом со старым хабом

**Что делает слайс:** коннектор `kraken-futures` публикует в шину всё, что Kraken отдаёт по
**сокету** (снимок, глубина, сделки, кадры книги); писатель пишет это в схему `shadow`;
состояние — в Redis. **Старый хаб не трогается и продолжает писать в `public`.** Сутки два
пути идут параллельно, и сравнение делается числами.

**Чего слайс не делает:** не переключает Studio, не выключает Kraken в хабе, не трогает
REST-наборы (discovery, свечи, фандинг, OI-история, ликвидации, spec) и прод. Двойная нагрузка
на REST Kraken в тени не нужна, а история по REST дважды в одну схему не пишется.

### Порядок коммитов

1. **Вынести писателей из коллекторов хаба** — без изменения поведения.
2. **Контракты** — envelope, темы, JSON.
3. **Инфраструктура** на тесте — NATS, Redis, роли, схема `shadow`; бенчмарк размеряет потоки.
4. **Коннектор** + стадия Docker + матрица CI.
5. **Писатель** → `shadow`.
6. **Состояние** → Redis.
7. **Сутки тени**, скрипт сравнения, отчёт.

Каждый — отдельный коммит в `main`, push деплоит на тест. Номера миграций брать свободные на
момент коммита (сегодня последний — `0064`); мигратор отказывается стартовать на дубле.

---

### Шаг 1. `CryptoSmithX.MarketData.Persistence`

Запись в базу сейчас вшита в пятнадцать файлов `Hub/Ingestion/*Collector.cs` вместе с
получением данных. Писатель, повторивший этот SQL, — вторая формула одной записи, и они
разойдутся. Для слайса выносятся четыре:

| из | в | что |
|---|---|---|
| `SnapshotCollector` | `SnapshotWriter` | upsert `market_snapshot_latest` + keep в `market_snapshot` по `history_interval_s` |
| `DepthCollector` | `DepthWriter` | полосы глубины, `depth_ref`, `book_reach_*` |
| `TradeCollector` | `TradeWriter` | `trade`, идемпотентно по PK |
| `BookCollector` | `BookFrameWriter` | `book_topn` |

- [ ] Писатели принимают **имя схемы** (`public` | `shadow`) и уже нормализованные записи, а не
      адаптер. SQL переносится дословно, включая `unnest`-батчи и `NpgsqlCommand` вместо Dapper
      (Dapper разворачивает `IEnumerable` в `@p1,@p2,…` и ломает `unnest`).
- [ ] Хаб использует их же. **Поведение хаба не меняется ни в одной строке.**
- [ ] Приёмка шага: на тесте счётчики строк по Kraken за час до и после деплоя совпадают с
      точностью до рыночного шума; тесты хаба зелёные.

### Шаг 2. `CryptoSmithX.MarketData.Contracts`

```csharp
public sealed record MarketEnvelope<T>(
    int SchemaVersion,            // 1
    string Segment,               // "kraken-futures"
    string Symbol,                // биржевой символ; id инструмента коннектор НЕ знает
    string Kind,                  // "ticker" | "depth" | "trade" | "book" | "gap" | "health"
    DateTimeOffset ReceivedAt,    // НАШЕ время получения кадра/ответа — ставит коннектор
    DateTimeOffset? SourceTime,   // время биржи из кадра, если есть
    long? Sequence,               // номер кадра биржи, если есть
    string Transport,             // "ws" | "rest"
    string ConnectorInstance,     // hostname контейнера + время старта процесса
    string? RequestId,            // для ответов на команды
    T Data);
```

- [ ] **`ReceivedAt` ставит коннектор в момент получения**, до любой обработки. Для REST — на
      ответ, не на строку. Никто ниже по шине его не перезаписывает.
- [ ] **Исключение Kraken сохраняется, а не «чинится».** `KrakenWsFeed.cs:212` прямо говорит:
      Kraken — единственная площадка, чей `received_at` всегда был временем биржи. Колонка
      `received_at` в `shadow` для Kraken пишется из `SourceTime` — ровно как в `public`.
      Иначе сравнение в шаге 7 разойдётся не из-за архитектуры, а из-за смены смысла колонки.
      Наше время получения живёт в envelope и в Redis; довести его до колонок — отдельное
      решение после флипа.
- [ ] JSON, `System.Text.Json`, опции в одном месте. Смена формата — только через `SchemaVersion`.

**Темы:**

| тема | поток | хранение |
|---|---|---|
| `md.state.{segment}.{symbol}.{kind}` | `MD_STATE` | `max_msgs_per_subject: 1`, file |
| `md.event.{segment}.{symbol}.{kind}` | `MD_EVENTS` | limits, `max_age: 24h`, `max_bytes` — из замера шага 3 |
| `md.health.{segment}.{instance}` | `MD_HEALTH` | `max_msgs_per_subject: 1` |
| `md.cmd.{segment}.{command}` | core NATS, request/reply | — (в слайсе 1 одна: `ping`) |

`max_age: 24h` у событий — это RPO: писатель, лежащий меньше суток, не теряет ничего.

### Шаг 3. Инфраструктура — только тест (`deploy/docker-compose.prod.yml`)

**Внимание к именам:** `docker-compose.prod.yml` катится на **тест** (VPS, на push),
`docker-compose.vm.yml` — на **прод** (VM клиента, `workflow_dispatch`). Слайс трогает только
первый.

`deploy/nats/nats.conf`:

```
port: 4222
http: 8222
jetstream {
  store_dir: /data
  max_file_store: 4G       # из замера шага 3, не из головы
}
authorization {
  users = [
    { user: $NATS_CONNECTOR_USER, password: $NATS_CONNECTOR_PASSWORD,
      permissions: { publish: ["md.state.>", "md.event.>", "md.health.>", "$JS.API.>"],
                     subscribe: ["md.cmd.>", "_INBOX.>"] } }
    { user: $NATS_CONSUMER_USER,  password: $NATS_CONSUMER_PASSWORD,
      permissions: { publish: ["md.cmd.>", "$JS.API.>", "$JS.ACK.>"],
                     subscribe: ["md.>", "_INBOX.>", "$JS.>"] } }
  ]
}
```

Права разведены: коннектор публикует данные и слушает команды; потребители — наоборот.
Коннектор, который попытается подписаться на чужие данные, получит отказ брокера.

Сервисы:

```yaml
  nats:
    image: nats:2.10-alpine
    command: ["-c", "/etc/nats/nats.conf"]
    volumes: ["./nats/nats.conf:/etc/nats/nats.conf:ro", "nats-data:/data"]
    environment:
      NATS_CONNECTOR_USER: ${NATS_CONNECTOR_USER:?}
      NATS_CONNECTOR_PASSWORD: ${NATS_CONNECTOR_PASSWORD:?}
      NATS_CONSUMER_USER: ${NATS_CONSUMER_USER:?}
      NATS_CONSUMER_PASSWORD: ${NATS_CONSUMER_PASSWORD:?}
    healthcheck: { test: ["CMD", "wget", "-qO-", "http://127.0.0.1:8222/healthz?js-enabled-only=true"],
                   interval: 10s, retries: 5 }
    mem_limit: 512m
    restart: unless-stopped

  redis:
    image: redis:7-alpine
    command: ["redis-server", "--appendonly", "yes", "--requirepass", "${REDIS_PASSWORD:?}",
              "--maxmemory", "256mb", "--maxmemory-policy", "noeviction"]
    volumes: ["redis-data:/data"]
    healthcheck: { test: ["CMD-SHELL", "redis-cli -a \"$$REDIS_PASSWORD\" ping | grep PONG"],
                   interval: 10s, retries: 5 }
    environment: { REDIS_PASSWORD: "${REDIS_PASSWORD:?}" }
    mem_limit: 384m
    restart: unless-stopped

volumes:
  nats-data:
  redis-data:
```

- **Ни одного `ports:`** — оба внутри сети compose, как `/live` хаба сегодня.
- **`noeviction`, а не LRU:** состояние ограничено (инструменты × виды), и вытеснение молча
  удалило бы кусок «сейчас». Упереться в память должно громко.
- **Не бэкапятся.** Долговечная запись — Postgres. Redis восстанавливается из `MD_STATE`,
  NATS — теряет не больше, чем отстал писатель.

Роли — по образцу 0025: суперпользователь создаёт роль один раз, миграция выдаёт права и
**падает с инструкцией**, если роли нет:

- [ ] `connector_reader` — `select` на `schema_version`, `exchange`, `segment`,
      `segment_dataset`, `dataset`, `dataset_setting`, `setting`, `exchange_instrument`.
- [ ] `marketdata_writer` — `usage` на схему `shadow`, `select/insert/update/delete` на её
      таблицы; `select` на `exchange_instrument` (сопоставление символа с id). **В `public`
      слайс 1 не пишет ничего.**

Схема `shadow` — миграция:

- [ ] `shadow.market_snapshot_latest`, `shadow.market_snapshot`, `shadow.book_topn`,
      `shadow.trade`, `shadow.collection_gap` — `like public.<t> including defaults including
      constraints including indexes`, **без партиций** и без FK. Писатель в режиме `shadow`
      удаляет строки старше трёх суток раз в час.
- [ ] Схема удаляется миграцией слайса 2 после флипа.

`.env` тест-хоста получает: `NATS_CONNECTOR_USER/PASSWORD`, `NATS_CONSUMER_USER/PASSWORD`,
`REDIS_PASSWORD`, `CONNECTOR_READER_PASSWORD`, `MARKETDATA_WRITER_PASSWORD`. Не в git.

**Бенчмарк** `tools/CryptoSmithX.BusBench` (консоль, не деплоится): публикует в `MD_EVENTS`
синтетические envelope размером реальных кадров Kraken (размер снять с живого сокета) на
темпах 1k / 5k / 20k сообщений в секунду, минуту на каждом; пишет p50/p99 публикации, отставание
потребителя, RSS NATS. Заодно снять **реальный** поток сделок Kraken за час. Результат —
`max_bytes` и `max_file_store` в конфиг, числа — в комментарий рядом.

### Шаг 4. `CryptoSmithX.MarketData.Connector`

Worker + Kestrel только для `/healthz`. Переиспользует `CryptoSmithX.MarketData.Connectors`
как есть: `KrakenFuturesClient`, `KrakenWsFeed`, `KrakenBookBuilder`, `KrakenFuturesMarketData`.

- [ ] `CONNECTOR_SEGMENTS=kraken-futures` — список сегментов одного домена лимита.
- [ ] Конфигурация — `DbSettings` ролью `connector_reader`, перечитывается как сейчас.
- [ ] Петли на такте из базы (`IntervalFor`), но вместо записи — публикация:
      - снимок: `GetTickersAsync()` → `md.state.kraken-futures.{sym}.ticker`;
      - глубина: `TryGetDepth` → `md.state.….depth`;
      - кадры книги: `TryGetBookFrame` → `md.event.….book`;
      - сделки: **`ObserveTrades(capacity)`, не `DrainTrades()`** — дренаж остаётся за хабом,
        иначе тень украдёт сделки у живого пути (это ровно правило `EventTap`, слайс B живого
        режима) → `md.event.….trade`.
- [ ] Факты разрыва сокета и дыры в номерах (`seq != book.Seq + 1`) → `md.event.….gap` с
      причиной из существующего CHECK `collection_gap.cause` (`ws_disconnected`,
      `ws_sequence_gap`, `resync`, `rate_limited`). Новых причин без миграции не заводить.
- [ ] Пульс раз в 5 с → `md.health.kraken-futures.{instance}`: состояние сокета, возраст
      последнего кадра по каждому каналу, число переподключений, `429` за окно.
- [ ] Шина недоступна → **ограниченный** буфер в памяти (сбрасывает старейшие и считает
      сброшенное), без роста до падения. Сброшенное не выдаётся за доставленное: при
      восстановлении публикуется `gap` на интервал недоступности.
- [ ] Команда `md.cmd.kraken-futures.ping` → ответ с `ConnectorInstance` и временем.

Два сокета к Kraken с одного IP на время тени (хаб + коннектор) — **проверить** лимит
соединений площадки до включения, не после.

`deploy/Dockerfile` — в стадию `build` добавить `COPY` и `restore` для `Contracts`,
`Persistence`, `Connector`, `State`, `Writer`, в `publish` — три новых выхода, и стадии:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS connector
WORKDIR /app
COPY --from=build /app/connector ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.Connector.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS marketdata-state
WORKDIR /app
COPY --from=build /app/state ./
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.State.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS marketdata-writer
WORKDIR /app
COPY --from=build /app/writer ./
ENTRYPOINT ["dotnet", "CryptoSmithX.MarketData.Writer.dll"]
```

`.github/workflows/deploy.yml`: `target: [migrator, hub, api, webapp-admin, webapp-studio,
webapp-agent, connector, marketdata-state, marketdata-writer]`.

Сервис compose (тест):

```yaml
  connector-kraken-futures:
    image: ghcr.io/blyn-ai/cryptosmithx-connector:latest
    depends_on: { nats: { condition: service_healthy },
                  database-migrator: { condition: service_completed_successfully } }
    environment:
      CONNECTOR_SEGMENTS: kraken-futures
      ConnectionStrings__Database: Host=postgres;Port=5432;Database=marketdata;Username=connector_reader;Password=${CONNECTOR_READER_PASSWORD:?}
      Nats__Url: nats://nats:4222
      Nats__User: ${NATS_CONNECTOR_USER:?}
      Nats__Password: ${NATS_CONNECTOR_PASSWORD:?}
      Sentry__Dsn: ${SENTRY_DSN:-}
    healthcheck: { test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:8080/healthz || exit 1"],
                   interval: 15s, retries: 3 }
    mem_limit: 256m
    restart: unless-stopped
```

### Шаг 5. `CryptoSmithX.MarketData.Writer`

- [ ] Durable-потребители JetStream: `writer-events` на `MD_EVENTS`, `writer-state` на
      `MD_STATE`, `writer-health` на `MD_HEALTH`. Ack только **после** коммита транзакции.
- [ ] Батч: 5 000 записей **или** 500 мс — что раньше; одна транзакция на батч, писатели из
      шага 1 с `schema: "shadow"`.
- [ ] Символ → `exchange_instrument_id` по кэшу, перечитываемому раз в 60 с. Символ, которого
      нет в каталоге, — не молча в мусор: счётчик и лог, как у снимка сейчас.
- [ ] **Правило живости при коалесценции истории.** Строка в `market_snapshot` за корзину
      `history_interval_s` пишется, если в этой корзине было **наблюдение** (`ReceivedAt` в её
      границах), — даже если значения не изменились. Отсутствие строки обязано значить «не
      видели», а не «не менялось». Одинаковые соседние снимки — это улика, по которой
      ревьюер однажды заподозрил лаг у Hyperliquid, а замер показал, что менялись размеры;
      дельта без пульса эту улику стирает.
- [ ] Пульс коннектора молчит дольше 3× интервала → `shadow.collection_gap`, `cause =
      'collector_down'`, `gap_end = null`; вернулся — закрыть. Живой контейнер, не
      достучавшийся до шины, должен выглядеть как пробел, а не как тихий рынок.
- [ ] `Kind = gap` из коннектора → `shadow.collection_gap` с его причиной.

### Шаг 6. `CryptoSmithX.MarketData.State`

- [ ] Потребитель `state` на `MD_STATE` и `MD_HEALTH`. На старте — повтор `MD_STATE` целиком
      (`deliver_policy: last_per_subject`): пустой Redis заполняется из шины, без запросов к
      бирже.
- [ ] `HSET md:{segment}:{symbol}` — поля тикера и глубины плюс `received_at`, `source_time`,
      `seq`, `transport`, `instance`.
- [ ] **Старое не перезаписывает новое.** Обновление принимается, только если его `ReceivedAt`
      не старше уже лежащего. Порядок доставки в шине не гарантирует порядок наблюдений между
      переподключениями.
- [ ] `SET md:health:{segment} … EX 15` — ключ исчезает сам, если пульс молчит. Отсутствие
      ключа = «источник молчит»; Studio в слайсе 2 скажет это словами.
- [ ] `SADD md:idx:family:{family} {segment}:{symbol}` — индекс для страницы актива; семейство
      из `asset_family_member` ролью `connector_reader`.

### Шаг 7. Сутки тени и сравнение

`deploy/ops/compare-kraken-shadow.sql` за окно, **целиком лежащее после включения тени**:

- **сделки** — точная разность множеств по `(exchange_instrument_id, event_time, venue_uid)`:
  `missing_in_shadow`, `extra_in_shadow`;
- **снимок** — по инструменту p50/p95 возраста `received_at` на момент запроса, `public` против
  `shadow`; число NULL по каждой колонке;
- **история** — строк на инструмент на корзину;
- **кадры книги** — число на инструмент в час;
- **пробелы** — все строки `shadow.collection_gap` с причинами.

---

## Приёмка слайса 1 — числами

| что | порог |
|---|---|
| сделки | `missing_in_shadow = 0` и `extra_in_shadow = 0` за сутки |
| снимок latest | p95 возраста `shadow` ≤ p95 `public` + 1 с |
| NULL | ни одной колонки, заполненной в `public` и пустой в `shadow` |
| кадры книги | число в `shadow` ≥ `public` |
| история | строк на корзину в `shadow` = `public` |
| отставание писателя | p99 < 2 с (`stream last seq − consumer ack seq`, пересчитать во время) |
| рестарты | ноль, или на каждый — строка пробела с причиной |
| память | connector ≤ 256 МБ, state ≤ 128 МБ, writer ≤ 256 МБ, NATS и Redis — снять числом |
| хаб | его строки и возрасты по Kraken **не изменились** — тень не отняла у живого пути ничего |

Отчёт — таблица с этими числами и командами, которыми они сняты. Без «должно работать».

---

## Тесты

- **Contracts:** `ReceivedAt` у envelope не позже момента получения в тестовом часах; тема
  собирается и разбирается обратно одинаково; неизвестная `SchemaVersion` отвергается.
- **Persistence:** писатель в `shadow` и в `public` пишет одинаковые строки из одинакового входа;
  `TradeWriter` идемпотентен по PK.
- **Connector:** ни одного `Npgsql`-вызова записи (роль и так не даст — тест защищает от
  попытки); `seq != prev + 1` → опубликован `gap` с `ws_sequence_gap`; буфер при недоступной
  шине не растёт выше потолка и считает сброшенное; сделки берутся через `ObserveTrades`, а
  `DrainTrades` коннектор не зовёт вовсе.
- **Writer:** корзина с наблюдением без изменения значений даёт строку истории, корзина без
  наблюдения — не даёт; ack не уходит до коммита; молчание пульса > 3× открывает пробел.
- **State:** более старое `ReceivedAt` не перезаписывает более новое; пустой Redis после
  повтора `MD_STATE` равен состоянию до рестарта.

---

## Слайс 2 — флип Kraken (кратко)

- Studio: `HubStream` получает источник-интерфейс; для сегментов коннектора — NATS/Redis.
  `LiveRoom`, слоты, SSE — без изменений.
- Писатель переключается на `public`, хаб исключает `kraken-futures` из своих сегментов.
- Схема `shadow` и временная роль удаляются миграцией.
- `md.health` → Studio говорит «источник молчит» словами, отличая от тихого рынка.

## Слайс 3 — остальное (кратко)

- REST-наборы Kraken в коннектор через команды `md.cmd.*`; координатор (знает водяные знаки из
  базы) выдаёт `backfill`, коннектор исполняет под своим гейтом.
- Остальные домены лимита по одному, тем же циклом «тень → сравнение → флип».
- **Maintenance** отдельным сервисом: партиции, retention, роллапы. И учётная строка сервисных
  джобов перестаёт висеть на сегменте `fake` — сегодня его удаление на проде молча остановило
  роллап на сутки.

---

## Чего не делать

- **Не писать в Postgres из коннектора.** Ни через какую роль.
- **Не дренировать сделки из тени.** `ObserveTrades`, не `DrainTrades`.
- **Не «исправлять» `received_at` Kraken в тени.** Сначала сравнение, потом отдельное решение.
- **Не открывать NATS и Redis наружу** — ни `ports:`, ни маршрута в Traefik.
- **Не включать LRU-вытеснение в Redis.**
- **Не трогать `docker-compose.vm.yml`** — это прод.
- **Не заводить новые причины пробелов** без миграции и довода.
- **Не дублировать SQL записи** — только через `Persistence`.
- **Не ставить Prometheus/Grafana ради слайса.** Пульс в шине и в Redis; метрик-стек — отдельное
  решение, когда появится кто-то, кто на него смотрит.

## Дисциплина проекта

- Сборка `~/.dotnet/dotnet build`; `TreatWarningsAsErrors`.
- Тесты **только по проекту**, не по решению — на решении зависает.
- Мигратор отказывается стартовать на дубле номера; номер брать свободный на момент коммита.
- Dapper: позиционные записи только с ctor точных типов; массивы Npgsql — `System.Array`;
  `IEnumerable`-параметр ломает `unnest`.
- `docker-compose.prod.yml` = **тест**; `docker-compose.vm.yml` = **прод**; ssh-алиас
  `csx-prod` ведёт на **тест**.
- Секреты — только `/opt/cryptosmithx/.env`, `${VAR:?}` в compose.
- Комментарии — почему, а не что, с числом замера.
