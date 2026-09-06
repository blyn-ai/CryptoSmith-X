The hourly history behind a figure, where the backend keeps one.

```jsx
<Sparkline values={[5.1, 4.9, 4.6, 4.7]} call="ticker" hot />
<Sparkline values={oiHours} call="oi" />
```

Do not draw one for bid size, ask size, turnover or depth 10 / 50bps — those keep no rollup.
Use `CompareBar` there instead.
