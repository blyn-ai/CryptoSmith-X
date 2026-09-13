-- ============================================================================
-- CryptoSmith X — миграция 0062: Vertex закрыт; Nado — отдельная площадка.
--
-- VERTEX. 0057 завела vertex-perp заглушкой каталога. Проверено 2026-09-13:
-- vertexprotocol.com отдаёт 404 (Vercel DEPLOYMENT_NOT_FOUND), хосты
-- gateway.prod.vertexprotocol.com и archive.prod.vertexprotocol.com резолвятся, но
-- соединений не принимают. Площадка закрыта в июле 2025; команда и стек ушли на Ink L2
-- и работают там как Nado.
--
-- Строка НЕ переименовывается и НЕ удаляется: она остаётся записью о том, почему площадки
-- нет в очереди. Сегмент — disabled, а не planned: planned значит «собираемся подключить»,
-- а про закрытую площадку это неправда. У строки exchange колонки status нет — причина
-- записана в её описание.
--
-- NADO. Другая площадка, а не Vertex под новым именем: другая сеть, другие контракты,
-- ликвидность с нуля. Переименование утверждало бы преемственность, которой нет.
--
-- Замерено 2026-09-13, без ключа:
--   archive  https://archive.prod.nado.xyz — /v2/contracts: 82 контракта одним вызовом, 8 КБ,
--            0,40 с; last, mark, index, объём в базе и в котировке, OI, next funding.
--   gateway  https://gateway.prod.nado.xyz — /v2/orderbook: не больше 100 уровней на сторону
--            при любом depth, ~38 bps на BTC; /v1/query?type=symbols — спецификации в x18.
--   условие  BTC-PERP_USDT0: оборот за сутки 78,7 млн USDT0, OI 19,7 млн USD;
--            ETH-PERP_USDT0: 27,4 млн USDT0, OI 11,2 млн USD — страницы BTC и ETH ей по силам.
--   котировка USDT0 — мостовой USDT на Ink, отдельный токен; в quote_assets он назван своим
--            именем, псевдонима USDT нет.
--   лимит    документирован по запросам как вес на IP: книга — вес 1 при 2 400 в минуту,
--            perp prices и funding — вес 2 при 1 200. Бюджет 10/с — ниже обоих.
--   модель   orderbook — книга настоящая и публичная.
-- ============================================================================

update segment
   set status = 'disabled',
       description = 'Закрыта в июле 2025: vertexprotocol.com — 404, хосты API gateway/archive.prod.vertexprotocol.com не принимают соединений — проверено 2026-09-13. Команда и стек ушли на Ink L2 как Nado (сегмент nado-perp, отдельная площадка). Строка оставлена записью о том, почему площадки нет в очереди.',
       updated_at = now(),
       updated_by = '0062'
 where code = 'vertex-perp';

update exchange
   set description = 'Закрыта в июле 2025; проверено 2026-09-13 — сайт 404, хосты API не отвечают. Преемник по команде и стеку — Nado на Ink L2, заведён отдельной биржей nado.',
       updated_at = now(),
       updated_by = '0062'
 where code = 'vertex';

insert into exchange (code, name, description, website_url,
                      request_budget_per_s, max_concurrent_requests,
                      request_budget_source, request_budget_note)
values ('nado', 'Nado',
        'CLOB-DEX на Ink L2: перпы, спот и единая маржа. Построен командой и стеком закрытого Vertex, но это другая площадка — другая сеть, контракты и ликвидность.',
        'https://www.nado.xyz',
        10, 4, 'documented',
        'docs.nado.xyz: вес на IP — книга 1 при 2 400/мин, perp prices и funding 2 при 1 200/мин. 10/с — ниже обоих. Замер 2026-09-13.')
on conflict (code) do nothing;

insert into segment (code, name, description, status, adapter, exchange_code, kind, market_model, base_url, quote_assets)
values ('nado-perp', 'Nado perpetuals',
        'Книга заявок на Ink L2, котировка USDT0. Лента не собирается: каждая сделка на /v2/trades приходит двумя строками с комиссией, вшитой в цену, и без стороны тейкера.',
        'planned', 'nado-perp', 'nado', 'perp', 'orderbook',
        'https://archive.prod.nado.xyz', '{USDT0}')
on conflict (code) do nothing;

insert into segment_dataset (segment_code, dataset_code, mode, note, updated_by)
select 'nado-perp', d.code, 'disabled', 'Каталог: включает ops/enable-nado-perp.sql.', '0062'
  from dataset d
on conflict (segment_code, dataset_code) do nothing;
