-- ============================================================================
-- CryptoSmith X — миграция 0065: Aster (asterdex) появляется в каталоге как
-- planned.
--
-- Замер: plans/aster-venue-blueprint.md, живые пробы fapi.asterdex.com и
-- fstream.asterdex.com, 2026-09-16, публичные эндпоинты, без ключа. Вывод
-- плана: протокол Aster — байт-в-байт клон Binance USDⓈ-M REST/WS (§0/§1), но
-- семантика — нет: 124 перпетуала на акции/ETF/сырьё/FX под тем же
-- contractType=PERPETUAL, потолок в 200, а не 1024, потоков на соединение,
-- тикер-массив с изменившимися символами вместо всей площадки, и отсутствие
-- истории открытого интереса. Поэтому адаптер — не новый класс и не смена
-- base_url, а параметризованное повторное использование через
-- BinanceUsdmProfile (Connectors/Binance/BinanceUsdmProfile.cs): Binance
-- воспроизводит свои прежние константы байт-в-байт, что закреплено тестом
-- AsterProfileTests.Binance_profile_reproduces_every_constant_it_replaced.
--
-- ---------------------------------------------------------------------------
-- 1. Бюджет запроса — 5 req/s, 'documented', до отдельного замера
-- ---------------------------------------------------------------------------
-- exchangeInfo.rateLimits отдаёт 2400 весов/мин на IP — число вендора,
-- совпадающее с Binance. Веса сняты живьём заголовком x-mbx-used-weight-1m
-- 2026-09-16: bookTicker 2, premiumIndex 10, ticker/24hr 40, fundingInfo 10,
-- openInterest 0–1 (недокументировано), depth 5/10/20 при limit 100/500/1000,
-- klines(100) 2, aggTrades(1000) 20. Перед API стоит CloudFront со своим,
-- невидимым в rateLimits лимитом: HTTP 429 без тела и без Retry-After после
-- ~40 запросов/мин к /fapi/v1/time при снятом весе ~107 из 2400 (§1.3). 5 req/s
-- взято с запасом под этот второй лимитер, а не выведено из арифметики по
-- весам, как в 0023 для Binance, — источник поэтому 'documented', а не
-- 'measured'. §9 шаг 10 предписывает пересчитать через сутки работы на TEST и
-- сменить источник на 'measured'.
--
-- ---------------------------------------------------------------------------
-- 2. Сегмент: два независимых сокета на один и тот же адрес
-- ---------------------------------------------------------------------------
-- ws_url и market_ws_url — ОДИН И ТОТ ЖЕ wss://fstream.asterdex.com/stream, и
-- это не опечатка: §1.4 плана подтверждает живьём, что все четыре пути Aster
-- (/stream, /ws/<name>, /public/stream, /market/stream) отдают ЛЮБОЙ поток —
-- маршрутизации, как у Binance, здесь нет. Два отдельных соединения на один
-- адрес держат стакан и рыночный фид независимыми доменами отказа, как на
-- Binance, и оба остаются далеко от потолка в 200 потоков на утверждённой
-- вселенной (26 и 55, §6/§8 плана).
--
-- ---------------------------------------------------------------------------
-- 3. status = 'planned', как в 0023/0057/0063
-- ---------------------------------------------------------------------------
-- Адаптер после этой миграции существует, но работает ли он — решение
-- оператора, не миграции (0023 §5, тот же аргумент, что и здесь). ops/
-- enable-aster-perp.sql переводит статус в 'enabled' постадийно (§9 плана).
--
-- ---------------------------------------------------------------------------
-- 4. segment_dataset: полный крест в disabled, такт — заранее
-- ---------------------------------------------------------------------------
-- Крест со всеми кодами dataset, как в 0014/0034/0044/0057 — отсутствие
-- строки не должно означать «выключено», иначе «не умеет» и «выключили
-- руками» неразличимы. Такт (interval_s) при этом проставляется здесь же, не
-- дожидаясь ops-скрипта: план §5 явно просит так, и это не включает сбор
-- (mode остаётся 'disabled' до ops/enable-aster-perp.sql) — только фиксирует
-- решённый такт рядом с решением его когда-нибудь включить.
--
-- ---------------------------------------------------------------------------
-- 5. Ликвидации — выборка, а не лента, закреплено здесь (план §3)
-- ---------------------------------------------------------------------------
-- Aster пушит !forceOrder@arr не чаще одного события на символ в секунду (те
-- же 1000 мс, что документирует Binance для своего потока) — сумма по бакету
-- поэтому нижняя граница, а не объём площадки. Note на segment_dataset и
-- строка capability history_depth='sampled_1s_per_symbol' записывают это ДО
-- того, как liquidations вообще может быть включён (§9 шаг 9 плана ждёт
-- именно этих двух строк и подписи в Studio). Про binance-usdm — тот же факт,
-- та же сноска в документации, но НЕ исправлено здесь: это отдельное
-- изменение (план §3, последний абзац), и эта миграция его не трогает.
-- ============================================================================

insert into exchange (code, name, description,
                      request_budget_per_s, max_concurrent_requests,
                      request_budget_source, request_budget_url, request_budget_note)
values ('aster', 'Aster', 'Perp DEX (asterdex); REST/WS протокол — клон Binance USDⓈ-M.',
        5, 4, 'documented',
        'https://github.com/asterdex/api-docs/blob/master/V3(Recommended)/EN/aster-finance-futures-api-v3.md',
        '2400 весов/мин на IP из exchangeInfo.rateLimits; веса сняты заголовком 2026-09-16: bookTicker 2, '
        || 'premiumIndex 10, ticker/24hr 40, fundingInfo 10, openInterest 0–1, depth 5/10/20, klines(100) 2, '
        || 'aggTrades(1000) 20. Перед API стоит CloudFront со своим лимитом: 429 без тела и без Retry-After '
        || 'после ~40 запросов/мин к /fapi/v1/time при весе ~107. 5 req/s — с запасом под него; перемерить '
        || 'через сутки работы и сменить источник на measured.')
on conflict (code) do nothing;

insert into segment (code, name, description, status, adapter, exchange_code, kind, market_model,
                     base_url, ws_url, market_ws_url)
values ('aster-perp', 'Aster perpetuals',
        'Клон REST/WS протокола Binance USDⓈ-M (план plans/aster-venue-blueprint.md, замер 2026-09-16): '
        || 'BinanceUsdmProfile.Aster сужает область до symbolType=0 (исключая 124 RWA-перпетуала под тем же '
        || 'contractType=PERPETUAL), REST-глубину до limit=500, историю открытого интереса — на sampled '
        || '(венью отдаёт 404 HTML на /futures/data/openInterestHist), а оба WS-фида — на собранный набор '
        || 'вместо всей площадки (потолок 200 потоков на соединение против 441 в области видимости).',
        'planned', 'aster-perp', 'aster', 'perp', 'orderbook',
        'https://fapi.asterdex.com',
        'wss://fstream.asterdex.com/stream',
        'wss://fstream.asterdex.com/stream')
on conflict (code) do nothing;

do $$
begin
    if not exists (select 1 from segment
                    where code = 'aster-perp' and base_url is not null and ws_url is not null
                      and market_ws_url is not null) then
        raise exception 'aster-perp остался без base_url, ws_url или market_ws_url: адаптер не построится';
    end if;
end $$;

-- ---------------------------------------------------------------------------
-- Полный крест наборов в disabled.
-- ---------------------------------------------------------------------------
insert into segment_dataset (segment_code, dataset_code, mode, note, updated_by)
select s.code, d.code, 'disabled', 'Выключено до ops/enable-aster-perp.sql', '0065'
  from segment s
 cross join dataset d
 where s.code = 'aster-perp'
on conflict (segment_code, dataset_code) do nothing;

do $$
declare missing int;
begin
    select count(*) into missing
      from dataset d
      left join segment_dataset sd
        on sd.segment_code = 'aster-perp' and sd.dataset_code = d.code
     where sd.segment_code is null;

    if missing > 0 then
        raise exception 'aster-perp: у % набор(ов) из dataset нет строки в segment_dataset', missing;
    end if;
end $$;

-- Такт для наборов, которые §9 плана включает постадийно — mode остаётся
-- disabled, это только число, которое ops-скрипт применит вместе с collect.
update segment_dataset sd
   set interval_s = w.interval_s, updated_at = now(), updated_by = '0065'
  from (values
        ('discovery', 900), ('spec_versions', 3600), ('snapshot', 15), ('depth', 300),
        ('book', 15), ('trades', 5), ('candles', 60), ('candles_mark', 60), ('candles_index', 60),
        ('funding', 3600), ('open_interest', 300), ('liquidations', 900)
       ) as w(dataset_code, interval_s)
 where sd.segment_code = 'aster-perp' and sd.dataset_code = w.dataset_code;

-- ---------------------------------------------------------------------------
-- Полный крест capability_key для aster-perp. Ни одна миграция после 0014 не
-- заводит эти строки для НОВОГО сегмента — только для нового capability_key
-- на все существующие сегменты (0014, 0034, 0044, 0052 — крест по оси
-- dataset, не по оси segment). Каждая площадка, заведённая после 0014
-- (0057-й пятёркой в том числе), унаследовала этот же пробел; здесь он не
-- лечится для них — только для aster-perp, которому иначе не во что писать
-- ни transports_venue, ни history_depth ниже.
-- ---------------------------------------------------------------------------
insert into segment_dataset_capability (segment_code, dataset_code, capability_key)
select sd.segment_code, sd.dataset_code, k.key
  from segment_dataset sd
 cross join capability_key k
 where sd.segment_code = 'aster-perp'
on conflict (segment_code, dataset_code, capability_key) do nothing;

do $$
declare missing int;
begin
    select count(*) into missing
      from segment_dataset sd
      cross join capability_key k
      left join segment_dataset_capability c
        on c.segment_code = sd.segment_code and c.dataset_code = sd.dataset_code and c.capability_key = k.key
     where sd.segment_code = 'aster-perp' and c.segment_code is null;

    if missing > 0 then
        raise exception 'aster-perp: % клетки capability не создались', missing;
    end if;
end $$;

-- ---------------------------------------------------------------------------
-- transports_venue — то, что площадка реально отдаёт (план §5), тем же
-- приёмом, что 0037 для четырёх других сегментов. transports_us остаётся
-- null: его объявит адаптер при сборке (ExchangeWorker.Build), не миграция.
-- ---------------------------------------------------------------------------
update segment_dataset_capability c
   set value = v.transports_venue, source = 'probed', valid_since = now(),
       filled_at = now(), filled_by = '0065 migration (plans/aster-venue-blueprint.md, 2026-09-16)'
  from (values
    ('discovery',      'rest'),
    ('snapshot',       'rest,ws'),
    ('depth',          'rest,ws'),
    ('book',           'rest,ws'),
    ('candles',        'rest,ws'),
    ('candles_mark',   'rest'),
    ('candles_index',  'rest'),
    ('funding',        'rest'),
    ('trades',         'rest,ws'),
    ('open_interest',  'rest'),
    ('liquidations',   'ws')
  ) as v(dataset_code, transports_venue)
 where c.segment_code = 'aster-perp' and c.dataset_code = v.dataset_code
   and c.capability_key = 'transports_venue';

-- ---------------------------------------------------------------------------
-- Ликвидации — выборка, не лента (план §3). Обе строки читаются Studio без
-- изменения схемы: note человеко-читаем сразу, capability — через
-- LiquidationVoice (WebApp.Studio).
-- ---------------------------------------------------------------------------
update segment_dataset
   set note = 'Sample: at most one liquidation per symbol per second is published (venue docs); '
              || 'buckets are lower bounds.',
       updated_at = now(), updated_by = '0065'
 where segment_code = 'aster-perp' and dataset_code = 'liquidations';

insert into capability_log (segment_code, dataset_code, capability_key, old_value, new_value, source, changed_by, note)
select 'aster-perp', 'liquidations', 'history_depth', c.value, 'sampled_1s_per_symbol', 'manual', '0065 migration',
       'Aster docs (V3 futures API, §"forceOrder"): at most one liquidation order per symbol is pushed per '
       || '1000 ms, and nothing is pushed if none happened — the same snapshot rule Binance documents for its '
       || 'own !forceOrder@arr. A bucket summed from these events is a lower bound, never the venue''s '
       || 'liquidated volume. See '
       || 'https://github.com/asterdex/api-docs/blob/master/V3(Recommended)/EN/aster-finance-futures-api-v3.md'
  from segment_dataset_capability c
 where c.segment_code = 'aster-perp' and c.dataset_code = 'liquidations' and c.capability_key = 'history_depth'
   and c.value is distinct from 'sampled_1s_per_symbol';

update segment_dataset_capability
   set value = 'sampled_1s_per_symbol', source = 'manual', valid_since = now(),
       filled_at = now(), filled_by = '0065 migration'
 where segment_code = 'aster-perp' and dataset_code = 'liquidations' and capability_key = 'history_depth';

do $$
begin
    if not exists (select 1 from segment_dataset_capability
                    where segment_code = 'aster-perp' and dataset_code = 'liquidations'
                      and capability_key = 'history_depth' and value = 'sampled_1s_per_symbol') then
        raise exception 'aster-perp: капабилити history_depth на liquidations не записалась';
    end if;
end $$;

-- ---------------------------------------------------------------------------
-- Алиасы: два глобальных алиаса, которых репозиторий ещё не знает (план §5).
-- Остальные 1000-префиксы Aster (1000BONK, 1000CAT, 1000CHEEMS, 1000FLOKI,
-- 1000LUNC, 1000PEPE, 1000RATS, 1000SATS, 1000SHIB, 1000XEC) уже есть
-- глобально. 0G, 2Z и 4 — имена, не префиксы, по правилу 0023; 4STOCK — тоже
-- имя.
-- ---------------------------------------------------------------------------
insert into asset (code) values ('NEX'), ('WOJAK')
on conflict (code) do nothing;

insert into asset_alias (segment_code, alias, asset_code, multiplier) values
    (null, '1000NEX',   'NEX',   1000),
    (null, '1000WOJAK', 'WOJAK', 1000)
on conflict (segment_code, alias) do nothing;
