-- ============================================================================
-- Exchange becomes an administrable integration.
--
-- Lifecycle vs health, deliberately separated:
--   * status — the DESIRED state, set by a human in the admin UI, stored here.
--   * connection health (ok/warning/error, response time) — the OBSERVED state,
--     never stored on exchange; the UI derives it from collector_status, which
--     gains duration columns below.
--
-- Ownership note: the lifecycle columns (status, name, description) are written
-- by the WebApp admin area; the Hub only READS status to decide whether to
-- collect. Everything else about the exchange family stays Hub-owned.
-- ============================================================================

alter table exchange
    add column status text not null default 'planned'
        check (status in ('planned', 'enabled', 'disabled', 'maintenance', 'abandoned'));

comment on column exchange.status is
    'Desired lifecycle, set by a human: planned = no adapter yet; enabled = collect; '
    'disabled = switched off by hand; maintenance = deliberate pause, not an alert; '
    'abandoned = kept for history, never runs. Observed health is NOT stored — it is '
    'derived from collector_status.';

-- Only the fake venue has an adapter today; the four real ones were never truly
-- "enabled", they were aspirations. Say so.
update exchange set status = case when code = 'fake' then 'enabled' else 'planned' end;

alter table exchange rename column note to description;
alter table exchange drop column is_enabled;

-- Response-time measurement, written by the collector loop on every attempt.
alter table collector_status
    add column last_duration_ms integer,
    add column avg_duration_ms  double precision;

comment on column collector_status.avg_duration_ms is
    'Exponentially weighted moving average (0.8 * prev + 0.2 * new) of attempt duration — '
    'a cheap "recent average" without an attempt-history table. If a strict windowed '
    'average is ever needed, that table is the upgrade path.';
