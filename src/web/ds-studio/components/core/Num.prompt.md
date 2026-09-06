Renders one figure — use it for every number on the surface, never a bare span.

```jsx
<Num value={6.4182} decimals={4} />
<Num value={null} decimals={4} />           {/* → "—", not 0 */}
<Num value={0.0031} decimals={4} signed percent tone="ticker" />
<Num value={8420000} decimals={0} unit="USD" opacity={0.42} />
```

`tone` ties a figure to its call (`ticker` / `oi` / `depth`); `opacity` is how the host's
freshness clock fades it. A dash never takes a tone — missing data has its own ink.
