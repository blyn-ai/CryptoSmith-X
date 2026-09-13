-- Цена у площадок без живого потока — не старше 15 секунд (владелец, 2026-09-13).
--
-- Замер на проде перед сменой: снапшот у REST-площадок шёл раз в 60 с, а сам проход занимает
-- 0,3–1,3 с (collector_status.last_duration_ms), так что возраст цены пилой доходил до минуты
-- только из-за интервала — GMX, OKX и Gate показывали 54–56 с. Снапшот у всех этих площадок —
-- один-два массовых вызова на рынок, поэтому 60 → 15 стоит несколько запросов в минуту.
--
-- Глубина новых DEX — 60 → 15 с, потому что у dYdX, Nado и Synthetix bid/ask и размеры топа берутся
-- из прохода глубины: на 30 с котировка и была бы на 30 с. Цена: dYdX 25 книг за проход (9 с) —
-- 1,7 запроса/с при ratelimit-limit 100 на 10 с; Nado 22 книги по весу 1 — 88 весов/мин из 2 400;
-- Synthetix 7 книг — 28 запросов/мин из 1 000; у GMX кривая считается из кадра снапшота. Глубину остальных площадок не трогает:
-- там интервал (300–600 с) выставлен по замеренному бюджету веса, а не по привычке.
--
-- Сегменты с пустым interval_s идут живым потоком — их не трогает. Кроме Avantis: его «поток» — сокет
-- каталога на data.avantisfi.com, а снапшот без своего интервала крутится с датасетным умолчанием в 1 с
-- и, пока сокет не подключён, каждую секунду ходит в REST /v2/trading того же хоста. Замерено
-- 2026-09-13: после перезапуска хаба (деплой) хост ответил 429, сокет не мог переподключиться (429 на
-- рукопожатии, 54 обрыва за 90 минут на проде), а снапшот падал 7 раз подряд за полминуты — петля,
-- которая сама себя держит, на обоих контурах. 15 с — тот же потолок возраста цены, и в худшем случае
-- 4 запроса в минуту вместо 60.
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/snapshot-every-15s.sql
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/snapshot-every-15s.sql

begin;

update segment_dataset sd
   set interval_s = 15, updated_at = now(), updated_by = 'ops/snapshot-every-15s.sql'
  from segment sg
 where sg.code = sd.segment_code and sg.status = 'enabled'
   and sd.dataset_code = 'snapshot' and sd.mode = 'collect'
   and sd.interval_s > 15;

update segment_dataset
   set interval_s = 15, updated_at = now(), updated_by = 'ops/snapshot-every-15s.sql'
 where segment_code = 'avantis-perp' and dataset_code = 'snapshot' and mode = 'collect'
   and (interval_s is null or interval_s > 15);

update segment_dataset sd
   set interval_s = 15, updated_at = now(), updated_by = 'ops/snapshot-every-15s.sql'
 where sd.dataset_code = 'depth' and sd.mode = 'collect'
   and sd.segment_code in ('dydx-perp', 'synthetix-perp', 'nado-perp', 'gmx-perp')
   and sd.interval_s > 15;

select sd.segment_code, sd.dataset_code, sd.interval_s
  from segment_dataset sd join segment sg on sg.code = sd.segment_code and sg.status = 'enabled'
 where sd.dataset_code in ('snapshot', 'depth') and sd.mode = 'collect'
 order by 2, 1;

commit;
