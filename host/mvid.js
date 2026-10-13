// The module version id (MVID) of a .NET assembly: the GUID the compiler gives each build. It tells two builds of an
// assembly apart where size and name do not, and the phone's mod preparation (patch/ModPrep.cs, Mono.Cecil) keeps it,
// so a prepared assembly on the phone can be held against the PC's original.
// As a module: mvidOf(buffer) -> "xxxxxxxx-...." or null (not a managed assembly).
// usage: node host/mvid.js <file.dll>...
function mvidOf(b) {
  try {
    if (b.length < 0x100 || b.readUInt16LE(0) !== 0x5a4d) return null;
    const pe = b.readUInt32LE(0x3c);
    if (pe + 0x108 > b.length || b.readUInt32LE(pe) !== 0x4550) return null;
    const sections = b.readUInt16LE(pe + 6), optSize = b.readUInt16LE(pe + 20), opt = pe + 24;
    const dirs = opt + (b.readUInt16LE(opt) === 0x20b ? 112 : 96);
    const cliRva = b.readUInt32LE(dirs + 14 * 8);
    if (!cliRva) return null;
    const secs = opt + optSize;
    const off = rva => {
      for (let i = 0; i < sections; i++) {
        const s = secs + i * 40, va = b.readUInt32LE(s + 12), size = Math.max(b.readUInt32LE(s + 8), b.readUInt32LE(s + 16));
        if (rva >= va && rva < va + size) return rva - va + b.readUInt32LE(s + 20);
      }
      return -1;
    };
    const cli = off(cliRva);
    if (cli < 0) return null;
    const meta = off(b.readUInt32LE(cli + 8));
    if (meta < 0 || b.readUInt32LE(meta) !== 0x424a5342) return null;
    const versionLength = b.readUInt32LE(meta + 12);
    let p = meta + 16 + versionLength;
    const streams = b.readUInt16LE(p + 2);
    p += 4;
    for (let i = 0; i < streams; i++) {
      const offset = b.readUInt32LE(p), size = b.readUInt32LE(p + 4);
      let end = p + 8;
      while (b[end] !== 0) end++;
      const name = b.toString('ascii', p + 8, end);
      p = (end + 4) & ~3;
      if (name === '#GUID' && size >= 16) {
        // The module table's Mvid column is an index into this heap; with one module per file it is the first entry.
        const g = b.subarray(meta + offset, meta + offset + 16);
        const hex = (from, to, little) => { const part = Buffer.from(g.subarray(from, to)); if (little) part.reverse(); return part.toString('hex'); };
        return hex(0, 4, true) + '-' + hex(4, 6, true) + '-' + hex(6, 8, true) + '-' + hex(8, 10) + '-' + hex(10, 16);
      }
    }
    return null;
  } catch (e) {
    return null;
  }
}
module.exports = { mvidOf };
if (require.main === module) {
  const fs = require('fs');
  for (const f of process.argv.slice(2)) console.log((mvidOf(fs.readFileSync(f)) || 'not a managed assembly') + '  ' + f);
}
