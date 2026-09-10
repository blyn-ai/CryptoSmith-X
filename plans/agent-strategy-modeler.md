# Agent strategy modeler

## Purpose

Replace the Agent page's configuration-owned identity mapping and four-field
override form with a database-backed Strategy Modeler. The supplied
`strategijos-nustatymai.static.html` is a visual and interaction reference; it
is not copied into production because its bundled runtime is not part of this
application.

The modeler writes the trading bot's own PostgreSQL database. A futures worker
reads an active strategy profile before each decision and fast-exit check, then
reads the separate position-limit overrides. No deploy, container recreation or
manual worker action is required for a valid saved change to take effect.

## Sources of truth

| Concern | Source | Read/write rule |
| --- | --- | --- |
| Signed-in username -> bot instance | `bot_instance_webapp_users` | Read on every request; never accept an instance id from a form. |
| Strategy profile | `bot_strategy_profiles`, `bot_strategy_profile_revisions`, `bot_instance_strategy_profiles` | Read the active assignment; create an immutable revision and move the assignment in one transaction. |
| Capital and concurrency limits | `bot_config_overrides` | Read and write the four known override keys in the same transaction as a strategy save. |
| Missing strategy assignment | Worker `appsettings` fallback | The Agent must say that values are unavailable here rather than display its own stale mirror. |

## Editable field map

| UI field | Worker configuration key | Storage |
| --- | --- | --- |
| Position margin | `position_margin_usd` | `bot_config_overrides` |
| Leverage | `leverage` | `bot_config_overrides` |
| Maximum open positions | `max_open_positions` | `bot_config_overrides` |
| Maximum positions per group | `max_open_positions_per_group` | `bot_config_overrides` |
| Markets evaluated | `Trading.MaxActiveInstruments` | profile `values_jsonb` |
| Minimum 24h volume | `Trading.StrongMoverMinDailyVolumeEur` | profile `values_jsonb` |
| Maximum entry spread | `Strategy.MaxEntrySpreadPercent` | profile `values_jsonb` |
| LONG score threshold | `Strategy.MinimumLongScore` | profile `values_jsonb` |
| SHORT score threshold | `Shorts.MinShortScore` | profile `values_jsonb` |
| BTC crash threshold | `Regime.BtcCrashPct` | profile `values_jsonb` |
| BTC crash lookback | `Regime.BtcCrashLookback` | profile `values_jsonb` |
| Stop loss | `Exits.StopAtrMult` | profile `values_jsonb` |
| Trail activation | `Exits.TrailingActivationRMultiple` | profile `values_jsonb` |
| Maximum hold | `Exits.MaxHoldMinutes` | profile `values_jsonb` |
| Stop-loss cooldown | `ExecutionPolicy.CooldownAfterStopLossSeconds` | profile `values_jsonb`; UI minutes convert to seconds at the boundary |

The anti-chase card is read-only. Its component parameters remain deliberately
separate and are not represented by one invented aggregate value.

## Safety boundaries

- A browser can address only the instance mapped to the authenticated username.
- Values are validated server-side with the same units and ranges shown by the UI.
- Unsupported JSON sections and protected configuration are never written:
  credentials, live-trading flags, account identity, worker scheduling, candle
  timeframe, market-data wiring, universe wiring and correlation taxonomy.
- A save creates a complete revision from the current resolved profile and
  patches only the documented editable fields. No unrelated strategy value is
  erased by a partial form post.
- The revision insert, active-assignment update and four limit upserts share one
  database transaction, so a worker observes either the old model or the new
  model, never a mixture.
- Existing exchange orders are not recreated. Dynamic exit checks use the new
  profile at their next check; position sizing applies to new entries.

## Delivery checklist

- [x] 1. Inspect the Agent, the static design artifact and the trading worker's
  current strategy-profile contract. Commit this plan first.
- [x] 2. Add Agent data records and a trading-bot store for owner lookup,
  active-profile resolution and profile history. Add a focused Agent test project
  for mapping, authorization and SQL contract tests.
- [x] 3. Replace `TradingBotOptions.Instances` and `Baselines` in the Parameters
  controller with database ownership and the resolved active profile. Retain a
  clear no-assignment state instead of a synthetic baseline.
- [x] 4. Add the Strategy Modeler parameter catalogue: labels, units, ranges,
  default metadata, JSON paths and server-side validation. Keep the four capital
  limits and the twelve profile fields above as the only write surface.
- [ ] 5. Implement transactional save: derive the instance from the session,
  patch the resolved JSON, append an immutable revision, activate it, update the
  four runtime limits and record the supplied note. Add history reads.
- [ ] 6. Bind the native Razor page to the new model, following the supplied
  design's three groups, explanations, confirmation and revision history. Do not
  ship the static bundle itself.
- [ ] 7. Run the Agent and full solution tests, verify the worker sees a saved
  revision and the four database limits, then remove this plan when all items are
  complete as required by `plans/README.md`.

## Commit sequence

1. `Document Agent strategy modeler plan`
2. `Add Agent strategy profile data access`
3. `Read Agent settings from bot database`
4. `Add Agent strategy parameter catalogue`
5. `Save Agent strategy profile revisions`
6. `Bind Agent strategy modeler page`

Each implementation commit updates the checklist above before it is created.
