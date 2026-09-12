-- Enabling the datasets the six REST venues now actually implement.
--
-- Enabling is an OPERATOR decision, which is why this is a script run against a contour and not a
-- migration: a migration would decide for every deployment at once, including ones whose operator
-- has a reason to leave a venue quiet.
--
-- Intervals are sized against the collected set, not the catalogue. Each of these venues discovers
-- hundreds to a thousand instruments and collects 25-60 of them, so a per-symbol sweep at 300 s is
-- well under a tenth of a request per second — two orders of magnitude below the slowest venue's
-- measured ceiling (MEXC, ~2.5/s).
--
-- The interval on an open-interest row matches that venue's own bucket GRAIN: asking more often
-- than the venue aggregates returns the same bucket again, and asking less often leaves the newest
-- one stale for the difference.

-- HOW TO RUN IT
--
--   test   ssh csx-prod 'docker exec -i cryptosmithx-postgres \
--              psql -U marketdata -d marketdata -v ON_ERROR_STOP=1' < ops/enable-queue-venue-datasets.sql
--
--   prod   ssh -F .local/ssh-config csx-datahub-jump \
--              'sudo -n -u postgres psql -d marketdata -v ON_ERROR_STOP=1' < ops/enable-queue-venue-datasets.sql
--
-- Peer authentication as the postgres user on both, so no connection string is read, copied or
-- passed anywhere. The whole file is one transaction: it either applies or it does not.
--
begin;

create temporary table wanted (
    segment_code text,
    dataset_code text,
    interval_s   int
) on commit drop;

insert into wanted (segment_code, dataset_code, interval_s) values
    -- Bybit: the book ladder, the polled tape, both reference series, and an open-interest series
    -- whose grain is five minutes — the finest this venue publishes, so the row is re-timed from
    -- the hour it sat at while the history method was dead code.
    ('bybit-perp',   'depth',         300),
    ('bybit-perp',   'trades',         60),
    ('bybit-perp',   'candles_mark',   60),
    ('bybit-perp',   'candles_index',  60),
    ('bybit-perp',   'open_interest', 300),

    -- Bitget: no open-interest series and no liquidation feed on any public route.
    ('bitget-perp',  'depth',         300),
    ('bitget-perp',  'trades',         60),
    ('bitget-perp',  'candles_mark',   60),
    ('bitget-perp',  'candles_index',  60),

    -- Gate: open interest and liquidations arrive in ONE response (contract_stats), so the two can
    -- never disagree about a period. Its grain is an hour.
    ('gate-perp',    'depth',         300),
    ('gate-perp',    'trades',         60),
    ('gate-perp',    'candles_mark',   60),
    ('gate-perp',    'candles_index',  60),
    ('gate-perp',    'open_interest', 3600),
    ('gate-perp',    'liquidations',   900),

    -- OKX: rubik's per-instrument open-interest history at its 1H grain, plus liquidations keyed by
    -- underlying.
    ('okx-perp',     'depth',         300),
    ('okx-perp',     'trades',         60),
    ('okx-perp',     'candles_mark',   60),
    ('okx-perp',     'candles_index',  60),
    ('okx-perp',     'open_interest', 3600),
    ('okx-perp',     'liquidations',   900),

    -- MEXC: the tape is folded out of the same call as the book, so trades cost nothing beyond the
    -- depth sweep. No open-interest history (403 on that one route), no liquidation flag, and no
    -- mark or index series behind its current fairPrice.
    -- 600 rather than 300: this venue answered 510 ("too frequent", under an HTTP 200) on the
    -- depth sweep at five minutes over sixty symbols. The client parks the venue for three minutes
    -- when it says so, so nothing broke — but this is the one venue whose reconnaissance refused to
    -- be pushed, on an unverified report of the host banning an IP outright, and the cost of being
    -- wrong here is the whole host rather than one pass.
    ('mexc-perp',    'depth',         600),
    ('mexc-perp',    'trades',         60);

-- Nothing is created here. The segment x dataset cross is always complete, so a missing row would
-- mean the cross is broken and is worth failing on rather than papering over.
update segment_dataset sd
   set mode       = 'collect',
       interval_s = w.interval_s,
       transport  = 'rest',
       updated_at = now(),
       updated_by = 'ops/enable-queue-venue-datasets.sql'
  from wanted w
 where sd.segment_code = w.segment_code
   and sd.dataset_code = w.dataset_code;

do $$
declare missing int;
begin
    select count(*) into missing
      from wanted w
      left join segment_dataset sd
        on sd.segment_code = w.segment_code and sd.dataset_code = w.dataset_code
     where sd.segment_code is null;

    if missing > 0 then
        raise exception 'segment_dataset is missing % of the rows this script expects', missing;
    end if;
end $$;

commit;

-- What the six now collect, for the record kept beside the run.
select segment_code, dataset_code, mode, interval_s
  from segment_dataset
 where segment_code in ('bybit-perp','bitget-perp','gate-perp','okx-perp','coinbase-perp','mexc-perp')
   and mode <> 'disabled'
 order by segment_code, dataset_code;
