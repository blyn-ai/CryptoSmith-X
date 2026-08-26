# Market data

One process: collectors, rollup, retention, migrations, the read-only API and its console. It
talks to exchanges and to its own PostgreSQL database, and to nothing else — no keys, no orders,
no bots. Everything in it is public market data.

Only the `fake` adapter exists today. It runs in-process, needs no network, and produces
deterministic data, so the whole pipeline can be exercised end to end before a real venue is
wired up. Real adapters arrive one per pull request.

## Run it with compose

```bash
cp deploy/.env.example deploy/.env
docker compose -f deploy/docker-compose.yml up --build
```

Then:

- console — <http://localhost:8080/>
- health — <http://localhost:8080/v1/health>
- snapshot — <http://localhost:8080/v1/snapshot?exchange=fake>

Instruments appear immediately, snapshots within ten seconds, 1-minute candles within a minute
and the first derived bars once a five-minute window has closed.

## Run it locally

Postgres has to exist first; the service creates its own schema but not its own database.

```bash
docker run --rm -d --name csx-pg -p 5432:5432 \
  -e POSTGRES_DB=marketdata -e POSTGRES_USER=marketdata -e POSTGRES_PASSWORD=marketdata postgres:16

dotnet run --project src/CryptoSmithX.MarketData
```

The default connection string in `appsettings.json` matches that container.

## Configuration

Everything lives under `MarketData` in `appsettings.json` and every key can be overridden by an
environment variable, double underscore per level:

```bash
MarketData__SnapshotIntervalSeconds=5
MarketData__Exchanges__0__Enabled=false
ConnectionStrings__MarketData="Host=...;Database=..."
```

An exchange is collected only when it is enabled in **both** the configuration and the `exchange`
table; the flag in the database is re-read on every discovery cycle, so a venue can be stopped
without a deploy.

## Schema

`Storage/Migrations/0001_initial.sql` is `plans/marketdata-schema.sql`, byte for byte. Migrations
run at startup under an advisory lock, in file-name order, recorded in `schema_version`. Month
partitions for `market_snapshot` and `market_candle` are created for the current and next month at
startup and again by the daily retention pass.

Snapshots are dropped after 90 days by dropping whole partitions. Candles are never dropped.

## The console

`wwwroot/` is plain HTML, one stylesheet and one script — no framework, no build step, nothing
fetched from a CDN. It reads the same public API as any other client; if a screen needs something
the API does not expose, that is a gap in the API rather than a reason for a private endpoint.

The design system is not copied into this project. The csproj links `src/web/ds` into
`wwwroot/ds` at build time, and `wwwroot/ds` is gitignored.
