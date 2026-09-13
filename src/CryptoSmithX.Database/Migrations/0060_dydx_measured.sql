-- ============================================================================
-- CryptoSmith X — миграция 0060: dYdX v4 получает измеренный адрес и лимит.
--
-- Замерено 2026-09-13, curl к публичному индексатору, без ключа:
--
--   base_url     https://indexer.dydx.trade — REST, хостит dYdX Trading; ключ не нужен.
--   снимок       GET /v4/perpetualMarkets — весь рынок одним вызовом: 296 рынков,
--                78 ACTIVE, 218 FINAL_SETTLEMENT; 167 КБ, 0,64 с.
--   лимит        заголовки каждого ответа: ratelimit-limit: 100, ratelimit-reset
--                примерно через 10 с после первого запроса окна — 100 запросов
--                на 10 секунд с адреса.
--   книга        /v4/orderbooks/perpetualMarket/{ticker}: 100 уровней на сторону,
--                ~190 bps от середины на BTC-USD — полосы 10/25/50 закрыты.
--
-- Бюджет — 8 в секунду, ниже заявленных 10: гейт считает запросы равномерно, а окно
-- площадки — десятисекундная пачка, и запас нужен на хвост прохода. Источник —
-- 'measured': число прочитано из заголовков самой площадки.
--
-- market_model из 0057 = orderbook, и замер это подтвердил: книга настоящая,
-- публичная, размеры в базовом активе.
-- ============================================================================

update exchange
   set request_budget_per_s = 8,
       max_concurrent_requests = 4,
       request_budget_source = 'measured',
       request_budget_note = 'Заголовки ответа indexer.dydx.trade: ratelimit-limit 100, окно ~10 с. 8/с — с запасом на хвост прохода. Замер 2026-09-13.'
 where code = 'dydx';

update segment
   set base_url = 'https://indexer.dydx.trade',
       updated_at = now(),
       updated_by = '0060'
 where code = 'dydx-perp';
