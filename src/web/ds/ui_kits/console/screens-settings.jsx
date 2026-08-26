window.CSXSettings = function CSXSettings() {
  const { Card, Input, Select, Switch, Button, Tag } = window.CryptoSmithXDesignSystem_d88f99;
  const [notif, setNotif] = React.useState(true);
  const [twofa, setTwofa] = React.useState(true);
  const keys = [
    { venue: 'KRAKEN', key: 'krkn_a81f········9c2e', scope: 'trade', ok: true },
    { venue: 'BINANCE', key: 'bnc_77d3········01aa', scope: 'trade', ok: true },
    { venue: 'WEEX', key: '—', scope: '—', ok: false },
    { venue: 'HYPERLIQUID', key: '0x4c9a········e11f', scope: 'trade', ok: true },
  ];
  const team = [
    { name: 'd.bykovas', role: 'Admin' }, { name: 'l.peciukonis', role: 'Admin' }, { name: 'guest.viewer', role: 'Viewer' },
  ];
  const rowFont = { font: '400 12.5px var(--font-mono)', color: 'var(--text-body)' };
  return (
    <main style={{ padding: '24px 30px', display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 20, alignItems: 'start', maxWidth: 1160 }}>
      <Card title="Profile">
        <div style={{ display: 'grid', gap: 16 }}>
          <Input label="Display name" defaultValue="d.bykovas" />
          <Input label="Email" defaultValue="denisas@blynai.eu" />
          <div style={{ display: 'flex', gap: 28 }}>
            <Switch checked={twofa} onChange={setTwofa} label="Two-factor auth" />
            <Switch checked={notif} onChange={setNotif} label="Fill notifications" />
          </div>
        </div>
      </Card>
      <Card title="Risk limits">
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16 }}>
          <Input label="Max exposure" mono defaultValue="60%" />
          <Input label="Max daily loss" mono defaultValue="−3.0%" />
          <Input label="Max leverage" mono defaultValue="5×" />
          <Input label="Per-market cap" mono defaultValue="$25,000" />
        </div>
      </Card>
      <Card title="API keys" pad={false}>
        {keys.map((k, i) => (
          <div key={k.venue} style={{ display: 'flex', alignItems: 'center', gap: 14, padding: '13px 20px', borderBottom: i < keys.length - 1 ? '1px solid var(--border-hairline)' : 0 }}>
            <b style={{ font: '500 11.5px var(--font-mono)', letterSpacing: '.08em', color: 'var(--text-heading)', width: 100 }}>{k.venue}</b>
            <span style={rowFont}>{k.key}</span>
            <span style={{ marginLeft: 'auto' }}>{k.ok ? <Tag tone="gold">Connected</Tag> : <Tag tone="neutral">Not set</Tag>}</span>
            <Button variant="quiet" size="sm">{k.ok ? 'Rotate' : 'Add key'}</Button>
          </div>
        ))}
      </Card>
      <Card title="Team" pad={false}>
        {team.map((m, i) => (
          <div key={m.name} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '11px 20px', borderBottom: i < team.length - 1 ? '1px solid var(--border-hairline)' : 0 }}>
            <img src="../../assets/cryptosmith-coin.svg" width="22" height="22" alt="" />
            <span style={rowFont}>{m.name}</span>
            <span style={{ marginLeft: 'auto', width: 120 }}><Select options={['Admin', 'Trader', 'Viewer']} defaultValue={m.role} /></span>
          </div>
        ))}
        <div style={{ padding: '14px 20px' }}><Button variant="ghost" size="sm">Invite member</Button></div>
      </Card>
    </main>
  );
};
