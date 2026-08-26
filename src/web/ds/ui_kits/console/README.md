# Console UI kit

The CryptoSmith X trading console — the product's core surface. Interactive click-through: sign in, then navigate Overview / Strategies / Settings from the top nav.

- `index.html` — app shell (login state + TopNav routing)
- `screens-login.jsx` — auth screen with the full wordmark lockup over the gold/violet washes
- `screens-dashboard.jsx` — Overview: KPI strip, equity curve + venue status, strategy list, positions table (also holds `window.CSX_DATA` sample data)
- `screens-strategies.jsx` — list + parameter editor, stop-strategy Dialog
- `screens-settings.jsx` — profile, risk limits, API keys per venue, multi-user team roles

All screens compose the design-system components from `window.CryptoSmithXDesignSystem_d88f99`; no styling outside the tokens.
