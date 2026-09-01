# Задание: UI market data — активы, инструменты, истории, управление

Репо: `blyn-ai/CryptoSmith-X`, `main`. Прочти: `src/CryptoSmithX.WebApp/wwwroot/app.css`
(шапка файла — там два закона дизайна), `Areas/Admin/Views/Exchanges/*` и
`Home/Index.cshtml` (образцы всех паттернов), `Data/Spark.cs`, `DashboardStore.cs`,
миграции 0006–0007, `Hub/Ingestion/` (петли — их коснётся флаг collect).

Правила: .NET 10, raw SQL + Dapper, без EF. Никаких JS-библиотек, CDN и чартов —
графики это серверный SVG (Spark.cs + панель throughput в Exchanges/Details как
образец). Дизайн НЕ изобретать: только существующие классы app.css (panel, tr, th,
kpi, dot, tag, sev, chips, section-h, ex-card, coll). Пустое состояние — панель
empty, не выдуманное число. Возрасты — Format.Age, «—» для не-измерено. Русских
строк в UI нет — весь интерфейс на английском, как сейчас.

## Миграция 0008_instrument_admin.sql (в стиле 0001, шапка: вошло/не вошло)

exchange_instrument дополняется НАШИМ решением (не путать с наблюдаемым status):
```sql
alter table exchange_instrument
    add column collect            boolean not null default true,
    add column collect_note       text,
    add column collect_changed_at timestamptz,
    add column collect_changed_by text;
```
Hub: snapshot/candles/depth/funding пропускают инструменты с collect=false
(discovery ПРОДОЛЖАЕТ их видеть и обновлять — наблюдение не выключается решением).
Тест на фильтр. В UI тумблер пишет все четыре поля.

## Навигация

Группа Operations: Dashboard, Exchanges, Assets, Instruments.
Старый пункт и страницу Market data УДАЛИТЬ (коллекторы дублируются дашбордом
и Exchanges/Details), контроллер оставить как redirect на /Admin/Instruments.

## Экраны (все — существующими паттернами, ничего нового в css без нужды)

### Assets — /Admin/Assets
Таблица канонов из asset: code, name, листингов (по биржам: "kraken 1 · fake 1"),
суммарный OI по листингам, худший возраст снапшота среди листингов (dot + age).
Поиск по code/name (GET-форма, серверный фильтр). Клик → Details.

### Asset details — /Admin/Assets/Details/{code}
1. Шапка: code, name (редактируемое поле + note), аудит «изменено когда/кем».
2. Панель «Listings» — ГЛАВНОЕ на странице: все листинги этого актива по биржам
   бок о бок: exchange, symbol, status-tag, price, funding, OI, spread bps,
   depth25, возраст. Сравнение бирж — смысл экрана. Клик строки → Instrument.
3. Панель «Aliases» из asset_alias: alias, exchange (— = global), multiplier +
   формы добавить/удалить. Рядом заметка как чинить авторегистрацию: «создай
   алиас кривого кода на канон — discovery перепривяжет инструменты следующим
   проходом, опустевший asset можно удалить» (кнопка удаления asset активна
   только когда листингов 0).

### Instruments — /Admin/Instruments
Все инструменты всех бирж: symbol, exchange, канон (ссылка на asset), status-tag,
collect-индикатор (off = приглушённая строка + tag), price, OI, funding,
возраст снапшота. Фильтры: биржа (select), status, «only trading», поиск по
символу. Сортировка кликом по OI/funding/age (query-параметр). Строк много (300+):
серверная пагинация по 50, счётчик «N of M». Клик → Details.

### Instrument details — /Admin/Instruments/Details/{id} — панели сверху вниз
1. Шапка: symbol, биржа (ссылка), канон (ссылка), status-tag биржи, listed_at,
   first/last seen.
2. Управление: тумблер collect (sw-переключатель как в consent, но ЖИВОЙ) +
   поле note; выключен → баннер «collection disabled by X at Y — reason».
   Это НАШЕ решение; рядом наблюдаемый статус биржи, их не смешивать.
3. Текущий снапшот: kpi-сетка + таблица полей (bid/ask + spread bps, mark/index,
   funding, OI, turnover, depth 10/25/50). КАЖДОМУ слою свой возраст: снапшота,
   глубины (depth_at!), последней свечи, funding — раздельно, у них разные
   законные каденции.
4. «Price» — свечи серверным SVG: переключатель таймфрейма (1m/5m/15m/1h/4h,
   ссылки-query), последние ~120 баров из market_candle. Не рисовать полноценный
   свечной чарт — линия close + заливка (паттерн throughput) достаточно; high/low
   диапазон — второй тонкой линией если просто, иначе не делать.
5. «Microstructure» — из market_metric_hour, ТРИ отдельных маленьких графика
   стопкой: OI, funding, spread bps (48 ч). Не лепить на один. Пусто → empty
   с честным «первые часовые срезы появятся после часа сбора».
6. «Funding history» — таблица последних ~20 из funding_rate_history.
7. «Data coverage»: минуток за 24 ч N/1440 + дыр M (считать по market_candle
   timeframe=1), диапазон свечей от/до, диапазон funding-истории. Если коллектор
   биржи жив, а инструмент молчит > 3 интервалов — warn-строка про это.

## Definition of done
1. 0008 применяется на существующую базу; hub уважает collect (тест).
2. Прогон на живых данных: локальный стек + kraken-futures enabled → все 4
   экрана отдают 200 с настоящими данными; выключение collect у пары реально
   останавливает её снапшоты (селектом до/после).
3. Поиск, фильтры, сортировка, пагинация работают (проверь curl-ом query-варианты).
4. Старого Market data нет, редирект стоит, навигация обновлена.
5. Все тесты зелёные; один-два коммита в стиле репо; push в main (CI довезёт).
