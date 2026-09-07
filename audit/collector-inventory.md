# Collector inventory — CryptoSmith-X

Read-only inventory assembled for a later task that will add full market-data collection for
Kraken Futures. No source file was changed; every database access was `select`; no secret value was
read or printed (secret-bearing keys are named, never quoted).

**Commit inventoried:** `6ac83df22cb2e477d2cb09f5bbf865ded0cd8c4e` (`git rev-parse HEAD`).
**Date:** 2026-09-07 (measurements taken 05:22–05:35 UTC unless a line says otherwise).
**Working tree:** NOT clean. At the moment this document was assembled `git status --short`
showed 9 files; by the time its pointers were re-verified it showed 16, all from a
concurrent workflow editing Studio and its tests. That workflow is still running, so section 6
describes a moving target and says so where it matters. Files open at verification time:
`src/CryptoSmithX.WebApp.Studio/Format.cs`, `src/CryptoSmithX.WebApp.Studio/Models/Statement.cs`, `src/CryptoSmithX.WebApp.Studio/Models/Strip.cs`, `src/CryptoSmithX.WebApp.Studio/Views/Pairs/Pair.cshtml`, `src/CryptoSmithX.WebApp.Studio/Views/Shared/_Layout.cshtml`, `src/CryptoSmithX.WebApp.Studio/Views/Shared/_PairTable.cshtml`, `src/CryptoSmithX.WebApp.Studio/wwwroot/studio-ages.js`, `src/CryptoSmithX.WebApp.Studio/wwwroot/studio-candles.js`, `src/CryptoSmithX.WebApp.Studio/wwwroot/studio.css`, `tests/CryptoSmithX.WebApp.Studio.Tests/DesignSystemTests.cs`, `tests/CryptoSmithX.WebApp.Studio.Tests/FigureFitTests.cs`, `tests/CryptoSmithX.WebApp.Studio.Tests/FreshnessTests.cs`, `tests/CryptoSmithX.WebApp.Studio.Tests/StatementTests.cs`, `src/CryptoSmithX.WebApp.Studio/Models/OhlcLine.cs`, `tests/CryptoSmithX.WebApp.Studio.Tests/OhlcLineTests.cs`.
Every pointer in this document was checked against the COMMITTED state at the hash above, not
against the working tree.

**Deployments:**

| | TEST | PROD |
|---|---|---|
| host | `38.242.248.83`, hostname `bykovas-contabo-vps` (ssh `root@`) | `10.11.12.28` over VPN, hostname `b-csx-datahub` |
| postgres | in container `cryptosmithx-postgres` (16.15 Debian), db/user `marketdata` | on the host: `sudo -u postgres psql -d marketdata` (16.15 Ubuntu, `data_directory = /csx-data/postgresql/16/main`) |
| `max(schema_version.version)` | **27**, last applied 2026-09-06 21:44:58+00 | **20**, last applied 2026-09-05 21:16:19+00 — **seven behind** (0021–0027 unapplied) |
| containers | `cryptosmithx-{postgres, marketdata-hub, webapp-admin, webapp-studio}` + `cryptosmithx-db-migrator` (Exited 0). No `marketdata-api`. | `cryptosmithx-{marketdata-hub, marketdata-api, webapp}` + `cryptosmithx-db-migrator` (Exited 0). No Studio/Arena container. |
| segments | binance-usdm **maintenance**, fake disabled, hyperliquid/kraken-futures/weex-futures enabled | binance-usdm **planned**, hyperliquid/kraken-futures/weex-futures enabled, **no `fake` row** |

**Provenance note.** This document assembles six survey passes plus my own verification. Where a
survey gave a line-approximate pointer I re-derived the exact one and say so. Where two surveys
disagree, both numbers appear with the reason. Area 7 (Admin) was cut off in the survey that owned
it; everything in section 7 below was read by me directly from the files it cites.

---

## 1. Layout

### 1.1 Projects

`CryptoSmithX.sln:6-28` lists 10 projects — 6 under `src/`, 4 under `tests/`.

| Project | SDK | Role |
|---|---|---|
| `src/CryptoSmithX.Database` | `Microsoft.NET.Sdk.Worker` | Owns the schema. One-shot exe (`Database/Program.cs:1-28`); migrations are embedded resources (`CryptoSmithX.Database.csproj`, `<EmbeddedResource Include="Migrations\*.sql" />`). Also `Db.cs` (Npgsql wrapper) and `Partitions.cs`. |
| `src/CryptoSmithX.MarketData.Connectors` | class library | Venue wire formats only. `Binance/`, `Hyperliquid/`, `Kraken/`, `Weex/`, `Fake/`, plus `Market/`, `Pacing/`, `Streaming/`. `InternalsVisibleTo` its test assembly (`.csproj:11-16`). |
| `src/CryptoSmithX.MarketData.Hub` | Worker | **The only service that ingests.** One hosted service, `ExchangeWorker` (`Hub/Program.cs:32`). |
| `src/CryptoSmithX.MarketData.Api` | Web | Read-only `/v1` (`Api/Endpoints.cs:15-22`), OpenAPI + Scalar (`Api/Program.cs:29,39-40`). |
| `src/CryptoSmithX.WebApp.Admin` | Web | Admin console, areas `Admin/` and `My/`; cookie auth (`Admin/Program.cs:50-58`); bot webhook `/api/ingest/*` (`Admin/Api/IngestEndpoints.cs:19-22`); SSE notifier (`Admin/Live/LiveNotifier.cs`, registered `Admin/Program.cs:45-46`). |
| `src/CryptoSmithX.WebApp.Studio` | Web | Public showcase under `UsePathBase("/studio")` (`Studio/Program.cs:128`), read-only DB role. |
| `src/web/ds-studio` | not a project | Design-system source, vendored into `Studio/wwwroot/ds/`; the Studio test csproj asserts the two trees are byte-identical. |

Reference graph as declared: `Connectors ← Hub`; `Database ← Hub, Api, WebApp.Admin,
WebApp.Studio`. Nothing references the Hub or the Api.
`Directory.Build.props:1-11` applies `net10.0`, nullable, implicit usings,
**`TreatWarningsAsErrors=true`**, `InvariantGlobalization`, `EnforceCodeStyleInBuild` to every project.

### 1.2 Which service ingests

`CryptoSmithX.MarketData.Hub`, and only it.

- `ExchangeWorker` supervises: rollup and retention run once service-wide; per-segment collector
  loops start/stop every 30 s to match `segment.status` and the `segment_dataset` matrix
  (`Hub/Ingestion/ExchangeWorker.cs:16-27, 88-105, 139-203`).
- Implemented collectors, hard-coded: `ExchangeWorker.cs:36` —
  `KnownCollectorDatasets = ["discovery", "snapshot", "depth", "candles", "funding"]`, with the doc
  comment at `:33-35` stating trades / open_interest / liquidations have no implementation
  regardless of what policy says.
- Bodies are wired at `ExchangeWorker.cs:291-307`; every loop is one `CollectorLoop`
  (`Hub/Ingestion/CollectorLoop.cs:26-153`) reporting through
  `ExchangeWorker.WriteStatusAsync` (`:355-412`) and `RecordGapAsync` (`:426-456`).
- The Admin app's `/api/ingest/*` is the trading-bot heartbeat webhook authenticated against
  `bot.token_hash` (`Admin/Api/IngestEndpoints.cs:9-22`) — not market data.
- **No exchange API key exists anywhere.** A repo-wide grep for
  `apikey|api_key|apisecret|secretkey|passphrase|X-MBX-APIKEY|signature` over `src/**` returns only
  two prose matches in `Connectors/Binance/BinanceWsFeed.cs:79,296`. Stated as architecture at
  `0001_initial.sql:25` and `0007_runtime_settings.sql:21`.

### 1.3 Build and CI

`deploy/Dockerfile`: one `build` stage (`:3`) publishing five outputs (`:20-24`), then five final
stages — `migrator` (`:27-30`), `hub` (`:33-36`), `api` (`:39-44`), `webapp-admin` (`:47-57`, takes
`ARG SOURCE_COMMIT` → `ENV CSX_COMMIT`, read at `Admin/Views/Shared/_Layout.cshtml:16`),
`webapp-studio` (`:64-69`, no `SOURCE_COMMIT` by decision).

`.github/workflows/deploy.yml:23-50`, job `images`: matrix
`[migrator, hub, api, webapp-admin, webapp-studio]` (`:26-27`), `docker/build-push-action@v6` with
`context: .`, `file: deploy/Dockerfile`, `target: ${{ matrix.target }}`,
`build-args: SOURCE_COMMIT=${{ github.sha }}` (`:36-43`), tags
`ghcr.io/blyn-ai/cryptosmithx-${{ matrix.target }}:latest` and `:${{ github.sha }}` (`:44-46`).

Two stale/broken facts found in the build files:

- `deploy/Dockerfile:2` says "the WebApp.Admin links src/web/ds". **Not found:** `src/web/` holds
  only `ds-studio/`, and `src/CryptoSmithX.WebApp.Admin/wwwroot/ds` is a real directory, not a
  symlink.
- `deploy/docker-compose.yml:109` sets `target: studio`; the Dockerfile has no such stage
  (`build, migrator, hub, api, webapp-admin, webapp-studio` at `:3,27,33,39,47,64`). Local
  `docker compose -f deploy/docker-compose.yml build studio` cannot resolve it.

### 1.4 Compose files and which host uses which

| File | Used by | Postgres | Services |
|---|---|---|---|
| `deploy/docker-compose.yml` | local dev (it `build:`s) | container `postgres:16`, `127.0.0.1:${POSTGRES_HOST_PORT:-5432}` (`:12-14`) | postgres, migrator, hub, api, webapp-admin, studio |
| `deploy/docker-compose.prod.yml` | **TEST** — `deploy/vps-deploy.sh:14-15` curls it from `raw.githubusercontent.com/.../main/deploy/docker-compose.prod.yml` each deploy | container `postgres:16`, `127.0.0.1:55432:5432` (`:18-21`), data at `/opt/cryptosmithx/postgres` (`:23`) | postgres, migrator, hub, webapp-admin, studio — **no `api`** |
| `deploy/docker-compose.vm.yml` | **PROD** — copied to `/opt/cryptosmithx/docker-compose.yml` (`.github/workflows/deploy.yml:92`) | **host** PostgreSQL 16 installed by `deploy/provision-host.sh postgres` (`docker-compose.vm.yml:5-9`), reached over external bridge `csx` (`:17-20`) | migrator, hub, api, webapp-admin, studio — **no `postgres`** |

Ports: TEST binds the WireGuard address — webapp-admin `10.8.0.1:7777:8080`, studio
`10.8.0.1:7778:8080` (`docker-compose.prod.yml:72,124`), public entrance traefik
`cryptosmithx.blynai.eu` with `PathPrefix(/studio)`, `priority: 100`, backend
`http://10.8.0.1:7778`, **no stripPrefix** (`deploy/traefik/cryptosmithx-webapp-studio.yml:22-33`;
that file is installed by hand, `:5-10`). PROD binds `10.11.12.28:{8080,7777,7778}`
(`docker-compose.vm.yml:52,71,107`). Local: `8080/8081/8082`, Studio at
`http://localhost:8082/studio` (`docker-compose.yml:68,91,121-125`).

Volume-path discrepancy: `docker-compose.yml:89` mounts `${CSX_DATA_ROOT:-./data}/webapp-keys`,
`deploy/.env.example:28` documents `webapp-admin-keys/`, `.github/workflows/deploy.yml:91`
pre-creates `/opt/cryptosmithx/webapp-admin-keys`, and both prod compose files mount
`/opt/cryptosmithx/webapp-keys` (`docker-compose.prod.yml:68`, `docker-compose.vm.yml:68`). The
directory CI creates is not the one any compose file mounts.

The compose file on the PROD host (`/opt/cryptosmithx/docker-compose.yml`, mtime 2026-09-06
20:59:19 UTC) still declares the pre-rename services `cryptosmithx-webapp` (line 56) and
`cryptosmithx-arena` (line 77) and the arena container does not exist; PROD's running containers are
pre-rename images (`ghcr.io/blyn-ai/cryptosmithx-webapp:latest`), a target the CI matrix no longer
builds (`.github/workflows/deploy.yml:27`).

### 1.5 Migrations

- Applied by exactly one place: `src/CryptoSmithX.Database/Migrator.cs:13-45` — session advisory
  lock `8_534_221_907_001`, `schema_version(version, applied_at)` created if absent, each unapplied
  embedded `.sql` applied in ordinal resource-name order in its own transaction together with its
  `insert into schema_version`. Version parsed from the filename prefix (`Migrator.cs:87`).
- Every other process verifies and refuses: `Migrator.VerifyAsync` (`Migrator.cs:52-75`), called at
  `Hub/Program.cs:44`, `Api/Program.cs:44`, `Admin/Program.cs:78`, `Studio/Program.cs:118`.
- Ordering enforced by compose `depends_on: service_completed_successfully`
  (`docker-compose.yml:32-34,45-47,60-62,77-79,110-112`; `.prod.yml:33-35,43-45,55-57,81-83`;
  `.vm.yml:32-34,43-45,58-60,79-81`).
- 27 files, `0001_initial.sql` … `0027_studio_rename.sql`. `0025_arena_reader.sql` and
  `0026_arena_metric_hour.sql` keep historical names; `0027_studio_rename.sql:36` renames the role
  `arena_reader` → `studio_reader` (grants ride the OID, `0027:17-21`).
- Partitions are not the migrator's: `Partitions.EnsureCurrentAndNextAsync` runs from the Hub at
  startup and from the daily retention pass (`ExchangeWorker.cs:68`).

### 1.6 Configuration and where secrets live (names only)

Four layers:

1. `appsettings.json` per project — only `ConnectionStrings:Database`, `Logging`, and (Api/Admin)
   `MarketData:SnapshotIntervalSeconds: 10`, `AllowedHosts` (`Hub/appsettings.json:1-10`,
   `Api/appsettings.json:1-15`, `Admin/appsettings.json:1-15`, `Studio/appsettings.json:1-12`). The
   Database project ships none and hard-codes a localhost fallback (`Database/Program.cs:6-13`).
2. Environment from compose: `ConnectionStrings__Database` (all), `Sentry__Dsn` (hub, api,
   webapp-admin, studio), `ASPNETCORE_ENVIRONMENT`, `Csx__Site` (`test` at
   `docker-compose.prod.yml:63`, `production` at `docker-compose.vm.yml:64`, read only at
   `Admin/Views/Shared/_Layout.cshtml:12`), `DataProtection__KeysDirectory` (webapp-admin),
   `CSX_COMMIT` (baked into the image, `Dockerfile:51-55`), `ASPNETCORE_HTTP_PORTS`.
3. **The database.** The Hub holds no static market-data options at all — see §2.4.
4. `.env` on the host, used only for compose interpolation.

Secret-bearing key names, values not read:
- TEST `/opt/cryptosmithx/.env` (mode 600, root): `POSTGRES_PASSWORD`, `SENTRY_DSN`.
  **`STUDIO_DATABASE_CONNECTION_STRING` is absent**, so Studio there runs on the compose default at
  `docker-compose.prod.yml:99`, which embeds a password committed to this public repository; the
  file's own header at `:85-98` records that this is deliberate and that the value is to be rotated
  (value not reproduced here).
- PROD `/opt/cryptosmithx/.env` (mode 600, root): `POSTGRES_PASSWORD`,
  `DATABASE_CONNECTION_STRING`, `CSX_NETWORK`, `STUDIO_DATABASE_CONNECTION_STRING`. **`SENTRY_DSN`
  is absent.** The file is generated once by `deploy/provision-host.sh:197-220` under `umask 077`.
- CI: `secrets.CSX_DEPLOY_SSH_KEY` (`deploy.yml:59`), `secrets.CSX_DEPLOY_HOST` (`:60`),
  `secrets.GITHUB_TOKEN` (`:35,61,87`); the GHCR token is piped over stdin (`:64-67`).
- `deploy/RUNBOOK.md` on the TEST host (not in git) names `PROD_WEBAPP_ADMIN_PASSWORD` as living in
  `.local/db.env`; `.local/` is gitignored (`.gitignore:2`). That file was not opened.
- `.gitignore:5-19` excludes `.env`, `*.pem`, `*.key`, `*.p12`, `*.pfx`, `id_rsa*`, `id_ed25519*`,
  `*.ovpn`, `wg*.conf`, `.netrc`, `.npmrc`, `.local/`, `.ai/`.

---

## 2. Collection policy model

Migration `0019_segment.sql:59,136,143,150,159` renamed `exchange`→`segment`,
`collection`→`dataset`, `collection_setting`→`dataset_setting`,
`exchange_collection`→`segment_dataset`, `exchange_collection_capability`→
`segment_dataset_capability`, and created a NEW `exchange` table (the venue). Current names used
throughout.

### 2.1 The four tables

**`dataset`** — `0014_collections.sql:57-66`, renamed `0019_segment.sql:136`, extended
`0020_history_interval_cascade.sql:59-61`:
`code` PK, `name`, `description`, `kind` not null `check in ('feed','derived')`, `default_mode` not
null `check in ('disabled','on_demand','collect')`, `default_interval_s` int null `> 0`,
`default_retention_days` int null `> 0` (null = never rotated, `0014:73-78`), `sort_order` smallint
default 100, `default_history_interval_s` int null `> 0`. `collector_status.collector` is an FK to
`dataset.code` (`0014_collections.sql:277-280`, renamed `0019_segment.sql:178`; the FK replaced a
CHECK at `0014_collections.sql:274-280`).

**`dataset_setting`** — `0014_collections.sql:81-90`: `(dataset_code, key)` PK, `value` text,
`kind check in ('int','text','int_list')`, `description`, `updated_at`, `updated_by`. The CHECK
constrains `kind`, not `value` (`0020_history_interval_cascade.sql:69-72`).

**`segment_dataset`** — the policy cell, `0014_collections.sql:113-124`, extended
`0020_history_interval_cascade.sql:63-65`: `(segment_code, dataset_code)` PK, `mode` not null
`check in ('disabled','on_demand','collect')`, `interval_s`, `retention_days`, `transport
check in ('rest','ws')`, `note`, `updated_at`, `updated_by`, `history_interval_s`. Trigger
`segment_dataset_notify AFTER UPDATE FOR EACH ROW` → `notify_segment_dataset_change()`
(`0019_segment.sql:206-215`). The matrix is **always full** — one row per (segment, dataset) pair
even where the venue cannot serve it (`0014_collections.sql:126-131`, backfilled `:218-227`).
`retention_days` is recorded per cell but **not honoured**: `DbSettings.cs:304-312` reads the
dataset level only, consumed at `Hub/Retention/RetentionJob.cs:41-49` (declared
`0014_collections.sql:41-47,132-137`).

**`segment`** — `code` PK, `name`, `description`, `status` not null default `'planned'`
`check in ('planned','enabled','disabled','maintenance','abandoned')` (`0005_exchange_admin.sql:15-17`),
`adapter`, `base_url`, `charts_url`, `quote_assets text[]` default `'{USD,USDT,USDC}'`,
`blacklist text[]`, `updated_by` (`0007_runtime_settings.sql:30-42`), `ws_url`
(`0010_ws_market.sql:29-30`), `exchange_code` FK→`exchange(code)`, `kind` not null
`check in ('spot','perp','futures','option','stock','synthetic')` (`0019_segment.sql:102-131`).
Trigger `segment_notify AFTER UPDATE`.

**`exchange`** (the venue) — `0019_segment.sql:85-93` plus `0021_venue_request_budget.sql:75-161`:
`request_budget_per_s` (1–10000), `max_concurrent_requests` (1–64), `request_budget_source
in ('documented','measured','assumed')`, `request_budget_note`, `request_budget_url`. **No notify
trigger** (`\d exchange` on TEST shows none).

Adjacent: `capability_key` (`0014_collections.sql:99-106`), `segment_dataset_capability`
(`:140-152`), `capability_log` (`:160-177`).

### 2.2 There is no enum of dataset kinds

Two different "kinds", neither a C# enum:

1. `dataset.kind` — a SQL CHECK (`0014_collections.sql:61`), carried as a plain `string`
   (`Hub/DbSettings.cs:185`) and compared to literals in Razor (`_FeedDialog.cshtml:68,140`,
   `_DataFeedsPanel.cshtml:71,89`).
2. The set of dataset **codes** — nine seeded rows (`0014_collections.sql:182-191`). There is **no
   CHECK on `dataset.code`** and **no C# enum**: `grep -rn --include='*.cs' "enum "` over `src/`
   returns only `Weex/WeexBookBuilder.cs:284`, `Streaming/ConnectionLog.cs:155`,
   `Market/InstrumentStatus.cs:8`, `Kraken/KrakenBookBuilder.cs:158`, `Binance/BinanceBookBuilder.cs:442`,
   `Studio/Models/Cells.cs:7,22,50`, `Studio/Data/Verdicts.cs:19,34,53,137`.

The real "enum" is a set of string literals in these places:
`ExchangeWorker.cs:36` (the gate), `:197` (`Bodies["discovery"]` is mandatory), `:299-306`
(`BuildBodies`), `:488-489` (`select code from dataset where code <> 'rollup'`);
`Connectors/IExchangeMarketData.cs:23,60`; the five adapter capability lists
(`Kraken/KrakenFuturesMarketData.cs:41-48`, `Weex/WeexFuturesMarketData.cs:49-56`,
`Hyperliquid/HyperliquidMarketData.cs:50-57`, `Binance/BinanceUsdmMarketData.cs:65-72`,
`Fake/FakeExchangeMarketData.cs:44-50`); `Admin/Data/ExchangeStore.cs:153,181` (the five-code `in`
lists — verified by me at those lines), `:405-450` (per-collector run-contents SQL switch),
`:483-497` (per-collector empty-run note); `Admin/Areas/Admin/Views/Exchanges/_DataFeedsPanel.cshtml:14-28`
(the output grouping, verified by me); `Runs.cshtml:6`
(`new[] { "snapshot","depth","candles","discovery","funding","rollup" }`, verified);
`Studio/Live/LiveRelevance.cs:33-66`; `Studio/Data/SegmentFreshnessStore.cs:94-112`;
`Hub/Ingestion/DiscoveryCollector.cs:152-153`.

Mode literals likewise have no enum (`DbSettings.cs:265-268`, `ExchangeWorker.cs:256`,
`Admin/Data/FeedStore.cs:200-201`, `_FeedDialog.cshtml:6-7,111`, `Datasets/Index.cshtml:22,35`).

**`on_demand` has no implementation.** `ExchangeWorker.DesiredCollectors` (`:251-258`) starts a loop
only on `== "collect"`; nothing reads `on_demand`. It is a selectable chip
(`_FeedDialog.cshtml:111`) behaviourally identical to `disabled`.

### 2.3 Intervals, the keep cascade, other parameters

- **Poll interval:** `segment_dataset.interval_s` → `dataset.default_interval_s`, throwing if
  neither is set (`DbSettings.cs:270-281`; documented `0014_collections.sql:126-131`).
- **Keep interval:** SQL function `keep_interval_s(segment, dataset)`
  (`0020_history_interval_cascade.sql:104-112`) =
  `greatest(coalesce(sd.history_interval_s, d.default_history_interval_s, sd.interval_s,
  d.default_interval_s), coalesce(sd.interval_s, d.default_interval_s))`. C# mirror
  `DbSettings.cs:283-302`.
- **Archive interval:** `archive_interval_s(segment, dataset)`
  (`0020_history_interval_cascade.sql:120-125`) returns `keep_interval_s(segment,'snapshot')` for
  `snapshot` and `depth`, else the dataset's own — because depth only writes
  `market_snapshot_latest` and reaches `market_snapshot` when the snapshot collector keeps
  (`:114-119`). Consumers: `Admin/Data/FeedStore.cs:49` and `:106` (verified by me),
  `Admin/Data/ExchangeStore.cs:471-478` (`keep_interval_s`).
- The "Keep every (s)" field is offered only where `dataset.default_history_interval_s is not null`
  (`_FeedDialog.cshtml:35,174-180`, verified by me; rule stated `0020:26-30,89-92`). Today only
  `snapshot` has one (60), so exactly one dataset shows the field.
- The superseded `dataset_setting('snapshot','history_interval_s')` row from
  `0018_snapshot_history_interval.sql:25-30` still exists on both hosts and is no longer read; `0020`
  only rewrote its description (`0020:73-87`), keeping it as rollback insurance (`:45-56`).
- **Non-interval parameters** live in `dataset_setting`, seeded `0014_collections.sql:194-198`, read
  through `DbSettings.DatasetSettingInt` / `DatasetSettingIntList` (`DbSettings.cs:314-329`), both
  throwing on a missing key. Consumers: `DiscoveryCollector.cs:152-153`
  (`delist_after_missed_discoveries`), candles/funding backfill and rollup timeframes
  (`DbSettings.cs:317,320`).
- The global `setting` table now carries only what did not move: `0014_collections.sql:266-272`
  deletes `snapshot_interval_s, candle_interval_s, depth_interval_s, discovery_interval_min,
  funding_interval_min, candle_backfill_hours, funding_backfill_hours, snapshot_retention_days,
  derived_timeframes, delist_after_missed_discoveries` (originally seeded
  `0007_runtime_settings.sql:84-95`) — verified by me. What remains and is read live is
  `ws_stale_after_s`, `ws_crosscheck_interval_s`, `ws_crosscheck_drift_bps`
  (`0010_ws_market.sql:38-42`; read at `DbSettings.cs:251-253`). Live TEST values: 30, 60, 50.

### 2.4 How the Hub reads policy, and what a toggle does

**The Hub does not LISTEN.** `grep -rn --include='*.cs' "csx_live\|LISTEN"` over `src/` matches only
`Studio/Live/LiveNotifier.cs` and `Admin/Live/LiveNotifier.cs`. Reload is by timer:

- `DbSettings.cs:17` — `Ttl = TimeSpan.FromSeconds(30)`; `CurrentAsync` returns the cache inside the
  TTL (`:38-62`); `LoadAsync` (`:64-132`) re-reads `setting`, `exchange`, `segment`, `dataset`,
  `dataset_setting` and the whole `segment_dataset` matrix in one connection.
- `ExchangeWorker.cs:27` — `ReconcileInterval = 30 s`; supervisor loop `:88-101`.

Toggling a feed in the admin, traced:

1. `POST /Admin/Exchanges/Feed/{id}` → `ExchangesController.cs:156-186` → `FeedStore.SaveAsync` →
   `update segment_dataset ... where segment_code = @SegmentCode and dataset_code = @DatasetCode`
   (`FeedStore.cs:209-217`, verified by me).
2. That UPDATE fires `segment_dataset_notify` → `pg_notify('csx_live', …)`
   (`0019_segment.sql:206-215`), consumed only by the two web apps for repaint; the Hub ignores it.
3. Within ≤30 s the settings cache expires, within ≤30 s `ReconcileAsync` runs
   (`ExchangeWorker.cs:96,145`) and calls `ReconcileCollectors` (`:222,227-247`), which cancels the
   CTS of loops no longer desired (`:232-237`) and starts newly desired ones (`:243-247`).
   **Hot reload of one loop; the rest are undisturbed (`:19-21`). No restart.**
4. Worst-case latency ≈ 30 s + 30 s + the loop's own interval.
5. An **interval** change needs no reconcile: `CollectorLoop` re-evaluates `_interval()` every
   iteration (`CollectorLoop.cs:40,46-49,143`), reading `_settings.Latest`
   (`ExchangeWorker.cs:310-311`).
6. A **segment status** change goes through the same 30 s reconcile
   (`ExchangeWorker.cs:146-155,157-201`); disabling cancels the per-segment CTS and kills its WS
   feeds (`:159-161`).
7. **Exception:** the venue request budget is not hot-reloaded. `GateFor` (`:266-286`) reuses the
   existing `VenueGate` and logs "the change applies on restart" (`:278-282`); the registry is
   `GetOrAdd` (`Connectors/Pacing/VenueGates.cs:18-34`, reasoning `:11-16`).
8. `rollup` and `retention` are service-wide loops started unconditionally
   (`ExchangeWorker.cs:78-85`) under `ServiceExchange = "fake"` (`:31`); their `segment_dataset.mode`
   is never consulted. Retention runs on a 24 h timer (`:323-353`).

### 2.5 Adding a NEW dataset kind today — the complete file list

There is no "add dataset" UI and no "add dataset" code path. Derived from the hard-coded sites above:

**Database:** a new `src/CryptoSmithX.Database/Migrations/00NN_*.sql` that (a) inserts into
`dataset`, (b) inserts one `segment_dataset` row per existing segment (full-matrix invariant,
pattern `0014_collections.sql:218-227`), (c) inserts `segment_dataset_capability` rows per new cell ×
capability key (pattern `0014_collections.sql:252-254`), (d) any `dataset_setting` rows, (e) a
`grant select` to `studio_reader` if it adds a table (pattern `0026_arena_metric_hour.sql:100`; no
`alter default privileges` exists, by decision `0025_arena_reader.sql:49-53`).

**Hub:** `ExchangeWorker.cs:36` (`KnownCollectorDatasets`), `:291-307` (`BuildBodies`), a new
collector class beside `SnapshotCollector.cs` / `DepthCollector.cs` / `CandleCollector.cs` /
`FundingCollector.cs` / `DiscoveryCollector.cs`, and — if the venue call does not exist — a method on
`Connectors/IExchangeMarketData.cs` plus a `Capabilities` entry in **every** adapter (a missing entry
writes `we_implement=false` and the loop never starts: `ExchangeWorker.cs:479-497` and `:251-258`).
If it distinguishes asking from keeping, set `dataset.default_history_interval_s` and honour
`SettingsSnapshot.HistoryInterval` (`DbSettings.cs:290-302`).

**Admin:** `_DataFeedsPanel.cshtml:14-28` (else the row falls into `unproduced`, `:34`, rendered
`:140-171` as "Nothing produced / no collector"), `ExchangeStore.cs:405-450` and `:483-497`,
`Runs.cshtml:6`; only if it needs a field on the legacy settings form, `ExchangeStore.cs:148-193`,
`:257-263`, `ExchangesController.cs:60-108`, `Details.cshtml:83-89`, `Models/ViewModels.cs`.

**Studio:** `Live/LiveRelevance.cs:33-66` (default `_ => false`), plus everything in §6.4.

**Tests that hard-code the set:** `tests/CryptoSmithX.MarketData.Hub.Tests/CollectorSelectionTests.cs:12,40,74`;
`tests/CryptoSmithX.MarketData.Connectors.Tests/WeexWsTests.cs:333` and
`BinanceUsdmMarketDataTests.cs:337`.

### 2.6 Defaults, and how each deployment departs from them

`dataset` is **byte-identical to the seed on both TEST and PROD**:

| code | kind | default_mode | default_interval_s | default_history_interval_s | default_retention_days | sort |
|---|---|---|---|---|---|---|
| discovery | feed | collect | 3600 | — | — | 10 |
| snapshot | feed | collect | 10 | 60 | 90 | 20 |
| depth | feed | collect | 60 | — | 90 | 30 |
| candles | feed | collect | 60 | — | — | 40 |
| funding | feed | collect | 3600 | — | — | 50 |
| rollup | derived | collect | 60 | — | — | 60 |
| trades | feed | disabled | — | — | — | 70 |
| open_interest | feed | disabled | — | — | — | 80 |
| liquidations | feed | disabled | — | — | — | 90 |

`dataset_setting`, identical on TEST and PROD, all at seeded values: `candles/backfill_hours=3`,
`discovery/delist_after_missed_discoveries=3`, `funding/backfill_hours=168`,
`rollup/derived_timeframes=5,15,60,240,720,1440`, `snapshot/history_interval_s=60` (superseded).

`segment_dataset` on TEST: 45 rows (5 segments × 9 datasets), every row still
`updated_by = '0014 migration'`. For `kraken-futures` **every `interval_s`, `history_interval_s` and
`transport` is NULL** — the cascade falls through to the dataset defaults; modes are
`candles/depth/discovery/funding/rollup/snapshot = collect`, `liquidations/open_interest/trades =
disabled`. The seeded note on `open_interest` reads "Carried inline in the snapshot ticker on every
venue we run today" (`0014_collections.sql:220-224`).

The survey that owned this area was truncated before it listed the per-cell departures on
binance-usdm and before it covered PROD's `segment_dataset` in full. **Marked incomplete:** the
non-Kraken cells that depart from NULL are not enumerated here.

---

## 3. Adapter seam

### 3.1 `IExchangeMarketData` — the whole contract

`src/CryptoSmithX.MarketData.Connectors/IExchangeMarketData.cs`, 60 lines:

| Member | Signature | line |
|---|---|---|
| `SegmentCode` | `string SegmentCode { get; }` (comment says `exchange.code`; it is matched against `segment.code`, e.g. `DiscoveryCollector.cs:58`) | 13 |
| `Capabilities` | `IReadOnlyList<DatasetCapability>` | 23 |
| discovery | `Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ct)` | 26 |
| ticker | `Task<IReadOnlyList<Ticker>> GetTickersAsync(ct)` | 29 |
| candles | `Task<IReadOnlyList<Candle>> GetCandles1mAsync(string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, ct)` | 32-36 |
| funding | `Task<IReadOnlyList<FundingRate>> GetFundingHistoryAsync(string exchangeSymbol, DateTimeOffset from, DateTimeOffset to, ct)` | 42-46 |
| depth | `Task<Depth?> GetOrderBookAsync(string exchangeSymbol, ct)` | 54 |

`public sealed record DatasetCapability(string DatasetCode, string TransportsUs)` — `:60`;
`TransportsUs` is comma-joined (`:57-59`).

**No interface method exists for** trades, liquidations, open-interest history, mark/index candles,
funding schedule / predicted / next time, instrument-spec versions, per-feed health, or order-book
sequence numbers. Order-book sequence exists only inside `Kraken/KrakenBookBuilder.cs:170`
(`public long Seq`) and never crosses the seam — `Depth` carries no sequence. **Not found.**

DTOs, all in `Connectors/Market/`:

- `Ticker.cs:13-27` — 14 fields, all non-nullable except `Depth? Depth`:
  `ExchangeSymbol, ReceivedAt, LastPrice, BidPrice, AskPrice, BidSize, AskSize, MarkPrice,
  IndexPrice, FundingRate, Turnover24h, OpenInterest, OpenInterestAt, Depth`. Semantics at `:7-12`.
  "Missing" travels as `double.NaN` and `SnapshotCollector.cs:90-101` drops the whole row
  (`BidSize`/`AskSize` are not NaN-checked, `:95-97`).
- `Depth.cs:9-16` — `(Bid10Bps, Ask10Bps, Bid25Bps, Ask25Bps, Bid50Bps, Ask50Bps, At)`, all six
  bands nullable, null = "not measured" (`:3-8`); cumulative quote notional within a bps band of mid.
- `Candle.cs:8-16` — `(ExchangeSymbol, OpenTime, Open, High, Low, Close, Volume, int? TradeCount)`;
  adapters only produce 1-minute bars (`:5-6`).
- `FundingRate.cs:12-15` — `(ExchangeSymbol, FundingTime, double Rate)`, rate = fraction of notional
  per interval (`:8-11`).
- `Instrument.cs:20-32` — 12 fields; nullable only `MinNotional` (`:16`) and `ListedAt` (`:17`).
  **No version / valid-from field**, and discovery upserts in place
  (`DiscoveryCollector.cs:91-148`) — **not found**.
- `InstrumentStatus.cs:8-15` — `Trading, PostOnly, ReduceOnly, Halted, Delisted`; `Delisted` is
  assigned by the store, never returned by an adapter (`:5-6`, assignment
  `DiscoveryCollector.cs:154-166`).

Declared capabilities: Kraken `KrakenFuturesMarketData.cs:41-48` (discovery=rest, snapshot=rest,ws,
depth=rest,ws, candles=rest, funding=rest); Hyperliquid `:50-57` identical; WEEX
`WeexFuturesMarketData.cs:49-56` (snapshot rest only); Binance `BinanceUsdmMarketData.cs:65-72`
(snapshot rest only); Fake `FakeExchangeMarketData.cs:44-50` (no `depth` entry —
`GetOrderBookAsync` always returns null, `:223-229`).

### 3.2 Registration — the switch

There is **no DI registration of adapters**: `Hub/Program.cs:26-32` registers only
`TimeProvider.System`, `Db`, `DbSettings`, `AddHostedService<ExchangeWorker>()`.

`ExchangeWorker.cs:531-541`:

```
private IExchangeMarketData Build(ExchangeConfig config, VenueGate gate, CancellationToken ct) => config.Adapter switch
{
    "fake" => new FakeExchangeMarketData(),
    "kraken-futures" => BuildKraken(config, ct),
    "weex-futures" => BuildWeex(config, gate, ct),
    "hyperliquid" => BuildHyperliquid(config, gate, ct),
    "binance-usdm" => BuildBinance(config, gate, ct),
    _ => throw new InvalidOperationException(...),
};
```

Key = `segment.adapter` (`DbSettings.cs:90` → `ExchangeConfig.Adapter`, `:150`). Only
`Status == "enabled"` segments are built (`ExchangeWorker.cs:149-151`) — which is why binance-usdm at
`maintenance` on TEST runs nothing. A build failure is logged, not fatal (`:174-180`).

### 3.3 WS feeds — construction, lifetime, fallback

All four WS feeds are constructed inside `ExchangeWorker`'s per-adapter builders, never in DI.
Lifetime = the per-exchange CTS created at `ExchangeWorker.cs:166` (created before the adapter
precisely so a socket dies with it, `:164-165`), cancelled when the exchange leaves `enabled`
(`:156`) or on shutdown (`:107-112`).

- Kraken: `BuildKraken` `:546-563` — `KrakenFuturesClient(baseUrl, chartsUrl)` (`:550`, both throw if
  null `:548-549`), WS built only when `ws_url` is non-blank (`:553`), knobs read once from
  `_settings.Latest` (`:555`), `ws.Start(ct)` (`:559`), `new KrakenFuturesMarketData(client, ws)`
  (`:562`) with `ws` possibly null → pure REST (`KrakenFuturesMarketData.cs:30-34`). **Because the
  knobs are read at build time, editing `ws_stale_after_s` etc. applies only after the exchange is
  rebuilt.**
- Hyperliquid `:597-616` (a REST book cycler always runs, `:602-603`), WEEX `:573-592` (an
  open-interest feed always runs, `:578-579`), Binance `:631-651` (same shape, `:636-637`). WEEX and
  Binance WS feeds take the `VenueGate`; **Kraken's does not** — see §4.4.

Fallback, Kraken being the only venue whose ticker is WS-served:
`KrakenFuturesMarketData.cs:89-92` (WS tickers if fresh) else `:95` REST;
`:192-195` (WS depth) else `:197-200` REST + `DepthMath.Compute`. Health gate
`KrakenWsFeed.cs:96-97`: `_conn.Connected && _tickers.FreshCount(_staleAfter) >= max(1, symbols/2)`.
Stale individual symbols are omitted (`Streaming/MarketCache.cs:38-51`). A dirty or unseeded book is
refused (`KrakenBookBuilder.cs:106-109`). Contract pinned by
`tests/CryptoSmithX.MarketData.Connectors.Tests/KrakenWsFallbackTests.cs:18-40`.
**WS feeds write no `collector_run` rows** — `CollectorAttempt.Transport` defaults to `"rest"` and
`CollectorLoop.cs:13-17` states the WS feeds do not report runs at all yet.

Socket plumbing: `Streaming/WsConnection.cs:14` (IdleTimeout 30 s `:16`, MaxBackoff 30 s `:17`,
`Connected` `:34`, `RunAsync` `:38`, `SendAsync` `:102`); `Streaming/ConnectionLog.cs:23` is used only
by `BinanceWsFeed` (`BinanceWsFeed.cs:145`).

### 3.4 Loop shape, concurrency, pacing

`CollectorLoop.cs:85-153`: timestamp (`:89`) → `_body(ct)` (`:95`) → success builds a
`CollectorAttempt` (`:97-99`) → exceptions increment `ConsecutiveFailures` and are swallowed
(`:107-131`) → `_report` (`:136`) → `Task.Delay(Jitter(DelayFor(_interval(), failures)))` (`:143-146`).
Backoff `interval × min(1 << failures, 5)` (`:74-83`, cap `:29`); jitter ±10 % (`:159-160`); log level
flips to Error at 3 consecutive failures (`:36`, used `:120-125`); error text truncated to 500 chars
(`:162-166`).

Concurrency: one loop per (segment, dataset) (`ExchangeWorker.cs:240-243`). Inside a pass,
`DepthCollector` fans out to `min(gate.MaxConcurrentRequests, items.Count)` workers
(`DepthCollector.cs:84-85,191-194`) with one serialized write lane (`:82,106-114`);
`CandleCollector.cs:76` and `FundingCollector.cs:74` are strictly serial; `SnapshotCollector` is one
batched adapter call (`:42`) then a serial loop inside one transaction (`:80-218`);
`DiscoveryCollector` is one call plus a serial upsert in one transaction (`:50,89-148,168`).

`VenueGate` (`Connectors/Pacing/VenueGate.cs:24`) enforces both a rate floor (`:57,84-100`) and a
concurrency semaphore (`:58,81`), keyed on `exchange.code`, never the segment
(`ExchangeWorker.cs:266-286`). `AcquireAsync` returns a `VenueLease` (`:79-112`), must not be nested
(`:21-22`), release idempotent (`:153-159`). `Penalize(cooldown)` folds a 429 into the same schedule,
default 10 s (`:34,121-148`) — and `:26-33` records that the 10 s is **our** choice, not a vendor
number. Callers: `DepthCollector.cs:120-123`, `CandleCollector.cs:135-138`,
`FundingCollector.cs:115-118`, each only on `HttpRequestException { StatusCode: TooManyRequests }`.
**`SnapshotCollector` and `DiscoveryCollector` take no gate at all** (`SnapshotCollector.cs:30-38`,
`DiscoveryCollector.cs:21-26`).

Live budgets (TEST `exchange` table): kraken 20 req/s × 8 `assumed`; binance 16 × 8 `documented`;
weex 200 × 8 `documented`; hyperliquid 10 × 4 `assumed`; fake 1000 × 16 `assumed`.
The Kraken survey flagged the binance row as differing from the `0021` seed (20 / `assumed`,
`0021_venue_request_budget.sql:127`) and could not say why. **Resolved by me:**
`0023_binance_usdm.sql:195-199` sets `request_budget_per_s = 16`,
`request_budget_source = 'documented'`, `request_budget_url =
https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info`. Not an operator
edit.

### 3.5 The path of one observation

- **Ticker (`snapshot`)** — loop `ExchangeWorker.cs:240-243`; interval `:310-311` →
  `DbSettings.cs:272-281` → 10 s; body `:302`; adapter `SnapshotCollector.cs:42` →
  `KrakenFuturesMarketData.cs:85-132`; instrument lookup `SnapshotCollector.cs:18-20`; NaN guard
  `:90-101`; upsert `:132` (`market_snapshot_latest`, DDL `0001_initial.sql:119-142`); keep-insert
  `:183` `on conflict … do nothing` `:197` (`market_snapshot`, DDL `0001_initial.sql:177-200`), gated
  on the wall-clock bucket `:64-75` with partitions ensured `:74` → `Database/Partitions.cs:15`;
  status `CollectorLoop.cs:136` → `ExchangeWorker.cs:360` (`collector_status`), `:399`
  (`collector_run`), `:432/:448` (`collector_gap`).
- **Depth** — body `:303`, built `:295` with the gate; targets `DepthCollector.cs:34-40`
  (`collect = true and status = 'trading'`); lease `:96` → `GetOrderBookAsync` `:98`; write `:209`
  `update market_snapshot_latest set …` (`:206-227`). First failure cancels the herd and is
  re-thrown (`:178-186,199`), so a partial pass records `ok=false`.
- **Candles** — body `:304`; window floor `now − candles/backfill_hours` (3 h,
  `CandleCollector.cs:58`); targets `:23-38` joined to `max(open_time) where timeframe = 1`; lease
  `:91` → `GetCandles1mAsync` `:93`; write `:106` … `on conflict … do update` `:110`
  (`market_candle`, DDL `0001_initial.sql:218-235`); per-symbol try/catch `:80-142`, the pass fails
  only if every symbol failed (`:145-148`).
- **Funding** — body `:305`; floor `now − funding/backfill_hours` (168 h,
  `FundingCollector.cs:57`); targets `:25-36`; lease `:89` → `GetFundingHistoryAsync` `:91`; write
  `:98` … `on conflict (exchange_instrument_id, funding_time) do nothing` `:100` — verified by me
  (one survey cited `:98`, another `:100`; both are inside the same statement).
- **Discovery** — one adapter call `DiscoveryCollector.cs:50`; asset auto-register `:83-84`;
  instrument upsert `:93` … `on conflict (segment_code, exchange_symbol) do update` `:103`; delist
  `update exchange_instrument` `:156-170` where `last_seen_at < cutoff`, cutoff = discovery interval
  × `delist_after_missed_discoveries` (3).

---

## 4. Kraken Futures today

### 4.1 Every REST endpoint the adapter calls

All in `Connectors/Kraken/KrakenFuturesClient.cs`; two bases injected from the DB row
(`segment.base_url`, `segment.charts_url`, ctor `:22-34`). Live on both hosts:
`base_url = https://futures.kraken.com`, `charts_url = https://futures.kraken.com/api/charts/v1`.

| # | file:lines | URL | caller |
|---|---|---|---|
| 1 | `KrakenFuturesClient.cs:37-56` | `/derivatives/api/v3/instruments` | `KrakenFuturesMarketData.GetInstrumentsAsync` (`:50-83`) and `KrakenWsFeed.RefreshSymbolsAsync` (`KrakenWsFeed.cs:279-312`, every 5 min) |
| 2 | `:58-59` | `/derivatives/api/v3/tickers` | `GetTickersAsync` REST fallback (`KrakenFuturesMarketData.cs:95`) and `KrakenWsFeed.CrosscheckAsync` (`KrakenWsFeed.cs:233`) |
| 3 | `:61-62` | `/api/charts/v1/trade/{symbol}/1m?from={s}&to={s}` | `GetCandles1mAsync` (`KrakenFuturesMarketData.cs:137-138`) |
| 4 | `:67-68` | `/derivatives/api/v4/historicalfundingrates?symbol={symbol}` | `GetFundingHistoryAsync` (`:169`) |
| 5 | `:70-71` | `/derivatives/api/v3/orderbook?symbol={symbol}` | `GetOrderBookAsync` REST fallback (`:197`) |

Five endpoints, no more. The charts path is hard-coded to `tick_type = trade`
(`KrakenFuturesClient.cs:62` is the only charts URL in the project) — no `mark`, no `spot`.
Error handling is `EnsureSuccessStatusCode()` only (`:40`, `:76`), deliberately (`:6-12`): no retry,
no backoff, no logging.

### 4.2 Every WS channel it subscribes to

WS URL from `segment.ws_url = wss://futures.kraken.com/ws/v1` (seeded `0010_ws_market.sql:32`; live on
both hosts). Feed built only when `ws_url` is non-blank (`ExchangeWorker.cs:553`).

Exactly two feeds, `KrakenWsFeed.cs:321`: `foreach (var feed in new[] { "ticker", "book" })`.
`ticker` handled `:155-156,168-192`; `book` arrives as `book_snapshot` (`:158-159,194-206`) and `book`
deltas (`:161-162,208-229`). Subscribe wire format `:327`
(`{"event":"subscribe","feed":"<feed>","product_ids":[…]}`), chunked 200 symbols per message
(`:323-325`); single-symbol form `:332-333`. `event: error` / `event: alert` frames are logged and
discarded (`:137-146`).

**No `trade` channel, no liquidation channel, no `heartbeat`** — grep of
`Connectors/Kraken/` for a `trade` feed name matches only the candles REST path
(`KrakenFuturesClient.cs:62`). Not found as a subscription.

### 4.3 Book builder and resync

`Connectors/Kraken/KrakenBookBuilder.cs` (175 lines).

- Sequencing rule, `:62`: `if (seq != book.Seq + 1) { book.Dirty = true; return DeltaResult.Gap; }`
  — one `seq` per product, shared across both sides (side only selects the dictionary, `:68`;
  contrast noted in `WeexBookBuilder.cs:13` and `BinanceBookBuilder.cs:14`).
- Dirty comes from three paths: a sequence gap (`:62-66`), explicit `MarkDirty` (`:76-82`, called
  only from the REST cross-check `KrakenWsFeed.cs:255`), and never-seeded/absent (`:85-86,153-156`).
  A delta before any snapshot returns `Ignored`, not `Gap` (`:50-53`).
- Reseed only through `ApplySnapshot` (`:18-44`), which clears both sides, drops `qty <= 0`
  (`:31,35`) and sets `Seq`, `Dirty=false`, `Seeded=true` (`:39-42`).
- Resync on gap: `KrakenWsFeed.cs:224-228` → `ResyncBookAsync` (`:266-277`) is an
  **unsubscribe-then-resubscribe of `book` for that one symbol** — there is no REST reseed on the
  Kraken WS path. Debounced 5 s per symbol (`:20,269-274`, `_lastResync` `:31`).
- Cross-check `:231-264` on `_crosscheckInterval` (`:113`, live 60 s) fetches REST `/tickers`,
  compares `restMid` against the WS **book's** top-of-book (`KrakenBookBuilder.cs:129-151`, reason
  `KrakenWsFeed.cs:243-245`) and marks dirty past `_driftBps` (live 50) (`:251-257`).
- Serving gates: `TryGetDepth` refuses a dirty or unseeded book and **age is deliberately not a
  gate** (`KrakenBookBuilder.cs:90-94,95-125`); feed-level `Healthy` `KrakenWsFeed.cs:96-97`.

### 4.4 Rate limits

- Budget in force: `exchange.request_budget_per_s = 20`, `max_concurrent_requests = 8`,
  `request_budget_source = 'assumed'` for venue `kraken` (seed
  `0021_venue_request_budget.sql:88-100`; live on TEST).
- The migration says why it is assumed (`0021:49-54`): Kraken's public market endpoints are outside
  the documented budget, so the ceiling is ours; the row note `:88-100` retracts an earlier
  justification and states the 20 req/s was never measured under real REST traffic.
  `request_budget_url = https://docs.kraken.com/api/docs/guides/futures-rate-limits` (`0021:100`).
- **No 429 handling exists in the Kraken connector** — grep of `Connectors/Kraken/` for
  `429|TooManyRequests|Penalize`: not found. Handling lives only in the Hub collectors
  (`DepthCollector.cs:120-123`, `CandleCollector.cs:135-138`, `FundingCollector.cs:115-118`), all
  calling `_gate.Penalize()` with no `Retry-After` read; the default penalty is 10 s
  (`VenueGate.cs:34,148`), documented as our own conservative number (`:26-33`).
- **Which Kraken calls are paced:** `BuildKraken` (`ExchangeWorker.cs:546-563`) is the only builder in
  the switch that does not take the `VenueGate` (compare `:573`, `:597`, `:631`). Gated: depth
  (`DepthCollector.cs:96`), candles (`CandleCollector.cs:91`), funding (`FundingCollector.cs:89`).
  **Ungated:** discovery and snapshot (constructed without a gate, `ExchangeWorker.cs:291-296`), and
  the WS feed's own REST traffic — `/instruments` every 5 min (`KrakenWsFeed.cs:19,112,281`) and
  `/tickers` every 60 s (`:113,233`); `KrakenWsFeed`'s ctor (`:36-49`) has no gate parameter, unlike
  `WeexWsFeed.cs:509-513` and `BinanceWsFeed.cs:800-804`.
- A 429 reaches `collector_gap` as `cause = 'rate_limited'` through the string classifier
  `ExchangeWorker.CauseOf` (`:463-472`), matching on "429" or "Too Many Requests" in the message.
- Venue-side statement, fetched during the survey:
  "Public endpoints do not have a cost and therefore do not count against any rate limiting budget."
  / "For `/derivatives` endpoints, clients can spend up to 500 every 10 seconds."
  (https://docs.kraken.com/api/docs/guides/futures-rate-limits/). No cost-table entry exists on that
  page for tickers/orderbook/history/instruments/funding/charts — **not found**.

### 4.5 Symbol universe

- Adapter filter: `PF_` flexible perpetuals only, non-expired —
  `KrakenFuturesMarketData.cs:24` (`PerpPrefix = "PF_"`), applied `:57`; scope statement `:22-23`
  (inverse `PI_` and dated `FI_`/`FF_` are skipped). Same filter repeated at `:101`,
  `KrakenWsFeed.cs:283` and `:237`.
- Hub filter: `segment.quote_assets` / `segment.blacklist` (`DiscoveryCollector.cs:41-46`); live
  values `{USD,USDT,USDC}` and `{}` on both hosts, and every Kraken `PF_` instrument is USD-quoted,
  so this filter removes nothing on Kraken today.
- Depth runs only over `collect = true and status = 'trading'` (`DepthCollector.cs:33-39`).

Counts, 2026-09-07:

| | TEST | PROD |
|---|---|---|
| kraken-futures `trading` | 276 | 276 |
| kraken-futures `delisted` | 4 | 0 |
| rows for the segment | 280 | 276 |
| all `collect = true` | yes | yes |
| `instruments_expected` discovery/snapshot/depth/funding | 276 | 276 |
| `instruments_expected` candles | 828 | 828 |

Kraken collectors are healthy on both hosts (`collector_status.consecutive_failures = 0`
everywhere, last successes within minutes of the query). TEST retains a stale
`collector_status.last_error` from 2026-09-03 on snapshot and depth:
`HttpRequestException: Response status code does not indicate success: 503 (Service Unavailable).`

### 4.6 Deployment configuration for Kraken

The URL columns live on **`segment`**, not `exchange`, since `0019_segment.sql`.

TEST (`schema_version` 27): `kraken-futures | kraken | kraken-futures |
https://futures.kraken.com | https://futures.kraken.com/api/charts/v1 |
wss://futures.kraken.com/ws/v1 | enabled`. Other rows: hyperliquid enabled
(`https://api.hyperliquid.xyz`, `wss://api.hyperliquid.xyz/ws`), weex-futures enabled
(`https://api-contract.weex.com`, `wss://ws-contract.weex.com/v3/ws/public`), binance-usdm
**maintenance** (`https://fapi.binance.com`, `wss://fstream.binance.com/public/stream`), fake
disabled.

PROD (`schema_version` 20): kraken-futures identical URLs and `enabled`; hyperliquid enabled;
weex-futures enabled but **`ws_url` empty** (0022 unapplied); binance-usdm `planned` with no URLs.
**PROD's `exchange` table has only 7 columns** (`code, name, description, website_url, created_at,
updated_at, updated_by`) — the whole 0021 budget block does not exist there.

### 4.7 Kraken quirks recorded in code

- **`XBT` is not folded to `BTC` in the adapter** — `KrakenFuturesMarketData.cs:65-66` and `:9-10`;
  raw base parsing `:203-208`; the `asset_alias` table resolves it downstream.
- **The REST ticker's funding rate is ABSOLUTE; the WS ticker's is RELATIVE.**
  `KrakenFuturesMarketData.cs:116-122` divides by mark and emits `double.NaN` when mark is absent
  (`:122`); `KrakenDtos.cs:47`. The WS path does not divide — `KrakenWsFeed.cs:187` uses
  `t.RelativeFundingRate` straight (`KrakenWsDtos.cs:23`). The same logical field arrives in two
  different units on the two transports.
- **Funding history is v4 and unbounded** — `KrakenFuturesClient.cs:64-66` (v3 404s for `PF_`);
  windowing is client-side (`KrakenFuturesMarketData.cs:171-179`), so a funding pass costs a
  full-series fetch per symbol whatever the window.
- **Candles live on a different host and path** — `KrakenFuturesClient.cs:9-11`.
- **No trade count** — `KrakenFuturesMarketData.cs:11-12`, `:160` (`TradeCount: null`),
  `Market/Candle.cs:7`, test `KrakenFuturesMarketDataTests.cs:68`.
- **No minimum quantity and no minimum notional** — `KrakenFuturesMarketData.cs:71-73`,
  `MinNotional: null` `:75`, `Market/Instrument.cs:16`.
- **`received_at` is the VENUE's clock on both transports** — `SnapshotCollector.cs:202-210`;
  downstream consequences documented at `Studio/Data/Freshness.cs:49`, `Studio/Format.cs:123`,
  `Admin/Data/DashboardStore.cs:31` and `:143`, `Studio/Models/StudioModels.cs:190-197`, with
  regression tests `tests/CryptoSmithX.WebApp.Admin.Tests/StaleThresholdTests.cs:21` and
  `tests/CryptoSmithX.WebApp.Studio.Tests/FreshnessTests.cs:116-124` (the latter is being edited by
  the concurrent workflow).
- **Open interest shares the ticker's timestamp** — `KrakenFuturesMarketData.cs:125-126`; this is why
  `open_interest` is a `disabled` dataset with the note "Carried inline in the snapshot ticker on
  every venue we run today".
- **On Kraken the ticker carries a book** — `SnapshotCollector.cs:171-173`.
- **REST order-book level ordering must not be trusted** — `KrakenDtos.cs:92-93`, handled by min/max
  in `DepthMath.cs:24-40`; a band the book does not reach yields `null`, not a partial sum
  (`DepthMath.cs:5-11`, test `KrakenWsTests.cs:37-54`).
- **`QtyStep` defect** — `KrakenFuturesMarketData.cs:215-224`
  (`for (var i = 0; i < precision; i++) step /= 10m;`) returns `1` for a negative
  `contractValueTradePrecision` (a bare `int`, `KrakenDtos.cs:18`); `MinQty` takes the same value
  (`:71,74`); `check (qty_step > 0)` (`0001_initial.sql:79`) passes it. Live, joined against
  `raw_json`: precision −3 on 5 instruments (TEST and PROD), −2 on 3 (TEST) / 2 (PROD), −1 on 8
  (both) — all stored as `qty_step = 1`. TEST symbols: `PF_BONKUSD, PF_FLOKIUSD, PF_MOGUSD,
  PF_PEPEUSD, PF_SHIBUSD` (−3); `PF_IOSTUSD, PF_PUMPUSD, PF_TURBOUSD` (−2); `PF_CFGUSD, PF_CROUSD,
  PF_IOTXUSD, PF_PENGUUSD, PF_TLMUSD, PF_VELOUSD, PF_XCNUSD, PF_ZILUSD` (−1). Written up at
  `plans/exchange-roadmap.md:28,89,173`.
- **Subscribe chunking of 200 is ours** — `KrakenWsFeed.cs:323`; no per-connection subscription cap
  is recorded anywhere, and `plans/studio-data-rework-brief.md:159` says "Kraken — verify cap", i.e.
  the cap is unknown.
- **Fields Kraken publishes that the DTOs do not bind** — `KrakenDtos.cs:36-55` declares nine
  fields and `KrakenWsDtos.cs:10-28` eleven; neither binds `fundingRatePrediction`,
  `next_funding_rate_time`, `suspended`, `tag`, `change24h`, `vwap24h`, `high24h/low24h/open24h` or
  `lastTime`. Predicted funding and next funding time are therefore not collected today.

### 4.8 The "trades work in progress" — not found

- `git branch -a -vv` and `git ls-remote --heads origin`: only `main`.
- `git status --porcelain`: the nine Studio files listed at the top, nothing about trades.
- `git stash list`: empty. `git worktree list`: one worktree.
- `gh pr list --state all`: nothing.
- `git log --all --grep=trade -i`: documentation commits only.

### 4.9 What Kraken actually serves (probed live, 2026-09-07 05:22–05:34 UTC, unauthenticated)

REST, all HTTP 200 unless stated: `/derivatives/api/v3/tickers` and `/tickers/{symbol}` (296
tickers), `/derivatives/api/v3/orderbook`, `/derivatives/api/v3/history`,
`/derivatives/api/v3/instruments` (296) and `/instruments/status`,
`/derivatives/api/v4/historical-funding-rates`, `/derivatives/api/v3/historical-funding-rates`,
`/derivatives/api/v4/historicalfundingrates`; **`/derivatives/api/v3/historicalfundingrates` → 404**
(`{"status":"NOT_FOUND"}`). Charts: `/api/charts/v1/{tick_type}/{symbol}/{resolution}` and
`/api/charts/v1/analytics/{symbol}/{analytics_type}`. Market history:
`/api/history/v3/market/{symbol}/{executions|orders|price}`.

WS feeds confirmed subscribable on `wss://futures.kraken.com/ws/v1`: `ticker`, `ticker_lite`,
`trade`, `book`, `heartbeat`. Confirmed **invalid** (each replied
`{"event":"alert","message":"Couldn't subscribe to invalid feed"}`): `liquidations`, `liquidation`,
`liquidations_lite`, `product`, `instrument`, `instruments`, `funding`, `open_interest`.

**Ticker fields.** REST key census over all 296 tickers: `symbol/tag/pair/markPrice/vol24h/
volumeQuote/openInterest/suspended/indexPrice/postOnly/change24h` on 296;
`bid/bidSize/ask/askSize` on 288; `last/lastTime/open24h/high24h/low24h/vwap24h/lastSize` on 283;
`fundingRate/fundingRatePrediction` on 280 (the 280 `tag:"perpetual"` rows); `isUnderlyingMarketClosed`
on 3. **Zero of 296 carry `nextFundingRateTime` or any equivalent — not found on REST.** The WS
ticker carries `funding_rate, funding_rate_prediction, relative_funding_rate,
relative_funding_rate_prediction, next_funding_rate_time, leverage, premium, bid, ask, bid_size,
ask_size, volume, dtm, index, last, change, suspended, tag, pair, openInterest, markPrice,
maturityTime, post_only, volumeQuote, open, high, low` — but **no last-trade timestamp**
(REST's `lastTime` has no WS counterpart). Observed `next_funding_rate_time = 1788760800000`, an
absolute epoch-ms instant (2026-09-07 06:00:00Z), while the doc calls it "milliseconds until" —
the wire and the doc disagree; the wire is what arrives
(https://docs.kraken.com/api/docs/futures-api/websocket/ticker/). WS ticker cadence measured at
~1 msg/s (t+0.048, +1.241, +2.146, +3.241 s), matching the documented 1 s throttle. REST
`markPrice`/`indexPrice` differed on all 5 consecutive 1 s polls, i.e. computed per request.
`ticker_lite` carries no mark/index/OI/funding.

**Book.** WS `book`: snapshot frames are `"feed":"book_snapshot"` with keys
`['feed','product_id','timestamp','seq','tickSize','bids','asks']`; deltas are `"feed":"book"` with
`side/price/qty/seq/timestamp`, `qty:0` meaning removal. The discriminator is the `feed` value, not a
boolean. `seq` is present on both; measured 7292 deltas in 15 s with strictly contiguous seq
(192947217→192954508). **No depth selector exists** — passing `"depth":10` is silently ignored and the
snapshot returned 1760 bid / 993 ask levels spanning price 1.0 to 364324.0
(https://docs.kraken.com/api/docs/futures-api/websocket/book/). REST
`/derivatives/api/v3/orderbook` returned 1744 bids / 1022 asks as `[price, size]` arrays with **no
sequence number**; `&depth=10` is ignored
(https://docs.kraken.com/api/docs/futures-api/trading/get-orderbook/). Sequenced top-N is obtainable
only from WS.

**Trades.** WS `trade`: `trade_snapshot` of 100 trades then deltas with fields
`product_id, feed, uid, side, type, time, qty, price, seq`. REST
`/derivatives/api/v3/history` returns 100 rows with `time, trade_id, price, size, side, type, uid,
sequence_id`; `lastTime` pages backwards but `lastTime=2026-09-01` and `2025-01-01` both returned
`"history":[]` — bounded to ~7 days or the last engine restart
(https://docs.kraken.com/api/docs/futures-api/trading/get-history/ — **404 on 2026-09-07, verified; the bound below is therefore UNSOURCED**). Full backfill exists on the
Market History API: `/api/history/v3/market/PF_XBTUSD/executions?sort=asc&since=…&count=…` reached
the earliest execution 2022-03-23T10:03:53.450Z (instrument openingDate 2022-03-22T13:15:36Z);
envelope `{elements, len, continuationToken}`; `count` max 1000 (5000 clamps to 1000); params
`sort`, `since`, `before`, `count`, `continuation_token` verified by call
(https://docs.kraken.com/api/docs/futures-api/history/market-history/ — the per-endpoint doc pages
returned HTTP 404 to every fetch, so the parameter facts are from the calls, not the docs).

**Liquidations.** No dedicated feed (see the invalid list above). They are tagged inside the trade
stream: a scan of the last 100 trades on 80 perpetual symbols found
`{'fill': 5279, 'partial liquidation': 38, 'liquidation': 1}` on REST, and the same events on WS
spelled `partialLiquidation` / `liquidation` — **the spelling differs between transports** (REST uses
a space, WS camelCase), and the doc enumerates only `fill, liquidation, termination, block`
(https://docs.kraken.com/api/docs/futures-api/websocket/trade/). Aggregate history exists as
`analytics_type = liquidation-volume` (decimal-string per bucket, no side split, no per-event
detail). The market-history `executions` stream carries **no** liquidation marker (1000 sampled
elements were plain `Execution`), so per-event liquidation backfill is **not found**.

**Funding.** Realized: `/derivatives/api/v4/historical-funding-rates` returns
`{timestamp, fundingRate (absolute), relativeFundingRate}` ascending; measured **8846–8847 rows
spanning 2025-09-03T08:00Z → 2026-09-07T05:00Z ≈ a rolling year**, whole series in one ~1.0 MB
response, no pagination parameters
(https://docs.kraken.com/api/docs/futures-api/trading/historical-funding-rates). Gap histogram over
the timestamps: `{3600: 8838, 7200: 6, 10800: 1}` — **hourly**, the larger gaps being missing hours.
Predicted: REST `fundingRatePrediction` (absolute) and WS `funding_rate_prediction` +
`relative_funding_rate_prediction`. Next funding time: WS only. A second history source,
`analytics_type = funding`, returns OHLC of the rate and is the **only** analytics type whose
`timestamp` array is in milliseconds; every other type returns seconds.
**Funding schedule versions: not found** — no endpoint publishes an interval, a schedule, or a
version history; `/instruments` exposes only `fundingRateCoefficient: 8`,
`maxRelativeFundingRate: 0.005` and (on 2 of 296) `minRelativeFundingRate`, all current-value scalars
with no effective-from date.

**Open-interest history.** `analytics_type = open-interest` returns an OHLC quadruple of
decimal strings per bucket with epoch-**second** timestamps. Retention measured: weekly buckets from
`since=1500000000` gave 184 points with the first at **2023-03-02T00:00:00Z**, `more:false`. Page cap
measured at exactly **2000 points** with `more:true` (interval=60 over 100 days). Allowed
`interval` values 60, 300, 900, 1800, 3600, 14400, 43200, 86400, 604800; `interval=1` and
`interval=7200` returned HTTP 400 `{"type":"Invalid arguments","field":"interval"}`
(https://docs.kraken.com/api-reference/analytics/market-analytics — the futures-api analytics doc
path returned 404). All 16 analytics types returned 200 on PF_XBTUSD: `open-interest,
aggressor-differential, trade-volume, trade-count, liquidation-volume, rolling-volatility,
long-short-ratio, long-short-info, cvd, top-traders, orderbook, spreads, liquidity, slippage,
future-basis, funding`.

**Mark and index candles.** `GET /api/charts/v1` enumerates the tick types itself:
`["mark","spot","trade"]`; anything else is HTTP 400 `Invalid tick type` — **there is no `index` tick
type**, the index series is the one named `spot`, proved by exact equality against the live ticker
(`indexPrice 79832.44` = spot 1m close 79832.44; `markPrice 79839.63132061172` = mark 1m close
79839.63132061172; `last 79842` = trade 1m close 79842). Envelope
`{"candles":[…],"more_candles":bool}`, `time` in epoch **ms**, OHLC as **strings**, and `volume` is
always `"0"` on mark and spot. Resolutions are enumerated per symbol at
`/api/charts/v1/trade/PF_XBTUSD` (`1m, 5m, 15m, 30m, 1h, 4h, 12h, 1d, 1w`). The survey's transcript
was truncated inside this list; the resolutions above are the ones it printed before the cut.

---

## 5. Storage

### 5.1 Every table ingestion writes to

| Table | Writer | file:line |
|---|---|---|
| `exchange_instrument` | DiscoveryCollector upsert + delist | `DiscoveryCollector.cs:93`, `:156` |
| `asset` | DiscoveryCollector auto-register | `DiscoveryCollector.cs:83` |
| `market_snapshot_latest` | SnapshotCollector (whole row), DepthCollector (depth columns) | `SnapshotCollector.cs:132`, `DepthCollector.cs:209` |
| `market_snapshot` | SnapshotCollector on keep passes only | `SnapshotCollector.cs:183` |
| `market_candle` | CandleCollector (`timeframe=1`), RollupJob (derived) | `CandleCollector.cs:106`, `RollupJob.cs:184` |
| `market_metric_hour` | RollupJob, last step | `RollupJob.cs:265` |
| `collector_status` | ExchangeWorker; `watermark_at` by RollupJob | `ExchangeWorker.cs:360`, `RollupJob.cs:351` |
| `collector_run` | ExchangeWorker | `ExchangeWorker.cs:399` |
| `collector_gap` | ExchangeWorker | `ExchangeWorker.cs:432` (close), `:448` (open) |
| `segment_dataset_capability` | ExchangeWorker reconcile | `ExchangeWorker.cs:517` |
| `capability_log` | ExchangeWorker reconcile | `ExchangeWorker.cs:525` |

(The storage survey gave `RollupJob.cs:~176` and `:~262` as approximations; I re-derived the exact
insert lines 184 and 265 and the matching `on conflict` at 190 and 306 with grep.)

**No table exists** for trades, liquidations, order-book levels or sequence, open-interest history as
its own series, mark/index candles, or instrument-spec versions — not found among the 34 user tables
on TEST or the 30 on PROD (`pg_stat_user_tables`).

### 5.2 Keys, partitioning, indexes, retention

- `exchange_instrument` (`0001_initial.sql:69`, 24 columns): PK `id`; UNIQUE
  `(segment_code, exchange_symbol)` (renamed `0019_segment.sql:81`); `exchange_instrument_pair
  (base_asset, quote_asset) WHERE collect` (`0024_asset_family.sql:198`) — **present on TEST, absent
  on PROD** (0024 unapplied). No retention.
- `market_snapshot_latest` (`0001_initial.sql:119`): PK `(exchange_instrument_id)`, that index only;
  mutable one row per instrument. TEST `n_tup_ins=2054`, `n_tup_upd=35,680,789`; PROD `1478` /
  `20,765,711`.
- `market_snapshot` (`0001_initial.sql:177`): `partition by range (received_at)`, monthly; PK
  `(exchange_instrument_id, received_at)`; BRIN on `received_at` (`:205`). Partitions created by
  `create_month_partition()` (`:300`) called from `Database/Partitions.cs:17` — current and next
  month only; **no default partition, deliberately** (`:294`).
- `market_candle` (`0001_initial.sql:218`): `partition by range (open_time)`, monthly; PK
  `(exchange_instrument_id, timeframe, open_time)`; BRIN `:237`; partial index
  `market_candle_incomplete … WHERE bar_count < timeframe` `:242`; `market_candle_updated_at_brin`
  (`0013_run_window_index.sql:11`). Header states it is not rotated.
- `funding_rate_history` (`0006_assets_and_metrics.sql:154`): PK
  `(exchange_instrument_id, funding_time)` and **no other index**; not partitioned; never rotated
  (`:154-164`).
- `market_metric_hour` (`0006_assets_and_metrics.sql:177`, extended `0017_collection_health.sql:86`):
  PK `(exchange_instrument_id, hour_time)`, no other index; not partitioned, not rotated.
- `collector_run` (`0009_collector_runs.sql:20` + `0017_collection_health.sql:24`): PK `id`; index
  `(segment_code, collector, started_at desc)` (`0009:39`). Its 7-day retention was **removed** at
  `0017_collection_health.sql` (header point 3) and `Hub/Retention/RetentionJob.cs:29-36` records
  that nothing deletes it now.
- `collector_gap` (created as `collection_gap` `0017_collection_health.sql:46`, renamed
  `0019_segment.sql:170`): PK `id`; indexes `collector_gap_lookup`, `collector_gap_open (… ) WHERE
  gap_end is null`, `collector_gap_instrument (…) WHERE exchange_instrument_id is not null`
  (`0017:74-78`). No retention.
- `collector_status` (`0001_initial.sql:272`): PK `(segment_code, collector)`, only that index;
  `collector` FK → `dataset.code`.

### 5.3 Row counts and size

| Object | TEST rows | TEST total | PROD rows | PROD total |
|---|---|---|---|---|
| `market_snapshot` (all partitions) | 11,669,277 | 2,323 MB | 14,955,165 | 3,277 MB |
| `market_candle` (all partitions) | 14,204,869 | 2,311 MB | 4,275,443 | 681 MB |
| `market_snapshot_latest` | 2,054 | 27 MB | 1,458 | 688 kB |
| `market_metric_hour` | 196,780 | 30 MB | 30,613 | 5,272 kB |
| `funding_rate_history` | 212,626 | 20 MB | 134,811 | 14 MB |
| `collector_run` | 126,841 | 25 MB | 51,390 | 10 MB |
| `collector_gap` | 365 (366 in a later sample) | 224 kB | 304 (305 later) | 192 kB |
| `exchange_instrument` | 2,258 | 5,872 kB | 1,532 | 2,096 kB |
| `collector_status` | 26 | 136 kB | 15 | 64 kB |
| `segment_dataset_capability` | 405 | 136 kB | 324 | 128 kB |
| `capability_log` | 65 | 80 kB | 48 | 48 kB |
| `asset` | 1,197 | 160 kB | 1,080 | 152 kB |
| `asset_family` / `asset_family_member` | 1 / 3 | 32 kB / 48 kB | **table absent** (0024 unapplied) | — |
| database total (`pg_database_size`) | **4,752 MB** | | **3,999 MB** | |

The two `collector_gap` numbers come from two survey passes minutes apart (365/304 and 366/305); the
difference is one new gap row on each host between samples, not a contradiction.

Partition index sizes, TEST: `market_snapshot_2026_09_pkey` 496 MB, `market_candle_2026_09_pkey`
510 MB, the `incomplete` partial 77 MB, BRINs 96 kB. PROD: 629 MB, 151 MB, 12 MB, 48–56 kB.

### 5.4 Latest vs history, and who reads which

`market_snapshot_latest` is one current row per instrument, written **only whole**
(`0001_initial.sql:119` and the note at `:145-148`). `market_snapshot` is append-only at the keep
cadence. There is no "latest" table for candles, funding or metrics.

- **Studio** (`studio_reader`): latest only for prices (`Studio/Data/StudioStore.cs:195`,
  `Data/SegmentFreshnessStore.cs:65`), history from `Data/CandleStore.cs:68` and
  `Data/MetricHourStore.cs:52`. It **cannot** read `market_snapshot` or `funding_rate_history` — the
  grants are the 11 tables at `0025_arena_reader.sql:202-227` plus `market_metric_hour`
  (`0026_arena_metric_hour.sql:100`), role renamed at `0027_studio_rename.sql:29-45`.
- **Admin:** both. History `Data/MarketStateStore.cs:71,128`, `Data/PairStore.cs:79`,
  `Data/AssetStore.cs:97,166`, `Data/ExchangeStore.cs:410`; latest `Data/DashboardStore.cs:187`,
  `Data/ExchangeStore.cs:100,414,421`, `Data/InstrumentStore.cs:77,147`, `Data/AssetStore.cs:31,35,206`;
  candles `Data/ExchangeStore.cs:435,442`, `Data/InstrumentStore.cs:157,239`, `Data/PairStore.cs:116`,
  `Data/AssetStore.cs:130,146`; `market_metric_hour` `Data/InstrumentStore.cs:172`;
  `funding_rate_history` `Data/InstrumentStore.cs:182,251`.
- **MarketData.Api** (PROD only): `market_snapshot_latest` at `Endpoints.cs:58,162,173`,
  `market_candle` at `:201`. No snapshot history, no funding history.
- **Depth** writes only `market_snapshot_latest` (`DepthCollector.cs:206-215`) and reaches
  `market_snapshot` solely because the keep-insert selects the depth columns back out of the latest
  row (`SnapshotCollector.cs:183-195`) — the behaviour `archive_interval_s` exists to describe
  (`0020_history_interval_cascade.sql:120-127`).

### 5.5 Candles and the derived timeframes

`Hub/Rollups/RollupJob.cs`. Constructed `ExchangeWorker.cs:73`, looped `:81` — 60 s, service-wide,
under `ServiceExchange = "fake"` (`:31`). Timeframes come from
`dataset_setting('rollup','derived_timeframes') = 5,15,60,240,720,1440`, read `RollupJob.cs:107`,
filtered `>1`, deduped, ascending. `SourceFor` (`:100`) picks the largest configured timeframe that
divides evenly, so 1→5→15→60→240, 720 from 240, 1440 from 720. The unit of work is *touched*
windows (a `touched` CTE on `updated_at`), and aggregation is one
`insert … select … group by … on conflict` (`:184-190`); `bar_count` sums the source's counts and
`trade_count` is `case when bool_and(c.trade_count is not null) then sum(...) else null end`. Only
windows whose end is `<= now()` are written. Progress is `collector_status.watermark_at` for
`('fake','rollup')`, read `:121-129`, written `:351`. Constants: `Slack` 10 min (`:27`), `MaxStep`
30 min (`:37`), `ColdStartWindow` 1 day (`:43`), `AggregateTimeoutSeconds` 180 (`:48`).
`Rollups/Rollup.cs` is the unit-tested arithmetic specification and is no longer called (`:25-27`).

TEST — all seven timeframes current (each lag equals the un-closed window):

| tf | rows | latest open_time | latest updated_at |
|---|---|---|---|
| 1 | 10,708,956 | 2026-09-07 05:28 | 05:29:19 |
| 5 | 2,420,786 | 05:20 | 05:28:33 |
| 15 | 795,611 | 05:00 | 05:28:42 |
| 60 | 200,254 | 04:00 | 05:28:43 |
| 240 | 49,617 | 00:00 | 05:07:41 |
| 720 | 16,742 | 2026-09-06 12:00 | 2026-09-07 00:20:10 |
| 1440 | 8,603 | 2026-09-06 00:00 | 2026-09-07 00:20:11 |

`trade_count` non-null on 2,198,409 of 14,204,869 rows (15.5 %); `bar_count < timeframe` on
1,238,501 rows. Per segment at tf=1: weex-futures 6,702,807; kraken-futures 2,291,935; hyperliquid
1,318,749; binance-usdm 373,055; fake 23,193 — and binance-usdm has **no `timeframe=1440` row at
all**.

PROD — tf=1 is current (latest 2026-09-07 05:26) and **every derived timeframe runs ~24 h behind, advancing rather than stopped** (re-measured 2026-09-07, §8.4 point 2)
(5: 2026-09-06 05:50; 15: 05:45; 60: 05:00; 240: 04:00; 720 and 1440: 2026-09-06 00:00), and
`market_metric_hour` stops at `hour_time = 2026-09-06 05:00`. Observed cause: **PROD has no `fake`
segment** (`select code from segment` returns four rows; `0019_segment.sql:111` seeds `fake`), so
`collector_status` has no `('fake','rollup')` row, the watermark read at `RollupJob.cs:121` returns
null every pass, `Window(null, startedAt)` takes the `ColdStartWindow` branch (`:86`) and the
`update collector_status set watermark_at` at `:351` affects zero rows. The PROD hub log confirms it
every pass: `"Category":"Collector.fake.rollup","Message":"fake/rollup status write failed"`,
`23503: insert or update on table "collector_status" violates foreign key constraint
"collector_status_segment_code_fkey"` from `ExchangeWorker.cs:320`. TEST has the row and it is live
(`watermark_at = 2026-09-07 05:25:34.974534+00`).

`market_metric_hour` is written by the same job as its last step (`RollupJob.cs:265-306`), reading
`market_snapshot` from `date_trunc('hour', @since)`, with `expected_count` from
`least(3600 / greatest(coalesce(keep_interval_s(ei.segment_code,'snapshot'),60),1), 32767)` and
`gap_seconds` from a lateral over `collector_gap` for collectors `('snapshot','depth','funding')`.

### 5.6 `funding_rate_history` today

Shape on both hosts: `exchange_instrument_id integer not null`, `funding_time timestamptz not null`,
`rate double precision not null`. **No column for a predicted rate, a next funding time, or a
schedule version**; `next_funding_at` was deliberately excluded (`0001_initial.sql`, the "Что НЕ
вошло" block ~lines 30-33), and the only schedule datum anywhere is
`exchange_instrument.funding_interval_hours`.

TEST (212,626 rows): binance-usdm 21,896 / 566 instruments (2026-08-30 13:00 → 2026-09-06 19:00);
fake 460 / 20; hyperliquid 50,831 / 177 (2026-08-26 05:00 → 2026-09-07 04:00); kraken-futures
83,188 / 280 (2026-08-25 14:00 → 2026-09-07 04:00); weex-futures 56,251 / 1,028.
PROD (134,811 rows, no binance-usdm, no fake): hyperliquid 37,130 / 177; kraken-futures 57,780 / 276
(2026-08-29 10:00 → 2026-09-07 05:00); weex-futures 39,899 / 1,023.
The earliest rows on both hosts are the 168-hour backfill floor from first discovery, not venue
history depth — Kraken serves ~1 year (§4.9).

### 5.7 `exchange_instrument` lifecycle

24 columns on both hosts; lifecycle-relevant: `status`, `status_changed_at`, `first_seen_at`,
`last_seen_at`, `listed_at` (`0006_assets_and_metrics.sql:96`), `collect`, `collect_note`,
`collect_changed_at`, `collect_changed_by` (`0008_instrument_admin.sql:30-34`), `updated_at`,
`raw_json`.

TEST, 2,258 rows: binance-usdm 697 (697 `listed_at`, 697 `min_notional`, 566 trading);
fake 20; hyperliquid 233 (**0** `listed_at`, **0** `min_notional`, 177 trading, 56 delisted);
kraken-futures 280 (280 `listed_at`, **0** `min_notional`, 276 trading, 4 delisted);
weex-futures 1,028 (**0** `listed_at`, **0** `min_notional`, 1,004 trading, 5 delisted).
Overall: trading 2,041, halted 151, delisted 65, post_only 1; `raw_json = '{}'` on 0 rows;
`status_changed_at > first_seen_at` on 19 of 2,258.

PROD, 1,532 rows: hyperliquid 233, kraken-futures 276 (276 `listed_at`, 0 `min_notional`, 276
trading, 0 delisted), weex-futures 1,023. Overall trading 1,457, halted 19, delisted 56;
`status_changed_at > first_seen_at` on 3 of 1,532.

On both hosts `collect = true` on **every** row, and `collect_note`, `collect_changed_at`,
`collect_changed_by` are null on every row. Delisting is time-driven, not counter-driven
(`DiscoveryCollector.cs:156-170`).

### 5.8 Health tables: schema vs. reality

**`collector_run`** allows 13 columns; the only INSERT (`ExchangeWorker.cs:399-403`) names eight
(`segment_code, collector, started_at, duration_ms, ok, error, items, transport`).
`exchange_instrument_id`, `http_status`, `request_weight`, `clock_offset_ms` are **never written**:
non-null count 0 on both hosts. `transport` non-null on 41,424 of 126,841 (TEST) and 37,310 of
51,390 (PROD); `items` is bound from `a.InstrumentsExpected` (`ExchangeWorker.cs:399-408`), i.e. the
expected-instrument count, not rows written. Earliest `started_at`: TEST 2026-09-01 08:45:10, PROD
2026-09-05 09:14:47.

**`collector_gap`** declares eight causes (`0017_collection_health.sql:52-61`); the classifier
`ExchangeWorker.CauseOf` (`:463-472`) can produce four (`error, rate_limited, timeout,
exchange_maintenance`), so `ws_sequence_gap`, `ws_disconnected`, `resync`, `collector_down` have **no
producer anywhere**. Observed: TEST `{rate_limited: 361, error: 5}` — hyperliquid/snapshot 347,
weex-futures/depth 5 (1 open), binance-usdm/snapshot 8, binance-usdm/depth 5 (1 open),
hyperliquid/funding 1; PROD `{rate_limited: 305}`, all hyperliquid/snapshot, 0 open.
`exchange_instrument_id` is non-null on **0 of 365 (TEST) and 0 of 304 (PROD)** rows, although the
column and a dedicated index exist (`0017:50,78-80`).

**`market_metric_hour`**, TEST 196,780 rows over 2026-08-31 20:00 → 2026-09-07 04:00:
`spread_bps_avg` non-null 196,645; `depth_bid_25bps_avg` 91,621 (46.6 %) and `depth_ask_25bps_avg`
91,691; `expected_count` and `gap_seconds` non-null on **66,324 (33.7 %)**, with `gap_seconds > 0` on
13,845. The boundary is exact: the last hour with nulls is 2026-09-05 10:00 and the first with values
2026-09-05 11:00 — migration 0017 was applied at 2026-09-05 11:55:22. The per-segment breakdown was
cut off in the survey transcript; **marked incomplete**.

---

## 6. Studio

Read at commit `6ac83df2…`; **the concurrent workflow has since modified `Format.cs`,
`Models/Statement.cs`, `Models/Strip.cs`, `Views/Pairs/Pair.cshtml`, `Views/Shared/_PairTable.cshtml`,
`wwwroot/studio-ages.js`, `wwwroot/studio.css`** (git status above), so line numbers in those seven
files may have moved.

### 6.1 Pages and routes

One controller, three actions — `Controllers/PairsController.cs` is the only controller file in the
project.

| Action | file:lines | Address |
|---|---|---|
| `Index(string? q, ct)` | `:47-98` | `/studio` (default route) and `/studio/Pairs/Index?q=…` |
| `Pair(baseFamily, quoteFamily, ct)` | `:115-136` | `/studio/{baseFamily}/{quoteFamily}` (named route `pair`) |
| `Live(baseFamily, quoteFamily, ct)` | `:245-480` | `/studio/live/{baseFamily}/{quoteFamily}`, `text/event-stream` |

Routes in `Studio/Program.cs`, order load-bearing (`:144-147`): default `:148`, `pair` `:191-194`,
`pair-live` `:206-209`, both with
`{…:regex(^[A-Za-z0-9][A-Za-z0-9_-]*\z):maxlength(16)}`. Path base `:128`; static files `:141`;
response compression `:105-107,135`; forwarded headers `:33-38,130`. The regex is duplicated as
`PairAddress.Pattern` / `MaxLength` (`PairAddress.cs:46,53`, `\z` not `$` per `:34-44`) and
re-checked at `PairsController.cs:122` and `:252`.

Views: `Views/Pairs/{Index,Pair,PairNotFound}.cshtml`,
`Views/Shared/{_Layout,_PairTable,_Stamps,_Statement}.cshtml`. Static assets:
`wwwroot/{studio-ages.js, studio-candles.js, studio-live.js, studio.css}`, `wwwroot/ds/…`, and a
vendored `wwwroot/vendor/lightweight-charts/lightweight-charts-5.2.1.standalone.production.js`.
Nothing is fetched from a third party (`Views/Shared/_Layout.cshtml:12-15`).

### 6.2 What feeds it

Studio holds **no `HttpClient`** (`grep -rn "HttpClient" src/CryptoSmithX.WebApp.Studio` → not
found). It opens `Db` directly under the read-only role (`Program.cs:43-47`). Its five queries:

| Query | file:lines | Tables |
|---|---|---|
| `StudioStore.PairsSql` | `Data/StudioStore.cs:70-118` | `exchange_instrument`, `segment`, `exchange`, `asset_family_member` ×2 |
| `StudioStore.PairVenuesSql` | `Data/StudioStore.cs:136-204` | + `market_snapshot_latest` |
| `SegmentFreshnessStore.Sql` | `Data/SegmentFreshnessStore.cs:55-116` | + `segment_dataset` ×3, `dataset` |
| `CandleStore.Sql` | `Data/CandleStore.cs:59-74` | `market_candle` |
| `MetricHourStore.Sql` | `Data/MetricHourStore.cs:42-57` | `market_metric_hour` |

Every public query carries four guards — `and i.collect`, `and i.status <> 'delisted'`,
`and sg.status = 'enabled'`, `and x.code <> 'fake'` (`Data/StudioStore.cs:84-98,198-202`,
verified in the SQL; note the class doc comment at `Data/StudioStore.cs:17-19` says "the same
**three** guards" and is one behind the code,
repeated `Data/SegmentFreshnessStore.cs:69-72`). `market_snapshot` is never read and the grant is
deliberately withheld (`Data/StudioStore.cs:11-16`); `collector_status.avg_duration_ms` is never read
(`Data/SegmentFreshnessStore.cs:18-24`).

Grants: `0025_arena_reader.sql:175-178` creates the role with `connection limit 30`; `:202,209,214-227`
grant `select` on `schema_version, exchange, segment, dataset, segment_dataset, exchange_instrument,
asset, asset_family, asset_family_member, market_snapshot_latest, market_candle`;
`0026_arena_metric_hour.sql:100` adds `market_metric_hour`; `0027_studio_rename.sql:36` renames the
role. `LISTEN csx_live` needs no grant (`Live/LiveNotifier.cs:40-42`).

Cache and live: `Data/StudioCache.cs` — TTL 1 s (`:45`), 256 entries (`:104`), key max 64 (`:128`),
failures not cached (`:14`); keys built at `PairsController.cs:87` and `:146`.
`Live/LiveNotifier.cs` holds one `LISTEN csx_live` connection (`:51,177`), opened on first subscriber
(`Program.cs:59-65`), max backoff 30 s (`:52`). `Live/LiveStreamGate.cs:28` caps concurrent streams
at 100; refusal is a `notice: full` event (`PairsController.cs:273-280`).
**`Live/LiveRelevance.cs:29-67`** decides what redraws: `snapshot`, `depth`, `open_interest`,
`discovery` and a null dataset (policy/segment change) → true; `candles`, `rollup`, `funding` →
false; **an unknown dataset code → false, deliberately (`:61-66`)**.

### 6.3 The read API nobody consumes

`src/CryptoSmithX.MarketData.Api/Endpoints.cs:13-22` maps `/v1/health` (`:24-78`), `/v1/exchanges`
(`:80-94`), `/v1/instruments` (`:100-126`), `/v1/snapshot` (`:128-181`), `/v1/candles` (`:183-213`;
`tf<=0` → 400 `:186-189`, `limit` clamped `Math.Clamp(limit ?? 300, 1, 5000)` `:191`).
Health's stale rule is `MarketData:SnapshotIntervalSeconds (default 10) × 3.0` (`:28,50`), degraded
if no collectors, any `ConsecutiveFailures > 0`, any null `LastSuccessAt`, or any stale instrument
(`:67-69`). The query parameter is still called `exchange` though the column is `segment_code`
(`:96-99`).
**No consumer exists in this repository:** `grep -rn "MapMarketDataApi"` matches only
`Endpoints.cs:13` and `MarketData.Api/Program.cs:46`; neither web app has an `HttpClient`; the only
in-repo `/v1/*` references are docs (`Hub/README.md:35-37`, `plans/capacity-sizing.md:207,233,262`,
`plans/notes-charts-and-tradingview.md:55`). The API container runs on PROD only.

### 6.4 Generic by feed, or hand-coded per column?

**Hybrid, and the split is sharp: the rendering of a cell is generic; the set of columns is
hand-written in five places with no descriptor tying them together.**

Generic: `Views/Shared/_PairTable.cshtml:231-344` is one loop
(`for (var column = 0; column < cells.Count; column++)`) drawing all fourteen metric cells
identically from `MetricCellModel` — tint `:234-239`, mark slot `:260-288`, figure `:290-301`,
history `:310-332`, age line `:336-342`, with zero per-column branches (stated `:10-13`).
`wwwroot/studio-live.js:102-108` is generic by region name. `wwwroot/studio-ages.js:493` gathers
cells by `.a-cell[data-at]` and re-derives ages from `data-at` / `data-win` / `data-rank`.

Not generic — the seventeen columns are written out by hand in five files:

1. `Models/Cells.cs:198-254` — `RowCells.Build` is a literal collection of 14 cells, each naming its
   builder; labels are string literals (`"Bid"` `:201`, `"Ask"` `:204`, `"Bid size"` `:214`,
   `"Ask size"` `:217`, `"Last"` `:222`, `"Mark"` `:228`, `"Index"` `:231`, `"Turnover 24h"` `:236`,
   `"Depth 10bps"` `:244`, `"Depth 25bps"` `:249`, `"Depth 50bps"` `:252`; `"Spread bps"` `:396`,
   `"Funding"` `:473`, `"Open interest"` `:508`).
2. `Views/Shared/_PairTable.cshtml:102-120` — the eyebrow row is 17 literal `<span>`s
   (`Platform, Symbol, St, Bid, Ask, Spread bps, Bid size, Ask size, Last, Mark, Index, Funding,
   Turnover 24h, Open interest, Depth 10bps, Depth 25bps, Depth 50bps`).
3. `Views/Shared/_PairTable.cshtml:95-100` — the three call bands, widths as CSS span counts.
4. `wwwroot/studio.css:348` — `--a-cols:` seventeen hard-coded track widths summing to 1836, matched
   by `.a-table-inner { min-width: 1836px }` `:353` and band spans `:386,394,395,396` (3+10+1+3 = 17).
5. `Data/Verdicts.cs:19-31` — `enum PairColumn` with 10 members and `Specs` (`:160-198`) a
   hand-written array of 10; `Data/Scales.cs:74-82` is a second hand-written array of 6.

Two further hard-coded counts: `Views/Shared/_Statement.cshtml:45` prints the literal string
`17 fields · ages live`, and `_PairTable.cshtml:3` says "seventeen columns". Column order is not free
to change (`Models/Cells.cs:127-134`).

### 6.5 Adding a new dataset to the page — the file list

**As a column:** (1) a migration granting `select` to `studio_reader` (pattern
`0026_arena_metric_hour.sql:100`); (2) `Data/StudioStore.cs:150-188` keeping the guards `:196-202`;
(3) `Models/StudioModels.cs:48-82` (+ derived properties, pattern `:92-130`); (4)
`Models/Cells.cs:198-254` and a builder beside `Figure`/`Size`/`Bar`/`Depth` (`:315-546`); (5)
`Data/Verdicts.cs:19-31` and `:160-198` if ranked; (6) `Data/Scales.cs:74-82` if it carries a bar;
(7) `Views/Shared/_PairTable.cshtml:102-120` and `:95-100`; (8) `wwwroot/studio.css:348,353,386-396`;
(9) `Views/Shared/_Statement.cshtml:45`; (10) `Live/LiveRelevance.cs:29-67`.
**If it is a new call with its own clock**, additionally: `Models/StudioModels.cs:173-176` and
`:203-233`, `Data/SegmentFreshnessStore.cs:55-116`, `Models/PairPage.cs:151` and `:187-190`,
`Controllers/PairsController.cs:191-194`, `Models/Cells.cs:22-27` and `:350-364`,
`Views/Shared/_PairTable.cshtml:16-21,234-239`, `wwwroot/studio.css` tone tokens,
`Models/Strip.cs:129-131`, and a new store beside `Data/MetricHourStore.cs` with its call site at
`PairsController.cs:164-167` and a member on `Models/PairPage.cs:86-124`.
**As a section:** a store + grant, models, `PairsController.cs:145-169` and `:596-599`, a new partial
rendered from `Views/Pairs/Pair.cshtml:117`, `PairsController.cs:536-541` (`LiveRegions`) plus
`data-live-region` on the partial's root (pattern `_PairTable.cshtml:87`), `Live/LiveRelevance.cs`,
CSS, and a glossary entry at `Views/Pairs/Pair.cshtml:282-486` if it introduces a word.

Tests that pin this surface: `tests/CryptoSmithX.WebApp.Studio.Tests/` — `PublicQueryTests.cs` (11
facts; the SQL is `const` precisely so tests can read it, `Data/StudioStore.cs:26-29`),
`DesignSystemTests.cs` (22, `:200` asserts the seventeen track widths live in `--a-cols`),
`VerdictScopeTests.cs` (44), `FreshnessTests.cs` (24), `StatementTests.cs` (24),
`PublicSurfaceTests.cs` (19), `LiveTests.cs` (17), `ControlsTests.cs` (16), `HourlySeriesTests.cs`
(12), `StudioCacheTests.cs` (10), `FigureFitTests.cs` (5). Two of these are open in the concurrent
workflow.

### 6.6 Ages

Every age on the pair page is a subtraction of one of **three** absolute instants read per row from
`market_snapshot_latest` (`Data/StudioStore.cs:170,181,188`): ticker `received_at` →
`PairVenueRow.ReceivedAt` → `CallAges.PriceSeconds`; open interest `open_interest_at`; depth
`depth_at`. Windows come from `SegmentFreshness` (`Models/StudioModels.cs:203-250`, cap
`PassCapWindows = 12.0` `:223`). The survey transcript for this area was **truncated inside the age
arithmetic**, so the exact fade formula and threshold code paths are not restated here; the design
rule they implement is `src/web/ds-studio/readme.md:25-28` (rule 1) and `:30-40` (rule 2), and the
clamps for a venue clock running ahead of ours are `Studio/Format.cs:149-151` and
`Studio/Data/Freshness.cs:48-51` — both files are being edited right now. **Marked incomplete.**

---

## 7. Admin

The survey that owned this area was cut off before delivering it. **Everything below I read
directly**, at the paths and lines cited.

### 7.1 Shape and access

`src/CryptoSmithX.WebApp.Admin` is an MVC app with two areas, `Admin/` and `My/`. There is no path
base: routes are `{area:exists}/{controller=Home}/{action=Index}/{id?}` (`Program.cs:166`) and
`{controller=Home}/{action=Index}/{id?}` (`:167`), plus `MapIngestEndpoints()` (`:168`), so the
console lives at `/Admin/...`. Cookie auth only (`Program.cs:50-58`, `LoginPath = "/"`), every
Admin-area controller carrying `[Area("Admin")] [Authorize(Roles = "admin")]` (e.g.
`DatasetsController.cs:13-14`, `ExchangesController.cs:19-20`, `SettingsController.cs:12-13`).
DataProtection keys are persisted to `DataProtection:KeysDirectory` (`:63-68`).
`Migrator.VerifyAsync` runs before serving (`:78`). Static mock pages under `/feature-demo/*` are
served with `X-Robots-Tag: noindex` (`:119-141`); `/studio` deliberately no longer redirects here
(`:114-127`).

Controllers present: `Assets, Bots, Clients, Datasets, Exchanges, Families, Home, Instruments,
MarketData, MarketState, Pairs, Search, Settings, Tenants` under `Areas/Admin/Controllers/`, plus
`Areas/My/Controllers/{Bots,Home}` and root `Controllers/{Auth,Home}`. Data access is raw SQL +
Dapper in `Data/*.cs` (15 files, `ExchangeStore.cs` 501 lines the largest).

### 7.2 Where collection policy is edited

**One place: the Edit-feed dialog on the exchange page.** `DatasetsController.cs:8-11` states it:
the Datasets page "never edits policy, that is the Edit feed dialog on the exchange page".

- `POST /Admin/Exchanges/Feed/{id}` — `ExchangesController.cs:156-186`, `[ValidateAntiForgeryToken]`,
  parameters `dataset, mode, intervalS, historyIntervalS, retentionDays, transport, note,
  confirmCode`; intervals validated by `TryInterval` (`:134-151`: empty = inherit, otherwise a
  positive int) → `FeedStore.SaveAsync`.
- `FeedStore.SaveAsync` (`Data/FeedStore.cs:183-219`) re-checks the **loss guard** server-side: if the
  feed was `collect`, is leaving `collect`, and its `history_depth` capability is null or `none`, the
  typed `confirmCode` must equal the dataset code, else "Not saved — the typed dataset code did not
  match." (`:200-207`). The write is a single `update segment_dataset set mode, interval_s,
  history_interval_s, retention_days, transport, note, updated_by, updated_at = now()` (`:209-217`).
  The class doc (`:7-11`) states capability is read-only here — only `ExchangeWorker` declares it.
- The dialog itself: `Areas/Admin/Views/Exchanges/_FeedDialog.cshtml`. Mode chips `:110-119` —
  and **`disabled` is the only selectable chip when `we_implement` is false**: `:113`
  (`var blocked = m != "disabled" && !d.WeImplement;`) renders the `collect` and `on_demand` radios
  `disabled` with the title "No collector for this feed yet — both collect and on demand need one".
  Transport chips `:139-167` are offered only where venue and our transports intersect
  (`FeedStore.cs:170`), hidden as a single value when there is one, and stated as not-a-choice.
  Interval / Keep-every / Retention fields `:169-185`, with the "Keep every (s)" field rendered only
  when `dataset.default_history_interval_s is not null` (`:35,174-180`). Cascade indicators
  `:186-207` (own → dataset → global for interval and retention; own → dataset → **poll** for keep,
  because keeping cannot outrun asking, `:194-195`). The loss sentence `:213-218` quantifies dropped
  observations (`dropsPerKept`, `:43-45`), and `:219-224` explains the depth case where the archive
  rate is set by the snapshot cell (`archivedElsewhere`, `:56`). Capability rows `:84-99` are
  read-only with their source tag (`:18-24`) and a "value without a source is an opinion" note
  (`:100`); the footer shows the latest `capability_log` line (`:235`). The dialog carries
  `data-live-skip` so a live tick never rewrites an open dialog (`:58`).
- The legacy settings form still edits five intervals by name: `ExchangesController.Save`
  (`:58-108`) takes `snapshotIntervalS, candleIntervalS, discoveryIntervalMin, fundingIntervalMin,
  depthIntervalS` and `ExchangeStore.SaveAsync` (`Data/ExchangeStore.cs:112-148`) writes `segment`
  and then five `update segment_dataset … set interval_s` in one transaction (`:140-144`, helper
  `:150-158`), converting the two minute-shaped fields (`:143-144`). Reading them back converts the
  other way (`ExchangeStore.cs:144-171`, dataset defaults as placeholders `:177-187`).
- Segment lifecycle: `ExchangesController.Status` (`:115-129`) requires typing the exchange code,
  then `ExchangeStore.SetStatusAsync` (`:165-178`) validates against
  `AllowedStatuses = ["planned","enabled","disabled","maintenance","abandoned"]` (`:100-101`).
  Status is deliberately not part of the settings form (`ExchangesController.cs:110-114`).
- Global settings: `SettingsController` (`:20-44`) lists and writes the `setting` table through
  `SettingStore` (`Data/SettingStore.cs:15-27`, `:31-53`), validating by declared `kind`
  (`:55-71`). After `0014_collections.sql:266-272` deleted the ten migrated keys, what remains for
  this page to edit is `ws_stale_after_s`, `ws_crosscheck_interval_s`, `ws_crosscheck_drift_bps`
  (`0010_ws_market.sql:38-42`). The success message says "applies within a minute"
  (`SettingsController.cs:40`), which matches the Hub's 30 s cache + reconcile — except that the
  three ws_* values are read once at adapter build time (§3.3), so on Kraken they in fact apply only
  when the exchange is rebuilt.
- **There is no admin surface that creates a dataset, a segment or an exchange row.** No
  `insert into dataset|segment|exchange` exists in `src/CryptoSmithX.WebApp.Admin` (grep over
  `Data/*.cs`); the Datasets page is read-only (`DatasetsController.cs:21-26`,
  `DatasetStore.ListAsync` `:11-43`), and its view shows a venue row only where a human departed from
  the default (`Areas/Admin/Views/Datasets/Index.cshtml:27-49`).

### 7.3 What the admin shows per feed

`_DataFeedsPanel.cshtml:14-28` groups by **what is produced**, not by which loop ran:
"Market state" (`snapshot, depth, open_interest` → `market_snapshot · market_snapshot_latest`),
"Bars" (`candles, rollup` → `market_candle`), "Funding history" (`funding` →
`funding_rate_history`), "Instruments" (`discovery` → `exchange_instrument`). The reason is stated
at `:5-13`: depth and snapshot are two loops with one artifact. Anything not in a group falls into
`unproduced` (`:34`) and renders under "Nothing produced / no collector — Named absences, not
outputs. The hub can only run the collectors it has classes for, so the policy control on these rows
changes nothing." (`:140-150`, rows `:152-171`). **Today that bucket is exactly `trades` and
`liquidations`** (`open_interest` is placed in Market state), and their rows are tagged `inert`
(`:165`).

Per-feed health comes from `FeedStore.ListAsync` (`Data/FeedStore.cs:28-70`): capability values
(`venue_supports`, `we_implement`, `history_depth` with its `source`), policy (`mode`, `transport`,
effective interval `coalesce(ec.interval_s, c.default_interval_s)`, **`archive_interval_s(@segmentCode,
c.code)`** `:49` with the depth explanation at `:42-48`, effective retention, note) and observation
(`last_success_at` age, `consecutive_failures`, `last_duration_ms`, `avg_duration_ms`) joined from
`collector_status` `:64`.

Runs and gaps: `ExchangesController.Runs` (`:188-195`) → `RunStore.ListAsync`
(`ExchangeStore.cs:317-331`, `limit 200`), filter list hard-coded at `Runs.cshtml:6`;
`ExchangesController.Run` (`:197-202`) → `RunStore.GetAsync` (`ExchangeStore.cs:368-500`), which
attributes rows to a run **by time**, because data is upserted with no run id (`:386-390`), with a
per-collector SQL switch (`:401-448`) and a per-collector empty-run note (`:476-494`) that spells out
the poll/keep ratio. `funding` has no branch at all — "funding_rate_history has no insert stamp
(funding_time is the payment time) … the items counter above is the source of truth"
(`:446-447`, `:491`). Gaps are read at `ExchangeStore.cs:391-407` (`open first`, 48 h window,
`limit 20`). Latency is 15-minute buckets over 12 h from `collector_run` where `ok`
(`RunStore.LatencyAsync`, `ExchangeStore.cs:334-366`), with a documented forward-fill of the last
known value after a collector's first run (`:352-362`).

Live updates: `ExchangesController.Live` (`:214-215`, doc comment `:205-213`) is SSE, filtered to the exchange in
the URL (`:225-226`), debounced 400 ms (`:267`), heartbeat `: ping` every 25 s (`:244`, `:258`),
re-rendering the same partials the page uses for first paint; the doc comment records that `live.js`
falls back to its 10 s poll whenever the stream is not open (`:211-212`).

---

## 8. Ops

### 8.1 Running locally

Documented at `src/CryptoSmithX.MarketData.Hub/README.md:25-60`:

```
cp deploy/.env.example deploy/.env
docker compose -f deploy/docker-compose.yml up --build      # README:29
```

Order is enforced by compose (postgres → migrator to completion → hub, api, webapp-admin, studio,
`docker-compose.yml:25-27,32-34,…`). URLs: api `http://localhost:8080/v1/health` (`README:35-37`),
webapp-admin `http://localhost:8081`, Studio **`http://localhost:8082/studio`** — the prefix is not
stripped locally either, on purpose (`docker-compose.yml:121-125`).

Without compose (`README.md:43-54`): run `postgres:16` in docker, then
`dotnet run --project src/CryptoSmithX.Database` (applies the schema and exits; it does not create
the database, `README.md:45`), then the Hub and the Api. Every service refuses to start on a behind
schema (`Migrator.VerifyAsync`).

Two caveats for anyone following the README verbatim:
- `README.md:21-23` ("Only the `fake` adapter exists today") and `:64-76`
  (`MarketData__Exchanges__0__Enabled`, "enabled in both the configuration and the `exchange` table")
  are **stale**: four real adapters exist and the Hub reads all market-data configuration from the
  database, with no `MarketData` section anywhere (`Hub/DbSettings.cs:7-14`, `Hub/Program.cs:30-31`,
  `Hub/appsettings.json:1-10`).
- `docker compose -f deploy/docker-compose.yml build studio` fails on the missing Dockerfile stage
  (§1.3).

Browser-preview servers configured for the repo: `.claude/launch.json:4-5`
(`python3 -m http.server 4173 --directory src/web`, plus a scratchpad mirror on 4180). No dev-server
config for the .NET apps.

### 8.2 SDK and tests

`global.json:1-6` pins `sdk.version = 10.0.400`, `rollForward: latestFeature`; the machine has exactly
that (`~/.dotnet/dotnet --list-sdks` → `10.0.400`). Four test projects (`tests/*/*.csproj`), all xunit 2.9.2 +
`Microsoft.NET.Test.Sdk` 17.12.0 + `xunit.runner.visualstudio` 2.8.2; Connectors/Hub/Studio also take
`Microsoft.Extensions.TimeProvider.Testing` 9.5.0.

Verified in the survey session:
`~/.dotnet/dotnet test tests/CryptoSmithX.MarketData.Hub.Tests/CryptoSmithX.MarketData.Hub.Tests.csproj --nologo`
→ `Passed! - Failed: 0, Passed: 56, Skipped: 0, Total: 56`. The survey transcript was **truncated**
before it reported the other three projects or a solution-wide run; **marked incomplete** — no claim
is made here about `dotnet test CryptoSmithX.sln`.

### 8.3 Deploy paths

- **TEST**: `deploy/vps-deploy.sh:14-15` curls `deploy/docker-compose.prod.yml` from
  `raw.githubusercontent.com/.../main/` on every deploy and runs it; the traefik router file is
  installed on the host by hand (`deploy/traefik/cryptosmithx-webapp-studio.yml:5-10`).
- **PROD**: `.github/workflows/deploy.yml:92` copies `deploy/docker-compose.vm.yml` to
  `/opt/cryptosmithx/docker-compose.yml`; the GHCR token is ephemeral and piped over stdin
  (`:64-67`); `:91` pre-creates `/opt/cryptosmithx/webapp-admin-keys`, which no compose file mounts
  (§1.4). PostgreSQL is installed on the host by `deploy/provision-host.sh postgres`, which also
  generates `/opt/cryptosmithx/.env` once (`:195-220`).
- Migrations always run as their own container to completion before anything else starts (§1.5).
- Partitions are ensured by the Hub at startup and again in the daily retention pass
  (`ExchangeWorker.cs:68`; `Partitions.EnsureCurrentAndNextAsync` creates the current and next month
  only). Retention runs on a 24 h timer (`ExchangeWorker.cs:323-353`) and deletes by dataset-level
  `default_retention_days` only (`RetentionJob.cs:41-49`); `collector_run` has no retention at all
  (`RetentionJob.cs:29-36`).

### 8.4 Standing operational facts, 2026-09-07

1. **PROD is seven migrations behind** (0021–0027 unapplied), so it has no venue request budgets, no
   WEEX `ws_url`, no `asset_family*`, no `studio_reader`, and no Studio container.
2. **PROD's derived timeframes run pinned roughly 24 h behind the clock — advancing, not stalled.**
   The cause is as stated in §5.5: the `fake` segment row does not exist on PROD (measured:
   `select count(*) from segment where code='fake'` returns 0, and `collector_status` holds 0 rows
   for it), so `RollupJob`'s watermark write under `('fake','rollup')` never persists. Every pass
   therefore starts from a null watermark, `Window(null, startedAt)` takes the `ColdStartWindow`
   branch (`RollupJob.cs:86`) and rebuilds a window one day back. The effect is a fixed lag, not a
   stop: `market_candle` tf=5 `max(open_time)` measured 2026-09-06 05:50, then 06:35, then 06:45 at
   three increasing wall-clock times on 2026-09-07. Lags at 06:17 UTC: tf1 0.02 h, tf5 and tf15
   23.55 h, tf60 24.30 h, tf240 26.30 h, tf720 and tf1440 30.30 h — each timeframe additionally
   behind by its own un-closed window. `market_metric_hour` stops at 2026-09-06 05:00.
3. **TEST's `binance-usdm` is `maintenance`**, set by the operator after a reconnect incident, so
   nothing is built for it (`ExchangeWorker.cs:149-151`) even though its 697 instruments and 373,055
   1m bars remain in the tables.
4. PROD runs pre-rename containers and images (`cryptosmithx-webapp`), a CI target that no longer
   exists (`.github/workflows/deploy.yml:27`).
5. Two `collector_gap` rows were open at the time of measurement on TEST (weex-futures/depth,
   binance-usdm/depth); none on PROD.

---

## 9. Invariants

### 9.1 The rules the repository states about itself

| Where | What it states |
|---|---|
| `docs/decisions.md:50-76` | three-level `exchange → segment → dataset`; the admin reaches PostgreSQL directly with raw SQL |
| `docs/datagaps.md` | seven numbered absences plus "what is cheap to fix" |
| `docs/product-vision.md:71-72` | missing is not zero |
| `docs/recovery-playbook.md:7-9,15-27` | what is recoverable and what is not |
| `plans/studio-data-rework-brief.md:71-100` | **nine invariants** (below), plus `:34` "Do not delete anything." |
| `plans/consult-what-to-store.md:65-75` | replenishable vs unreplenishable, stated first |
| `plans/blueprint-studio.md:42-44,56-67,71-91,95-106` | read-only role; mandatory public predicates; the freshness window belongs to the call; clocks |
| `plans/architecture.mermaid:6,12` | Hub and WebApp share one database — "a decision, not an omission" |
| `src/web/ds-studio/readme.md:23-102` | **twelve design rules**; content fundamentals `:104-117`; iconography `:138-179`; corrections `:232-267` |
| `brand/rules/severity-and-colour.md` | three rules for the admin surface |
| migration headers | `0001:5-38`, `0003:4-12`, `0007:4-21`, `0014:11-48`, `0017:16`, the whole `0025` header, `0026:1-45`, `0027:1-26` |
| `CLAUDE.md` | **not found** (`.claude/` holds only `launch.json` and `settings.local.json`) |

The nine brief invariants (`plans/studio-data-rework-brief.md:71-100`): 1 never invent, `NULL ≠ 0`,
an outage produces no rows plus a gap record; 2 append-only history, no UPDATE/DELETE on observation
tables, `*_latest` the only mutable table and never a research source; 3 bitemporal `event_time` +
`known_from`; 4 no silent forward-fill, as-of lookups return `value + observed_at + age`; 5 instrument
identity and SCD2 specs, never overwritten; 6 raw is the asset, derived is a cache with
`derived_version`; 7 units explicit; 8 UTC from a disciplined clock, offset logged per run; 9 coverage
decisions causal.

The named rules the brief for this inventory expected, located: **bot boundary**
`0003_webapp.sql:10-12` ("Nobody ever calls into a bot… the bot keeps its own SQLite"),
`plans/architecture.mermaid:25,38-39`; **one database per service**
`plans/studio-data-rework-brief.md:31-32`; **raw SQL, no ORM** same line and `docs/decisions.md:52`;
**public endpoints only** `plans/studio-data-rework-brief.md:43`, `0001_initial.sql:25`,
`0007_runtime_settings.sql:21`; **missing is not zero** `docs/product-vision.md:71-72`,
`0001_initial.sql:170`, `0017_collection_health.sql:66-69`; **no LOCF**
`plans/studio-data-rework-brief.md:84-85`; **every figure carries the age of the call that wrote it**
`src/web/ds-studio/readme.md:25-28`, `0001_initial.sql:11-12,156-159`,
`plans/blueprint-studio.md:71-91`; **a dash is not a zero** `src/web/ds-studio/readme.md:70-73`,
implemented `Studio/Format.cs:18-28`.

### 9.2 Where the code contradicts a stated rule

**9.2.1 The absence channel has one producer in the entire codebase.**
`SnapshotCollector.cs:90-101` is the enforcement point ("an adapter signals 'the venue did not give
me this number' as NaN, because the columns here are NOT NULL", `:90-94`) and guards eight fields.
`double.NaN` is produced in exactly one line of `src/CryptoSmithX.MarketData.Connectors/`:
`Kraken/KrakenFuturesMarketData.cs:122`. Every other absence becomes a zero, because the DTOs declare
observation fields non-nullable with zero defaults: `Kraken/KrakenDtos.cs:36-55` (ten bare
`double`s), `Kraken/KrakenWsDtos.cs:10-28` (same ten, mapped straight through at
`KrakenWsFeed.cs:177-191`, so the WS path has no NaN case at all), `Weex/WeexDtos.cs:27-63`
(`string … = "0"`, parsed `WeexFuturesMarketData.cs:277`), `Hyperliquid/HyperliquidDtos.cs:23-31`
(only `MidPx` nullable, `:30`).
Live proof from the venue: of 296 REST tickers, **five omit the `last` key entirely** —
`PF_USDTUSD, PF_EURUSD, PF_CHFUSD, PF_OPENAIXUSD, PF_GBPUSD` — and those same symbols head the
`last_price = 0` list in both databases. `openInterest: 0` and `volumeQuote: 0` **are** present in
`PF_GBPUSD`'s payload, so some stored zeros are genuine; the two cases are indistinguishable after
the fact because no raw-payload archive exists (`docs/audit/site-facts.md:188`; the brief's WP2 at
`plans/studio-data-rework-brief.md:116-124` is unstarted — no `zstd`/`Parquet` reference in any
`.csproj`).

**9.2.2 History rows carry copied depth.** `SnapshotCollector.cs:181-199`: the keep-insert takes
price fields from the ticker parameters but reads the six depth columns and `depth_at` out of
`market_snapshot_latest` (`:193-196`). `depth_at` travels with them (`:179-180`), so the age is not
lost, but it breaks brief invariants 2 (`:76-78`) and 4 (`:84-85`). Measured over 24 h:

| segment | TEST rows / distinct `(instrument, depth_at)` | PROD |
|---|---|---|
| hyperliquid | 254,815 / 188,897, avg depth age 43.8 s, max 225 s | 1,345,023 / 221,797 (6.1×), avg 38.2 s |
| weex-futures | 1,416,081 / 838,374, avg 81.6 s, max 6,119 s | 6,731,434 / 208,019 (**32.4×**), avg 209.7 s, max 7,272 s |
| kraken-futures | 396,953 / 396,953 (1:1) | 2,281,244 / 2,281,244 (1:1) |
| binance-usdm | 263,240 with depth / 40,141 (6.6×) | segment `planned` |

**9.2.3 Twelve NOT NULLs make a dash unstorable.** `0001_initial.sql:119-143` declares
`received_at, last_price, bid_price, ask_price, bid_size, ask_size, mark_price, index_price,
funding_rate, turnover_24h, open_interest, open_interest_at` not null (lines 121,123-131,133,134);
only the seven depth columns are nullable (`:136-142`). This contradicts ds-studio rule 8
(`src/web/ds-studio/readme.md:70-73`, which says spot venues show dashes for mark/index/funding/OI),
and the product code states the contradiction against itself at
`Studio/Models/StudioModels.cs:36-43`. `segment.kind` already permits `'spot'`
(`0019_segment.sql:127-130`); no spot segment exists on either host.

**9.2.4 An unknown funding interval becomes a number.** `Market/Instrument.cs:29` is a non-nullable
`short`; `0001_initial.sql:82` is `smallint not null check (> 0)` — the schema cannot say "unknown".
WEEX falls back to 8 when the funding call missed a symbol (`WeexFuturesMarketData.cs:86-90`);
Kraken and Hyperliquid hardcode 1 with no observation (`KrakenFuturesMarketData.cs:25,76`,
`HyperliquidMarketData.cs:25,84`); Binance falls back to `BinanceMarkets.cs:43`
(`DefaultFundingIntervalHours = 8`), a value pinned by tests
(`tests/…/BinanceUsdmMarketDataTests.cs:117,125`). `DiscoveryCollector.cs:115` overwrites the column
every pass, against brief invariant 5; no `instrument_spec_version` or `funding_schedule` table
exists in 0001–0027.

**9.2.5 The Kraken `QtyStep` defect** — see §4.7; live in both databases.

**9.2.6 Kraken mixes two clocks in one row.** `depth_at` is **our** clock: `KrakenWsFeed.cs:69`
takes `_clock.GetUtcNow()` and passes it to `_books.TryGetDepth(..., now, ...)` (`:74`), and
`KrakenBookBuilder.cs:95,117` stamps `DepthMath.Compute(..., asOf)` with it — the book's own WS
timestamp (applied at `KrakenWsFeed.cs:204-205`) is discarded. `received_at` for the same row is
Kraken's instant (`KrakenWsFeed.cs:176`, `KrakenFuturesMarketData.cs:96`;
`SnapshotCollector.cs:201-206`). Measured on TEST over 24 h: `depth_at > received_at` on
**397,228 of 397,228 kraken-futures rows (100 %)** — hyperliquid 37, weex 9,705, binance 907;
`open_interest_at > received_at` on 34 weex rows and nowhere else. The render layer clamps
(`Studio/Format.cs:149-151`, `Studio/Data/Freshness.cs:48-51`), which keeps the page honest without
making `depth_at` an age.

**9.2.7 A gap is never recorded per instrument, and half the declared causes have no producer.**
`collector_gap.exchange_instrument_id` exists with its own index (`0017_collection_health.sql:50,78-80`)
and is never written (`ExchangeWorker.cs:446-455` names five columns); non-null on 0 rows on both
hosts. Four of eight causes are unreachable (§5.8). The two per-instrument absences the system
actually generates leave no record: a ticker dropped by `SnapshotCollector.cs:99` (a log line only,
`:238-240`) and a WEEX contract dropped by `WeexFuturesMarketData.cs:131-134`.

**9.2.8 WEEX discards observations the venue did make.** `WeexMarkets.cs:30`
(`IsLive(t) => Parse(t.Last) > 0 && Parse(t.Volume24h) > 0`) applied at
`WeexFuturesMarketData.cs:131-134` drops a contract with no 24 h trades even though the payload
carried bid, ask, mark, index and OI. The discovery half of this was fixed —
`WeexFuturesMarketData.cs:78-84` now reports `Halted` instead of dropping — the ticker half was not.

**9.2.9 Observation tables are mutable in four places** (brief invariant 2):
`CandleCollector.cs:110`, `RollupJob.cs:190`, `RollupJob.cs:306`, and the permitted cache update
`DepthCollector.cs:209`. `FundingCollector.cs:100` is the one append-only writer.
`market_candle` (`0001_initial.sql:218-235`) carries no `received_at`, no `known_from`, no
`close_time`, no `is_final`, no `derived_version` — only a mutable `updated_at` (`:230`).
`grep -rn "derived_version\|known_from\|event_time" src/CryptoSmithX.Database/Migrations/*.sql` →
**no hits**, so brief invariants 3 and 6 have no expression anywhere in the schema.

**9.2.10 "One DB per service" is contradicted, with a counter-decision on record.** All four services
point at `marketdata` (`Hub/appsettings.json:3`, `Api/appsettings.json:3`, `Admin/appsettings.json:3`,
`Studio/appsettings.json:3`, the last under the read-only role). Against
`plans/studio-data-rework-brief.md:32`; for it, `plans/architecture.mermaid:6` and
`docs/decisions.md:50-76`. Two live documents disagree; that is the finding.

**9.2.11 Minor:** `MarketData.Api/Endpoints.cs:170-180` collapses every venue onto one envelope
`asOf` (`select max(received_at)`), and exposes no `depthAgeSeconds`, so a consumer computing one
gets 9.2.6's negative number on every Kraken row. Per-row ages are intact (`:137-138,154,161`).

**9.2.12 Minor:** `Admin/Data/ExchangeStore.cs:349-365` forward-fills the latency panel (carry at
`:361-362`) on a health surface, against brief invariant 4; the comment at `:352-356` records that
the previous version seeded the carry with `0.0` and that this was worse.

---

## 10. Gap table

One row per target dataset. "Kraken provides it" is what the venue was observed to serve on
2026-09-07 (§4.9). "Switchable in admin" means: does a `segment_dataset` cell exist for it and can a
human change its mode in the Edit-feed dialog — the dialog blocks `collect` and `on_demand` whenever
`we_implement` is false (`_FeedDialog.cshtml:113-117`).

| dataset | Kraken provides it (endpoint / channel + doc) | collected today | stored where | shown in Studio | switchable in admin | what is missing |
|---|---|---|---|---|---|---|
| **ticker** | REST `GET /derivatives/api/v3/tickers` (296 rows; https://docs.kraken.com/api/docs/futures-api/trading/get-tickers/) and WS feed `ticker`, ~1 msg/s (https://docs.kraken.com/api/docs/futures-api/websocket/ticker/) | **yes** — dataset `snapshot`, mode `collect`, interval 10 s from `dataset.default_interval_s` (all `segment_dataset` overrides NULL for kraken-futures); WS preferred, REST fallback (`KrakenFuturesMarketData.cs:89-95`) | `market_snapshot_latest` whole row (`SnapshotCollector.cs:132`, DDL `0001_initial.sql:119-142`); `market_snapshot` on keep passes every 60 s (`SnapshotCollector.cs:183`, `keep_interval_s`) | **yes** — 14 metric cells built at `Studio/Models/Cells.cs:198-254`, rendered `_PairTable.cshtml:231-344` | **yes** — Edit-feed dialog `POST /Admin/Exchanges/Feed/{id}` (`ExchangesController.cs:156-186` → `FeedStore.cs:209-217`); mode, interval, keep-every, retention, transport, note | the `Ticker` record binds 14 fields, 10 of them numeric (`Market/Ticker.cs:13-27`); Kraken publishes ≥18 REST keys and ≥27 WS keys. Absence travels only as `double.NaN` and is produced in one line for one field (§9.2.1). REST funding is absolute and WS funding relative (`KrakenFuturesMarketData.cs:116-122` vs `KrakenWsFeed.cs:187`). `received_at` is the venue's clock, `depth_at` ours (§9.2.6) |
| ticker field: **predicted funding** | REST `fundingRatePrediction` (absolute; present on 280 of 296 = the perpetual rows); WS `funding_rate_prediction` **and** `relative_funding_rate_prediction` | **no** — not bound in `Kraken/KrakenDtos.cs:36-55` or `Kraken/KrakenWsDtos.cs:10-28` | **nowhere** — no column in `market_snapshot_latest`/`market_snapshot` (`0001_initial.sql:119-200`) | no | **no** — not a dataset, not a field on any form | DTO field, `Ticker` field, storage column, Studio cell. Note the REST value is absolute and the WS one is offered in both units |
| ticker field: **next funding time** | **WS `ticker.next_funding_rate_time` only** (observed `1788760800000`, an absolute epoch-ms instant, while the doc calls it "milliseconds until"). REST: **not found** — 0 of 296 tickers carry it | **no** | **nowhere** — `next_funding_at` was deliberately excluded at build time (`0001_initial.sql`, the "Что НЕ вошло" block ~lines 30-33); the only schedule datum is `exchange_instrument.funding_interval_hours` | no | **no** | DTO field, storage column, and a decision on the doc/wire unit disagreement. Collecting it makes the WS transport mandatory for the field |
| ticker field: **last trade time** | REST `lastTime` (ISO-8601 with ms, present on 283 of 296). WS: **not found** — the WS ticker carries `time` (server time) and `last` (price) but no last-trade timestamp | **no** | **nowhere** — `market_snapshot_latest` has `received_at` only (`0001_initial.sql:121`) | no | **no** | DTO field, storage column. The field exists on exactly one transport, which is the opposite of "next funding time" |
| ticker field: **base volume** | REST `vol24h` (base units) alongside `volumeQuote`; WS `volume` alongside `volumeQuote` | **partly** — only the quote figure is taken (`Ticker.Turnover24h`, semantics `Market/Ticker.cs:7-12`); `vol24h`/`volume` are unbound | `market_snapshot_latest.turnover_24h` (quote asset) | **yes**, as the "Turnover 24h" cell (`Models/Cells.cs:236`) | part of `snapshot`; no separate switch | a base-volume DTO field, column and cell. Both venues' numbers are already in the payload we parse |
| **L2 top-N with seq and is_snapshot** | WS feed `book`: `"feed":"book_snapshot"` carries `seq`, `tickSize`, full `bids`/`asks`; `"feed":"book"` carries one level plus `seq` (`qty:0` = removal). Sequence measured strictly contiguous (7292 deltas, no gap). **No depth selector** — `"depth":10` is ignored, snapshot returned 1760/993 levels (https://docs.kraken.com/api/docs/futures-api/websocket/book/). REST `/derivatives/api/v3/orderbook` has **no sequence** | **no, not as levels** — only six aggregated notional bands: `depth` dataset, 60 s, `GetOrderBookAsync` → `Depth` (`Market/Depth.cs:9-16`) computed by `DepthMath.cs:14-56` | `market_snapshot_latest.depth_bid_10bps … depth_ask_50bps, depth_at` (`DepthCollector.cs:209`), copied into `market_snapshot` on the snapshot keep pass (`SnapshotCollector.cs:193-196`) | **yes**, three cells: Depth 10/25/50 bps (`Models/Cells.cs:244,249,252`) | **yes** — `depth` cell; the dialog also states that its archive rate is set by the snapshot cell (`_FeedDialog.cshtml:219-224`, `archive_interval_s`) | a levels table, a `seq` column, a snapshot/delta marker, and any top-N at all. `KrakenBookBuilder.cs:170` holds `Seq` in memory and it never crosses `IExchangeMarketData` — `Depth` has no sequence field. Top-N would have to be truncated client-side |
| **trades** | WS feed `trade` (`trade_snapshot` of 100 + deltas: `uid, side, type, time, qty, price, seq`); REST `GET /derivatives/api/v3/history` (100 rows, ≤7 days or last engine restart; https://docs.kraken.com/api/docs/futures-api/trading/get-history/ — **404 on 2026-09-07, verified; the bound below is therefore UNSOURCED**); full history via `GET /api/history/v3/market/{symbol}/executions` with `since/before/sort/count≤1000/continuation_token`, reaching 2022-03-23 | **no** — `trades` is not in `KnownCollectorDatasets` (`ExchangeWorker.cs:36`, comment `:33-35`), there is no interface method (`IExchangeMarketData.cs`), and no adapter declares the capability | **nowhere** — no table on either host | **no** — `LiveRelevance.cs:29-67` has no case, default `false` | **row exists, control inert**: `segment_dataset('kraken-futures','trades')` mode `disabled`, `dataset.default_mode = 'disabled'`; the dialog disables the `collect`/`on_demand` chips because `we_implement=false`, and `_DataFeedsPanel.cshtml:140-171` renders it under "Nothing produced / no collector — the policy control on these rows changes nothing" | everything: interface method, adapter method, DTO, collector, table + partitioning, capability entry in five adapters, admin output group, Studio relevance case. Kraken's own `type` field is what makes row 8 possible, so the two are one decision |
| **liquidations** | **No dedicated feed** — `liquidations`, `liquidation`, `liquidations_lite` all rejected as invalid feeds. They arrive tagged inside the trade stream: measured `{fill 5279, "partial liquidation" 38, liquidation 1}` on REST and `partialLiquidation`/`liquidation` on WS — **the spelling differs between transports**, and the doc lists only `fill, liquidation, termination, block` (https://docs.kraken.com/api/docs/futures-api/websocket/trade/). Aggregate series: `analytics_type=liquidation-volume` | **no** — same three reasons as trades | **nowhere** | **no** | **row exists, control inert** — same as trades | everything, plus: per-event liquidation **backfill is not found** (the market-history `executions` stream carries no liquidation marker across 1000 sampled elements), so anything not captured live is only ever available as the daily `liquidation-volume` aggregate |
| **funding realized** | `GET /derivatives/api/v4/historical-funding-rates?symbol=` → `{timestamp, fundingRate (absolute), relativeFundingRate}` ascending; measured 8846–8847 rows ≈ a rolling year, one response, no pagination; gap histogram `{3600: 8838, 7200: 6, 10800: 1}` = **hourly** (https://docs.kraken.com/api/docs/futures-api/trading/historical-funding-rates). v3 of the same path 404s for `PF_` | **yes** — dataset `funding`, mode `collect`, 3600 s; `dataset_setting('funding','backfill_hours') = 168`; the endpoint takes no bounds so the adapter fetches the series and windows it client-side (`KrakenFuturesMarketData.cs:171-179`) | `funding_rate_history(exchange_instrument_id, funding_time, rate)`, `on conflict do nothing` (`FundingCollector.cs:98-100`); TEST 83,188 rows / 280 instruments, PROD 57,780 / 276 | **no** — `studio_reader` has no grant on this table (`0025_arena_reader.sql:214-227`); Studio's Funding cell comes from `market_snapshot_latest.funding_rate` and `market_metric_hour` | **yes** — `funding` cell in the dialog | only the **relative** rate is stored; the absolute `fundingRate` is discarded. The stored depth is the 168-hour backfill floor, not the ~1 year Kraken serves. No `known_from`, so a restatement is indistinguishable from an original. The admin's run page cannot attribute rows to a run (`ExchangeStore.cs:446-447,491`) |
| **funding predicted** | REST `fundingRatePrediction`; WS `funding_rate_prediction` + `relative_funding_rate_prediction`; also `analytics_type=funding` as OHLC of the rate (the only analytics type whose timestamps are ms, not s) | **no** | **nowhere** — `funding_rate_history` has three columns and no predicted-rate column | no | **no** — no dataset row, no field | a column or table, a DTO field, and a decision whether it belongs to the ticker (a per-poll observation) or to a funding series (a per-hour forecast, which is what `analytics funding` returns) |
| **funding schedule versions** | **not found** — no endpoint publishes a funding interval, a schedule or a version of one. `/derivatives/api/v3/instruments` carries only `fundingRateCoefficient: 8`, `maxRelativeFundingRate: 0.005` and (on 2 of 296) `minRelativeFundingRate` — current-value scalars with no effective-from. The only observable route is diffing timestamps in the realized series, itself capped at ~1 year | **no** | the only schedule datum stored is `exchange_instrument.funding_interval_hours`, **hard-coded to 1 for Kraken** (`KrakenFuturesMarketData.cs:25,76`), `smallint not null check (> 0)` (`0001_initial.sql:82`), overwritten every discovery pass (`DiscoveryCollector.cs:115`) | no | **no** | a versioned table, and a source. Kraken publishes no schedule, so any "version" could only be inferred from the realized series |
| **OI history** | `GET /api/charts/v1/analytics/{symbol}/open-interest?since&to&interval` → OHLC quadruple of decimal strings per bucket, epoch-**second** timestamps; retention measured back to **2023-03-02**; page cap measured at exactly **2000 points** with `more:true`; `interval` ∈ {60,300,900,1800,3600,14400,43200,86400,604800}, others HTTP 400 (https://docs.kraken.com/api-reference/analytics/market-analytics). Current value also in `/tickers.openInterest` and WS `ticker.openInterest` | **only the current point**, inline in the ticker — `Ticker.OpenInterest` + `OpenInterestAt`, which on Kraken shares the ticker's timestamp (`KrakenFuturesMarketData.cs:125-126`) | `market_snapshot_latest.open_interest, open_interest_at` and, on keep passes, `market_snapshot` | **yes**, as the "Open interest" cell (`Models/Cells.cs:508`) and the hourly `MetricHourSeries.OpenInterest`; `open_interest` is one of the four codes that redraw the page (`LiveRelevance.cs:29-67`) | **row exists, control inert** — `segment_dataset('kraken-futures','open_interest')` mode `disabled`, `dataset.default_mode='disabled'`, seeded note "Carried inline in the snapshot ticker on every venue we run today" (`0014_collections.sql:220-224`); chips blocked because `we_implement=false` | a series of its own: no table, no analytics client (nothing in the repo calls `/api/charts/v1/analytics`), no backfill. What is stored is a point sample at the snapshot cadence, thinned to the keep cadence |
| **mark-price candles** | `GET /api/charts/v1/mark/{symbol}/{resolution}`; `GET /api/charts/v1` enumerates the tick types as `["mark","spot","trade"]`; verified equal to `ticker.markPrice` at the 1m close. `volume` is always `"0"` on this series | **no** — `KrakenFuturesClient.cs:61-62` hard-codes `trade` and it is the only charts URL in the project | **nowhere it could be told apart** — `market_candle`'s PK is `(exchange_instrument_id, timeframe, open_time)` (`0001_initial.sql:218-235`); there is no series/tick-type column, so a second series cannot coexist with the trade series | no — `Studio/Data/CandleStore.cs:68` reads `market_candle` with no series notion | **no** — `candles` is one dataset cell covering the trade series; there is no per-series switch | a tick-type parameter on the client, a series discriminator in the schema (or a separate table), a `Candle` that admits `volume = 0` as meaningful, and a rollup decision (`RollupJob` aggregates by `(instrument, timeframe)` only) |
| **index-price candles** | `GET /api/charts/v1/spot/{symbol}/{resolution}` — **there is no `index` tick type**; `/api/charts/v1/index/...` returns HTTP 400 `Invalid tick type`. Proved to be the index series by exact equality with `ticker.indexPrice` at the 1m close. `volume` always `"0"` | **no** — same single hard-coded path | **nowhere** — same PK problem as mark candles | no | **no** | the same four things as mark candles, plus the naming trap: the series a reader would look for under "index" is served under `spot` |
| **instrument spec versions** | `GET /derivatives/api/v3/instruments` returns current state only (296 instruments) — `openingDate` exists (mapped to `ListedAt`), but there is no version, no effective-from and no history endpoint. **Not found** | **current state only** — `discovery`, 3600 s, upserted in place (`DiscoveryCollector.cs:93-125`), delisting time-driven (`:156-170`) | `exchange_instrument` (24 columns) plus `raw_json`; every field overwritten each pass, so the previous spec is gone. `first_seen_at`, `last_seen_at`, `status_changed_at` are the only history — `status_changed_at > first_seen_at` on 19 of 2,258 rows (TEST) and 3 of 1,532 (PROD) | indirectly: symbol and status only | **yes** — the `discovery` cell (interval, mode); instrument-level `collect` exists on the instrument page (`exchange_instrument.collect`, `0008_instrument_admin.sql:30-34`) and is `true` on every row on both hosts | an SCD2 table — brief invariant 5 (`plans/studio-data-rework-brief.md:86-88`) is unimplemented and `grep` finds no `known_from`/`event_time` anywhere in 0001–0027. Also: the specs currently written are wrong for 16 Kraken instruments (§4.7 `QtyStep`), and `funding_interval_hours` is a constant, not an observation |
| **per-feed gaps** | not applicable — the venue publishes nothing about our collection; this is entirely ours | **yes, partly** — `CollectorLoop` reports every pass (`CollectorLoop.cs:136`) and `ExchangeWorker` writes `collector_status` (`:360`), `collector_run` (`:399`) and opens/closes `collector_gap` (`:432`, `:448`), classifying the cause from the error text (`:463-472`). **WS feeds report nothing** (`CollectorLoop.cs:13-17`) | `collector_status` (26 rows TEST / 15 PROD), `collector_run` (126,841 / 51,390), `collector_gap` (365 / 304) | **no** — health is not on the public surface; `collector_status.avg_duration_ms` is deliberately not read (`Studio/Data/SegmentFreshnessStore.cs:18-24`). Studio reads `segment_dataset`/`dataset` intervals to size its freshness windows, not gap rows | **no** — health writing is unconditional and has no dataset cell; the admin only *displays* it (`_DataFeedsPanel.cshtml`, `Runs.cshtml`, `ExchangeStore.cs:391-407`) | `exchange_instrument_id` is never written (0 of 365 / 0 of 304), although the column and its index exist (`0017_collection_health.sql:50,78-80`); four of eight causes (`ws_sequence_gap`, `ws_disconnected`, `resync`, `collector_down`) have no producer; four `collector_run` columns (`exchange_instrument_id`, `http_status`, `request_weight`, `clock_offset_ms`) are never written; `collector_run` has no retention (`RetentionJob.cs:29-36`); `items` carries the expected-instrument count, not rows written (`ExchangeWorker.cs:399-408`) |

### 10.1 Where this document is knowingly incomplete

- The per-cell `segment_dataset` departures for the non-Kraken segments, and PROD's matrix in full
  (§2.6) — the owning survey was truncated.
- The per-segment breakdown of `market_metric_hour` (§5.8) — same.
- Studio's exact age arithmetic and fade thresholds (§6.6) — same, and those files are being edited
  by a concurrent workflow.
- Test results beyond `CryptoSmithX.MarketData.Hub.Tests` (56 passed) (§8.2) — same.
- Kraken charts resolutions beyond `1m, 5m, 15m, 30m, 1h, 4h, 12h, 1d, 1w` (§4.9) — the probe
  transcript was cut inside the list.

Nothing in this document was inferred from a survey claim that carried no pointer; where a pointer
was approximate (`RollupJob.cs:~176`, `:~262`) it was re-derived (`:184`, `:265`), and where two
surveys disagreed (funding writer line, `collector_gap` counts, the binance budget's provenance) both
readings are stated above with the resolution.
