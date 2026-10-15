// Prints the names listed in the first file that the second does not list (one name per line, CR/LF either).
// usage: node host/names-only-in.js <names file> <other names file>
const fs = require('fs');
const read = f => fs.readFileSync(f, 'utf8').split(/\r?\n/).map(x => x.trim()).filter(Boolean);
const other = new Set(read(process.argv[3]));
console.log(read(process.argv[2]).filter(x => !other.has(x)).join(' '));
