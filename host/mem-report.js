// The summary of one memory sample taken by host/mem-report.sh: the process's mappings (smaps) grouped by kind, with
// what is in memory (Pss) and what has been pushed out to the compressed swap (SwapPss) side by side; the graphics
// driver's account; the system's own totals for comparison.
// usage: node host/mem-report.js <logs/mem-label> [--maps N]     (--maps: the N largest single mappings as well)
const fs = require('fs'), path = require('path');
const dir = process.argv[2];
const wantMaps = process.argv.indexOf('--maps') > 0 ? Number(process.argv[process.argv.indexOf('--maps') + 1]) : 0;
const read = (name) => { try { return fs.readFileSync(path.join(dir, name), 'utf8'); } catch (e) { return ''; } };

function kindOf(name, perms) {
  if (name === '') return perms.startsWith('---') ? 'reserved, no access (unnamed)' : 'anonymous, unnamed (the collector\'s heap and other mmap)';
  if (/^\[anon:scudo:|^\[anon:libc_malloc|^\[heap\]|^\[anon:GWP-ASan/.test(name)) return 'malloc (native heap)';
  if (/^\[anon:stack_and_tls|^\[stack|^\[anon:thread signal stack|^\[anon:bionic_alloc/.test(name)) return 'thread stacks';
  if (/^\[anon:\.bss\]|^\[anon:\.rel\.ro|^\[anon:linker|^\[anon:cfi|^\[anon:System property|^\[anon:atexit|^\[anon:abort/.test(name)) return 'libraries\' data';
  if (/^\[anon:dalvik|^\[anon:.*jit|\.art$|\.oat$|\.vdex$|\.odex$|\.jar$|^\/memfd:jit/.test(name)) return 'the Java side (ART)';
  if (/^\[anon:/.test(name)) return 'anonymous: ' + name.replace(/^\[anon:/, '').replace(/\]$/, '').replace(/[0-9a-f]{8,}/g, '*').slice(0, 40);
  if (/^\/dev\/kgsl/.test(name)) return 'graphics memory mapped into the process (kgsl)';
  if (/^\/dmabuf|^anon_inode:dmabuf/.test(name)) return 'dmabuf mapped into the process';
  if (/^\/memfd:doublemapper/.test(name)) return 'compiled code and the runtime\'s loader heaps (doublemapper)';
  if (/^\/memfd:/.test(name)) return 'memfd: ' + name.replace(/ \(deleted\)$/, '');
  if (/\.dll$/i.test(name)) return 'assemblies mapped from files (.dll)';
  if (/\.so$/.test(name)) return 'native libraries (.so)';
  if (/\.apk$/.test(name)) return 'the package (.apk)';
  if (/^\/dev\/ashmem|^\/dev\/__properties__|^\/dev\//.test(name)) return 'system shared memory';
  if (/^\[/.test(name)) return 'kernel pages (' + name + ')';
  return 'other files: ' + path.posix.extname(name.replace(/ \(deleted\)$/, '')) ;
}

const kinds = {}, maps = [];
let current = null;
const header = /^([0-9a-f]+)-([0-9a-f]+) (\S+) \S+ \S+ \S+\s*(.*)$/;
for (const line of read('smaps.txt').split('\n')) {
  const h = header.exec(line);
  if (h) {
    current = { name: h[4].trim(), perms: h[3], size: (parseInt(h[2], 16) - parseInt(h[1], 16)) / 1024, pss: 0, swap: 0, rss: 0, dirty: 0, start: h[1] };
    maps.push(current);
    continue;
  }
  if (!current) continue;
  const f = /^(\w+):\s+(\d+) kB/.exec(line);
  if (!f) continue;
  const v = Number(f[2]);
  if (f[1] === 'Pss') current.pss = v;
  else if (f[1] === 'SwapPss') current.swap = v;
  else if (f[1] === 'Rss') current.rss = v;
  else if (f[1] === 'Private_Dirty') current.dirty = v;
}
let pss = 0, swap = 0;
for (const m of maps) {
  const k = kindOf(m.name, m.perms);
  const e = kinds[k] || (kinds[k] = { pss: 0, swap: 0, size: 0, count: 0, dirty: 0 });
  e.pss += m.pss; e.swap += m.swap; e.size += m.size; e.count++; e.dirty += m.dirty;
  pss += m.pss; swap += m.swap;
}
const mib = (kb) => (kb / 1024).toFixed(0).padStart(6);
console.log('mappings by kind, MiB           in memory  swapped   both   (address space, mappings)');
const rows = Object.entries(kinds).sort((a, b) => (b[1].pss + b[1].swap) - (a[1].pss + a[1].swap));
let small = { pss: 0, swap: 0, size: 0, count: 0 };
for (const [k, e] of rows) {
  if (e.pss + e.swap < 4096) { small.pss += e.pss; small.swap += e.swap; small.size += e.size; small.count += e.count; continue; }
  console.log('  ' + mib(e.pss) + ' ' + mib(e.swap) + ' ' + mib(e.pss + e.swap) + '   ' + k + '   (' + mib(e.size).trim() + ', ' + e.count + ')');
}
console.log('  ' + mib(small.pss) + ' ' + mib(small.swap) + ' ' + mib(small.pss + small.swap) + '   everything under 4 MiB together   (' + mib(small.size).trim() + ', ' + small.count + ')');
console.log('  ' + mib(pss) + ' ' + mib(swap) + ' ' + mib(pss + swap) + '   all mappings');

if (wantMaps) {
  console.log('\nthe ' + wantMaps + ' largest single mappings (in memory + swapped):');
  for (const m of maps.slice().sort((a, b) => (b.pss + b.swap) - (a.pss + a.swap)).slice(0, wantMaps))
    console.log('  ' + mib(m.pss + m.swap) + ' of ' + mib(m.size) + '  ' + m.perms + ' ' + m.start + '  ' + (m.name || '(unnamed)'));
}

// The graphics driver's account of the process (bytes): what it holds for it whether mapped or not.
const kgsl = {};
for (const line of read('kgsl.txt').split('\n')) { const p = line.trim().split(/\s+/); if (p.length === 2 && /^\d+$/.test(p[1])) kgsl[p[0]] = Number(p[1]); }
if (Object.keys(kgsl).length) {
  console.log('\nthe graphics driver\'s account (kgsl), MiB:');
  for (const [k, v] of Object.entries(kgsl).sort((a, b) => b[1] - a[1])) if (v >= 1048576) console.log('  ' + (v / 1048576).toFixed(0).padStart(6) + '   ' + k);
}
// kgsl's own list of the process's allocations, by usage ("texture", "vertexarray", ...), when the kernel exposes it.
const byUsage = {};
for (const line of read('kgsl-mem.txt').split('\n')) {
  const p = line.trim().split(/\s+/);
  if (p.length < 7 || !/^[0-9a-f]+$/.test(p[0])) continue;
  const size = Number(p[2]), usage = p[5] || '?';
  if (!isFinite(size)) continue;
  const e = byUsage[usage] || (byUsage[usage] = { size: 0, count: 0 });
  e.size += size; e.count++;
}
if (Object.keys(byUsage).length) {
  console.log('\nthe driver\'s allocations for the process by use, MiB:');
  for (const [k, e] of Object.entries(byUsage).sort((a, b) => b[1].size - a[1].size)) if (e.size >= 1048576) console.log('  ' + (e.size / 1048576).toFixed(0).padStart(6) + '   ' + k + ' (' + e.count + ')');
}
const meminfo = read('meminfo.txt');
const pick = (re) => { const m = re.exec(meminfo); return m ? m[1] : '?'; };
console.log('\nthe system\'s summary (dumpsys meminfo), kB: total PSS ' + pick(/TOTAL PSS:\s+(\d+)/) + ', total RSS ' + pick(/TOTAL RSS:\s+(\d+)/) + ', swapped PSS ' + pick(/TOTAL SWAP PSS:\s+(\d+)/)
  + '; native heap ' + pick(/Native Heap:\s+(\d+)/) + ', graphics ' + pick(/Graphics:\s+(\d+)/) + ', private other ' + pick(/Private Other:\s+(\d+)/) + ', unknown ' + pick(/Unknown\s+(\d+)/));
const watch = read('memwatch.txt').trim().split('\n').filter((l) => l).slice(-12).map((l) => l.replace(/^.*\[memwatch\] /, '  '));
if (watch.length && watch[0]) console.log('\nthe runtime\'s and the engine\'s own accounts (last lines):\n' + watch.join('\n'));
