// The allocator's own statistics (malloc_info, written by "memdump=LABEL" as memdump-LABEL-malloc.xml) in a few lines:
// per arena, bytes in small bins, in large runs (up to a chunk) and in huge blocks (their own mappings).
// usage: node host/malloc-info.js <file.xml>
const s = require('fs').readFileSync(process.argv[2], 'utf8');
const mib = (n) => (n / 1048576).toFixed(1).padStart(8);
let large = 0, huge = 0, bins = 0;
for (const heap of s.split('<heap nr=').slice(1)) {
  const get = (name) => { const m = new RegExp('<' + name + '>(\\d+)</' + name + '>').exec(heap); return m ? Number(m[1]) : 0; };
  const l = get('allocated-large'), h = get('allocated-huge'), b = get('allocated-bins');
  large += l; huge += h; bins += b;
  console.log('arena ' + heap.slice(0, heap.indexOf('>')) + ': large ' + mib(l) + ', huge ' + mib(h) + ', bins ' + mib(b) + ' MiB');
  const sizes = [];
  const re = /<bin nr="(\d+)"><allocated>(\d+)<\/allocated><nmalloc>(\d+)<\/nmalloc><ndalloc>(\d+)<\/ndalloc>/g;
  let m;
  while ((m = re.exec(heap))) sizes.push({ nr: Number(m[1]), bytes: Number(m[2]), live: Number(m[3]) - Number(m[4]) });
  for (const bin of sizes.sort((a, b) => b.bytes - a.bytes).slice(0, 6))
    console.log('    bin ' + String(bin.nr).padStart(2) + ': ' + mib(bin.bytes) + ' MiB in ' + bin.live + ' blocks' + (bin.live ? ' of ' + Math.round(bin.bytes / bin.live) + ' bytes' : ''));
}
console.log('all arenas: large ' + mib(large) + ', huge ' + mib(huge) + ', bins ' + mib(bins) + ' MiB; together ' + mib(large + huge + bins));
