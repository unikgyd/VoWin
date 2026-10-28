// Task-scoped download proxy for an SSH reverse forward. Never expose it on a LAN interface.
// No credentials, request URLs, query strings or headers are logged.
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';

const port = Number(process.argv[2] ?? 43881);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid local proxy port');
const hosts = new Set(['sh.rustup.rs', 'static.rust-lang.org', 'index.crates.io', 'static.crates.io',
  'crates.io', 'github.com', 'raw.githubusercontent.com', 'sg.archive.ubuntu.com', 'archive.ubuntu.com',
  'security.ubuntu.com', 'download.docker.com']);
const allowed = (host, targetPort) => hosts.has(host.toLowerCase()) && [80, 443].includes(Number(targetPort));
let active = 0;
const server = http.createServer((request, response) => {
  let url;
  try { url = new URL(request.url); } catch { response.writeHead(400).end(); return; }
  if (!['GET', 'HEAD'].includes(request.method) || !['http:', 'https:'].includes(url.protocol) || url.username || url.password ||
      !allowed(url.hostname, url.port || (url.protocol === 'https:' ? 443 : 80)) || active >= 32) {
    response.writeHead(403).end(); return;
  }
  active++;
  const headers = { ...request.headers, host: url.host, connection: 'close' };
  delete headers['proxy-authorization']; delete headers['proxy-connection'];
  const upstream = (url.protocol === 'https:' ? https : http).request(url, { method: request.method, headers, timeout: 30000 }, result => {
    response.writeHead(result.statusCode ?? 502, result.headers); result.pipe(response);
  });
  upstream.on('timeout', () => upstream.destroy());
  upstream.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end(); });
  response.on('close', () => { active--; upstream.destroy(); });
  request.pipe(upstream);
});
server.on('connect', (request, client, head) => {
  const match = /^([^:]+):(\d+)$/.exec(request.url ?? '');
  if (!match || !allowed(match[1], match[2]) || Number(match[2]) !== 443 || active >= 32) {
    client.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return;
  }
  active++;
  const upstream = net.connect({ host: match[1], port: Number(match[2]), timeout: 30000 }, () => {
    client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    if (head.length) upstream.write(head);
    upstream.pipe(client); client.pipe(upstream);
  });
  upstream.on('timeout', () => upstream.destroy());
  upstream.on('error', () => client.destroy()); client.on('error', () => upstream.destroy());
  client.on('close', () => { active--; upstream.destroy(); });
  upstream.on('close', () => client.destroy());
});
server.listen(port, '127.0.0.1', () => console.log(`Build download proxy on loopback port ${port}; allowlisted hosts only`));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close(() => process.exit(0)));
