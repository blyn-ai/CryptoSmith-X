The workhorse of the venue table — one per figure.

```jsx
<MetricCell value={6.4182} decimals={4} series={priceHours} hot age={3} />
<MetricCell value={1820} decimals={0} max={3200} age={3} />
<MetricCell bid={162000} ask={143000} max={248000} call="depth" tint="depth" age={11} best />
<MetricCell value={null} decimals={4} age={null} />   {/* spot venue: no mark price */}
```

Pass `series` OR `max`, never both: a column either has an hourly rollup or it doesn't.
