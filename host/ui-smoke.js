// UI smoke test of the running game (normally the test package): the same walk through the interface every time,
// a screenshot of each stop and the errors logged on the way.
//   RW_DRIVER_PORT=47198 node host/ui-smoke.js <name> [save]      (a driver must be serving: host/mcp/driver.js serve)
// Screenshots go to logs/smoke/<name>/NN-stop.png, the report to logs/smoke/<name>/report.txt. The save (default
// mitopia-calm) is loaded with "load anyway" when its mods differ; nothing is saved.
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = Number(process.env.RW_DRIVER_PORT || 47198);
const name = process.argv[2] || 'run';
const saveName = process.argv[3] || 'mitopia-calm';
const out = path.join(__dirname, '../logs/smoke', name);
fs.mkdirSync(out, { recursive: true });
const report = [];
let stop = 0;

function call(tool, args) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify({ name: tool, args: args || {} });
    const req = http.request({ host: '127.0.0.1', port: PORT, path: '/call', method: 'POST', headers: { 'Content-Type': 'application/json' } }, res => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', c => (data += c));
      res.on('end', () => resolve(data));
    });
    req.on('error', reject);
    req.setTimeout(90000, () => req.destroy(new Error('timeout')));
    req.end(body);
  });
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

function json(text) {
  const start = text.indexOf('{');
  try { return JSON.parse(text.slice(start)); } catch (e) { return null; }
}

async function state() { return json(await call('rw_state', {})) || {}; }

function windows(s) { return (s.windows || []).map(w => w.type); }

async function shot(label) {
  stop++;
  const text = await call('rw_screenshot', { scale: 0.3 });
  const m = /\[image ([^\]]+)\]/.exec(text);
  const file = path.join(out, String(stop).padStart(2, '0') + '-' + label + '.png');
  if (m) fs.copyFileSync(m[1], file);
  const log = await call('rw_log', { all: false });
  const errors = /([0-9]+) errors \(([0-9]+) distinct\)/.exec(log);
  const s = await state();
  report.push('== ' + String(stop).padStart(2, '0') + ' ' + label + ': ' + (errors ? errors[1] + ' errors (' + errors[2] + ' distinct)' : 'log ?') + '; windows ' + windows(s).filter(w => !/ImmediateWindow|MiniMap/.test(w)).join(', '));
  if (errors && errors[1] !== '0') report.push(log.split('\n').filter(l => l.trim() && !/^== rw_log/.test(l)).slice(0, 40).join('\n'));
  console.log(report[report.length - (errors && errors[1] !== '0' ? 2 : 1)]);
}

async function closeAll(keep) {
  for (let i = 0; i < 12; i++) {
    const s = await state();
    const ws = windows(s).filter(w => !/ImmediateWindow|MiniMap|MainTabWindow_Inspect/.test(w) && !(keep || []).some(k => w.includes(k)));
    if (ws.length === 0) return;
    const w = ws[ws.length - 1];
    if (w.includes('NewFactionSpawning')) await call('rw_click', { text: '忽略' });
    else await call('rw_close_window', { type: w.split('.').pop() });
    await sleep(500);
  }
}

async function click(text, wait) {
  const r = await call('rw_click', { text });
  await sleep(wait || 1200);
  return !/error|nothing on the screen/.test(r);
}

async function waitFor(test, seconds) {
  for (let i = 0; i < seconds; i++) {
    const s = await state();
    if (test(s)) return s;
    await sleep(1000);
  }
  return null;
}

async function main() {
  const started = Date.now();
  await waitFor(s => s.program === 'Entry' && !s.loading, 240);
  report.push('mods: ' + (process.env.SMOKE_MODS || '?'));
  await call('rw_log', {});
  await closeAll();
  await shot('menu');

  // Options: the interface page (the port's own rows are at its end) and the mods' settings.
  if (await click('选项')) {
    await click('界面');
    await shot('options-interface');
    await call('rw_swipe', { x1: 1500, y1: 900, x2: 1500, y2: 400, ms: 500 });
    await sleep(800);
    await shot('options-interface-scrolled');
    if (await click('Mod选项')) await shot('options-mods');
    await closeAll();
  }

  // The save: its row's own load button.
  await click('载入', 1500);
  const screen = json(await call('rw_screen', { contains: '' , max: 400 })) || {};
  const row = (screen.texts || []).find(t => t.t === saveName);
  const button = row && (screen.controls || []).find(c => c.t === '载入' && Math.abs(c.r[1] - row.r[1]) < 40);
  if (!button) { report.push('save ' + saveName + ' not found'); return finish(started); }
  await call('rw_tap', { x: button.r[0] + button.r[2] / 2, y: button.r[1] + button.r[3] / 2 });
  await sleep(3000);
  await call('rw_click', { text: '仍然载入' });
  const playing = await waitFor(s => s.program === 'Playing' && !s.loading, 120);
  if (!playing) { report.push('the save did not load'); await shot('load-failed'); return finish(started); }
  await sleep(3000);
  await closeAll();
  await shot('map');

  for (const tab of ['工作', '管制', '方案', '动物', '研究', '任务', '世界']) {
    if (await click(tab, 1500)) await shot('tab-' + tab);
    await closeAll();
  }
  if (await click('建筑规划')) {
    await click('生产');
    await shot('architect-production');
    await closeAll();
  }

  // A colonist and the inspect tabs.
  const found = json(await call('rw_find', { query: '', kind: 'pawn', limit: 60 })) || {};
  const colonist = (found.things || []).find(t => t.faction === 'player' && t.def === 'Human');
  if (colonist) {
    await call('rw_select', { id: colonist.id });
    await sleep(1200);
    await shot('colonist');
    for (const tab of ['健康', '需求', '角色', '装备']) {
      if (await click(tab)) await shot('inspect-' + tab);
    }
    await closeAll();
  }

  // A workbench's bills.
  for (const def of ['ElectricStove', 'FueledStove', 'TableButcher', 'ElectricTailoringBench', 'HandTailoringBench', 'CraftingSpot']) {
    const bench = (json(await call('rw_find', { query: def, kind: 'building', limit: 1 })) || {}).things;
    if (bench && bench.length) {
      await call('rw_select', { id: bench[0].id });
      await sleep(1000);
      if (await click('清单')) await shot('bills-' + def);
      await closeAll();
      break;
    }
  }

  // Trade with an orbital trader through the comms console.
  const consoles = (json(await call('rw_find', { query: 'CommsConsole', kind: 'building', limit: 1 })) || {}).things;
  if (colonist && consoles && consoles.length) {
    await call('rw_incident', { def: 'OrbitalTraderArrival' });
    await call('rw_select', { id: colonist.id, jump: false });
    await call('rw_camera', { x: consoles[0].cell[0], z: consoles[0].cell[1], zoom: 20 });
    await sleep(800);
    const c = (json(await call('rw_find', { query: 'CommsConsole', kind: 'building', limit: 1 })) || {}).things[0];
    await call('rw_hold', { x: c.screen[0], y: c.screen[1] });
    await sleep(1000);
    const menu = json(await call('rw_screen', { contains: '呼叫' })) || {};
    const option = (menu.controls || []).find(o => /运输舰|轨道/.test(o.t || ''));
    if (option) {
      await call('rw_tap', { x: option.r[0] + option.r[2] / 2, y: option.r[1] + option.r[3] / 2 });
      await call('rw_speed', { speed: 3 });
      const trading = await waitFor(s => windows(s).some(w => w.includes('Trade')), 20);
      await call('rw_speed', { speed: 0 });
      if (trading) {
        await closeAll(['Trade']);
        await shot('trade');
      } else {
        await shot('trade-not-opened');
      }
    } else {
      await shot('comms-menu');
    }
    await closeAll();
  }
  return finish(started);
}

function finish(started) {
  report.push('took ' + Math.round((Date.now() - started) / 1000) + ' s');
  fs.writeFileSync(path.join(out, 'report.txt'), report.join('\n') + '\n');
  console.log('report: ' + path.join(out, 'report.txt'));
}

main().catch(e => { report.push('failed: ' + e.stack); finish(Date.now()); });
