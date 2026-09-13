-- ============================================================================
-- CryptoSmith X — миграция 0063: GMX v2 на Arbitrum — замер, хост и бюджет.
--
-- 0057 завела gmx-perp с market_model = oracle_vault по конструкции и с вопросом в
-- description: не врёт ли это имя про пул настолько, чтобы заводить третью модель.
--
-- Замерено 2026-09-13, curl к arbitrum.gmxapi.io, без ключа (схема — /swagger.json):
--
--   рынки       GET /v1/markets — 126 рынков (4 только своп, 6 не в листинге); у пула свой
--               символ "BTC/USD [BTC-USDC]": у BTC три пула, у ETH три.
--   снимок      GET /v1/markets/tickers — вся площадка одним вызовом, 232 КБ: minPrice/maxPrice
--               оракула, markPrice, OI в токенах и в USD, availableLiquidity по сторонам,
--               fundingRateLong/Short, borrowingRate*. Числа — целые с точностью 1e30.
--   импакт      GET /v1/markets/info — параметры функции импакта позиции (factor, exponent,
--               max factor, virtual inventory). Сама функция опубликована в SDK
--               (gmx-interface sdk/src/utils/fees/priceImpact.ts) — котировка при размере
--               выводится из неё, без запроса на каждую пробу.
--   фандинг     ставки тикера — за ЧАС: fundingFactorPerSecond 1,1547e-9 × 3 600 = 4,157e-6 =
--               fundingRateShort на BTC [BTC-USDC]. Знак по SDK getFundingFactorPerPeriod:
--               платящая сторона отрицательна. Начисляется непрерывно, дискретной выплаты нет.
--   оборот      GET /v1/pairs — base_volume и target_volume за 24 ч. Сверено с лентой: сумма
--               sizeDeltaUsd исполненных позиционных ордеров за 24 ч на BTC [BTC-USDC]
--               3 379 479 против 3 377 896, на ETH [WETH-USDC] 3 322 971 против 3 213 037.
--   лента       POST /v1/trades/search forAllAccounts — исполнения всех счетов: цена
--               исполнения, размер в токенах и в USD, orderType (7 — ликвидация), timestamp.
--               Страница ≤ 100 при фильтре (limit > 300 → HTTP 400), курсор-смещение.
--   свечи       GET /v1/prices/ohlcv — свечи ОРАКУЛА по символу индекс-токена, последние
--               ≤ 1 000; параметр since не действует.
--   книга       нет по конструкции. Контрагент — пул, цена — оракул, котировка — функция
--               импакта при размере. Это та же форма, что у Avantis: oracle_vault остаётся.
--   лимит       не опубликован, заголовков нет. 40 запросов подряд и 60 при 30 параллельных —
--               все 200. 5/с — с запасом; источник 'measured'.
-- ============================================================================

update exchange
   set request_budget_per_s = 5,
       max_concurrent_requests = 4,
       request_budget_source = 'measured',
       request_budget_note = 'arbitrum.gmxapi.io лимит не публикует и заголовков не шлёт; 40 подряд и 60 при 30 параллельных — все 200. 5/с — с запасом. Замер 2026-09-13.'
 where code = 'gmx';

update segment
   set base_url = 'https://arbitrum.gmxapi.io',
       name = 'GMX perpetuals (Arbitrum)',
       description = 'Перпы против пула на Arbitrum (arbitrum.gmxapi.io). Цена — оракул (min/max), котировка — опубликованная функция импакта позиции при заявленном размере; книги нет. Замер 2026-09-13 подтвердил oracle_vault: форма та же, что у Avantis.',
       updated_at = now(),
       updated_by = '0063'
 where code = 'gmx-perp';
