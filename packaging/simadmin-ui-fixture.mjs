// Offline UI acceptance fixture only. Never proxies to a modem, API or production server.
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';

const root = resolve(process.argv[3] || 'SimAdmin-sip/frontend/dist');
const port = Number(process.argv[2] || 43882);
const envelope = data => ({ status: 'ok', message: 'UI fixture only', data });
const security = { password_protection_enabled: true, password_min_length: 8, password_require_letters: true, password_require_digits: true, password_require_symbols: true, session_ttl_seconds: 86400, idle_timeout_seconds: 0 };
const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.woff2': 'font/woff2' };
const imsKey = index => 'modem-' + String(index + 1).repeat(64);
const imsPath = index => `/org/freedesktop/ModemManager1/Modem/${index}`;
const imsLines = {
  [imsKey(0)]: { phase: 'registered', generation: '1', cleanup: 'owned', error: null, expires_in_seconds: 600, refresh_after_seconds: 300, modem_path: imsPath(0) },
  [imsKey(1)]: { phase: 'failed', generation: '2', cleanup: 'released', error: 'Synthetic SIM identity check failed; no real card was used.', expires_in_seconds: null, refresh_after_seconds: null, modem_path: imsPath(1) },
};
let imsGeneration = 2;
const imsStatus = () => ({ experimental_registration_only: true, carrier_calls: false, carrier_sms: false, vo_wifi: false, startup_restore: false, lines: imsLines });
http.createServer(async (request, response) => {
  const url = new URL(request.url, `http://127.0.0.1:${port}`);
  // These two POST endpoints only mutate synthetic memory in this fixture.
  // There is no forwarding, process invocation, device or production API.
  if (request.method === 'POST' && ['/api/ims/start', '/api/ims/stop'].includes(url.pathname)) {
    let body = ''; for await (const chunk of request) { body += chunk; if (body.length > 4096) { response.writeHead(413); response.end(); return; } }
    try {
      const value = JSON.parse(body);
      if (url.pathname.endsWith('/start')) {
        const index = [0, 1].find(index => value.modem_path === imsPath(index) && value.expected_line === imsKey(index));
        if (index === undefined || value.confirm_registration_only !== true || value.expected_subscriber !== 'sim-' + String(index + 3).repeat(64)) throw new Error('invalid synthetic selection');
        imsLines[imsKey(index)] = { phase: 'registering', generation: String(++imsGeneration), cleanup: 'owned', error: null, expires_in_seconds: null, refresh_after_seconds: null, modem_path: imsPath(index) };
        setTimeout(() => { if (imsLines[imsKey(index)].phase === 'registering') Object.assign(imsLines[imsKey(index)], { phase: 'registered', expires_in_seconds: 600, refresh_after_seconds: 300 }); }, 700);
      } else {
        const line = imsLines[value.line];
        if (!line || value.generation !== line.generation) throw new Error('stale synthetic generation');
        line.phase = 'stopping'; setTimeout(() => Object.assign(line, { phase: 'stopped', cleanup: 'released', error: null, expires_in_seconds: null, refresh_after_seconds: null }), 600);
      }
      response.writeHead(202, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); response.end(JSON.stringify(envelope(imsStatus())));
    } catch { response.writeHead(409, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ status: 'error', message: 'Synthetic selection or generation mismatch' })); }
    return;
  }
  if (request.method !== 'GET') { response.writeHead(405); response.end(); return; }
  if (url.pathname.startsWith('/api/')) {
    const fixtures = {
      '/api/auth/status': { configured: true, authenticated: true, settings: security },
      '/api/work-mode': { mode: 'sim', worker_running: false, esim_supported: false },
      '/api/data/status': { active: false },
      '/api/airplane-mode': { enabled: false, powered: true, online: true },
      '/api/hub': { config: { enabled: false, url: '', local_fallback_timeout_seconds: 120, local_fallback_enabled: true }, runtime: { connection_state: 'waiting_for_hub' } },
      '/api/health': { status: 'ok' },
      '/api/ims/status': imsStatus(),
      '/api/ims/modems': [0, 1].map(index => ({ modem_path: imsPath(index), label: `测试模组 ${index === 0 ? 'A' : 'B'}` })),
      '/api/ims/selection': (() => { const index = [0, 1].find(index => url.searchParams.get('modem_path') === imsPath(index)); return index === undefined ? null : { modem_path: imsPath(index), line: imsKey(index), subscriber: 'sim-' + String(index + 3).repeat(64) }; })(),
      '/api/sms/lines': { lines: [{ slot_id: 'modem-' + '1'.repeat(64), label: '测试模组 A · 11111111' }, { slot_id: 'modem-' + '2'.repeat(64), label: '测试模组 B · 22222222' }], unavailable_count: 0 },
      '/api/sms/list': { messages: [] },
      '/api/sms/stats': { total: 0, incoming: 0, outgoing: 0, notification_success: 0, notification_attempted: 0 },
      '/api/settings/sms-api': { enabled: false, bind_address: '127.0.0.1', port: 8787, allow_lan_access: false, token_configured: false, running: false, error: null, base_url: 'http://127.0.0.1:8787/' },
      '/api/settings/sip-gateway': { enabled: false, bind_address: '127.0.0.1', sip_port: 5060, realm: 'simadmin.local', messages_enabled: false, extensions: [{ extension: '1001', password_configured: true }, { extension: '1002', password_configured: true }], running: false, component_available: true, registered_contacts: 0, active_dialogs: 0, error: null, capabilities: [], carrier_calls: false, carrier_sms: false, host_ims: false, vo_wifi: false, sms_transport_available: true, sms: { allowOutgoing: false, forwardIncoming: false, defaultLine: null, lines: { ['modem-' + '1'.repeat(64)]: { allowOutgoing: false, forwardIncoming: false, target: '1001' }, ['modem-' + '2'.repeat(64)]: { allowOutgoing: false, forwardIncoming: false, target: '1002' } }, permissions: {} } },
    };
    response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    response.end(JSON.stringify(envelope(fixtures[url.pathname] ?? {}))); return;
  }
  try {
    const relative = decodeURIComponent(url.pathname);
    const file = resolve(root, `.${relative}`);
    if (file !== root && !file.startsWith(root + sep)) { response.writeHead(403); response.end(); return; }
    const target = extname(file) ? file : resolve(root, 'index.html');
    let content = await readFile(target);
    if (extname(target) === '.html') content = Buffer.from(content.toString('utf8').replace('<body>', '<body><div role="note" style="position:fixed;right:12px;bottom:8px;z-index:1500;padding:4px 8px;border-radius:4px;background:#fff3cd;color:#332701;font:12px sans-serif;pointer-events:none">离线界面模拟 · 不访问模组</div>'));
    response.writeHead(200, { 'Content-Type': mime[extname(target)] || 'application/octet-stream' }); response.end(content);
  } catch { response.writeHead(404); response.end(); }
}).listen(port, '127.0.0.1', () => process.stdout.write(`Offline UI fixture: http://127.0.0.1:${port}/config\n`));
