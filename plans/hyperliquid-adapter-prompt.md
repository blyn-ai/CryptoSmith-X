# Задание: адаптер Hyperliquid

Репо: `blyn-ai/CryptoSmith-X`, `main`. Прочти: `plans/architecture.mermaid`,
`Connectors/Kraken/*` и `Connectors/Weex/*` (два образца: кракен — WS-first,
weex — чистый REST с разведкой), `Connectors/Streaming/*` (общий WS-каркас),
`Hub/Ingestion/ExchangeWorker.cs` (BuildKraken/BuildWeex — гнёзда),
миграции 0006/0007/0010/0011, комментарии схемы 0001 — закон семантики полей.

Правила: .NET 10 (`global.json` пинит 10.0.400; собирай `~/.dotnet/dotnet` —
системный SDK на машине битый), raw SQL + Dapper, TreatWarningsAsErrors,
тесты без сети на реальных сохранённых ответах, hub без ключей, один адаптер
= один PR (fix-и общего Hub-кода — отдельным коммитом, как с per-symbol
изоляцией коллекторов).

## Фаза 0 — разведка (фикстуры прежде кода)

API у Hyperliquid приличный, но НЕобычный: всё через POST на один эндпоинт
`https://api.hyperliquid.xyz/info` с телом `{"type": ...}`. Сними живые ответы:
- `meta` (universe — список перпов, szDecimals),
- `metaAndAssetCtxs` (ВСЕ тикеры одним вызовом: mark, oracle, funding, OI,
  объём — проверь соответствие нашим полям снапшота),
- `l2Book` (стакан по монете),
- `candleSnapshot` (1m, окно времени),
- `fundingHistory` (по монете; funding у HL ЕЖЕЧАСНЫЙ — funding_interval_hours=1).
И кадры WS `wss://api.hyperliquid.xyz/ws`: подписки l2Book/activeAssetCtx.
ВАЖНО проверить в разведке: WS l2Book у HL, по слухам, шлёт ПОЛНЫЕ снапшоты
книги, а не дельты — если так, книжный билдер тривиален (без seq/ресинка),
скажи это в вердикте явно. Фикстуры — в `tests/.../Fixtures/hyperliquid/`.

Известное заранее:
- символы — голые монеты (`BTC`, `kPEPE`); quote у перпов — USD-семейство.
  `kPEPE→PEPE ×1000` уже в сид-алиасах 0006 — проверь резолв; новые k-префиксы
  сидь алиасами в своей миграции;
- `trade_count` у HL ЕСТЬ (комментарий 0001) — заполняй;
- price_step/qty_step выводятся из szDecimals по правилам HL — зафиксируй
  формулу комментарием со ссылкой на их доку;
- min_notional: если HL не задаёт — null (schema это разрешает, как у кракена).

## Фаза 1 — REST-адаптер (обязательная)

`Connectors/Hyperliquid/`: `HyperliquidMarketData`, `HyperliquidClient`
(POST-хелпер), `HyperliquidDtos`. Глубина — через общий `DepthMath`.
Тикеры батчатся (`metaAndAssetCtxs`) — снапшот-петля дешёвая, как у кракена.
Миграция (следующий свободный номер, сейчас 0012): сид `base_url`
`https://api.hyperliquid.xyz` (+ `ws_url`, если фаза 2 состоится).
`ExchangeWorker`: ветка `"hyperliquid" => BuildHyperliquid(...)`.

## Фаза 2 — WS (делай, если разведка не покажет сюрпризов — у HL WS хороший)

На каркасе `Streaming/`: свой фид и билдер. Если l2Book шлёт полные снапшоты —
билдер без seq-машины (это НЕ упрощение задачи, а свойство протокола — но
кросс-чек по top-of-book против REST всё равно обязателен, и замёрзший сокет
не имеет права выглядеть свежим). Перпов у HL ~200 — CPU-бюджет спокойный,
но проверь фактический трафик и скажи цифру в вердикте.

## Definition of done

1. Локальный стек: hyperliquid → enabled в UI → discovery (канон через
   справочник, kPEPE→PEPE), снапшоты с OI/глубиной, свечи с trade_count,
   ежечасная funding-история. Селектами.
2. Если фаза 2: свежесть глубины ≈ интервалу снапшота по всем перпам;
   обрыв WS → зелёные коллекторы на REST. Как в кракен-DoD.
3. Фикстуры реальные; вся сюита зелёная; вердикт по API — в коммит-месседж.
4. Прод не трогать: включает владелец в UI.
5. Один-два коммита в стиле репо, push в main.
