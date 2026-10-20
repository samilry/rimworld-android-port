#!/usr/bin/env node
// An MCP server (stdio, JSON-RPC, no dependencies) for playing and watching the 1.7 port on the phone from Claude:
// screenshots, real touches through adb (so the touch layer is what gets exercised), the game's own view of the screen
// and of the map through the in-game agent (mobile/Agent.cs, reached through adb's port forwarding), and the new lines
// of the game's log. Nothing of the user's is changed by starting it: the agent is switched on by a file in the game
// folder (game/agent.txt) and switched off again when this server ends.
//
// Register (Claude Code, project scope): .mcp.json at the repository root, {"mcpServers":{"rimworld-phone":
// {"command":"node","args":["port17/host/mcp/rimworld-mcp.js"]}}}. Test by hand: node host/mcp/test-client.js.
'use strict';
const { spawnSync, spawn } = require('child_process');
const crypto = require('crypto');
const net = require('net');
const path = require('path');
const png = require('./png');

const ADB = process.env.ADB || 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe';
// RW_PACKAGE: another package to drive, e.g. the side-by-side test APK (com.local.waylith.porttest).
const PKG = process.env.RW_PACKAGE || 'com.local.waylith.port';
const GAME = '/data/media/0/Android/data/' + PKG + '/files/game';
const PORT = Number(process.env.RW_AGENT_PORT || 47101);

function log(text) {
  process.stderr.write('[rimworld-mcp] ' + text + '\n');
}

// ---- adb ----

function adb(args, options = {}) {
  const result = spawnSync(ADB, args, { encoding: options.binary ? 'buffer' : 'utf8', maxBuffer: 256 * 1024 * 1024, timeout: options.timeout || 60000, windowsHide: true });
  if (result.error) throw new Error('adb: ' + result.error.message);
  if (result.status !== 0 && !options.allowFail) {
    const err = options.binary ? result.stderr.toString() : result.stderr;
    throw new Error('adb ' + args.join(' ') + ' failed: ' + (err || '').trim().slice(0, 300));
  }
  return result.stdout;
}

function shell(command, options) {
  return adb(['shell', command], options);
}

function rootShell(command, options) {
  return shell("su -c '" + command.replace(/'/g, "'\\''") + "'", options);
}

let appUid = null;
function uid() {
  if (!appUid) {
    const m = /appId=(\d+)/.exec(shell('dumpsys package ' + PKG + ' | grep -m1 appId'));
    if (!m) throw new Error('the game is not installed');
    appUid = m[1];
  }
  return appUid;
}

function gamePid() {
  return (shell('pidof ' + PKG, { allowFail: true }) || '').trim().split(/\s+/)[0] || null;
}

function putGameFile(name, content) {
  const tmp = '/data/local/tmp/rw-mcp-' + name;
  const local = path.join(require('os').tmpdir(), 'rw-mcp-' + process.pid + '-' + name);
  require('fs').writeFileSync(local, content);
  try {
    adb(['push', local, tmp]);
  } finally {
    require('fs').unlinkSync(local);
  }
  rootShell('cp ' + tmp + ' ' + GAME + '/' + name + ' && chown ' + uid() + ':1078 ' + GAME + '/' + name + ' && chmod 660 ' + GAME + '/' + name + ' && rm ' + tmp);
}

// ---- the in-game agent ----

let token = null;
let socket = null;
let buffered = '';
const waiting = [];

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function connectOnce() {
  return new Promise((resolve, reject) => {
    const s = net.connect({ host: '127.0.0.1', port: PORT }, () => resolve(s));
    s.once('error', reject);
  });
}

async function ensureAgent() {
  if (socket && !socket.destroyed) return;
  if (!gamePid()) throw new Error('the game is not running (rw_game start)');
  if (!token) {
    token = crypto.randomBytes(16).toString('hex');
    adb(['forward', 'tcp:' + PORT, 'tcp:' + PORT]);
    putGameFile('agent.txt', PORT + ' ' + token + '\n');
  }
  let lastError = null;
  for (let i = 0; i < 20; i++) {
    try {
      const s = await connectOnce();
      // The forwarded port accepts even when nothing listens on the phone: a ping tells.
      socket = s;
      buffered = '';
      s.setEncoding('utf8');
      s.on('data', chunk => {
        buffered += chunk;
        let nl;
        while ((nl = buffered.indexOf('\n')) >= 0) {
          const line = buffered.slice(0, nl);
          buffered = buffered.slice(nl + 1);
          const next = waiting.shift();
          if (next) next.resolve(line);
        }
      });
      s.on('close', () => {
        socket = null;
        while (waiting.length) waiting.shift().reject(new Error('the agent connection closed'));
      });
      s.on('error', () => {});
      const pong = await rawRequest({ token, cmd: 'ping' }, 3000);
      if (pong.ok) return;
      lastError = new Error(pong.error);
    } catch (e) {
      lastError = e;
      if (socket) socket.destroy();
      socket = null;
    }
    await sleep(500);
  }
  throw new Error('the in-game agent did not answer (is this a PortMobile with Agent.cs?): ' + (lastError && lastError.message));
}

function rawRequest(message, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      const i = waiting.indexOf(entry);
      if (i >= 0) waiting.splice(i, 1);
      reject(new Error('no answer from the agent within ' + timeoutMs / 1000 + ' s'));
    }, timeoutMs);
    const entry = {
      resolve: line => { clearTimeout(timer); try { resolve(JSON.parse(line)); } catch (e) { reject(e); } },
      reject: e => { clearTimeout(timer); reject(e); },
    };
    waiting.push(entry);
    socket.write(JSON.stringify(message) + '\n');
  });
}

async function agent(cmd, args) {
  await ensureAgent();
  const answer = await rawRequest({ token, cmd, args: args || {} }, 12000);
  if (!answer.ok) throw new Error(answer.error || 'the agent refused');
  delete answer.ok;
  return answer;
}

function switchAgentOff() {
  try {
    if (socket) socket.destroy();
    if (token) {
      rootShell('rm -f ' + GAME + '/agent.txt', { allowFail: true, timeout: 5000 });
      adb(['forward', '--remove', 'tcp:' + PORT], { allowFail: true, timeout: 5000 });
    }
  } catch (e) {
    log('switching the agent off: ' + e.message);
  }
}

// ---- the log ----

let logCursor = null;          // the phone's time of the last line read, "MM-DD HH:MM:SS.mmm"
let seenAtCursor = new Set();  // lines with exactly that time already reported (logcat -T is inclusive)

function phoneNow() {
  return shell('date +"%m-%d %H:%M:%S.000"').trim();
}

function maskSecrets(text) {
  return text.replace(/(sk-|key=|Bearer )[A-Za-z0-9_\-]+/g, '$1***');
}

function newLogLines() {
  if (!logCursor) {
    logCursor = phoneNow();
    return [];
  }
  const pid = gamePid();
  const out = adb(['logcat', '-d', '-v', 'threadtime', '-T', logCursor], { timeout: 30000 });
  const lines = [];
  let last = logCursor;
  const atLast = new Set();
  for (const raw of out.split('\n')) {
    const m = /^(\d\d-\d\d \d\d:\d\d:\d\d\.\d+)\s+(\d+)\s+\d+\s+\w\s+([^:]+?)\s*: ?(.*)$/.exec(raw.replace(/\r$/, ''));
    if (!m) continue;
    const [, time, linePid, tag, text] = m;
    if (time === logCursor && seenAtCursor.has(raw)) continue;
    if (time !== last) atLast.clear();
    last = time;
    atLast.add(raw);
    if (pid && linePid !== pid) continue;
    if (tag !== 'DOTNET' && tag !== 'port17') continue;
    lines.push({ time, text: maskSecrets(text.replace(/\x1b\[[0-9;]*m/g, '')) });
  }
  if (last !== logCursor) {
    logCursor = last;
    seenAtCursor = atLast;
  } else {
    for (const l of atLast) seenAtCursor.add(l);
  }
  return lines;
}

// The game's lines carry their own clock ("06:38:47  INFO ...") and a stack below most of them ("06:38:47    at ...").
function summarizeLog(lines, all, maxLines) {
  const groups = new Map();
  const plain = [];
  let current = null;
  for (const { text } of lines) {
    const body = text.replace(/^\d\d:\d\d:\d\d\s?/, '');
    const head = /^\s*(ERROR|WARN|INFO|DEBUG)\s+(.*)$/.exec(body);
    if (head) {
      current = null;
      if (head[1] === 'ERROR' || head[1] === 'WARN') {
        const key = head[1] + ' ' + head[2].replace(/\d+/g, '#').slice(0, 160);
        let g = groups.get(key);
        if (!g) groups.set(key, g = { level: head[1], text: head[2], count: 0, stack: [] });
        g.count++;
        current = g.count === 1 ? g : null;
      } else if (all && head[1] === 'INFO') {
        plain.push(head[2]);
      }
      continue;
    }
    if (/^\s+at /.test(body) || /^\[Ref [0-9A-F]+\]/.test(body) || /^\s+- (PREFIX|POSTFIX|TRANSPILER|FINALIZER)/.test(body)) {
      if (current && current.stack.length < 10) current.stack.push(body.trim());
      continue;
    }
    if (current && current.stack.length < 10 && /Exception|^\s/.test(body)) {
      current.stack.push(body.trim());
      continue;
    }
    current = null;
    if (all && body.trim() && !/Asset pack miss|cold textures|^vulkan:|^audio:/.test(body)) plain.push(body.trim());
  }
  const errors = [...groups.values()].filter(g => g.level === 'ERROR');
  const warnings = [...groups.values()].filter(g => g.level === 'WARN');
  const parts = [];
  parts.push(lines.length + ' new log lines; ' + errors.reduce((a, g) => a + g.count, 0) + ' errors (' + errors.length + ' distinct), ' + warnings.reduce((a, g) => a + g.count, 0) + ' warnings');
  for (const g of errors.concat(all ? warnings : [])) {
    parts.push('\n' + g.count + 'x ' + g.level + ' ' + g.text.slice(0, 400) + (g.stack.length ? '\n    ' + g.stack.slice(0, 8).join('\n    ') : ''));
  }
  if (all && plain.length) {
    parts.push('\nother lines:\n' + plain.slice(-maxLines).join('\n'));
  }
  return parts.join('\n');
}

// ---- tools ----

const tools = [];
function tool(name, description, properties, required, run) {
  tools.push({ name, description, inputSchema: { type: 'object', properties, required: required || [] }, run });
}
const num = description => ({ type: 'number', description });
const str = description => ({ type: 'string', description });

function textResult(value) {
  return { content: [{ type: 'text', text: typeof value === 'string' ? value : JSON.stringify(value) }] };
}

function tapAt(x, y, quick) {
  x = Math.round(x); y = Math.round(y);
  shell(quick ? 'input tap ' + x + ' ' + y : 'input swipe ' + x + ' ' + y + ' ' + x + ' ' + y + ' 150');
}

tool('rw_screenshot',
  'Screenshot of the phone (landscape 2772x1272 px). All other tools take phone pixel coordinates: divide an image coordinate by the returned scale and add the region origin. scale 0.5 is usually enough to read the interface; use region with scale 1 for small text.',
  { scale: num('0.1 ... 1, default 0.5'), region: { type: 'array', items: { type: 'number' }, description: '[x, y, width, height] in phone pixels, optional' } },
  [],
  async args => {
    const scale = Math.max(0.1, Math.min(1, Number(args.scale) || 0.5));
    const image = png.decode(adb(['exec-out', 'screencap', '-p'], { binary: true }));
    const out = png.cropScale(image, args.region, scale);
    return {
      content: [
        { type: 'image', data: png.encode(out).toString('base64'), mimeType: 'image/png' },
        { type: 'text', text: 'image ' + out.width + 'x' + out.height + ' = phone region [' + out.x0 + ', ' + out.y0 + ', ' + out.w + ', ' + out.h + '] at scale ' + (out.width / out.w).toFixed(3) + ' (phone x = ' + out.x0 + ' + image x / ' + (out.width / out.w).toFixed(3) + ')' },
      ],
    };
  });

tool('rw_tap', 'A finger tap at phone pixels (x, y): down for 150 ms, as a person taps. quick: true taps in one go (adb input tap).',
  { x: num('phone px'), y: num('phone px'), quick: { type: 'boolean' } }, ['x', 'y'],
  async args => { tapAt(args.x, args.y, args.quick); return textResult('tapped ' + Math.round(args.x) + ',' + Math.round(args.y)); });

tool('rw_hold', 'Press and hold at (x, y). With something selected in the game a hold is the right click (orders); on the interface it shows tooltips.',
  { x: num('phone px'), y: num('phone px'), ms: num('how long, default 800') }, ['x', 'y'],
  async args => {
    const ms = Math.round(args.ms || 800);
    shell('input swipe ' + Math.round(args.x) + ' ' + Math.round(args.y) + ' ' + Math.round(args.x) + ' ' + Math.round(args.y) + ' ' + ms);
    return textResult('held ' + ms + ' ms');
  });

tool('rw_swipe', 'One finger from (x1, y1) to (x2, y2) in ms milliseconds: pans the map, scrolls lists, drags designations.',
  { x1: num(''), y1: num(''), x2: num(''), y2: num(''), ms: num('default 500; under 150 is a fling') }, ['x1', 'y1', 'x2', 'y2'],
  async args => {
    const ms = Math.round(args.ms || 500);
    shell(['input swipe', args.x1, args.y1, args.x2, args.y2, ms].map(v => typeof v === 'number' ? Math.round(v) : v).join(' '));
    return textResult('swiped');
  });

tool('rw_gesture', 'Scripted touches with several fingers (pinch zoom and the like), played by the game itself: lines "seconds id down|move|up x y", e.g. "0.00 1 down 1400 650".',
  { script: str('the lines') }, ['script'],
  async args => { putGameFile('gesture.txt', args.script.trim() + '\n'); return textResult('gesture handed to the game (it looks for it about twice a second)'); });

tool('rw_type', 'Types ASCII text into the focused text field (adb input text; letters, digits and simple punctuation). clear: true first deletes up to 40 characters.',
  { text: str(''), clear: { type: 'boolean' } }, ['text'],
  async args => {
    if (args.clear) shell('input keyevent KEYCODE_MOVE_END && for i in $(seq 1 40); do input keyevent KEYCODE_DEL; done', { timeout: 60000 });
    if (!/^[ -~]*$/.test(args.text)) return { ...textResult('only ASCII can be typed this way'), isError: true };
    shell('input text ' + "'" + args.text.replace(/ /g, '%s').replace(/'/g, '') + "'");
    return textResult('typed');
  });

tool('rw_key', 'An Android key event by name, e.g. ENTER, DEL, ESCAPE, TAB, MOVE_END (KEYCODE_ is added).',
  { key: str('') }, ['key'],
  async args => { shell('input keyevent KEYCODE_' + String(args.key).replace(/^KEYCODE_/, '').replace(/[^A-Z0-9_]/gi, '')); return textResult('key ' + args.key); });

tool('rw_back', 'The Android back key, which the game takes as Escape (closes the top window, deselects).', {}, [],
  async () => { shell('input keyevent 111'); return textResult('escape'); });

tool('rw_screen',
  'What the game drew in its last frame, as it knows it: windows (drawing order, later on top), controls (k: button/text field/slider/control; r: [x, y, w, h] phone px; w: window index, -1 outside windows; t: the text on it) and the other texts. Filter with contains and window ("top", "map" or an index).',
  { contains: str('only what holds this text'), window: str('"top", "map" or an index'), max: num('texts and controls each, default 150') }, [],
  async args => textResult(await agent('screen', { contains: args.contains, window: args.window, max: args.max || 150 })));

tool('rw_click',
  'Taps the control (or text) that says `text`, found through rw_screen: an exact match first, then one that contains it. nth picks among several; window narrows to "top" (the topmost window with a match), "map" or a window index.',
  { text: str('the label'), nth: num('0 = first'), window: str('"top", "map" or an index') }, ['text'],
  async args => {
    // "top": the topmost window that has a match (the topmost window itself may be a title or a tooltip).
    const topOnly = args.window === 'top';
    const screen = await agent('screen', { contains: args.text, window: topOnly ? undefined : args.window, max: 400 });
    const wanted = args.text.toLowerCase();
    const candidates = []
      .concat(screen.controls.filter(c => c.t).map(c => ({ ...c, from: 'control' })))
      .concat(screen.texts.filter(t => t.r).map(t => ({ ...t, from: 'text' })));
    const score = c => {
      const parts = c.t.toLowerCase().split(' | ');
      return parts.includes(wanted) ? 0 : c.from === 'control' ? 1 : 2;
    };
    if (topOnly && candidates.length) {
      const topWindow = Math.max(...candidates.map(c => c.w));
      for (let i = candidates.length - 1; i >= 0; i--) if (candidates[i].w !== topWindow) candidates.splice(i, 1);
    }
    candidates.sort((a, b) => score(a) - score(b) || b.w - a.w);
    const pick = candidates[args.nth || 0];
    if (!pick) return { ...textResult('nothing on the screen says "' + args.text + '"'), isError: true };
    let [x, y, w, h] = pick.r;
    if (pick.from === 'text') {
      // A gizmo's label hangs below its button: the button is what takes the tap.
      const cx = x + w / 2;
      const all = await agent('screen', { window: pick.w === -1 ? 'map' : String(pick.w), max: 2000 });
      // Only a button of about the label's width whose bottom edge the label touches (a gizmo); not a wide bar the text lies on.
      const above = all.controls.filter(c => c.w === pick.w && c.r[0] <= cx && cx <= c.r[0] + c.r[2] && c.r[2] <= w * 1.6 + 10 && y - (c.r[1] + c.r[3]) >= -h && y - (c.r[1] + c.r[3]) <= 30 && c.r[1] < y)
        .sort((a, b) => (y - (a.r[1] + a.r[3])) - (y - (b.r[1] + b.r[3])))[0];
      if (above) { [x, y, w, h] = above.r; pick.from = 'button above the text'; }
    }
    tapAt(x + w / 2, y + h / 2);
    return textResult('tapped ' + pick.from + ' "' + pick.t + '" at ' + Math.round(x + w / 2) + ',' + Math.round(y + h / 2) + ' (window ' + pick.w + ')' + (candidates.length > 1 ? '; ' + (candidates.length - 1) + ' other match(es): ' + candidates.filter(c => c !== pick).slice(0, 5).map(c => '"' + c.t + '"').join(', ') : ''));
  });

tool('rw_state', 'The game\'s state: program state, loading, windows, and in a game: paused/speed, date, map, camera cell and zoom, open main tab, designator, selection with its inspect text, the newest letters.', {}, [],
  async () => textResult(await agent('state')));

tool('rw_find', 'Things on the current map whose id, def name or label holds query (nearest to the camera first), with their cell and, when visible, their screen position.',
  { query: str('part of a label, def name, or an id'), kind: str('pawn, hostile (pawns hostile to the colony), building, item, plant or any'), limit: num('default 20') }, ['query'],
  async args => textResult(await agent('find', args)));

tool('rw_camera', 'Moves the camera to a thing (id) or a cell (x, z); zoom sets the view size (11 close ... 60 far).',
  { id: str('a thing id from rw_find'), x: num('cell x'), z: num('cell z'), zoom: num('') }, [],
  async args => textResult(await agent('camera', args)));

tool('rw_select', 'Selects a thing by id (and moves the camera to it unless jump is false; add keeps the current selection).',
  { id: str(''), add: { type: 'boolean' }, jump: { type: 'boolean' } }, ['id'],
  async args => textResult(await agent('select', args)));

tool('rw_speed', 'Sets the time speed: paused, normal, fast, superfast, ultrafast.',
  { speed: str('') }, ['speed'],
  async args => textResult(await agent('speed', args)));

tool('rw_close_window', 'Closes the topmost window (or the topmost whose type name contains type).',
  { type: str('optional') }, [],
  async args => textResult(await agent('close', args)));

tool('rw_messages', 'The newest letters and messages from the game\'s history.',
  { count: num('default 10') }, [],
  async args => textResult(await agent('messages', args)));

tool('rw_cell', 'Where a map cell is on the screen (phone px).',
  { x: num(''), z: num('') }, ['x', 'z'],
  async args => textResult(await agent('cell', args)));

tool('rw_zones', 'The zones of the current map (growing, stockpile, ...) with their size, bounds and crop.', {}, [],
  async () => textResult(await agent('zones')));

tool('rw_incident', 'For testing: fires an incident on the current map now (def, e.g. TraderCaravanArrival, RaidEnemy, VisitorGroup; points optional). Without def, lists incident defs whose name holds query.',
  { def: str(''), points: num(''), query: str('') }, [],
  async args => textResult(await agent('incident', args)));

tool('rw_world', 'The world: the player\'s caravans (pawns, tile, moving, inspect text) and the nearest settlements (faction, hostile, can trade, distance), with screen positions while the world is shown.',
  { limit: num('settlements, default 12'), from: str('measure from this caravan instead of home') }, [],
  async args => textResult(await agent('world', args)));

tool('rw_pawn', 'What the game thinks of a pawn (id): downed, bed, mental state, job, lord, carried by, hediffs; with by (another pawn id) whether that one can reach / reserve / rescue it.',
  { id: str(''), by: str('') }, ['id'],
  async args => textResult(await agent('pawn', args)));

tool('rw_menu', 'Diagnosis: the orders the selected pawns would get for a right click on map cell (x, z), and what every applying menu provider gave for each pawn there. Nothing is ordered.',
  { x: num('cell x'), z: num('cell z') }, ['x', 'z'],
  async args => textResult(await agent('menu', args)));

tool('rw_research', 'TEST GAMES ONLY: finishes a research project and its prerequisites (def). Without def lists projects matching query.',
  { def: str(''), query: str('') }, [],
  async args => textResult(await agent('research', args)));

tool('rw_spawn', 'TEST GAMES ONLY: spawns a thing at a cell (def, stuff, count, x, z, rot); buildings come complete and the colony\'s. Without def lists defs matching query.',
  { def: str(''), stuff: str(''), count: num(''), x: num(''), z: num(''), rot: num(''), query: str('') }, [],
  async args => textResult(await agent('spawn', args)));

tool('rw_makepawn', 'TEST GAMES ONLY: makes a pawn (kind, faction = player/hostile/none/FactionDef, x, z, downed).',
  { kind: str(''), faction: str(''), x: num(''), z: num(''), downed: { type: 'boolean' } }, [],
  async args => textResult(await agent('makepawn', args)));

tool('rw_heal', 'TEST GAMES ONLY: heals a pawn (id) or all colonists (all: true).',
  { id: str(''), all: { type: 'boolean' } }, [],
  async args => textResult(await agent('heal', args)));

tool('rw_kill', 'TEST GAMES ONLY: kills a pawn or destroys a thing (id), or every hostile pawn and turret on the map (kind: hostile).',
  { id: str(''), kind: str('') }, [],
  async args => textResult(await agent('kill', args)));

tool('rw_hash', 'string.GetHashCode() on the phone for each of texts (should be the PC\'s Mono hash: host/corelib.sh).',
  { texts: { type: 'array', items: { type: 'string' } } }, ['texts'],
  async args => textResult(await agent('hash', args)));

tool('rw_tune', 'For measuring: switches frame rate and scheduling settings while the game runs (set: ["frames=90", "schedule=2", "wait=300"], as perfcmd.txt takes them; not saved), and reports the frame wait since the last call.',
  { set: { type: 'array', items: { type: 'string' } } }, [],
  async args => textResult(await agent('tune', args)));

tool('rw_save', 'TEST GAMES ONLY: saves the game being played under name (overwrites a save of that name: never a name of the user\'s).',
  { name: str('') }, ['name'],
  async args => textResult(await agent('save', args)));

tool('rw_relation', 'TEST GAMES ONLY: adds a relation (def: Lover, Fiance, Spouse ...) between pawns a and b.',
  { a: str(''), b: str(''), def: str('') }, ['a', 'b'],
  async args => textResult(await agent('relation', args)));

tool('rw_rituals', 'The rituals of the colony\'s ideoligion and what they take place at.', {}, [],
  async () => textResult(await agent('rituals')));

tool('rw_marry', 'TEST GAMES ONLY: makes pawns a and b engaged if needed and starts their marriage ceremony now.',
  { a: str(''), b: str('') }, ['a', 'b'],
  async args => textResult(await agent('marry', args)));

tool('rw_log', 'The game\'s log since the last call (the first call only sets the starting point): errors with their stacks, counted. all: true also lists warnings and other lines. Secrets (API keys) are masked.',
  { all: { type: 'boolean' }, max_lines: num('other lines shown with all, default 60') }, [],
  async args => textResult(summarizeLog(newLogLines(), !!args.all, args.max_lines || 60)));

tool('rw_wait', 'Waits (up to 60 s) for the game to move on, then reports the errors logged meanwhile.',
  { seconds: num('default 3') }, [],
  async args => {
    await sleep(Math.max(0, Math.min(60, Number(args.seconds) || 3)) * 1000);
    return textResult(summarizeLog(newLogLines(), false, 0));
  });

tool('rw_game',
  'The game process: status (running or not, agent reachable), start (launches it), stop (kills it: unsaved progress is lost, and the user may be playing; needs confirm: true).',
  { action: str('status, start or stop'), confirm: { type: 'boolean' } }, ['action'],
  async args => {
    if (args.action === 'start') {
      if (gamePid()) return textResult('already running');
      shell('am start -n ' + PKG + '/.MainActivity');
      token = null;
      return textResult('started; the main menu takes about a minute (rw_state answers when the game is up)');
    }
    if (args.action === 'stop') {
      if (!args.confirm) return { ...textResult('not stopped: stopping loses unsaved progress and the user may be playing. Call again with confirm: true.'), isError: true };
      shell('am force-stop ' + PKG);
      if (socket) socket.destroy();
      token = null;
      return textResult('stopped');
    }
    const pid = gamePid();
    let agentState = 'not asked';
    if (pid) {
      try { agentState = JSON.stringify(await agent('ping')); } catch (e) { agentState = e.message; }
    }
    return textResult({ running: !!pid, pid, agent: agentState });
  });

// ---- MCP over stdio ----

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

async function handle(message) {
  const { id, method, params } = message;
  if (method === 'initialize') {
    return send({
      jsonrpc: '2.0', id,
      result: {
        protocolVersion: (params && params.protocolVersion) || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: 'rimworld-phone', version: '0.1.0' },
        instructions: 'Drives RimWorld 1.7 on the user\'s phone. Coordinates are phone pixels (2772x1272, landscape). Look with rw_screenshot / rw_screen / rw_state, act with rw_tap / rw_click / rw_hold / rw_swipe, check rw_log after actions. The user may be playing: ask before stopping the game or discarding progress; do not save over the user\'s saves.',
      },
    });
  }
  if (method === 'ping') return send({ jsonrpc: '2.0', id, result: {} });
  if (method === 'tools/list') {
    return send({ jsonrpc: '2.0', id, result: { tools: tools.map(({ name, description, inputSchema }) => ({ name, description, inputSchema })) } });
  }
  if (method === 'tools/call') {
    const entry = tools.find(t => t.name === params.name);
    if (!entry) return send({ jsonrpc: '2.0', id, error: { code: -32602, message: 'unknown tool ' + params.name } });
    try {
      const result = await entry.run(params.arguments || {});
      return send({ jsonrpc: '2.0', id, result });
    } catch (e) {
      return send({ jsonrpc: '2.0', id, result: { content: [{ type: 'text', text: 'error: ' + e.message }], isError: true } });
    }
  }
  if (id !== undefined) send({ jsonrpc: '2.0', id, error: { code: -32601, message: 'method not found: ' + method } });
}

let input = '';
let queue = Promise.resolve();
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => {
  input += chunk;
  let nl;
  while ((nl = input.indexOf('\n')) >= 0) {
    const line = input.slice(0, nl).trim();
    input = input.slice(nl + 1);
    if (!line) continue;
    let message;
    try { message = JSON.parse(line); } catch (e) { log('not JSON: ' + line.slice(0, 100)); continue; }
    queue = queue.then(() => handle(message)).catch(e => log('handler: ' + e.stack));
  }
});
process.stdin.on('end', () => { switchAgentOff(); process.exit(0); });
process.on('SIGINT', () => { switchAgentOff(); process.exit(0); });
process.on('SIGTERM', () => { switchAgentOff(); process.exit(0); });
log('ready (' + tools.length + ' tools)');
