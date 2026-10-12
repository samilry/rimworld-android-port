// Some of the game's assemblies are built for x64 only (PE machine type AMD64), which the stock runtime refuses on
// arm64 ("The assembly architecture is incompatible"); Ludeon's runtime fork has a switch to ignore that. They are
// pure IL, so the machine type in the COFF header is all that matters: it is changed to ARM64 here.
// usage: node fix-arch.js <folder> [--list] [--prune-native]
//   rewrites the .dll files in place unless --list; --prune-native deletes the Windows native DLLs (not assemblies)
const fs = require('fs'), path = require('path');
const folder = process.argv[2];
const listOnly = process.argv.includes('--list');
const pruneNative = process.argv.includes('--prune-native');
let pruned = 0;
const MACHINES = { 0x14c: 'I386 (any CPU)', 0x8664: 'AMD64', 0xaa64: 'ARM64', 0x1c4: 'ARMNT' };
let changed = 0;
for (const name of fs.readdirSync(folder).filter(n => n.toLowerCase().endsWith('.dll')).sort()) {
	const file = path.join(folder, name);
	const buf = fs.readFileSync(file);
	if (buf.length < 0x40 || buf.readUInt16LE(0) !== 0x5a4d) continue;
	const pe = buf.readUInt32LE(0x3c);
	if (pe + 24 > buf.length || buf.readUInt32LE(pe) !== 0x4550) continue;
	const machine = buf.readUInt16LE(pe + 4);
	const optSize = buf.readUInt16LE(pe + 20);
	const opt = pe + 24;
	const magic = buf.readUInt16LE(opt);
	const pe32plus = magic === 0x20b;
	const dirs = opt + (pe32plus ? 112 : 96);
	const cliSize = buf.readUInt32LE(dirs + 14 * 8 + 4);
	const managed = cliSize > 0;
	if (listOnly) {
		console.log(`${name}: ${MACHINES[machine] || machine.toString(16)}, ${pe32plus ? 'PE32+' : 'PE32'}, ${managed ? 'managed' : 'native'}`);
		continue;
	}
	if (!managed) {
		if (pruneNative) {
			fs.unlinkSync(file);
			pruned++;
		}
		continue;
	}
	if (machine === 0x8664) {
		buf.writeUInt16LE(0xaa64, pe + 4);
		fs.writeFileSync(file, buf);
		changed++;
		console.log(`${name}: AMD64 -> ARM64`);
	}
}
if (!listOnly) console.log(`${changed} assemblies changed, ${pruned} native Windows DLLs removed`);
