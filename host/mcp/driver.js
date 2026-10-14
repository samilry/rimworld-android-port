// Keeps one rimworld-mcp.js running (so the agent stays switched on and the log cursor carries over) and passes tool
// calls to it from the command line. For sessions where the MCP tools are not loaded.
//   node host/mcp/driver.js serve                 (in the background; stops when the server ends or on "stop")
//   node host/mcp/driver.js <tool> '<json>' [<tool> '<json>' ...]
//   node host/mcp/driver.js stop
// Images are written to logs/play/<n>.png and their path printed.
const http = require('http');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const PORT = Number(process.env.RW_DRIVER_PORT || 47199);
const IMAGES = path.join(__dirname, '../../logs/play');

function serve() {
  const server = spawn(process.execPath, [path.join(__dirname, 'rimworld-mcp.js')], { stdio: ['pipe', 'pipe', 'inherit'] });
  let buffer = '';
  let nextId = 1;
  const pending = new Map();
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
  server.on('exit', code => { console.log('server ended (' + code + ')'); process.exit(0); });
  const rpc = (method, params) => {
    const id = nextId++;
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    return new Promise(resolve => pending.set(id, resolve));
  };
  rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'driver', version: '0' } }).then(() => {
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  });
  fs.mkdirSync(IMAGES, { recursive: true });
  let imageNumber = fs.readdirSync(IMAGES).filter(f => /^\d+\.png$/.test(f)).map(f => parseInt(f)).reduce((a, b) => Math.max(a, b), 0);
  http.createServer((req, res) => {
    let body = '';
    req.on('data', c => { body += c; });
    req.on('end', async () => {
      if (req.url === '/stop') {
        res.end('stopping\n');
        server.stdin.end();
        return;
      }
      try {
        const { name, args } = JSON.parse(body);
        const answer = await rpc('tools/call', { name, arguments: args || {} });
        const out = [];
        if (answer.error) out.push('error: ' + JSON.stringify(answer.error));
        else {
          if (answer.result.isError) out.push('[error]');
          for (const c of answer.result.content) {
            if (c.type === 'image') {
              const file = path.join(IMAGES, ++imageNumber + '.png');
              fs.writeFileSync(file, Buffer.from(c.data, 'base64'));
              out.push('[image ' + file + ']');
            } else out.push(c.text);
          }
        }
        res.end(out.join('\n') + '\n');
      } catch (e) {
        res.end('driver error: ' + e.message + '\n');
      }
    });
  }).listen(PORT, '127.0.0.1', () => console.log('driver listening on ' + PORT));
}

function post(url, body) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port: PORT, path: url, method: 'POST' }, res => {
      let text = '';
      res.setEncoding('utf8');
      res.on('data', c => { text += c; });
      res.on('end', () => resolve(text));
    });
    req.on('error', reject);
    req.end(body || '');
  });
}

(async () => {
  const args = process.argv.slice(2);
  if (args[0] === 'serve') return serve();
  if (args[0] === 'stop') return console.log(await post('/stop'));
  for (let i = 0; i < args.length; i += 2) {
    const started = Date.now();
    const text = await post('/call', JSON.stringify({ name: args[i], args: JSON.parse(args[i + 1] || '{}') }));
    process.stdout.write('== ' + args[i] + ' (' + (Date.now() - started) + ' ms)\n' + text);
  }
})().catch(e => { console.error(e.message); process.exit(1); });
