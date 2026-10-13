// A profile of one thread of the game on the phone, with the compiled (JIT) code named.
// usage: node host/perf-profile.js <dir> [pattern...]
//   <dir> holds what host/perf-profile.sh pulled: chains.txt (one sample per line: "period address address ...", the
//   raw call chain, sampled instruction first), perf.map (the runtime's method map, DOTNET_PerfMapEnabled=3) and
//   maps.txt (/proc/<pid>/maps, for the native libraries).
// Prints the thread's time by kind of code (own time: where the sampled instruction was), the functions with the most
// own time, and for each pattern (a regular expression on "module!function"):
//   the inclusive time of the frames that match (anywhere in the chain), or, written "callers:<regex>", who called the
//   matching function when it was the one running (the three frames above it).
// simpleperf's own reports cannot do this: they drop every frame that has no file behind it, which is all managed code.
const fs = require('fs'), path = require('path');
const dir = process.argv[2], patterns = process.argv.slice(3);
if (!dir) { console.log('usage: node host/perf-profile.js <dir> [pattern...]'); process.exit(1); }

// The method map: "start size name".
const methods = [];
for (const line of fs.readFileSync(path.join(dir, 'perf.map'), 'utf8').split('\n')) {
  const a = line.indexOf(' '), b = line.indexOf(' ', a + 1);
  if (a < 0 || b < 0) continue;
  const start = parseInt(line.slice(0, a), 16), size = parseInt(line.slice(a + 1, b), 16);
  if (size > 0) methods.push([start, start + size, line.slice(b + 1).trim()]);
}
methods.sort((x, y) => x[0] - y[0]);
// The native libraries: "start-end perms offset dev inode path".
const libs = [];
for (const line of fs.readFileSync(path.join(dir, 'maps.txt'), 'utf8').split('\n')) {
  const m = line.match(/^([0-9a-f]+)-([0-9a-f]+) (\S+) ([0-9a-f]+) \S+ \S+\s+(\S.*)$/);
  if (m && m[3][2] === 'x') libs.push([parseInt(m[1], 16), parseInt(m[2], 16), m[5].replace(/^.*\//, ''), parseInt(m[4], 16)]);
}
libs.sort((x, y) => x[0] - y[0]);
function find(table, addr) {
  let lo = 0, hi = table.length - 1, at = -1;
  while (lo <= hi) { const mid = (lo + hi) >> 1; if (table[mid][0] <= addr) { at = mid; lo = mid + 1; } else hi = mid - 1; }
  for (let i = at; i >= 0 && i > at - 4; i--) if (addr < table[i][1]) return table[i];
  return null;
}
const cache = new Map();
function nameOf(text) {
  let n = cache.get(text);
  if (n !== undefined) return n;
  if (text.length > 14) n = 'kernel!kernel';              // 0xffffff...: the kernel's half of the address space
  else {
    const addr = parseInt(text, 16);
    const m = find(methods, addr);
    if (m) {
      if (m[2].startsWith('stub ')) n = 'runtime stubs!' + m[2].slice(5);
      else {
        const p = m[2].match(/\[([^\]]+)\]\s*(.*?)(\[[A-Za-z0-9]+\])?$/);
        n = p ? p[1] + '!' + p[2].replace(/\(.*$/, '') : 'managed!' + m[2];
      }
    } else {
      const l = find(libs, addr);
      n = l ? l[2] + '!' + l[2] + '+' + (addr - l[0] + l[3]).toString(16) : 'unknown!' + text;
    }
  }
  cache.set(text, n);
  return n;
}
function kind(name) {
  const mod = name.slice(0, name.indexOf('!')), fn = name.slice(name.indexOf('!') + 1);
  if (mod === 'PortMobile' || mod === 'PortHooks') return 'the port: PortMobile, PortHooks';
  if (/^lib(glfw|port17|vma|waylithaudio|exhelper|monomodc|VulkanEx)\.so$/.test(mod)) return 'the port: native shims (glfw, audio, vma...)';
  if (mod === 'libvulkan.so') return 'the port: Vulkan interposer and loader';
  if (/^DMDASM\.|HarmonyGenerated|DMD</.test(name)) return 'Harmony: patched methods as generated';
  if (/dynamicClass::/.test(fn)) return 'runtime: generated stubs';
  if (mod === 'Assembly-CSharp') return 'game (Assembly-CSharp)';
  if (/^(Waylith|Brutal\.|UnityStubs$|RimWorld$|UnityEngine)/.test(mod)) return 'engine (Waylith, Brutal, UnityStubs)';
  if (mod === '0Harmony' || /^MonoMod/.test(mod)) return 'Harmony library';
  if (/^System\.|^Microsoft\.|^netstandard$|^mscorlib$/.test(mod)) return 'runtime: framework';
  if (/^(libcoreclr|libclrjit|libSystem\..*)\.so$/.test(mod) || mod === 'runtime stubs') return 'runtime: native and stubs';
  if (mod === 'kernel') return 'kernel';
  if (mod === '[vdso]') return 'reading the clock ([vdso])';
  if (/adreno|libgsl|llvm|vulkan\./i.test(mod)) return 'graphics driver';
  if (/\.so$/.test(mod)) return 'other native (' + mod + ')';
  if (mod === 'unknown') return 'unknown';
  return 'mods';
}
let total = 0;
const byKind = {}, self = {}, modSelf = {};
const jobs = patterns.map(p => p.startsWith('callers:') ? { callers: new RegExp(p.slice(8)), sum: 0, by: {} } : { re: new RegExp(p), sum: 0, by: {} });
for (const line of fs.readFileSync(path.join(dir, 'chains.txt'), 'utf8').split('\n')) {
  if (!line) continue;
  const parts = line.split(' ');
  const w = Number(parts[0]);
  const frames = [];
  for (let i = 1; i < parts.length; i++) if (!parts[i].startsWith('0xfffffffffffff')) frames.push(nameOf(parts[i]));
  if (!frames.length) continue;
  total += w;
  const k = kind(frames[0]);
  byKind[k] = (byKind[k] || 0) + w;
  self[frames[0]] = (self[frames[0]] || 0) + w;
  if (k === 'mods') { const mod = frames[0].slice(0, frames[0].indexOf('!')); modSelf[mod] = (modSelf[mod] || 0) + w; }
  for (const job of jobs) {
    if (job.callers) {
      if (!job.callers.test(frames[0])) continue;
      job.sum += w;
      const who = frames.slice(1, 4).join('  <-  ') || '(nothing above)';
      job.by[who] = (job.by[who] || 0) + w;
    } else {
      const seen = new Set();
      for (const f of frames) if (job.re.test(f)) seen.add(f);
      if (!seen.size) continue;
      job.sum += w;
      for (const f of seen) job.by[f] = (job.by[f] || 0) + w;
    }
  }
}
const pct = v => (100 * v / total).toFixed(2).padStart(6) + ' %';
const top = (o, n) => Object.entries(o).sort((a, b) => b[1] - a[1]).slice(0, n);
console.log('own time by kind of code:');
for (const [k, v] of top(byKind, 30)) console.log('  ' + pct(v) + '  ' + k);
console.log('\nmods, own time by assembly:');
for (const [k, v] of top(modSelf, 12)) console.log('  ' + pct(v) + '  ' + k);
console.log('\nfunctions with the most own time:');
for (const [k, v] of top(self, 30)) console.log('  ' + pct(v) + '  ' + k.slice(0, 160));
patterns.forEach((p, i) => {
  const job = jobs[i];
  console.log('\n' + (job.callers ? 'who calls /' + p.slice(8) + '/ (own time ' + pct(job.sum).trim() + '):' : 'inclusive, frames like /' + p + '/: ' + pct(job.sum).trim()));
  for (const [k, v] of top(job.by, 14)) console.log('  ' + pct(v) + '  ' + k.slice(0, 420));
});
