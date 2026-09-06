window.CSX_DATA = {
  kpis: [
    { label: 'Equity', value: '$128,430', delta: '↑ +$1,284.10 · 24h', deltaTone: 'up' },
    { label: 'Unrealized PnL', value: '+$842.55', delta: '↑ +0.66%', deltaTone: 'up' },
    { label: 'Open positions', value: '7', delta: '4 long · 3 short', deltaTone: 'muted' },
    { label: 'Exposure', value: '42%', delta: 'limit 60%', deltaTone: 'muted' },
    { label: 'Win rate · 30d', value: '61.4%', delta: '212 trades', deltaTone: 'muted' },
  ],
  venues: [
    { name: 'Kraken', latency: '142ms' }, { name: 'Binance', latency: '96ms' },
    { name: 'WEEX', ok: false }, { name: 'Hyperliquid', latency: '88ms' },
  ],
  strategies: [
    { name: 'Momentum Perps v3', status: 'running', ai: true, metrics: [{ label: 'PnL 30d', value: '+4.8%', tone: 'up' }, { label: 'DD', value: '−2.1%' }, { label: 'trades', value: '96' }] },
    { name: 'Grid BTC/USD', status: 'running', metrics: [{ label: 'PnL 30d', value: '+1.9%', tone: 'up' }, { label: 'DD', value: '−0.8%' }, { label: 'trades', value: '301' }] },
    { name: 'Funding Harvest', status: 'paused', metrics: [{ label: 'PnL 30d', value: '−0.3%', tone: 'down' }, { label: 'DD', value: '−1.2%' }, { label: 'trades', value: '44' }] },
  ],
  positions: [
    { market: 'BTC-PERP', venue: 'Hyperliquid', side: 'long', size: '0.42 BTC', entry: '96,410.00', mark: '98,102.50', upnl: '+$710.85' },
    { market: 'ETH-PERP', venue: 'Hyperliquid', side: 'short', size: '6.0 ETH', entry: '4,821.00', mark: '4,760.20', upnl: '+$364.80' },
    { market: 'SOL/USD', venue: 'Kraken', side: 'long', size: '180 SOL', entry: '231.40', mark: '228.95', upnl: '−$441.00' },
    { market: 'XRP/USDT', venue: 'Binance', side: 'long', size: '9,400 XRP', entry: '2.841', mark: '2.863', upnl: '+$206.80' },
  ],
  curve: [100, 102, 101, 105, 104, 109, 108, 114, 112, 119, 117, 124, 122, 130],
};
window.CSXDashboard = function CSXDashboard() {
  const { KpiTile, Card, Tabs, EquityCurve, VenueStatus, StrategyCard, PositionsTable, Button } = window.CryptoSmithXDesignSystem_d88f99;
  const [range, setRange] = React.useState('1W');
  const d = window.CSX_DATA;
  return (
    <main style={{ padding: '24px 30px', display: 'grid', gap: 20 }}>
      <section style={{ display: 'grid', gridTemplateColumns: 'repeat(5,1fr)', gap: 14 }}>
        {d.kpis.map((k) => <KpiTile key={k.label} {...k} />)}
      </section>
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 340px', gap: 20 }}>
        <Card title="Equity curve" pad={false} actions={<Tabs items={['1D','1W','1M','ALL']} value={range} onChange={setRange} />}>
          <EquityCurve points={d.curve} height={240} style={{ padding: '14px 20px 0' }} />
          <VenueStatus venues={d.venues} style={{ padding: '14px 20px', borderTop: '1px solid var(--border-hairline)', marginTop: 8 }} />
        </Card>
        <Card title="Strategies" pad={false}>
          {d.strategies.map((s, i) => <StrategyCard key={s.name} {...s} />)}
          <div style={{ display: 'flex', gap: 10, padding: '16px 18px' }}>
            <Button>New strategy</Button><Button variant="ghost">Backtest</Button>
          </div>
        </Card>
      </div>
      <Card title="Open positions" pad={false}>
        <PositionsTable rows={d.positions} />
      </Card>
    </main>
  );
};
