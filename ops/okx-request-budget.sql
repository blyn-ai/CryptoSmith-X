-- OKX's request budget, lowered after the venue refused us on both contours.
--
-- WHAT HAPPENED. The depth sweep moved from `books` (400 levels) to `books-full` (5 000), because
-- 400 levels of BTC-USDT-SWAP reach only 7 bps from the mid and every depth band was empty on this
-- venue's most traded instrument. That change did not raise the request COUNT — it is the same one
-- call per symbol — but it landed alongside mark candles, index candles and an open-interest series
-- that had just been switched on, and the venue's per-IP budget is shared by all of them. Within the
-- hour both contours answered 50011, "Rate limit reached", on the depth pass.
--
-- WHY THE NUMBER WAS NEVER EARNED. The row said ten a second, marked 'assumed'. Nothing had ever
-- measured it; it was the same ten every venue in that migration was given.
--
-- 'assumed' is kept, deliberately. What is known is one point — ten a second refuses under this
-- workload — and not where the boundary is. Calling 5 'measured' would claim a reading nobody took.
-- The sustained need is about 1.5 a second: three candle series over 25 symbols a minute is 1.25,
-- and depth with its tape is another 0.17. Five leaves room for the bursts a sweep makes without
-- sitting at the ceiling.
--
-- The lasting fix is in the client, not here: OKX reports "too fast" as a CODE under an HTTP 200,
-- so until this week the pacing could not see it at all and the gate kept the pace that caused it.
-- That is now translated into a real penalty, the way MEXC's 510 already was — so if five is still
-- too many, the gate will back off instead of failing every pass.
--
-- HOW TO RUN IT
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/okx-request-budget.sql
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/okx-request-budget.sql
--
-- A gate is built once per process from the budget in force at that moment, so THE HUB MUST BE
-- RESTARTED for this to take effect.

begin;

update exchange
   set request_budget_per_s = 5,
       max_concurrent_requests = 4,
       request_budget_source = 'assumed'
 where code = 'okx';

select code, request_budget_per_s, max_concurrent_requests, request_budget_source
  from exchange where code = 'okx';

commit;
