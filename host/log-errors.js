// Summarizes the errors of a phone run log (host/run.sh output): each distinct ERROR headline with its count, the
// mods named in it, and the exception type that follows it; then the mods that failed to load an assembly.
// usage: node host/log-errors.js logs/phone-run-<n>.log
const fs = require('fs');
const file = process.argv[2];
const lines = fs.readFileSync(file, 'utf8').split('\n').map(l => l.replace(/\x1b\[[0-9;]*m/g, '').replace(/^.*DOTNET\s*\(\s*\d+\):\s*/, ''));
const headlines = new Map();
for (let i = 0; i < lines.length; i++) {
  const m = /^\d\d:\d\d:\d\d\s+ERROR\s+(.*)$/.exec(lines[i]);
  if (!m) continue;
  let text = m[1].replace(/\d+/g, '#').slice(0, 150);
  let detail = '';
  for (let j = i + 1; j < Math.min(i + 6, lines.length); j++) {
    const e = /(System\.[A-Za-z.]+Exception[^\n]{0,100})/.exec(lines[j]);
    if (e) { detail = e[1].replace(/\d+/g, '#').slice(0, 120); break; }
  }
  const key = text + (detail ? ' | ' + detail : '');
  headlines.set(key, (headlines.get(key) || 0) + 1);
}
const sorted = [...headlines.entries()].sort((a, b) => b[1] - a[1]);
console.log(sorted.length + ' distinct error headlines');
for (const [key, count] of sorted.slice(0, 60)) {
  console.log(String(count).padStart(4) + '  ' + key);
}
const loadFailures = lines.filter(l => /ReflectionTypeLoadException getting types in assembly|Exception loading .*\.dll|Could not load file or assembly|no facade for/.test(l));
if (loadFailures.length) {
  console.log('\nassembly load problems:');
  for (const l of new Set(loadFailures.map(l => l.slice(0, 160)))) console.log('  ' + l);
}
