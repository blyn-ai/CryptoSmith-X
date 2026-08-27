-- ============================================================================
-- CryptoSmith X — WebApp (platform), V1 schema (PostgreSQL 14+)
--
-- The platform box of plans/architecture.mermaid: tenant and bot registry,
-- per-bot policy, and the idempotent event/heartbeat inbox. The WebApp is the
-- only writer of these tables; it reads the market-data tables read-only.
--
-- Conventions of 0001: timestamptz everywhere (UTC), statuses as text + CHECK,
-- updated_at set by the application (no triggers), comments on the tables.
-- Nobody ever calls into a bot: a bot POSTs its heartbeat and events here and
-- receives its policy in the heartbeat response. There is no shared table with
-- the bot — the bot keeps its own SQLite.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- tenant — an owner of bots. One row per member instance (LUKAS, DENIS).
-- ----------------------------------------------------------------------------
create table tenant (
    code        text        primary key,           -- 'LUKAS' | 'DENIS' — matches the user's tenantCode claim
    name        text        not null,
    created_at  timestamptz not null default now()
);

comment on table tenant is
    'A bot owner. Created in the admin UI; there is no seed row. A human user is bound to one tenant '
    'through the hardcoded WebApp:Users config, not through this table.';


-- ----------------------------------------------------------------------------
-- bot — a bot instance registered with the platform.
-- ----------------------------------------------------------------------------
create table bot (
    id                    integer     generated always as identity primary key,
    tenant_code           text        not null references tenant (code),
    bot_instance_id       text        not null unique,   -- 'futures-live' — the bot's own stable id
    name                  text        not null,
    token_hash            text,                           -- sha-256 (hex) of the opaque bearer token; null = no access
    is_enabled            boolean     not null default true,
    created_at            timestamptz not null default now(),
    last_heartbeat_at     timestamptz,
    last_heartbeat_json   jsonb,
    updated_at            timestamptz not null default now()
);

comment on table bot is
    'A bot instance. token_hash is the sha-256 of an opaque token shown once at creation; a null hash '
    'means the bot cannot authenticate. last_heartbeat_at / _json are stamped by the ingest webhook.';

comment on column bot.token_hash is
    'sha-256 (lowercase hex) of the opaque bearer token. The token itself is never stored; it is shown '
    'exactly once when the bot is created or its token is regenerated. null = the bot has no usable token.';

create index bot_tenant_code on bot (tenant_code);


-- ----------------------------------------------------------------------------
-- bot_policy — the current policy for a bot, one row per bot.
-- This is the only thing a heartbeat response carries back to the bot.
-- ----------------------------------------------------------------------------
create table bot_policy (
    bot_id      integer     primary key references bot (id),
    policy_json jsonb       not null,
    version     integer     not null check (version > 0),
    updated_at  timestamptz not null default now()
);

comment on table bot_policy is
    'The policy handed to a bot in its heartbeat response. version is bumped by the editor on every save; '
    'a bot with no row here is told { policyVersion: 0, policy: null }.';


-- ----------------------------------------------------------------------------
-- bot_event — the bot's outbox, received idempotently.
-- ----------------------------------------------------------------------------
create table bot_event (
    id           bigint      generated always as identity primary key,
    bot_id       integer     not null references bot (id),
    event_id     text        not null,               -- the id from the bot's own outbox
    utc          timestamptz not null,               -- when the event happened, per the bot
    type         text        not null,
    payload      jsonb       not null,
    received_at  timestamptz not null default now(),

    unique (bot_id, event_id)                         -- idempotency: re-POSTing a batch is harmless
);

comment on table bot_event is
    'The bot''s append-only outbox as received here. (bot_id, event_id) is unique, so a bot that retries '
    'a batch after a lost response inserts nothing the second time.';

create index bot_event_bot_id_received_at on bot_event (bot_id, received_at desc);
