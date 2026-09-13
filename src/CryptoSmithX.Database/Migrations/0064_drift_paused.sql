-- ============================================================================
-- CryptoSmith X — миграция 0064: Drift остановлен; Velocity не подключается.
--
-- DRIFT. 0057 завела drift-perp заглушкой каталога. Проверено 2026-09-13:
--   хосты API   data.api.drift.trade — CNAME на CloudFront без адреса; dlob.drift.trade и
--               mainnet-beta.api.drift.trade не резолвятся. Ни один не отвечает.
--   сайт        app.drift.trade и docs.drift.trade перенаправляют на velocity.exchange.
--   программа   по руководству Velocity «Migrating from Drift»: программа Drift
--               (dRiftyHA39MWEi3m9aunc5MzRF1JYuBsbn6VPcn33UH) на паузе, состояние не
--               переносится.
--
-- Строка НЕ переименовывается и НЕ удаляется — тот же порядок, что у Vertex в 0062, по тому же
-- основанию: запись о том, почему площадки нет в очереди. disabled, а не planned: planned
-- значит «собираемся подключить».
--
-- VELOCITY. Форк Drift v2, но другая площадка: новая программа
-- (vELoC1audYbSYVRXn1vPaV8Axoa9oU6BYmNGZZBDZ1P), счета заводятся заново, котировка USDT вместо
-- USDC, свой хост данных (data.velocity.exchange) и книги (dlob.velocity.exchange).
-- Переименование Drift утверждало бы преемственность, которой нет.
--
-- В каталог НЕ заводится. Условие владельца, поставленное для Nado, — перпы BTC и ETH с
-- несимволическим оборотом за сутки, иначе страницы /studio/v2/BTC и /studio/v2/ETH площадке не по
-- силам. Замер 2026-09-13, GET data.velocity.exchange/external/coingecko/contracts — всего три
-- перпа:
--   BTC-PERP  оборот 0,0043 BTC (331 USDT), OI 0,3358 BTC; книга: лучший бид 77 325 (vAMM,
--             0,0032 BTC), лучший аск 77 388,5 — одна заявка на 0,001 BTC.
--   ETH-PERP  оборот 0, OI 2 ETH.
--   SOL-PERP  оборот 100 SOL (10 036 USDT).
-- Это символический оборот; площадка остановлена на замере.
-- ============================================================================

update segment
   set status = 'disabled',
       description = 'Остановлен: хосты API data.api.drift.trade, dlob.drift.trade, mainnet-beta.api.drift.trade не отвечают, app/docs.drift.trade перенаправляют на velocity.exchange; программа Drift на паузе, состояние не переносится — проверено 2026-09-13. Velocity — форк на новой программе с котировкой USDT, отдельная площадка; не подключена: BTC-PERP 331 USDT и ETH-PERP 0 оборота за сутки. Строка оставлена записью о том, почему площадки нет в очереди.',
       updated_at = now(),
       updated_by = '0064'
 where code = 'drift-perp';

update exchange
   set description = 'Остановлен; проверено 2026-09-13 — хосты API не отвечают, сайт ведёт на Velocity. Velocity — форк Drift v2 на новой программе, отдельная площадка; в каталог не заведена: оборот BTC и ETH за сутки символический (331 и 0 USDT).',
       updated_at = now(),
       updated_by = '0064'
 where code = 'drift';
