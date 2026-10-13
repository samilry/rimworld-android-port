// A small SPIR-V reader for comparing compilers: names, decorations and interface variables of a module.
// usage: node spv-info.js <module.spv | record.spv-cache>
const fs = require('fs');
let d = fs.readFileSync(process.argv[2]);
let start = 0;
for (let i = 0; i + 4 <= d.length; i += 4) { if (d.readUInt32LE(i) === 0x07230203) { start = i; break; } }
d = d.subarray(start);
const words = new Uint32Array(d.buffer.slice(d.byteOffset, d.byteOffset + (d.length & ~3)));
const DEC = { 2: 'Block', 3: 'BufferBlock', 4: 'RowMajor', 5: 'ColMajor', 6: 'ArrayStride', 7: 'MatrixStride', 11: 'BuiltIn', 14: 'Flat', 18: 'NonWritable', 24: 'NonWritable', 30: 'Location', 31: 'Component', 32: 'Index', 33: 'Binding', 34: 'DescriptorSet', 35: 'Offset' };
const STORAGE = { 0: 'UniformConstant', 1: 'Input', 2: 'Uniform', 3: 'Output', 9: 'PushConstant', 12: 'StorageBuffer' };
function str(i, end) { const b = []; for (let w = i; w < end; w++) for (let k = 0; k < 4; k++) { const c = (words[w] >>> (8 * k)) & 255; if (!c) return Buffer.from(b).toString('utf8'); b.push(c); } return Buffer.from(b).toString('utf8'); }
const names = new Map(), members = new Map(), decorations = new Map(), memberDecorations = new Map(), vars = [], types = new Map();
console.log('version ' + ((words[1] >> 16) & 255) + '.' + ((words[1] >> 8) & 255) + ', generator ' + (words[2] >>> 16) + ', bound ' + words[3]);
for (let i = 5; i < words.length;) {
  const op = words[i] & 0xffff, n = words[i] >>> 16;
  if (n === 0) break;
  if (op === 5) names.set(words[i + 1], str(i + 2, i + n));
  else if (op === 6) members.set(words[i + 1] + '.' + words[i + 2], str(i + 3, i + n));
  else if (op === 71) { const k = words[i + 1]; if (!decorations.has(k)) decorations.set(k, []); decorations.get(k).push((DEC[words[i + 2]] || 'dec' + words[i + 2]) + (n > 3 ? ' ' + words[i + 3] : '')); }
  else if (op === 72) { const k = words[i + 1] + '.' + words[i + 2]; if (!memberDecorations.has(k)) memberDecorations.set(k, []); memberDecorations.get(k).push((DEC[words[i + 3]] || 'dec' + words[i + 3]) + (n > 4 ? ' ' + words[i + 4] : '')); }
  else if (op === 59) vars.push({ id: words[i + 2], type: words[i + 1], storage: words[i + 3] });
  else if (op === 32) types.set(words[i + 1], { pointer: words[i + 3] });
  else if (op === 30) types.set(words[i + 1], { struct: Array.from(words.subarray(i + 2, i + n)) });
  else if (op === 24) types.set(words[i + 1], { matrix: words[i + 3] });
  else if (op === 15) console.log('entry point: model ' + words[i + 1] + ' "' + str(i + 3, i + n) + '"');
  i += n;
}
for (const v of vars) {
  if (!(v.storage in STORAGE) ) continue;
  const pointee = (types.get(v.type) || {}).pointer;
  const t = types.get(pointee) || {};
  console.log(STORAGE[v.storage].padEnd(15) + ' "' + (names.get(v.id) || '') + '"  [' + (decorations.get(v.id) || []).join(', ') + ']' + (t.struct ? '  struct "' + (names.get(pointee) || '') + '" [' + (decorations.get(pointee) || []).join(', ') + ']' : ''));
  if (t.struct) t.struct.forEach((m, k) => console.log('    member ' + k + ' "' + (members.get(pointee + '.' + k) || '') + '"  [' + (memberDecorations.get(pointee + '.' + k) || []).join(', ') + ']' + ((types.get(m) || {}).matrix ? '  matrix' : '')));
}
