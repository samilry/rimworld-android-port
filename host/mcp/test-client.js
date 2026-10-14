// Talks to rimworld-mcp.js the way Claude Code does (stdio JSON-RPC), for testing without a restart of the session.
// usage: node host/mcp/test-client.js '<tool>' '<json args>' ['<tool>' '<json args>' ...]
//        node host/mcp/test-client.js --list
// Images are written to logs/mcp-<n>.png instead of being printed.
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const server = spawn(process.execPath, [path.join(__dirname, 'rimworld-mcp.js')], { stdio: ['pipe', 'pipe', 'inherit'] });
let buffer = '';
const pending = new Map();
let nextId = 1;
server.stdout.setEncoding('utf8');
server.stdout.on('data', chunk => {
  buffer += chunk;
  let nl;
  while ((nl = buffer.indexOf('\n')) >= 0) {
    const message = JSON.parse(buffer.slice(0, nl));
    buffer = buffer.slice(nl + 1);
    const resolve = pending.get(message.id);
    if (resolve) { pending.delete(message.id); resolve(message); }
  }
});

function call(method, params) {
  const id = nextId++;
  server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return new Promise(resolve => pending.set(id, resolve));
}

(async () => {
  await call('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'test', version: '0' } });
  server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const args = process.argv.slice(2);
  if (args[0] === '--list') {
    const list = await call('tools/list', {});
    for (const t of list.result.tools) console.log(t.name + ': ' + t.description.slice(0, 100));
  } else {
    for (let i = 0; i < args.length; i += 2) {
      const started = Date.now();
      const answer = await call('tools/call', { name: args[i], arguments: JSON.parse(args[i + 1] || '{}') });
      console.log('== ' + args[i] + ' (' + (Date.now() - started) + ' ms)' + (answer.result && answer.result.isError ? ' [error]' : ''));
      if (answer.error) { console.log(JSON.stringify(answer.error)); continue; }
      for (const c of answer.result.content) {
        if (c.type === 'image') {
          let n = 1;
          while (fs.existsSync(path.join(__dirname, '../../logs/mcp-' + n + '.png'))) n++;
          const file = path.join(__dirname, '../../logs/mcp-' + n + '.png');
          fs.writeFileSync(file, Buffer.from(c.data, 'base64'));
          console.log('[image -> ' + path.relative(process.cwd(), file) + ']');
        } else {
          console.log(c.text);
        }
      }
    }
  }
  server.stdin.end();
})();
