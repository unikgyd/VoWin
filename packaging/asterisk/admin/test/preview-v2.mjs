// Synthetic loopback UI preview only. Never proxies to Asterisk, SIP, a SIM or a store.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { adoptV2View, serializeV2Draft } from '../web/v2-model.mjs';

const web = new URL('../web/', import.meta.url);
const token = 'synthetic-v2-preview-csrf';
const scenarios = new Set(['normal', 'empty', 'all-off', 'conflict', 'invalid', 'expire-on-status', 'login', 'status-down', 'load-failure', 'invalid-mode',
  'application-enabled', 'application-disabled', 'application-rollback', 'application-recovery', 'application-index-gap', 'application-drift', 'application-busy', 'application-invalid', 'application-lost-response']);
function fixture(scenario) {
  return {
    schemaVersion: 2, revision: 'A'.repeat(43), state: 'saved-not-applied', canApply: false,
    sipServer: 'pbx.example.invalid', sipPort: 5060,
    features: { calls: scenario !== 'all-off', carrier: scenario !== 'all-off', messages: scenario !== 'all-off', sms: scenario !== 'all-off' },
    extensions: scenario === 'empty' ? [] : [
      { number: '2101', sourceNumber: '2101', name: '合成桌面', enabled: true, ring: true, maxContacts: 4,
        allowOutgoing: true, allowMessages: true, allowMobileSmsSend: false, allowMobileSmsReceive: true, hasPassword: true },
      { number: '2102', sourceNumber: '2102', name: '合成移动端', enabled: false, ring: false, maxContacts: 4,
        allowOutgoing: false, allowMessages: false, allowMobileSmsSend: true, allowMobileSmsReceive: false, hasPassword: true },
    ],
  };
}
export function createV2Preview({ scenario: requested = 'normal' } = {}) {
  if (!scenarios.has(requested)) throw new Error('Unknown synthetic preview scenario');
  let scenario = requested, value = fixture(scenario), revision = 0, statusReads = 0, authenticated = scenario !== 'login';
  let passwords = new Map(value.extensions.map(row => [row.number, 'Synthetic_preview-only_123']));
  let application, applicationJobs, applicationCounter, previousInstalled;
  const op = (state, revision, operation) => ({ operation, revision, state, fileCount: 20,
    changedFiles: ['config/pjsip.conf'], pbxApplied: state === 'applied', gatewayApplied: false, firewallApplied: false, statusSource: 'journal' });
  const emptyApplication = () => ({ schemaVersion: 1, busy: false, operation: null, installed: null,
    installedState: 'unknown', statusSource: 'journal', gatewayApplied: false, firewallApplied: false });
  function resetApplication() {
    application = emptyApplication(); applicationJobs = new Map(); applicationCounter = 0; previousInstalled = null;
    if (['application-rollback', 'application-recovery', 'application-index-gap', 'application-drift'].includes(scenario)) {
      application.operation = op('applied', 'A'.repeat(43), 'O'.repeat(43));
      application.installed = { operation: 'O'.repeat(43), revision: 'A'.repeat(43) }; application.installedState = 'known';
      previousInstalled = { ...application.installed }; value.revision = 'B'.repeat(43);
      if (scenario === 'application-recovery') { application.operation = op('recovery-required', value.revision, 'P'.repeat(43)); application.installedState = 'unconfirmed'; }
      if (scenario === 'application-index-gap') { application.operation = op('applied', value.revision, 'P'.repeat(43)); application.installedState = 'unconfirmed'; }
      if (scenario === 'application-drift') application.installedState = 'drifted';
    }
    if (scenario === 'application-busy') application = { ...emptyApplication(), busy: true, installedState: 'unconfirmed' };
  }
  resetApplication();
  const staticFiles = new Map([
    ['index.html', 'text/html; charset=utf-8'], ['app.js', 'text/javascript; charset=utf-8'],
    ['style.css', 'text/css; charset=utf-8'], ['v2-model.mjs', 'text/javascript; charset=utf-8'],
    ['v2-app.mjs', 'text/javascript; charset=utf-8'],
    ['v2-application-model.mjs', 'text/javascript; charset=utf-8'], ['v2-application.mjs', 'text/javascript; charset=utf-8'],
  ]);
  return createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    const reply = (body, status = 200) => {
      response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
      response.end(JSON.stringify(body));
    };
    async function body() {
      const parts = []; let size = 0;
      for await (const part of request) { size += part.length; if (size > 65536) throw new Error(); parts.push(part); }
      return JSON.parse(Buffer.concat(parts).toString('utf8'));
    }
    function exact(object, keys) {
      return object && typeof object === 'object' && !Array.isArray(object) && Object.keys(object).sort().join('|') === [...keys].sort().join('|');
    }
    try {
      if (url.pathname === '/' || url.pathname === '/index.html') {
        const next = url.searchParams.get('scenario');
        if (next !== null) {
          if (!scenarios.has(next)) return reply({ error: 'Unknown synthetic scenario' }, 400);
          scenario = next; value = fixture(scenario); revision = 0; statusReads = 0; authenticated = scenario !== 'login';
          passwords = new Map(value.extensions.map(row => [row.number, 'Synthetic_preview-only_123']));
          resetApplication();
        }
      }
      if (url.pathname === '/api/configuration') {
        if (scenario === 'invalid-mode') return reply({ error: 'Synthetic mode unavailable' }, 503);
        return reply({ schemaVersion: 2, canApply: false });
      }
      if (url.pathname === '/api/login' && request.method === 'POST') {
        const input = await body();
        if (!exact(input, ['key']) || typeof input.key !== 'string' || !input.key) return reply({ error: 'Synthetic login requires a nonempty placeholder' }, 401);
        authenticated = true; statusReads = 0; return reply({ csrf: token });
      }
      if (url.pathname.startsWith('/api/')) {
        if (!authenticated) return reply({ error: 'Synthetic login required' }, 401);
        if (request.method !== 'GET' && request.headers['x-csrf-token'] !== token) return reply({ error: 'Synthetic CSRF check failed' }, 403);
      }
      if (url.pathname === '/api/session' && request.method === 'GET') return reply({ csrf: token });
      if (url.pathname === '/api/logout' && request.method === 'POST') { authenticated = false; return reply({ ok: true }); }
      if (url.pathname === '/api/v2/application' || url.pathname.startsWith('/api/v2/application/')) {
        const enabled = scenario.startsWith('application-') && scenario !== 'application-disabled';
        const latest = () => [...applicationJobs.values()].at(-1)?.job ?? null;
        const envelope = (job = latest(), includeOverview = true) => {
          const active = latest() && ['queued', 'running'].includes(latest().state);
          const overview = active ? { ...emptyApplication(), busy: true, installedState: 'unconfirmed' } : application;
          return { capability: { enabled }, overview: enabled && includeOverview ? overview : null, job: enabled ? job : null };
        };
        if (url.pathname === '/api/v2/application' && request.method === 'GET') {
          const selected = url.searchParams.get('request');
          if (selected && !applicationJobs.has(selected)) return reply({ error: 'Synthetic request not found' }, 404);
          const result = envelope(selected ? applicationJobs.get(selected).job : latest());
          if (scenario === 'application-invalid') result.overview = { ...application, unexpected: 'synthetic-invalid-state' };
          return reply(result);
        }
        if (!enabled) return reply({ error: 'Synthetic application disabled' }, 403);
        const action = url.pathname.split('/').at(-1);
        if (request.method !== 'POST' || !['prepare', 'apply', 'recover'].includes(action)) return reply({ error: 'Method not allowed' }, 405);
        const input = await body();
        const keys = action === 'prepare' ? ['request', 'revision', 'confirm'] : action === 'apply'
          ? ['request', 'revision', 'operation', 'confirm'] : ['request', 'operation', 'confirm'];
        const validToken = token => typeof token === 'string' && /^[A-Za-z0-9_-]{43}$/u.test(token);
        if (!exact(input, keys) || input.confirm !== true || !validToken(input.request) ||
            (action !== 'recover' && !validToken(input.revision)) || (action !== 'prepare' && !validToken(input.operation))) return reply({ error: 'Synthetic invalid application request' }, 400);
        const old = applicationJobs.get(input.request);
        if (old) return old.payload === JSON.stringify(input) ? reply(envelope(old.job, false), ['queued', 'running'].includes(old.job.state) ? 202 : 200) : reply({ error: 'Synthetic request conflict' }, 409);
        if (latest() && ['queued', 'running'].includes(latest().state)) return reply({ error: 'Synthetic job busy' }, 409);
        if (action !== 'recover' && input.revision !== value.revision) return reply({ error: 'Synthetic revision conflict' }, 409);
        if (action !== 'prepare' && input.operation !== application.operation?.operation) return reply({ error: 'Synthetic operation conflict' }, 409);
        const createdAt = new Date().toISOString(), job = { request: input.request, action, state: 'queued', revision: input.revision ?? '',
          operation: input.operation ?? '', createdAt, updatedAt: createdAt, result: null, errorCode: '' };
        applicationJobs.set(input.request, { payload: JSON.stringify(input), job });
        console.log(JSON.stringify({ syntheticApplicationRequest: action, requests: applicationJobs.size }));
        setTimeout(() => { job.state = 'running'; job.updatedAt = new Date().toISOString(); }, 100);
        setTimeout(() => {
          const operation = action === 'prepare' ? String.fromCharCode(81 + (++applicationCounter % 10)).repeat(43) : input.operation;
          if (action === 'prepare') previousInstalled = application.installed && { ...application.installed };
          const state = action === 'prepare' ? 'prepared' : action === 'recover' || scenario === 'application-rollback' ? 'rolled-back' : 'applied';
          job.result = op(state, input.revision ?? application.operation.revision, operation); job.operation = operation; job.revision = job.result.revision;
          job.state = action === 'apply' && state === 'rolled-back' ? 'failed' : 'succeeded';
          job.errorCode = job.state === 'failed' ? 'rolled-back' : ''; job.updatedAt = new Date().toISOString();
          application.operation = { ...job.result };
          if (state === 'applied') application.installed = { operation, revision: job.revision };
          else if (state === 'rolled-back') application.installed = previousInstalled;
          application.installedState = application.installed ? 'known' : 'unknown';
        }, 1800);
        if (scenario === 'application-lost-response') { response.destroy(); return; }
        return reply(envelope(job, false), 202);
      }
      if (url.pathname === '/api/status' && request.method === 'GET') {
        ++statusReads;
        if (scenario === 'expire-on-status' && statusReads > 1) { authenticated = false; return reply({ error: 'Synthetic session expired' }, 401); }
        if (scenario === 'status-down') return reply({ error: 'Synthetic status unavailable' }, 503);
        // Actual contacts deliberately remain independent of saved draft account switches.
        return reply({ running: true, contacts: [{ number: '2102', state: 'Avail' }] });
      }
      if (url.pathname === '/api/v2/extensions') {
        if (request.method === 'GET') return scenario === 'load-failure' ? reply({ error: 'Synthetic store unavailable' }, 503) : reply(value);
        if (request.method !== 'PUT') return reply({ error: 'Method not allowed' }, 405);
        const input = await body();
        if (!exact(input, ['revision', 'extensions']) || !Array.isArray(input.extensions)) return reply({ error: 'Synthetic invalid draft' }, 400);
        if (input.revision !== value.revision || scenario === 'conflict') return reply({ error: 'Synthetic version conflict' }, 409);
        if (scenario === 'invalid') return reply({ error: 'Synthetic validation failure' }, 400);
        const extensionKeys = ['number', 'sourceNumber', 'name', 'enabled', 'ring', 'maxContacts', 'allowOutgoing', 'allowMessages', 'allowMobileSmsSend', 'allowMobileSmsReceive', 'passwordAction'];
        for (const row of input.extensions) {
          if (!exact(row, [...extensionKeys, ...(row.passwordAction === 'replace' ? ['password'] : [])])) return reply({ error: 'Synthetic invalid row shape' }, 400);
          if (row.sourceNumber ? !value.extensions.some(old => old.number === row.sourceNumber) : value.extensions.some(old => old.number === row.number)) return reply({ error: 'Synthetic invalid source identity' }, 400);
        }
        const checked = serializeV2Draft({ ...value, extensions: input.extensions.map(row => ({ ...row,
          password: row.passwordAction === 'replace' ? row.password : '', hasPassword: Boolean(row.sourceNumber && passwords.has(row.sourceNumber)),
        })) });
        const nextPasswords = new Map();
        for (const row of checked.extensions) nextPasswords.set(row.number, row.passwordAction === 'keep' ? passwords.get(row.number) : row.passwordAction === 'replace' ? row.password : 'Synthetic_generated-only_' + (++revision));
        const nextView = { ...value, revision: String.fromCharCode(66 + (++revision % 25)).repeat(43),
          extensions: checked.extensions.map(({ password, passwordAction, ...row }) => ({ ...row, sourceNumber: row.number, hasPassword: true })) };
        adoptV2View(nextView); value = nextView; passwords = nextPasswords; return reply(value);
      }
      if (url.pathname === '/api/v2/connection' && request.method === 'POST') {
        const input = await body();
        if (!exact(input, ['revision', 'number'])) return reply({ error: 'Synthetic invalid request' }, 400);
        if (input.revision !== value.revision) return reply({ error: 'Synthetic version conflict' }, 409);
        if (!passwords.has(input.number)) return reply({ error: 'Synthetic account not found' }, 404);
        return reply({ revision: value.revision, state: 'saved-not-applied', server: value.sipServer, port: value.sipPort,
          username: input.number, password: passwords.get(input.number), transport: 'UDP' });
      }
      const name = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
      if (request.method !== 'GET' || !staticFiles.has(name)) return reply({ error: 'Not found' }, 404);
      let bytes = await readFile(new URL(name, web));
      if (name === 'index.html') bytes = Buffer.from(bytes.toString().replace('<main>', '<main><p class="note">隔离 v2 UI 预览 · 合成数据，不连接 PBX / 模组 / SIP · 可用场景：?scenario=normal / empty / all-off / conflict / invalid / expire-on-status / login / status-down / load-failure / invalid-mode</p>'));
      response.writeHead(200, { 'Content-Type': staticFiles.get(name), 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
        'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'" });
      response.end(bytes);
    } catch { if (!response.headersSent) reply({ error: 'Synthetic request rejected' }, 400); else response.end(); }
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const portIndex = process.argv.indexOf('--port'), rawPort = portIndex < 0 ? '5189' : process.argv[portIndex + 1];
  const port = Number(rawPort);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid loopback preview port');
  createV2Preview().listen(port, '127.0.0.1', () => console.log('Synthetic v2 PBX UI preview: http://127.0.0.1:' + port));
}
