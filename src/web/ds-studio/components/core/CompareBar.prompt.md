The scale of a figure relative to the other venues on screen.

```jsx
<CompareBar value={1820} max={3200} call="ticker" />
<CompareBar value={2140000} max={2140000} call="oi" />
```

Always pass the column's own maximum — a bar against a global constant says nothing.
