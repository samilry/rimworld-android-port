// Sets the mods on the phone: pushes each Workshop mod that is not there yet (to <game>/Mods/<id>, where the
// game looks, next to its Data folder) and writes ModsConfig.xml with the official content first, then Harmony,
// then the others in the order given. Mods are the PC's Workshop folders; the ones with assemblies are staged
// first (build/modstage/<id>, a copy) and their assemblies prepared for the phone's runtime by
// patch.dll --mods (machine type, IgnoresAccessChecksTo, no initonly statics). A mod is pushed again when its
// Workshop folder changed since (a stamp of the newest file time and the file count).
// usage: node host/mods-set.js <workshopId>...        (no ids: official content only)
const { execFileSync } = require('child_process');
const fs = require('fs'), path = require('path');
const ADB = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe';
const WORKSHOP = 'D:/SteamLibrary/steamapps/workshop/content/294100';
const PKG = 'com.local.waylith.port';
const FILES = `/sdcard/Android/data/${PKG}/files`;
const MODS = `${FILES}/game/Mods`;
const CONFIG = `${FILES}/home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config`;
const VERSION = '1.7.4976 rev1043';
const STAGE = 'build/modstage';
const adb = (...args) => execFileSync(ADB, args, { encoding: 'utf8', maxBuffer: 64 << 20 });

function walk(dir, visit) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, visit); else visit(full);
  }
}

function stampOf(dir) {
  let newest = 0, count = 0;
  walk(dir, f => { count++; const t = fs.statSync(f).mtimeMs; if (t > newest) newest = t; });
  return `${count}:${Math.round(newest)}`;
}

function hasAssemblies(dir) {
  let found = false;
  walk(dir, f => { if (f.toLowerCase().endsWith('.dll')) found = true; });
  return found;
}

const ids = process.argv.slice(2);
adb('shell', `am force-stop ${PKG}`);
const present = adb('shell', `ls ${MODS}`).split(/\s+/).filter(Boolean);
fs.mkdirSync(STAGE, { recursive: true });
const packages = [];
let pushed = 0;
for (const id of ids) {
  const dir = path.join(WORKSHOP, id);
  const about = fs.readFileSync(path.join(dir, 'About', 'About.xml'), 'utf8');
  // The mod's own id and name, not those of the dependencies it lists.
  const own = about.replace(/<modDependencies(ByVersion)?>[^]*?<\/modDependencies(ByVersion)?>/g, '');
  const pkg = /<packageId>([^<]+)</i.exec(own)[1].trim().toLowerCase();
  const name = (/<name>([^<]+)</.exec(own) || [, id])[1];
  packages.push(pkg);
  const stampFile = path.join(STAGE, id + '.stamp');
  const stamp = stampOf(dir);
  const stale = !present.includes(id) || !fs.existsSync(stampFile) || fs.readFileSync(stampFile, 'utf8') !== stamp;
  if (!stale) continue;
  let source = dir;
  if (hasAssemblies(dir)) {
    const staged = path.join(STAGE, id);
    fs.rmSync(staged, { recursive: true, force: true });
    fs.cpSync(dir, staged, { recursive: true });
    const report = execFileSync('dotnet', ['build/patch/patch.dll', '--mods', staged], { encoding: 'utf8' }).trim().split('\n');
    console.log(`${id} ${name}: ${report[report.length - 1]}`);
    source = staged;
  }
  if (present.includes(id)) adb('shell', `rm -rf ${MODS}/${id}`);
  adb('push', source, `${MODS}/${id}`);
  fs.writeFileSync(stampFile, stamp);
  pushed++;
  console.log(`pushed ${id} ${name}`);
}
const official = ['ludeon.rimworld', 'ludeon.rimworld.royalty', 'ludeon.rimworld.ideology', 'ludeon.rimworld.biotech', 'ludeon.rimworld.anomaly', 'ludeon.rimworld.odyssey'];
const harmonyFirst = packages.includes('brrainz.harmony') ? ['brrainz.harmony'] : [];
const rest = packages.filter(p => p !== 'brrainz.harmony');
const active = harmonyFirst.concat(official, rest);
const xml = '<?xml version="1.0" encoding="utf-8"?>\n<ModsConfigData>\n  <version>' + VERSION + '</version>\n  <activeMods>\n'
  + active.map(p => `    <li>${p}</li>`).join('\n') + '\n  </activeMods>\n  <knownExpansions>\n'
  + official.slice(1).map(p => `    <li>${p}</li>`).join('\n') + '\n  </knownExpansions>\n</ModsConfigData>\n';
fs.mkdirSync('logs', { recursive: true });
const tmp = path.join('logs', 'ModsConfig.generated.xml');
fs.writeFileSync(tmp, xml);
adb('push', tmp, `${CONFIG}/ModsConfig.xml`);
const uid = /appId=(\d+)/.exec(adb('shell', `dumpsys package ${PKG}`))[1];
adb('shell', `su -c 'chown -R ${uid}:1078 "/data/media/0/Android/data/${PKG}/files/game/Mods" "/data/media/0/Android/data/${PKG}/files/home"'`);
console.log(`${pushed} mods pushed; active: ` + harmonyFirst.concat(rest).join(', '));
