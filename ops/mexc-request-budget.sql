-- MEXC's request budget, set to what was measured rather than to what was assumed.
--
-- The row said 10 requests a second with a burst of 8, marked 'assumed'. The adapter's own remarks
-- say something else and always have: the reconnaissance measured a safe ceiling of about 2.5 per
-- SECOND, refused to push past it, and recorded an unverified report of the host banning an IP
-- outright. Nobody reconciled the two, and at ten a second nothing complained — on the test host.
--
-- Production complained immediately. From that egress the depth sweep and the funding-history sweep
-- both came back 510 ("too frequent", delivered under an HTTP 200), and the depth column stayed
-- empty for all sixty collected symbols while the same code filled it on test. The venue was
-- telling us the number was wrong and the client was parking itself for three minutes at a time
-- rather than slowing down, because the gate's ceiling is what decides the pace and the gate had
-- been told ten.
--
-- 2 per second with a burst of 2, under the measured ceiling rather than at it. The sweeps this
-- has to carry are small: sixty symbols of depth every ten minutes is half a minute of asking, and
-- the funding history is sixty calls an hour. Nothing here needs ten a second; the number was never
-- earned, only assumed.
--
-- HOW TO RUN IT
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/mexc-request-budget.sql
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/mexc-request-budget.sql
--
-- A gate is built once per process from the budget in force at that moment, deliberately: a venue's
-- per-IP budget does not reset because we stopped looking. So THE HUB MUST BE RESTARTED for this to
-- take effect.

begin;

update exchange
   set request_budget_per_s = 2,
       max_concurrent_requests = 2,
       request_budget_source = 'measured'
 where code = 'mexc';

select code, request_budget_per_s, max_concurrent_requests, request_budget_source
  from exchange where code = 'mexc';

commit;
