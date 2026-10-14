// Reads a native heap dump written by malloc debug (the phone's own libc_malloc_debug, switched on for the game with
// host/heap-debug.sh): the live allocations of 16 000 bytes and more, each with the call stack that made it.
// Sums them by the library that asked for the memory (the first frame outside the allocator), by the chain of
// libraries above it, and lists the largest call stacks with every frame as library+offset (and a name, when a file
// of symbols is given: "address name" lines per library, see host/heap-debug.sh).
// usage: node host/heap-dump.js <dump.txt> [--stacks N] [--symbols <folder>] [--perfmap <perf-PID.map>]
const fs = require('fs'), path = require('path');
const file = process.argv[2];
const opt = (name, fallback) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : fallback; };
const wantStacks = Number(opt('--stacks', 40));
const symbolDir = opt('--symbols', null), perfMapFile = opt('--perfmap', null);
const lines = fs.readFileSync(file, 'utf8').split('\n');

const maps = [];
let inMaps = false;
const records = [];
for (const line of lines) {
  if (line.startsWith('MAPS')) { inMaps = true; continue; }
  if (line.startsWith('END')) break;
  if (inMaps) {
    const m = /^([0-9a-f]+)-([0-9a-f]+) (\S+) ([0-9a-f]+) \S+ \S+\s*(.*)$/.exec(line);
    if (m) maps.push({ start: BigInt('0x' + m[1]), end: BigInt('0x' + m[2]), perms: m[3], offset: BigInt('0x' + m[4]), name: m[5].trim() });
    continue;
  }
  const r = /^z (\d+)\s+sz\s+(\d+)\s+num\s+(\d+)\s+bt (.*)$/.exec(line);
  if (r) records.push({ size: Number(r[2]), count: Number(r[3]), frames: r[4].trim().split(/\s+/).map((a) => BigInt('0x' + a)) });
}
maps.sort((a, b) => (a.start < b.start ? -1 : 1));
// The load address of each library: its lowest mapping (file offset 0).
const base = {};
for (const m of maps) if (m.name.startsWith('/') && (base[m.name] === undefined || m.start - m.offset < base[m.name])) base[m.name] = m.start - m.offset;

function find(address) {
  let lo = 0, hi = maps.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (address < maps[mid].start) hi = mid - 1; else if (address >= maps[mid].end) lo = mid + 1; else return maps[mid];
  }
  return null;
}

// Symbols: <folder>/<library file name>.sym with "hexaddress name" lines sorted by address (from llvm-nm).
const symbols = {};
function symbolsOf(lib) {
  if (!symbolDir) return null;
  if (symbols[lib] !== undefined) return symbols[lib];
  const f = path.join(symbolDir, path.posix.basename(lib) + '.sym');
  if (!fs.existsSync(f)) return (symbols[lib] = null);
  const list = [];
  for (const l of fs.readFileSync(f, 'utf8').split('\n')) { const m = /^([0-9a-f]+) (.*)$/.exec(l); if (m) list.push([BigInt('0x' + m[1]), m[2]]); }
  list.sort((a, b) => (a[0] < b[0] ? -1 : 1));
  return (symbols[lib] = list);
}
function lookup(list, address) {
  let lo = 0, hi = list.length - 1, best = null;
  while (lo <= hi) { const mid = (lo + hi) >> 1; if (list[mid][0] <= address) { best = list[mid]; lo = mid + 1; } else hi = mid - 1; }
  return best;
}
// Compiled managed code: the runtime's perf map ("start size name").
let jit = null;
if (perfMapFile && fs.existsSync(perfMapFile)) {
  jit = [];
  for (const l of fs.readFileSync(perfMapFile, 'utf8').split('\n')) { const m = /^([0-9a-f]+) ([0-9a-f]+) (.*)$/.exec(l); if (m) jit.push([BigInt('0x' + m[1]), m[3], BigInt('0x' + m[2])]); }
  jit.sort((a, b) => (a[0] < b[0] ? -1 : 1));
}

const short = (name) => (name.startsWith('/') ? path.posix.basename(name) : name || '(anonymous)');
function describe(address) {
  const m = find(address);
  if (!m) return { lib: '?', text: '? ' + address.toString(16) };
  if (!m.name.startsWith('/') || /memfd:doublemapper/.test(m.name)) {
    if (jit) { const s = lookup(jit, address); if (s && address < s[0] + s[2]) return { lib: 'managed code', text: 'managed: ' + s[1] }; }
    return { lib: /doublemapper/.test(m.name) ? 'managed code' : short(m.name), text: short(m.name) + ' ' + address.toString(16) };
  }
  const offset = address - base[m.name];
  const list = symbolsOf(m.name);
  const s = list ? lookup(list, offset) : null;
  return { lib: short(m.name), text: short(m.name) + '+' + offset.toString(16) + (s ? '  ' + s[1] + '+' + (offset - s[0]).toString(16) : '') };
}

const allocator = /^(libc\.so|libc_malloc_debug\.so|libc\+\+\.so|libc\+\+_shared\.so)$/;
const mib = (n) => (n / 1048576).toFixed(1).padStart(8);
let total = 0;
const byLib = {}, byChain = {}, stacks = [];
for (const r of records) {
  const bytes = r.size * r.count;
  total += bytes;
  const frames = r.frames.map(describe);
  const libs = [];
  for (const f of frames) if (!allocator.test(f.lib) && libs[libs.length - 1] !== f.lib) libs.push(f.lib);
  const owner = libs[0] || '(only the allocator)';
  (byLib[owner] || (byLib[owner] = { bytes: 0, count: 0 })).bytes += bytes; byLib[owner].count += r.count;
  const chain = libs.slice(0, 4).join(' <- ') || '(only the allocator)';
  (byChain[chain] || (byChain[chain] = { bytes: 0, count: 0 })).bytes += bytes; byChain[chain].count += r.count;
  stacks.push({ bytes, r, frames });
}
console.log(records.length + ' call stacks, ' + mib(total) + ' MiB live in allocations of 16 000 bytes and more');
console.log('\nby the library that asked (MiB, allocations):');
for (const [k, v] of Object.entries(byLib).sort((a, b) => b[1].bytes - a[1].bytes).slice(0, 25)) console.log('  ' + mib(v.bytes) + ' ' + String(v.count).padStart(7) + '  ' + k);
console.log('\nby the libraries on the stack, innermost first (MiB, allocations):');
for (const [k, v] of Object.entries(byChain).sort((a, b) => b[1].bytes - a[1].bytes).slice(0, 30)) console.log('  ' + mib(v.bytes) + ' ' + String(v.count).padStart(7) + '  ' + k);
// The same stack with different sizes is one place in the code: sum by the frames.
const places = {};
for (const s of stacks) {
  const key = s.r.frames.slice(0, 12).map((a) => a.toString(16)).join(' ');
  const p = places[key] || (places[key] = { bytes: 0, count: 0, frames: s.frames, sizes: {} });
  p.bytes += s.bytes; p.count += s.r.count; p.sizes[s.r.size] = (p.sizes[s.r.size] || 0) + s.r.count;
}
console.log('\nthe ' + wantStacks + ' places with the most live memory:');
for (const p of Object.values(places).sort((a, b) => b.bytes - a.bytes).slice(0, wantStacks)) {
  const sizes = Object.entries(p.sizes).sort((a, b) => b[0] * b[1] - a[0] * a[1]).slice(0, 4).map(([s, n]) => n + ' x ' + s).join(', ');
  console.log('\n' + mib(p.bytes) + ' MiB in ' + p.count + ' allocations (' + sizes + (Object.keys(p.sizes).length > 4 ? ', ...' : '') + ')');
  for (const f of p.frames.slice(0, 14)) console.log('      ' + f.text);
}
