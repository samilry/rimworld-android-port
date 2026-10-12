// Reads an entry of a Waylith asset pack (a zip with zstd-compressed entries): prints the compression methods used
// and writes the entry's bytes to a file. usage: node pack-read.js <Assets.zip> <entry name> <out file>
const fs = require('fs'), zlib = require('zlib');
const [zipPath, entryName, outPath] = process.argv.slice(2);
const fd = fs.openSync(zipPath, 'r');
const size = fs.fstatSync(fd).size;
function readAt(off, len) { const b = Buffer.alloc(len); fs.readSync(fd, b, 0, len, off); return b; }
// End of central directory (zip64 aware).
const tail = readAt(Math.max(0, size - 65557 - 20), Math.min(size, 65557 + 20));
let eocd = tail.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
let cdOffset = tail.readUInt32LE(eocd + 16), cdSize = tail.readUInt32LE(eocd + 12), count = tail.readUInt16LE(eocd + 10);
if (cdOffset === 0xffffffff || count === 0xffff) {
	const loc = tail.lastIndexOf(Buffer.from([0x50, 0x4b, 0x06, 0x07]));
	const z64Off = Number(tail.readBigUInt64LE(loc + 8));
	const z64 = readAt(z64Off, 56);
	count = Number(z64.readBigUInt64LE(32)); cdSize = Number(z64.readBigUInt64LE(40)); cdOffset = Number(z64.readBigUInt64LE(48));
}
const cd = readAt(cdOffset, cdSize);
const methods = {};
let p = 0, found = null;
for (let i = 0; i < count; i++) {
	if (cd.readUInt32LE(p) !== 0x02014b50) break;
	const method = cd.readUInt16LE(p + 10), csize = cd.readUInt32LE(p + 20), usize = cd.readUInt32LE(p + 24);
	const nlen = cd.readUInt16LE(p + 28), elen = cd.readUInt16LE(p + 30), clen = cd.readUInt16LE(p + 32);
	let lho = cd.readUInt32LE(p + 42);
	const name = cd.toString('utf8', p + 46, p + 46 + nlen);
	methods[method] = (methods[method] || 0) + 1;
	if (name === entryName) found = { method, csize, usize, lho, extra: cd.slice(p + 46 + nlen, p + 46 + nlen + elen) };
	p += 46 + nlen + elen + clen;
}
console.log('entries:', count, 'methods:', JSON.stringify(methods));
if (!found) { console.log('entry not found'); process.exit(1); }
if (found.lho === 0xffffffff) { // zip64 extra: sizes then offset
	let q = 0; const e = found.extra;
	while (q + 4 <= e.length) { const id = e.readUInt16LE(q), len = e.readUInt16LE(q + 2); if (id === 1) { let r = q + 4; if (found.usize === 0xffffffff) { found.usize = Number(e.readBigUInt64LE(r)); r += 8; } if (found.csize === 0xffffffff) { found.csize = Number(e.readBigUInt64LE(r)); r += 8; } found.lho = Number(e.readBigUInt64LE(r)); } q += 4 + len; }
}
const lh = readAt(found.lho, 30);
const dataOff = found.lho + 30 + lh.readUInt16LE(26) + lh.readUInt16LE(28);
const raw = readAt(dataOff, found.csize);
let data;
if (found.method === 0) data = raw;
else if (found.method === 8) data = zlib.inflateRawSync(raw);
else if (found.method === 93) data = zlib.zstdDecompressSync(raw);
else { console.log('unknown method', found.method, 'first bytes', raw.slice(0, 8).toString('hex')); data = raw; }
fs.writeFileSync(outPath, data);
console.log(entryName + ':', 'method', found.method, 'compressed', found.csize, 'size', data.length, 'first bytes', data.slice(0, 16).toString('hex'));
