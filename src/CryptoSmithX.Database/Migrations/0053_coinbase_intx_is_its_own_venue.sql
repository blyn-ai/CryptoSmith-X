-- ============================================================================
-- CryptoSmith X — миграция 0053: Coinbase International Exchange получает
-- собственную строку exchange, потому что бюджет запросов у неё собственный.
--
-- ЗАМЕРЕНО, а не предположено (plans/exchange-roadmap.md §3). Общая посылка
-- уровня «площадка» — что бюджет запросов на IP общий у всех сегментов одной
-- биржи — на Coinbase ЛОЖНА. Advanced Trade насыщен до {429: 60, 200: 20}, и
-- НЕМЕДЛЕННО, тем же egress, api.international.coinbase.com отдал 12×200 и
-- api.exchange.coinbase.com отдал 12×200. Три хоста — три потолка.
--
-- Значит coinbase-perp не может делить request_budget_per_s со спотовым
-- Coinbase: один VenueGate на обе поверхности тормозил бы INTX из-за
-- насыщения Advanced Trade, которого INTX не замечает вовсе.
--
-- Юридически это тоже другая площадка: отдельное лицо, отдельный хост, розница
-- США туда не ходит. Строка exchange здесь описывает то, что в реальности и
-- есть одной площадкой, а не удобную группировку по бренду.
--
-- Спотовый coinbase-spot остаётся на строке coinbase и не трогается: он и есть
-- тот Advanced Trade, чей потолок измерен.
--
-- Бюджет — 'measured' с оговоркой: 80 параллельных quote прошли все, но это НЕ
-- потолок, его никто не искал. Цифра ниже консервативна намеренно.

insert into exchange (
    code, name, description, website_url,
    request_budget_per_s, max_concurrent_requests, request_budget_source,
    request_budget_note, request_budget_window_s)
values (
    'coinbase-intx', 'Coinbase International',
    'Офшорная деривативная площадка Coinbase: отдельное юрлицо, отдельный хост, '
    'собственный потолок запросов — см. шапку 0053.',
    'https://international.coinbase.com',
    10, 8, 'measured',
    '80 параллельных /instruments/{sym}/quote прошли все; потолок НЕ найден. '
    'Цифра консервативна намеренно: адаптер читает один bulk-вызов на проход.',
    1)
on conflict (code) do nothing;

-- Сегмент переезжает на новую строку площадки. Это и есть вся суть миграции:
-- сам сегмент, его код и его адаптер не меняются.
update segment
   set exchange_code = 'coinbase-intx',
       updated_by    = '0053 migration'
 where code = 'coinbase-perp';

comment on table exchange is
    'Площадка как ЕДИНИЦА БЮДЖЕТА ЗАПРОСОВ, а не как бренд. Coinbase занимает '
    'две строки (coinbase и coinbase-intx) именно поэтому: замер показал, что '
    'насыщение одного хоста не трогает другой (0053). Там, где посылка верна — '
    'OKX, где двадцать SWAP и двадцать SPOT одновременно дали по десять каждому, '
    '— строка одна.';
