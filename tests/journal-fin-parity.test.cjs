const { readFileSync } = require('node:fs');
const { strict: assert } = require('node:assert');
const { test } = require('node:test');
const html = readFileSync(require('node:path').join(__dirname, '../src/CryptoSmithX.WebApp.Admin/wwwroot/ui-mocks/zurnalas.html'), 'utf8');
const template = JSON.parse(html.match(/<script type="__bundler\/template">\s*([\s\S]*?)\s*<\/script>/)[1]);
const DCLogic = class {};
const Component = eval(template.slice(template.indexOf('    function dayResult(day)'), template.lastIndexOf('</script>')) + '\nComponent;');
const component = () => { const c = new Component(); c.props = {}; c.symbol = '$'; return c; };
test('cards rank on working margin and do not call a positive day the worst', () => {
  const c = component();
  c.state.data = { equity: { days: [
    { date: '2026-10-10', open: 60, close: 70, peakMarginEur: 50 },
    { date: '2026-10-11', open: 70, close: 74, peakMarginEur: 10 }
  ] } };
  const v = c.renderVals();
  assert.equal(v.proof[0].from, '10,00 $');
  assert.equal(v.proof[0].to, '14,00 $');
  assert.equal(v.proof[1].from, '—');
  assert.match(v.proof[0].fine, /70,00.*74,00/);
});
test('zero or null TP is not a target; trailing has a profit scale', () => {
  const c = component();
  const p = { pair: 'ETH/USD', side: 'LONG', entryPrice: 100, lastPrice: 105,
    stopLossPrice: 98, takeProfitPrice: 0, quantity: 1, trailingStopPercent: 0.75 };
  const plain = c.positionRows({ positions: [p] })[0];
  assert.equal(plain.params.find(x => x.label === 'Tikslas (TP)').value, 'nenustatytas');
  const trail = c.positionRows({ positions: [{ ...p, trailingStopState: 'EXCHANGE_OPEN' }] })[0];
  assert.equal(trail.trailing, true);
  assert.match(trail.barRight, /skalė \+15/);
  assert.doesNotMatch(trail.barRight, /TP/);
  assert.match(trail.trailLabel, /orientacinis/);
  assert.equal(trail.params.find(x => x.label === 'Tikslas (TP)').value, 'atšauktas');
  const simulated = c.positionRows({ positions: [{ ...p, peakPnlPercent: 10, trailingStopState: 'SIMULATED_OPEN' }] })[0];
  assert.equal(simulated.trailing, true);
  assert.match(simulated.trailLabel, /109/);
  assert.match(simulated.barGradient, /linear-gradient/);
  assert.equal(simulated.hasPeak, true);
  assert.match(simulated.peakLabel, /Pikas/);
  assert.equal(trail.hasPeak, false);
});
test('unobserved gap is grey and separate from bot result', () => {
  const c = component();
  const chart = c.chart([{ date: '2026-10-10', open: 50, close: 55 },
    { date: '2026-10-11', open: 60, chainOpen: 60, close: 61, outage: true }], 62, 0);
  assert(chart.hits[0].rows.some(x => x.label === 'Stebėjimo spraga'));
  assert(chart.candles.some(x => x.fill === 'var(--text-faint)'));
});
test('risk scale is fixed at entry plus/minus ten percent with only SL and now ticks', () => {
  const c = component();
  const p = { pair: 'DOGE/USD', side: 'LONG', entryPrice: 100, lastPrice: 99, stopLossPrice: 98 };
  const a = c.positionRows({ positions: [p] })[0];
  const b = c.positionRows({ positions: [{ ...p, lastPrice: 104, stopLossPrice: 101 }] })[0];
  assert.equal(a.scaleMin, b.scaleMin);
  assert.equal(a.scaleMax, b.scaleMax);
  assert.equal(a.stopAt, '40.0%');
  assert.equal(a.markerAt, '45.0%');
  assert.equal(b.markerAt, '70.0%');
  const short = c.positionRows({ positions: [{ ...p, side: 'SHORT', lastPrice: 96, stopLossPrice: 102 }] })[0];
  assert.equal(short.markerAt, '70.0%');
  assert.equal(short.stopAt, '40.0%');
  assert(!template.includes('{{ p.peakAt }}'));
  assert(!template.includes('{{ p.barRight }}'));
});
test('TS range is frozen from initial stop, not the moving peak or current price', () => {
  const c = component();
  const p = { pair: 'ETH/USD', side: 'LONG', entryPrice: 100, lastPrice: 104,
    peakPnlPercent: 4, trailingStopPercent: 1, trailingStopState: 'SIMULATED_OPEN', trailingInitialStopPrice: 102 };
  const a = c.positionRows({ positions: [p] })[0];
  const b = c.positionRows({ positions: [{ ...p, lastPrice: 107, peakPnlPercent: 8 }] })[0];
  assert.equal(a.scaleMin, b.scaleMin);
  assert(Math.abs(a.scaleMin - 1.49) < 1e-9);
  assert.equal(a.scaleMax, 10);
  assert.match(a.scaleLeftLabel, /Pirmas TS/);
  assert.notEqual(a.stopAt, b.stopAt);
  assert.notEqual(a.markerAt, b.markerAt);
  assert.match(c.positionRows({ positions: [{ ...p, lastPrice: 115 }] })[0].nowLabel, /už skalės/);
  assert.match(c.positionRows({ positions: [{ ...p, trailingInitialStopPrice: null }] })[0].scaleNote, /neužfiksuota/);
});
