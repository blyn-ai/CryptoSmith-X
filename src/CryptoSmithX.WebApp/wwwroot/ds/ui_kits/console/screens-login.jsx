const csxLoginStyles = { wrap: { minHeight: '100vh', display: 'grid', placeItems: 'center', background: 'var(--wash-gold), var(--wash-violet), var(--surface-page)' } };
window.CSXLogin = function CSXLogin({ onLogin }) {
  const { Wordmark, Input, Button, Checkbox } = window.CryptoSmithXDesignSystem_d88f99;
  const [remember, setRemember] = React.useState(true);
  return (
    <div style={csxLoginStyles.wrap}>
      <div style={{ width: 400, maxWidth: '92vw' }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', marginBottom: 28 }}>
          <img src="../../assets/cryptosmith-mark.svg" width="56" height="56" alt="" style={{ marginBottom: 16 }} />
          <Wordmark size={30} descriptor />
        </div>
        <div style={{ background: 'var(--surface-card)', border: '1px solid var(--border-card)', borderRadius: 'var(--radius-lg)', padding: '26px 26px 24px', display: 'flex', flexDirection: 'column', gap: 16 }}>
          <Input label="Email" placeholder="you@example.com" />
          <Input label="Password" type="password" placeholder="••••••••" />
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <Checkbox checked={remember} onChange={setRemember} label="Remember me" />
            <a href="#" onClick={(e) => e.preventDefault()} style={{ font: '400 12.5px var(--font-body)' }}>Forgot?</a>
          </div>
          <Button size="lg" onClick={onLogin} style={{ width: '100%' }}>Sign in</Button>
        </div>
        <p style={{ textAlign: 'center', marginTop: 18, font: '400 11px var(--font-mono)', letterSpacing: '.08em', color: 'var(--text-faint)' }}>MULTI-USER · ADMIN / TRADER / VIEWER</p>
      </div>
    </div>
  );
};
