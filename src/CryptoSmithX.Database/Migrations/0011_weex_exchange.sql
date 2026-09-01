-- ============================================================================
-- CryptoSmith X — миграция 0011: сиды для адаптера WEEX Futures.
--
-- Третья биржа, чисто REST в V1 (разведка живого API — в коммит-месседже).
-- WEEX отдаёт один REST-хост на всё (нет отдельного charts_url, как у Кракена) —
-- adapter='weex-futures', base_url сидится, charts_url и ws_url остаются null.
-- ws_url null здесь означает не «забыли», а решение фазы 2 не делать сейчас —
-- см. вердикт в коммите: публичный WS у WEEX живой, но с другим протоколом
-- дельт (диапазоны версий, а не единый seq) и без текущего способа сузить
-- подписку на глубину (collect=true по умолчанию у всех) — риск по CPU при
-- ~1000 инструментах сочли неоправданным, пока владелец не прорядит список.
--
-- Алиасы: WEEX отдаёт сырые базовые активы вида '1000FLOKI' (актив с
-- множителем в имени — тот же приём, для которого уже был сеян '1000PEPE' в
-- 0006). Без алиаса авторегистрация создала бы кривой канон '1000FLOKI'
-- вместо 'FLOKI' с множителем 1000. Глобальные (exchange_code null) — приём
-- общий для бирж, не только WEEX.
-- ============================================================================

-- adapter is already 'weex-futures' (0007 seeds it to the exchange's own code by
-- default); only base_url needs filling in here.
update exchange
   set base_url = 'https://api-contract.weex.com'
 where code = 'weex-futures';

-- The alias target must exist in asset first (FK) — 0006 seeded PEPE this way for the same reason;
-- the rest of these canons have never been referenced before now.
insert into asset (code) values
    ('FLOKI'), ('SHIB'), ('SATS'), ('RATS'), ('BONK'), ('BTT'), ('XEC')
on conflict (code) do nothing;

insert into asset_alias (exchange_code, alias, asset_code, multiplier) values
    (null, '1000FLOKI', 'FLOKI', 1000),
    (null, '1000SHIB',  'SHIB',  1000),
    (null, '1000SATS',  'SATS',  1000),
    (null, '1000RATS',  'RATS',  1000),
    (null, '1000BONK',  'BONK',  1000),
    (null, '1000BTT',   'BTT',   1000),
    (null, '1000XEC',   'XEC',   1000)
on conflict (exchange_code, alias) do nothing;
