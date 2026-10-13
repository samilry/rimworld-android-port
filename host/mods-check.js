// Are the mods on the phone the ones the PC has now?  Compares every mod folder on the phone (<game>/Mods/<id>) with the
// PC's Workshop folder of the same id, file by file, and says what Steam itself knows about pending updates.
//   - files: the same paths, the same sizes; the text files (.xml, .txt) also by MD5, since an edit can keep the size;
//   - assemblies: by their module version id (host/mvid.js). The phone's copies were rewritten by the mod preparation,
//     so sizes and bytes differ from the PC's, but the id of the build stays; an assembly that is no .NET image is
//     compared by size;
//   - Steam's own record (steamapps/workshop/appworkshop_294100.acf): an item whose installed version is older than the
//     latest Steam has seen is an update the PC has not downloaded yet (Steam downloads it when it runs).
// The PC's Workshop folders are only read. With --update the mods that differ are replaced on the phone by the PC's
// copies as they are; the phone prepares their assemblies before it loads them the next time (hooks/ModPrepHost.cs).
// ModsConfig.xml (which mods are active, and their order) is not touched.
// usage: node host/mods-check.js [--update]
const { execFileSync } = require('child_process');
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const { mvidOf } = require('./mvid.js');
const ADB = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe';
const STEAMAPPS = 'D:/SteamLibrary/steamapps';
const WORKSHOP = STEAMAPPS + '/workshop/content/294100';
const PKG = 'com.local.waylith.port';
const MODS = `/data/media/0/Android/data/${PKG}/files/game/Mods`;
const PUSH = `/sdcard/Android/data/${PKG}/files/game/Mods`;
const update = process.argv.includes('--update');
const adb = (...args) => execFileSync(ADB, args, { encoding: 'utf8', maxBuffer: 256 << 20, stdio: ['ignore', 'pipe', 'pipe'], env: Object.assign({}, process.env, { MSYS_NO_PATHCONV: '1' }) });
const root = command => adb('shell', `su -c '${command}'`);
const isText = f => /\.(xml|txt)$/i.test(f);
const isAssembly = f => /\.dll$/i.test(f);
const day = seconds => new Date(seconds * 1000).toLocaleString('sv-SE').slice(0, 16);

// Steam's record: "id" { ... "timeupdated" "N" ... "latest_timeupdated" "N" } in the WorkshopItemDetails block.
function steamRecord() {
  const file = STEAMAPPS + '/workshop/appworkshop_294100.acf';
  const items = new Map();
  if (!fs.existsSync(file)) return { items, checked: null };
  const text = fs.readFileSync(file, 'utf8');
  const details = text.slice(text.indexOf('"WorkshopItemDetails"'));
  for (const m of details.matchAll(/"(\d+)"\s*\{([^}]*)\}/g)) {
    const field = name => Number((new RegExp('"' + name + '"\\s*"(\\d+)"').exec(m[2]) || [0, 0])[1]);
    items.set(m[1], { installed: field('timeupdated'), latest: field('latest_timeupdated') });
  }
  return { items, checked: Number((/"TimeLastUpdated"\s*"(\d+)"/.exec(text) || [0, 0])[1]), needsUpdate: (/"NeedsUpdate"\s*"(\d+)"/.exec(text) || [0, '?'])[1], needsDownload: (/"NeedsDownload"\s*"(\d+)"/.exec(text) || [0, '?'])[1] };
}

function nameOf(dir) {
  try {
    const about = fs.readFileSync(path.join(dir, 'About', 'About.xml'), 'utf8');
    return (/<name>([^<]+)</.exec(about) || [0, '?'])[1].trim();
  } catch (e) {
    return '?';
  }
}

function walk(dir, base, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name), rel = base ? base + '/' + entry.name : entry.name;
    if (entry.isDirectory()) walk(full, rel, out); else out.push(rel);
  }
}

// What the phone has: every file of every mod with its size, the text files' MD5, the assemblies themselves.
function phoneState() {
  const files = new Map();   // id -> Map(rel -> size)
  const sums = new Map();    // "id/rel" -> md5
  const put = (line, value) => {
    const p = line.replace(/^\.\//, ''), slash = p.indexOf('/');
    if (slash < 0) return;
    const id = p.slice(0, slash);
    if (!files.has(id)) files.set(id, new Map());
    files.get(id).set(p.slice(slash + 1), value);
  };
  // (The phone's find cannot hand long lists to a command with "-exec ... +": xargs does it.)
  for (const line of root(`cd ${MODS} && find . -type f -print0 | xargs -0 stat -c "%s %n"`).split('\n')) {
    const space = line.indexOf(' ');
    if (space > 0) put(line.slice(space + 1).replace(/\r$/, ''), Number(line.slice(0, space)));
  }
  for (const line of root(`cd ${MODS} && find . -type f \\( -iname "*.xml" -o -iname "*.txt" \\) -print0 | xargs -0 md5sum`).split('\n')) {
    const m = /^([0-9a-f]{32})\s+(.*?)\r?$/.exec(line);
    if (m) sums.set(m[2].replace(/^\.\//, ''), m[1]);
  }
  // The assemblies come over in one archive (some hundred megabytes at most); their build ids are read here.
  root(`cd ${MODS} && find . -type f -iname "*.dll" > /data/local/tmp/moddlls.txt && tar -cf /data/local/tmp/moddlls.tar -T /data/local/tmp/moddlls.txt && chmod 644 /data/local/tmp/moddlls.tar`);
  fs.rmSync('build/moddlls', { recursive: true, force: true });
  fs.mkdirSync('build/moddlls', { recursive: true });
  adb('pull', '/data/local/tmp/moddlls.tar', 'build/moddlls.tar');
  root('rm -f /data/local/tmp/moddlls.tar /data/local/tmp/moddlls.txt');
  execFileSync('tar', ['-xf', 'build/moddlls.tar', '-C', 'build/moddlls'], { stdio: 'ignore' });
  fs.rmSync('build/moddlls.tar', { force: true });
  return { files, sums };
}

function compare(id, phone) {
  const dir = path.join(WORKSHOP, id);
  const pc = [];
  walk(dir, '', pc);
  const there = phone.files.get(id), differences = [];
  const seen = new Set();
  for (const rel of pc) {
    seen.add(rel);
    const full = path.join(dir, rel);
    if (!there.has(rel)) { differences.push('new on the PC: ' + rel); continue; }
    if (isAssembly(rel)) {
      const mine = mvidOf(fs.readFileSync(full));
      const copy = path.join('build/moddlls', id, rel);
      const theirs = fs.existsSync(copy) ? mvidOf(fs.readFileSync(copy)) : null;
      if (mine && theirs ? mine !== theirs : fs.statSync(full).size !== there.get(rel)) differences.push('another build: ' + rel);
    } else if (fs.statSync(full).size !== there.get(rel)) {
      differences.push('changed: ' + rel);
    } else if (isText(rel)) {
      const md5 = crypto.createHash('md5').update(fs.readFileSync(full)).digest('hex');
      if (phone.sums.get(id + '/' + rel) !== md5) differences.push('changed: ' + rel);
    }
  }
  for (const rel of there.keys()) {
    // (The phone's own marks: the preparation stamp, and the checksum Lunar Framework keeps beside a component, which is recomputed there.)
    if (!seen.has(rel) && rel !== '.port17-prepared') differences.push('gone on the PC: ' + rel);
  }
  return differences;
}

const steam = steamRecord();
console.log(`Steam's record of ${steam.items.size} Workshop items, last written ${steam.checked ? day(steam.checked) : '?'}; NeedsUpdate ${steam.needsUpdate}, NeedsDownload ${steam.needsDownload}`);
const pending = [...steam.items].filter(([, v]) => v.latest > v.installed);
for (const [id, v] of pending) console.log(`  not downloaded yet: ${id} ${nameOf(path.join(WORKSHOP, id))} (installed ${day(v.installed)}, Steam has ${day(v.latest)})`);
if (!pending.length) console.log('  every item is at the latest version Steam has seen');

adb('shell', `am force-stop ${PKG}`);
const phone = phoneState();
const ids = [...phone.files.keys()].sort();
let same = 0;
const changed = [], unknown = [];
for (const id of ids) {
  if (!fs.existsSync(path.join(WORKSHOP, id, 'About', 'About.xml'))) { unknown.push(id); continue; }
  const differences = compare(id, phone);
  if (!differences.length) { same++; continue; }
  const record = steam.items.get(id);
  changed.push(id);
  console.log(`\n${id} ${nameOf(path.join(WORKSHOP, id))}: ${differences.length} difference(s)` + (record ? `; Steam's version of ${day(record.installed)}` : ''));
  for (const d of differences.slice(0, 8)) console.log('    ' + d);
  if (differences.length > 8) console.log('    ... and ' + (differences.length - 8) + ' more');
}
console.log(`\n${ids.length} mods on the phone: ${same} the same as the PC's, ${changed.length} differ` + (unknown.length ? `, ${unknown.length} have no Workshop folder on this PC (${unknown.join(', ')})` : ''));
const onlyPc = fs.readdirSync(WORKSHOP).filter(id => !phone.files.has(id) && fs.existsSync(path.join(WORKSHOP, id, 'About', 'About.xml')));
console.log(`${onlyPc.length} Workshop mods on the PC are not on the phone` + (onlyPc.length ? ':' : ''));
for (const id of onlyPc) console.log(`    ${id} ${nameOf(path.join(WORKSHOP, id))}` + (steam.items.has(id) ? ` (Steam's version of ${day(steam.items.get(id).installed)})` : ''));

if (update && changed.length) {
  const uid = /appId=(\d+)/.exec(adb('shell', `dumpsys package ${PKG}`))[1];
  for (const id of changed) {
    root(`rm -rf ${MODS}/${id}`);
    adb('push', path.join(WORKSHOP, id), `${PUSH}/${id}`);
    root(`chown -R ${uid}:1078 ${MODS}/${id}`);
    console.log(`replaced on the phone: ${id} ${nameOf(path.join(WORKSHOP, id))}`);
  }
  // Once more, for what was replaced: the phone must have the PC's files now.
  const after = phoneState();
  for (const id of changed) {
    const left = compare(id, after);
    console.log(`  ${id}: ` + (left.length ? left.length + ' difference(s) remain: ' + left.slice(0, 3).join('; ') : 'the same as the PC now'));
  }
}
fs.rmSync('build/moddlls', { recursive: true, force: true });
