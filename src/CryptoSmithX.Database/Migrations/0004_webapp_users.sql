-- ============================================================================
-- CryptoSmith X — WebApp users move from configuration into the database.
--
-- V1 of the scaffold kept human users hardcoded in WebApp:Users with PBKDF2
-- hashes. This moves them into a table so they can be managed without a redeploy.
-- For now the password is stored in clear text — an interim choice for an
-- internal two-person tool; it is to be hashed again before any wider use.
-- The cookie scheme is unchanged, so this stays the seam for an OIDC provider.
--
-- Seeded accounts carry NO password (null), so they cannot sign in. An operator
-- sets one by hand directly in the database, which is why no password ever lives
-- in this file or in the repository:
--
--     update webapp_user set password = 'chosen-password' where username = 'admin';
-- ============================================================================

create table webapp_user (
    username     text        primary key,
    password     text,                                        -- PLAINTEXT for now; null = no password set, cannot sign in
    role         text        not null check (role in ('admin', 'user')),
    tenant_code  text        references tenant (code),        -- null for admin; a user is bound to one tenant
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now()
);

comment on table webapp_user is
    'Human sign-in accounts. password is clear text for now (interim, internal tool) and is to be '
    'hashed again later. A null password means the account cannot sign in until an operator sets one '
    'by hand; seeded accounts start that way so no password is ever committed to the repository.';

-- The admin account, without a password. Set one directly in the database to enable sign-in.
insert into webapp_user (username, role, tenant_code) values
    ('admin', 'admin', null)
on conflict (username) do nothing;
