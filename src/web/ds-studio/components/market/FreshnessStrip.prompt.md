Goes in the first column, under the platform name.

```jsx
<FreshnessStrip calls={[
  { label: 'Price', seconds: 3 },
  { label: 'Depth', seconds: 11 },
  { label: 'OI', seconds: null }   // spot venue — no open-interest call
]} />
```

Past twelve windows it stops counting and says `live data degraded` instead.
