-- ============================================================================
-- CryptoSmith X — миграция 0057: пять DEX появляются в каталоге как planned.
--
-- dYdX v4, GMX, Vertex, Synthetix Perps V3, Drift. Это ЗАГЛУШКИ КАТАЛОГА, а не
-- подключение: адаптеров нет, base_url пуст, все наборы выключены. Строка
-- нужна, чтобы площадку можно было назвать в очереди, в матрице и в разговоре,
-- — ровно тем же способом, каким в каталоге уже стоят двенадцать planned-строк
-- от Bybit до Deribit.
--
-- ПОЧЕМУ base_url ОСТАЁТСЯ ПУСТЫМ. Адреса этих площадок я знаю из вторых рук, а
-- в этой папке принят другой стандарт: очередь CEX переставлялась по ответам
-- шестнадцати эндпоинтов, опрошенных живьём, и один такой прогон отправил
-- Coinbase с пятнадцатого места на девятое. Написать сюда непроверенный хост
-- значило бы выдать догадку за факт в таблице, которую потом читают как факт.
-- Адрес ставит §0 плана (plans/dex-five-venues.md), вместе с замером.
--
-- ПОЧЕМУ market_model ВСЁ-ТАКИ ПРОСТАВЛЕН. Колонка not null с умолчанием
-- 'orderbook', то есть промолчать нельзя — умолчание само сделает утверждение,
-- и для GMX и Synthetix заведомо ложное. Поэтому значение ставится по
-- конструкции площадки, а неуверенность уезжает в description дословно: у трёх
-- книжных она проверяется, у двух оракульных — подтверждается.
--
-- Бюджет запросов у всех пяти — 'assumed' и намеренно скромный. После 0054
-- потолок ключуется на ХОСТ и читается как segment.request_budget_per_s ??
-- exchange.request_budget_per_s; пока хоста нет, число не работает ни на что и
-- существует только затем, чтобы not null не заставил выдумать большое.
-- ============================================================================

insert into exchange (code, name, description,
                      request_budget_per_s, max_concurrent_requests,
                      request_budget_source, request_budget_note) values
    ('dydx',      'dYdX',      'Перп-DEX v4 на собственной сети Cosmos; ордербук в консенсусе, данные через Indexer.',
     5, 4, 'assumed', 'Заглушка: площадка не подключена, лимит не мерен. Ставит §0 плана dex-five-venues.'),
    ('gmx',       'GMX',       'Перпы против пула ликвидности на Arbitrum и Avalanche; цена от оракула, стакана нет по конструкции.',
     5, 4, 'assumed', 'Заглушка: площадка не подключена, лимит не мерен. Ставит §0 плана dex-five-venues.'),
    ('vertex',    'Vertex',    'Гибрид ордербука и AMM с ончейн-клирингом на Arbitrum.',
     5, 4, 'assumed', 'Заглушка: площадка не подключена, лимит не мерен. Ставит §0 плана dex-five-venues.'),
    ('synthetix', 'Synthetix', 'Perps V3 на Base и Optimism; цена от оракула, механика родственна Avantis.',
     5, 4, 'assumed', 'Заглушка: площадка не подключена, лимит не мерен. Ставит §0 плана dex-five-venues.'),
    ('drift',     'Drift',     'Ордербук и виртуальный AMM на Solana; вторая экосистема в продукте.',
     5, 4, 'assumed', 'Заглушка: площадка не подключена, лимит не мерен. Ставит §0 плана dex-five-venues.')
on conflict (code) do nothing;

-- Сегмент на развёртывание, а не на площадку: Synthetix живёт на Base и на
-- Optimism, GMX на Arbitrum и Avalanche, и это разные хосты с разными
-- потолками. Заводится по одному сегменту — тому, с которого начнём; второй
-- добавляется своей строкой, когда до него дойдёт, и делит биржу, а не гейт.
insert into segment (code, name, description, status, adapter, exchange_code, kind, market_model) values
    ('dydx-perp',      'dYdX v4 perpetuals',        'Настоящая книга заявок. market_model=orderbook по конструкции; §0 подтверждает, что Indexer отдаёт её публично.',
     'planned', 'dydx-perp',      'dydx',      'perp', 'orderbook'),
    ('vertex-perp',    'Vertex perpetuals',         'Гибрид: книга И кривая AMM. market_model=orderbook поставлен как ближайший из двух существующих; §0 решает, хватает ли двух имён или гибриду нужно своё.',
     'planned', 'vertex-perp',    'vertex',    'perp', 'orderbook'),
    ('drift-perp',     'Drift perpetuals',          'Книга плюс виртуальный AMM. Та же оговорка про имя модели, что у Vertex.',
     'planned', 'drift-perp',     'drift',     'perp', 'orderbook'),
    ('synthetix-perp', 'Synthetix Perps V3 (Base)', 'Цена от оракула, стакана нет. market_model=oracle_vault по родству с Avantis; §0 подтверждает, публикует ли площадка функцию импакта — от этого зависит, будут ли bid/ask выводимыми, как у Avantis, или колонки останутся пустыми.',
     'planned', 'synthetix-perp', 'synthetix', 'perp', 'oracle_vault'),
    ('gmx-perp',       'GMX perpetuals (Arbitrum)', 'Перпы против пула. market_model=oracle_vault — ближайшее из двух, но пул это не хранилище Avantis; §0 решает, врёт ли это имя настолько, чтобы завести третье.',
     'planned', 'gmx-perp',       'gmx',       'perp', 'oracle_vault')
on conflict (code) do nothing;

-- Полный крест наборов в disabled — как у остальных planned-сегментов. Хаб
-- поднимает петлю только если адаптер объявил способность И режим collect,
-- так что строки здесь ничего не запускают; они дают матрице, что показать,
-- и человеку — что переключить, когда адаптер появится.
insert into segment_dataset (segment_code, dataset_code, mode, note, updated_by)
select s.code, d.code, 'disabled',
       'Заглушка каталога: адаптера нет. Включать по одному после §0.', '0057'
  from segment s
 cross join dataset d
 where s.code in ('dydx-perp', 'vertex-perp', 'drift-perp', 'synthetix-perp', 'gmx-perp')
on conflict (segment_code, dataset_code) do nothing;

comment on column segment.market_model is
    'Как устроен рынок сегмента: ''orderbook'' — есть стакан; ''oracle_vault'' — цена от '
    'оракула, контрагент пул. Имя модели ЦЕЛИКОМ, а не ось: третье значение заводится тогда, '
    'когда существующие начинают врать про конкретную площадку, и вместе с её замером. '
    'Объясняет пустые и выводимые колонки как факт о рынке, а не как потерю данных.';
