-- ============================================================================
-- CryptoSmith X — миграция 0012: сиды для адаптера Hyperliquid.
--
-- Четвёртая биржа, и первая с WS сразу в этом же PR (разведка живого API — в
-- коммит-месседже). base_url — единственный REST-хост (POST /info на всё, как
-- у WEEX — один хост, никакого отдельного charts_url). ws_url сидится сразу:
-- разведка подтвердила публичный WS живьём — l2Book шлёт ПОЛНЫЕ снапшоты
-- книги, а не дельты (без seq-машины вообще), сокет пережил 75 с без
-- клиентского пинга, и подписка на все ~177 живых перпов даёт ~34 msg/s /
-- ~52 KB/s суммарно — заметно легче, чем у Кракена.
--
-- Алиасы: Hyperliquid отдаёт голые монеты, но с тем же k-префиксом = ×1000,
-- что и на других биржах. kPEPE уже сеян в 0006; здесь — остальные пять живых
-- k-монет из /meta. kLUNC и kNEIRO целятся в активы, которых ещё не было в
-- канон-справочнике — сидим их первыми (тот же приём, что PEPE в 0006 и
-- FLOKI/SHIB/... в 0011).
-- ============================================================================

update exchange
   set base_url = 'https://api.hyperliquid.xyz',
       ws_url   = 'wss://api.hyperliquid.xyz/ws'
 where code = 'hyperliquid';

insert into asset (code) values
    ('LUNC'), ('NEIRO')
on conflict (code) do nothing;

insert into asset_alias (exchange_code, alias, asset_code, multiplier) values
    (null, 'kSHIB',  'SHIB',  1000),
    (null, 'kBONK',  'BONK',  1000),
    (null, 'kFLOKI', 'FLOKI', 1000),
    (null, 'kLUNC',  'LUNC',  1000),
    (null, 'kNEIRO', 'NEIRO', 1000)
on conflict (exchange_code, alias) do nothing;
