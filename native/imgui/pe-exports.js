// Lists the names a Windows DLL exports, in the order of its export name table (sorted, as the PE format keeps them):
// native/build.sh takes the names the PC game's imgui.dll exports from it, the flat C API gen-imgui-c.js generates.
// usage: node pe-exports.js <file.dll>
const fs = require('fs');
const b = fs.readFileSync(process.argv[2]);
const pe = b.readUInt32LE(0x3c);
if (b.toString('latin1', pe, pe + 4) !== 'PE\0\0') throw new Error(process.argv[2] + ' is not a PE file');
const sectionCount = b.readUInt16LE(pe + 6);
const optionalSize = b.readUInt16LE(pe + 20);
const optional = pe + 24;
// The data directories follow the optional header's fixed part (PE32+ for 64-bit DLLs); the export table is the first.
const directories = optional + (b.readUInt16LE(optional) === 0x20b ? 112 : 96);
const exportRva = b.readUInt32LE(directories);
const sections = [];
for (let i = 0; i < sectionCount; i++) {
	const s = optional + optionalSize + i * 40;
	sections.push({ va: b.readUInt32LE(s + 12), size: Math.max(b.readUInt32LE(s + 8), b.readUInt32LE(s + 16)), raw: b.readUInt32LE(s + 20) });
}
function offset(rva) {
	const s = sections.find(s => rva >= s.va && rva < s.va + s.size);
	if (!s) throw new Error('RVA ' + rva.toString(16) + ' is in no section');
	return rva - s.va + s.raw;
}
const table = offset(exportRva);
const nameCount = b.readUInt32LE(table + 24);
const names = offset(b.readUInt32LE(table + 32));
const out = [];
for (let i = 0; i < nameCount; i++) {
	const start = offset(b.readUInt32LE(names + 4 * i));
	let end = start;
	while (b[end]) end++;
	out.push(b.toString('latin1', start, end));
}
process.stdout.write(out.join('\n') + '\n');
