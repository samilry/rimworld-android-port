// The tester guide as a file of its own, to send along with the tester APK (opened in a phone's browser): the page
// host/tester-guide.html (an Artifact page, which gets its document skeleton when it is published) with a doctype,
// the charset and the viewport. A plain copy of the page has no charset, and a browser may then take the Chinese
// for another encoding.
// usage: node host/guide-standalone.js   (writes build/Port17测试指南.html)
const fs = require('fs');
const path = require('path');
const root = path.join(__dirname, '..');
const src = fs.readFileSync(path.join(root, 'host', 'tester-guide.html'), 'utf8');
const head = '<!doctype html>\n<html lang="zh-CN">\n<head>\n<meta charset="utf-8">\n<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n';
const i = src.indexOf('<div class="page">');
if (i < 0) throw new Error('host/tester-guide.html: no <div class="page">');
const out = head + src.slice(0, i).replace('<meta name="claude:viewport-fit-sides" content="cover">\n', '')
  + '<style>html{color-scheme:light}body{margin:0}</style>\n</head>\n<body>\n' + src.slice(i) + '\n</body>\n</html>\n';
fs.writeFileSync(path.join(root, 'build', 'Port17测试指南.html'), out);
console.log('build/Port17测试指南.html ' + Buffer.byteLength(out) + ' bytes');
