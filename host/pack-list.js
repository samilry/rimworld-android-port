// Lists the entries of a Waylith asset pack (a zip) by file extension: how many, stored and compressed sizes, and
// the compression methods used. With a second argument, prints the entries whose name contains it.
// usage: node host/pack-list.js <Assets.zip> [name part]
const fs = require('fs');
const [zipPath, part] = process.argv.slice(2);
const fd = fs.openSync(zipPath, 'r');
const size = fs.fstatSync(fd).size;
function readAt(off, len) { const b = Buffer.alloc(len); fs.readSync(fd, b, 0, len, off); return b; }
const tail = readAt(Math.max(0, size - 65557 - 20), Math.min(size, 65557 + 20));
let eocd = tail.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
let cdOffset = tail.readUInt32LE(eocd + 16), cdSize = tail.readUInt32LE(eocd + 12), count = tail.readUInt16LE(eocd + 10);
if (cdOffset === 0xffffffff || count === 0xffff) {
  const loc = tail.lastIndexOf(Buffer.from([0x50, 0x4b, 0x06, 0x07]));
  const z64 = readAt(Number(tail.readBigUInt64LE(loc + 8)), 56);
  count = Number(z64.readBigUInt64LE(32)); cdSize = Number(z64.readBigUInt64LE(40)); cdOffset = Number(z64.readBigUInt64LE(48));
}
const cd = readAt(cdOffset, cdSize);
const byExt = {};
let p = 0;
for (let i = 0; i < count; i++) {
  if (cd.readUInt32LE(p) !== 0x02014b50) break;
  const method = cd.readUInt16LE(p + 10), csize = cd.readUInt32LE(p + 20), usize = cd.readUInt32LE(p + 24);
  const nlen = cd.readUInt16LE(p + 28), elen = cd.readUInt16LE(p + 30), clen = cd.readUInt16LE(p + 32);
  const name = cd.toString('utf8', p + 46, p + 46 + nlen);
  const ext = (/\.[A-Za-z0-9]+$/.exec(name) || ['(none)'])[0].toLowerCase();
  const e = byExt[ext] || (byExt[ext] = { count: 0, csize: 0, usize: 0, methods: {} });
  e.count++; e.csize += csize; e.usize += usize; e.methods[method] = (e.methods[method] || 0) + 1;
  if (part && name.includes(part)) console.log(String(usize).padStart(10) + ' ' + String(csize).padStart(10) + ' m' + method + '  ' + name);
  p += 46 + nlen + elen + clen;
}
if (!part) {
  console.log('extension      entries   stored MiB   unpacked MiB   methods (0 stored, 8 deflate, 93 zstd)');
  for (const [ext, e] of Object.entries(byExt).sort((a, b) => b[1].csize - a[1].csize))
    console.log(ext.padEnd(14) + String(e.count).padStart(8) + (e.csize / 1048576).toFixed(1).padStart(13) + (e.usize / 1048576).toFixed(1).padStart(15) + '   ' + JSON.stringify(e.methods));
}
