The first column of the venue-comparison table.

```jsx
<VenueCell platform="Bybit" kind="perp" title="Price 12:03:41Z · OI 12:03:30Z · Depth 12:03:40Z"
  calls={[{ label: 'Price', seconds: 1 }, { label: 'Depth', seconds: 8 }, { label: 'OI', seconds: 19 }]} />
```

The absolute timestamps live in `title` — the columns that used to hold them were dropped
once every cell carried its own age.
