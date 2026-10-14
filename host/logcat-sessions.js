// Splits a saved logcat (adb logcat -d -v threadtime, colours stripped) into the game's runs and summarizes each:
// when it ran, the saves it loaded, new games, saves written, and its distinct ERROR headlines with counts.
// usage: node host/logcat-sessions.js logs/<file>.log [max headlines per run]
const fs = require('fs');
const file = process.argv[2];
const maxHeadlines = Number(process.argv[3] || 12);
const runs = new Map();
const re = /^(\d\d-\d\d \d\d:\d\d:\d\d)\.\d+\s+(\d+)\s+\d+\s+\w\s+DOTNET\s*:\s?(.*)$/;
for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
  const m = re.exec(line);
  if (!m) continue;
  const [, time, pid, text] = m;
  let run = runs.get(pid);
  if (!run) runs.set(pid, run = { pid, first: time, last: time, loads: [], news: 0, saves: [], errors: new Map() });
  run.last = time;
  let x;
  if ((x = /INFO Loading game from file (.*) with mods/.exec(text))) run.loads.push(time.slice(6) + ' ' + x[1]);
  else if (/Game\.InitNewGame\(\)|InitNewGame_Patch\d+\(Game this\) \[0x0\]/.test(text) && !run.newSeen) { run.newSeen = true; run.news++; }
  else if ((x = /Saving game to (.*)|SaveGame\(.*?"(.*?)"/.exec(text))) run.saves.push(time.slice(6) + ' ' + (x[1] || x[2]));
  else if ((x = /^\d\d:\d\d:\d\d\s+ERROR\s+(.*)$/.exec(text))) {
    const key = x[1].replace(/\d+/g, '#').slice(0, 140);
    const e = run.errors.get(key) || { count: 0, first: time.slice(6) };
    e.count++;
    run.errors.set(key, e);
  }
}
for (const run of runs.values()) {
  const total = [...run.errors.values()].reduce((a, e) => a + e.count, 0);
  console.log(`== pid ${run.pid}  ${run.first} .. ${run.last}  errors ${total}${run.news ? '  new game' : ''}`);
  for (const l of run.loads) console.log('   load ' + l);
  for (const s of run.saves) console.log('   save ' + s);
  const sorted = [...run.errors.entries()].sort((a, b) => b[1].count - a[1].count).slice(0, maxHeadlines);
  for (const [k, e] of sorted) console.log(`   ${String(e.count).padStart(5)} ${e.first} ${k}`);
}
