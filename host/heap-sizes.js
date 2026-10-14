// Size distribution of the live allocations of one library in a malloc debug heap dump.
// usage: node host/heap-sizes.js <dump.txt> <library file name>
const fs = require('fs');
const lines = fs.readFileSync(process.argv[2], 'utf8').split('\n');
const lib = process.argv[3];
const maps = []; let inMaps = false; const recs = [];
for (const line of lines) {
  if (line.startsWith('MAPS')) { inMaps = true; continue; }
  if (inMaps) { const m = /^([0-9a-f]+)-([0-9a-f]+) \S+ [0-9a-f]+ \S+ \S+\s*(.*)$/.exec(line); if (m && m[3].endsWith('/' + lib)) maps.push([BigInt('0x' + m[1]), BigInt('0x' + m[2])]); continue; }
  const r = /^z \d+\s+sz\s+(\d+)\s+num\s+(\d+)\s+bt (.*)$/.exec(line);
  if (r) recs.push({ size: Number(r[1]), count: Number(r[2]), second: BigInt('0x' + (r[3].trim().split(/\s+/)[1] || '0')) });
}
const edges = [16e3, 64e3, 256e3, 512e3, 1048576, 2097152, 4194304, 8388608, 1e12];
const buckets = edges.map(() => ({ bytes: 0, count: 0 }));
let total = 0, n = 0;
for (const r of recs) {
  if (!maps.some(([a, b]) => r.second >= a && r.second < b)) continue;
  const i = edges.findIndex((e) => r.size < e);
  buckets[i].bytes += r.size * r.count; buckets[i].count += r.count; total += r.size * r.count; n += r.count;
}
console.log(lib + ': ' + n + ' live allocations, ' + (total / 1048576).toFixed(0) + ' MiB');
let low = 0;
edges.forEach((e, i) => { console.log('  ' + (i ? (low / 1024).toFixed(0) + ' KiB' : 'under').padStart(10) + ' .. ' + (e < 1e11 ? (e / 1024).toFixed(0) + ' KiB' : 'up').padEnd(10) + (buckets[i].bytes / 1048576).toFixed(0).padStart(6) + ' MiB in ' + buckets[i].count); low = e; });
