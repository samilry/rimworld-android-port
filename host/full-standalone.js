// The complete guide as one file of its own, to send along with the tester APK (opened in a phone's browser, also
// offline): host/full-guide/index.html (an Artifact page, which gets its document skeleton when it is published, and
// its pictures from img/) with a doctype, the charset, the viewport and every picture inlined.
// usage: node host/full-standalone.js   (writes build/Port17完全指南.html)
const fs = require('fs');
const path = require('path');
const root = path.join(__dirname, '..');
const dir = path.join(root, 'host', 'full-guide');
let src = fs.readFileSync(path.join(dir, 'index.html'), 'utf8');
let pictures = 0;
src = src.replace(/src="(img\/[^"]+\.jpg)"/g, (m, file) => {
  pictures++;
  return 'src="data:image/jpeg;base64,' + fs.readFileSync(path.join(dir, file)).toString('base64') + '"';
});
const head = '<!doctype html>\n<html lang="zh-CN">\n<head>\n<meta charset="utf-8">\n<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n';
const i = src.indexOf('<div class="page">');
if (i < 0) throw new Error('host/full-guide/index.html: no <div class="page">');
const out = head + src.slice(0, i).replace('<meta name="claude:viewport-fit-sides" content="cover">\n', '')
  + '<style>html{color-scheme:light}body{margin:0}[hidden]{display:none!important}</style>\n</head>\n<body>\n' + src.slice(i) + '\n</body>\n</html>\n';
const file = path.join(root, 'build', 'Port17完全指南.html');
fs.writeFileSync(file, out);
console.log('build/Port17完全指南.html ' + (Buffer.byteLength(out) / 1048576).toFixed(1) + ' MB, ' + pictures + ' pictures');
