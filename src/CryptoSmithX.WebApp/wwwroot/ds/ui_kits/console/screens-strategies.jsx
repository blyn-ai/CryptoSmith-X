window.CSXStrategies = function CSXStrategies() {
  const { Card, Tag, Button, Input, Select, Switch, StrategyCard, EquityCurve, Dialog } = window.CryptoSmithXDesignSystem_d88f99;
  const [sel, setSel] = React.useState('Momentum Perps v3');
  const [closeOnly, setCloseOnly] = React.useState(false);
  const [reduce, setReduce] = React.useState(true);
  const [confirm, setConfirm] = React.useState(false);
  const d = window.CSX_DATA;
  return (
    <main style={{ padding: '24px 30px', display: 'grid', gridTemplateColumns: '380px minmax(0,1fr)', gap: 20, alignItems: 'start' }}>
      <Card title="Strategies" pad={false}>
        {d.strategies.map((s) => (
          <div key={s.name} onClick={() => setSel(s.name)} style={{ cursor: 'pointer', background: sel === s.name ? 'var(--surface-raised)' : 'none' }}>
            <StrategyCard {...s} />
          </div>
        ))}
        <div style={{ padding: '16px 18px' }}><Button style={{ width: '100%' }}>New strategy</Button></div>
      </Card>
      <Card title={sel} pad={false} actions={<React.Fragment><Tag tone="gold">Running</Tag><Tag tone="violet">AI watchlist</Tag></React.Fragment>}>
        <EquityCurve points={[100,103,101,107,106,112,110,118,115,121]} height={140} style={{ padding: '14px 20px 0' }} />
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 16, padding: '18px 20px', borderTop: '1px solid var(--border-hairline)', marginTop: 10 }}>
          <Select label="Venue" options={['Hyperliquid', 'Kraken', 'Binance', 'WEEX']} defaultValue="Hyperliquid" />
          <Input label="Max position" mono defaultValue="0.50 BTC" />
          <Input label="Leverage" mono defaultValue="3×" />
          <Input label="Stop loss" mono defaultValue="−2.5%" />
          <Input label="Take profit" mono defaultValue="+6.0%" />
          <Select label="Cycle" options={['60 s', '120 s', '300 s']} defaultValue="120 s" />
        </div>
        <div style={{ display: 'flex', gap: 28, padding: '0 20px 18px' }}>
          <Switch checked={closeOnly} onChange={setCloseOnly} label="Trade on close only" />
          <Switch checked={reduce} onChange={setReduce} label="Reduce-only after drawdown" />
        </div>
        <div style={{ display: 'flex', gap: 10, padding: '16px 20px', borderTop: '1px solid var(--border-hairline)' }}>
          <Button>Save changes</Button><Button variant="ghost">Run backtest</Button>
          <Button variant="danger" style={{ marginLeft: 'auto' }} onClick={() => setConfirm(true)}>Stop strategy</Button>
        </div>
      </Card>
      {confirm && (
        <Dialog open title="Stop strategy?" danger confirmLabel="Stop" onConfirm={() => setConfirm(false)} onCancel={() => setConfirm(false)}>
          Open positions stay open; the bot just stops managing them.
        </Dialog>
      )}
    </main>
  );
};
