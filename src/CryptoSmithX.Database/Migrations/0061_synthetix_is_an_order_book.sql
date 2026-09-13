-- ============================================================================
-- CryptoSmith X — миграция 0061: Synthetix — книга заявок, а не оракульный пул.
--
-- 0057 завела synthetix-perp как «Synthetix Perps V3 (Base)», market_model =
-- oracle_vault — по конструкции площадки, какой она была, и с оговоркой в
-- description, что это подтверждает §0. Замер §0 это опроверг.
--
-- Замерено 2026-09-13, curl к papi.synthetix.io, без ключа:
--
--   транспорт   REST, POST https://papi.synthetix.io/v1/info, action в теле; торговля
--               — отдельно, с подписью EIP-712 (chainId 1). Нужен описательный User-Agent.
--   рынки       getMarkets — 10 рынков, все с котировкой USDT, contractSize = 1.
--   снимок      getMarketPrices — вся площадка одним вызовом: bestBid, bestAsk,
--               markPrice, indexPrice, lastPrice, volume24h (база), quoteVolume24h (USDT),
--               fundingRate (ОЦЕНКА текущего периода), openInterest (база), timestamp.
--   книга       getOrderbook — настоящая книга; на BTC-USDT 12 и 11 уровней, ~200 bps
--               от середины.
--   фандинг     getFundingRate — lastSettlementRate, nextFundingTime, fundingInterval
--               3 600 000 мс; getFundingRateHistory — ряд.
--   лимит       документирован: 1 000 запросов в минуту с IP, всплески до 100/с;
--               отказ — HTTP 429 RATE_LIMIT_EXCEEDED.
--   ликвидации  публично не публикуются: в ленте нет пометки, публичного маршрута нет;
--               единственный поток — subAccountUpdates, активность своего аккаунта.
--
-- РЕШЕНИЕ ВЛАДЕЛЬЦА по замеру (2026-09-13): market_model = orderbook. Колонки котировки
-- — измерения книги, без выведения и без бейджей.
--
-- Бюджет 12/с (720 в минуту) — ниже задокументированных 1 000 в минуту, с запасом на
-- хвост прохода. Источник — 'documented'.
-- ============================================================================

update exchange
   set request_budget_per_s = 12,
       max_concurrent_requests = 4,
       request_budget_source = 'documented',
       request_budget_note = 'developers.synthetix.io: 1 000 запросов в минуту с IP, всплески до 100/с. 12/с — с запасом. Замер 2026-09-13.'
 where code = 'synthetix';

update segment
   set market_model = 'orderbook',
       name = 'Synthetix perpetuals',
       description = 'Офчейн-биржа с книгой заявок (papi.synthetix.io), котировка USDT. Была заведена в 0057 как Perps V3 на Base с оракульным пулом; замер 2026-09-13 показал книгу, модель сменена решением владельца. Ликвидации площадка публично не публикует.',
       base_url = 'https://papi.synthetix.io',
       updated_at = now(),
       updated_by = '0061'
 where code = 'synthetix-perp';
