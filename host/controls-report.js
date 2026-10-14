// Sums up a dump of the interface's controls (perfcmd "controls=LABEL", fetched to logs/controls/LABEL.txt):
// how large the controls are in millimetres, and how many are smaller than a fingertip needs.
// usage: node host/controls-report.js logs/controls/LABEL.txt [pixels per mm, default 17.8] [--all]
const fs = require('fs');
const file = process.argv[2], perMm = parseFloat(process.argv[3]) || 17.8, all = process.argv.includes('--all');
const lines = fs.readFileSync(file, 'utf8').split('\n').filter(x => x.trim());
const rows = [];
for (const line of lines) {
  if (line.startsWith('window ') || line.startsWith('#') || / controls, /.test(line)) { console.log(line); continue; }
  const m = line.match(/^(.+) (-?\d+) (\d+) (\d+) (\d+) (\d+)$/);
  if (m) rows.push({ kind: m[1], window: +m[2], x: +m[3], y: +m[4], w: +m[5], h: +m[6] });
}
const mm = v => (v / perMm).toFixed(1);
const buckets = [[0, 2.5], [2.5, 3.5], [3.5, 5], [5, 7], [7, 1e9]];
console.log('smaller side      controls');
for (const [a, b] of buckets) {
  const n = rows.filter(r => Math.min(r.w, r.h) / perMm >= a && Math.min(r.w, r.h) / perMm < b).length;
  console.log(('  ' + a + (b > 1e8 ? ' mm and more' : ' - ' + b + ' mm')).padEnd(18) + n);
}
// Distinct sizes, most frequent first.
const sizes = new Map();
for (const r of rows) { const k = r.kind + ' ' + r.w + 'x' + r.h; sizes.set(k, (sizes.get(k) || 0) + 1); }
console.log('sizes (px, mm), most frequent first');
for (const [k, n] of [...sizes].sort((a, b) => b[1] - a[1]).slice(0, all ? 1000 : 25)) {
  const [w, h] = k.split(' ').pop().split('x').map(Number);
  console.log('  ' + String(n).padStart(4) + '  ' + k.padEnd(24) + mm(w) + ' x ' + mm(h) + ' mm');
}
if (all) for (const r of rows) console.log([r.kind, r.window, r.x, r.y, r.w, r.h, mm(r.w) + 'x' + mm(r.h) + 'mm'].join(' '));
