# Market data

Public market data, split into four projects that share one PostgreSQL database and nothing else —
no keys, no orders, no bots.

| Project | Kind | What it does |
|---|---|---|
| `CryptoSmithX.Database` | one-shot exe | Owns the schema. Applies the embedded migrations under an advisory lock and exits. Every other project reads the tables it creates but never migrates them. |
| `CryptoSmithX.MarketData.Connectors` | class library | Knows exchange wire formats: `IExchangeMarketData`, the `Market/` records, the `Fake/` adapter. No Postgres, no HTTP. |
| `CryptoSmithX.MarketData.Hub` | worker (Generic Host) | Collects tickers and candles, rolls up derived timeframes, retains snapshots, keeps partitions ahead of the writers. No HTTP server. |
| `CryptoSmithX.MarketData.Api` | ASP.NET minimal API | The read-only `/v1/*` surface. Reads tables only; derives spread, OI notional and ages on the way out. Port 8080. |

Reference graph — the Hub references Connectors and Database, the Api references Database, and
nothing references the Hub or the Api:

```
Connectors ← Hub
Database   ← Hub, Api
```

Only the `fake` adapter exists today. It runs in-process, needs no network, and produces
deterministic data, so the whole pipeline can be exercised end to end before a real venue is wired
up. Real adapters arrive one per pull request.

## Run it with compose

```bash
cp deploy/.env.example deploy/.env
docker compose -f deploy/docker-compose.yml up --build
```

Compose runs `postgres`, then the one-shot `database-migrator` to completion, then `hub` and `api`.
Then:

- health — <http://localhost:8080/v1/health>
- snapshot — <http://localhost:8080/v1/snapshot?exchange=fake>
- candles — <http://localhost:8080/v1/candles?exchange=fake&symbol=FAKE-BTC-USD&tf=5>

Instruments appear immediately, snapshots within ten seconds, 1-minute candles within a minute and
the first derived bars once a five-minute window has closed. There is no UI here — `/v1/*` is
curl-able and that is enough until `CryptoSmithX.WebApp.Admin` arrives.

## Run it locally

Postgres has to exist first; the migrator creates the schema but not the database.

```bash
docker run --rm -d --name csx-pg -p 5432:5432 \
  -e POSTGRES_DB=marketdata -e POSTGRES_USER=marketdata -e POSTGRES_PASSWORD=marketdata postgres:16

dotnet run --project src/CryptoSmithX.Database        # apply the schema, then exit
dotnet run --project src/CryptoSmithX.MarketData.Hub  # collect
dotnet run --project src/CryptoSmithX.MarketData.Api  # serve /v1 on :8080
```

The Hub's and the Api's `appsettings.json` default to that container; the migrator ships no settings
file (one would collide with theirs when they reference it) and falls back to the same localhost
string, or reads `ConnectionStrings__Database` from the environment. The Hub and the Api verify the
schema is present and current at startup and refuse to run against one that is behind — run
`CryptoSmithX.Database` first.

## Configuration

The Hub's settings live under `MarketData` in its `appsettings.json`; every key can be overridden by
an environment variable, double underscore per level. All three services read one connection string,
`ConnectionStrings:Database`:

```bash
MarketData__SnapshotIntervalSeconds=5
MarketData__Exchanges__0__Enabled=false
ConnectionStrings__Database="Host=...;Database=..."
```

An exchange is collected only when it is enabled in **both** the configuration and the `exchange`
table; the flag in the database is re-read on every discovery cycle, so a venue can be stopped
without a deploy.

The Api needs just one value from this world, `MarketData:SnapshotIntervalSeconds` (default 10), for
the 3× staleness window on `/health`. It reads it straight from configuration rather than sharing an
options type with the Hub.

## Schema

`CryptoSmithX.Database/Migrations/0001_initial.sql` is `plans/marketdata-schema.sql`, byte for byte.
Migrations run in exactly one place — the `CryptoSmithX.Database` exe — under a session advisory
lock, in file-name order, recorded in `schema_version`. Month partitions for `market_snapshot` and
`market_candle` are created by the Hub for the current and next month at startup and again by the
daily retention pass.

Snapshots are dropped after 90 days by dropping whole partitions. Candles are never dropped.
