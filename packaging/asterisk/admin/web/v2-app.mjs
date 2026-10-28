import { V2ModelError, adoptV2View, newV2Extension, setV2PasswordAction, serializeV2Draft } from './v2-model.mjs';
import { createV2Application } from './v2-application.mjs';

// Saving remains draft-only. Explicit PBX jobs use a separate controller/API.
export async function startV2App() {
  const $ = id => document.getElementById(id);
  const show = (id, visible) => $(id).classList.toggle('hidden', !visible);
  let csrf = '', draft = null, saved = '', contacts = [], running = false, stale = true;
  let busy = false, serial = 0, epoch = 0, revealSerial = 0, connectionText = '', deleting = null;
  let application = null;
  const locked = () => busy || application?.isBusy() === true;
  const signature = () => JSON.stringify(draft?.extensions ?? []);
  const changed = () => Boolean(draft) && signature() !== saved;
  const node = (tag, className = '', text = '') => {
    const value = document.createElement(tag); value.className = className; value.textContent = text; return value;
  };
  function banner(message, error = false) {
    $('banner').textContent = message; $('banner').classList.toggle('error', error);
  }
  function closeSecrets() {
    ++revealSerial;
    if ($('connection').open) $('connection').close();
    $('connection-data').textContent = ''; $('copy-status').textContent = ''; connectionText = '';
  }
  function expire() {
    application?.expire();
    ++epoch; csrf = ''; busy = false; closeSecrets();
    for (const row of draft?.extensions ?? []) row.password = '';
    draft = null; saved = ''; contacts = []; stale = true;
    $('extensions').replaceChildren();
    for (const id of ['delete-dialog', 'reload-dialog']) if ($(id).open) $(id).close();
    $('key').value = ''; show('workspace', false); show('logout', false); show('login', true);
  }
  const errors = {
    400: '草稿未保存，请检查分机、权限和密码设置',
    401: '登录已过期，请重新登录；填写的密码已清除',
    403: '请求未获授权，请重新登录后重试',
    409: '配置已被其他操作修改，本地草稿已保留。请确认后重新加载',
    413: '草稿过大，请减少分机数量后重试',
    503: '草稿操作结果待确认；请先重新加载核对，勿直接重复保存',
  };
  async function api(path, method = 'GET', body) {
    let response;
    try {
      response = await fetch('/api/' + path, {
        method, credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', ...(csrf ? { 'X-CSRF-Token': csrf } : {}) },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
    } catch { throw new Error(method === 'PUT'
      ? '保存结果待确认；请先重新加载核对，勿直接重复保存'
      : '无法连接管理服务，请稍后重新读取'); }
    if (!response.ok) {
      if (response.status === 401) { const hadSession = Boolean(csrf); expire(); if (hadSession) $('login-error').textContent = errors[401]; }
      const error = new Error(errors[response.status] || (method === 'PUT'
        ? '保存结果待确认；请先重新加载核对，勿直接重复保存'
        : '请求失败，请稍后重新读取'));
      error.status = response.status; throw error;
    }
    try { return await response.json(); } catch { throw new Error(method === 'PUT'
      ? '保存响应无效，结果待确认；请先重新加载核对，勿直接重复保存'
      : '服务器响应无效，请检查服务版本'); }
  }
  function updateSave() {
    $('dirty').textContent = busy ? '正在保存草稿…' : changed() ? '有未保存的草稿修改' : '草稿已保存 · 应用状态见上方';
    $('save').disabled = locked() || !changed(); $('add').disabled = locked(); $('reload').disabled = locked();
    $('refresh').disabled = busy; $('logout').disabled = busy;
  }
  function actualState(row) {
    if (stale) return ['loading', '实际状态待刷新'];
    const current = contacts.filter(contact => contact.number === row.number);
    if (current.some(contact => contact.state === 'Avail')) return ['online', '终端在线'];
    if (current.length) return ['loading', '终端待确认'];
    return ['', '无在线终端'];
  }
  function paintStatus() {
    const online = new Set(contacts.filter(contact => contact.state === 'Avail').map(contact => contact.number));
    $('online').textContent = stale ? '—' : String(online.size);
    const [state, label] = stale ? ['loading', '状态暂不可用'] : running ? ['online', '运行正常'] : ['error', '连接失败'];
    $('pbx-state').replaceChildren(node('i', 'dot ' + state), document.createTextNode(label));
    for (const [index, card] of [...$('extensions').children].entries()) {
      if (!draft?.extensions[index]) continue;
      const [color, status] = actualState(draft.extensions[index]);
      card.querySelector('[data-actual-dot]').className = 'dot ' + color;
      card.querySelector('[data-actual-label]').textContent = status;
    }
  }
  async function refreshStatus() {
    const currentEpoch = epoch;
    try {
      const value = await api('status');
      if (currentEpoch !== epoch || !csrf) return;
      if (typeof value.running !== 'boolean' || !Array.isArray(value.contacts)) throw new Error();
      contacts = value.contacts.filter(contact => contact && typeof contact.number === 'string' && typeof contact.state === 'string')
        .map(contact => ({ number: contact.number, state: contact.state }));
      running = value.running; stale = false;
    } catch { if (currentEpoch === epoch) stale = true; }
    if (currentEpoch === epoch) paintStatus();
  }
  function action(label, callback, className = '') {
    const button = node('button', className, label); button.type = 'button'; button.disabled = locked();
    button.addEventListener('click', callback); return button;
  }
  function field(label, value, type, onchange, attrs = {}) {
    const box = node('div', 'field'), id = 'v2-field-' + (++serial), lab = node('label', '', label);
    lab.htmlFor = id;
    const input = document.createElement('input'); input.id = id; input.type = type; input.value = value;
    input.disabled = locked(); Object.assign(input, attrs);
    input.addEventListener('input', () => onchange(input.value)); box.append(lab, input); return { box, input };
  }
  function check(label, value, onchange, hint = '') {
    const box = node('div', 'policy-item'), lab = node('label', 'check');
    const input = document.createElement('input'); input.type = 'checkbox'; input.checked = value; input.disabled = locked();
    input.addEventListener('change', () => onchange(input.checked));
    lab.append(input, node('span', '', label)); box.append(lab);
    if (hint) box.append(node('p', 'hint', hint));
    return box;
  }
  function patch(row, key, value) { row[key] = value; updateSave(); application?.render(); }
  function features() {
    const labels = { calls: '电话', carrier: '运营商线路', messages: '分机消息', sms: '手机短信' };
    $('v2-features').replaceChildren(...Object.entries(labels).map(([key, label]) =>
      node('span', 'feature-tag ' + (draft.features[key] ? 'feature-on' : ''), label + (draft.features[key] ? '：全局允许' : '：全局关闭'))));
  }
  function render() {
    if (!draft) return;
    const list = $('extensions'); list.replaceChildren(); show('empty', draft.extensions.length === 0);
    features();
    for (const row of draft.extensions) {
      const card = node('article', 'extension'), top = node('div', 'extension-top'), title = node('div', 'account-title');
      const [status, statusLabel] = actualState(row), dot = node('i', 'dot ' + status), badge = node('span', 'badge', statusLabel);
      dot.dataset.actualDot = ''; badge.dataset.actualLabel = '';
      const numberLabel = node('span', '', row.number || '新分机'); title.append(dot, numberLabel, badge);
      const actions = node('div', 'actions');
      if (row.sourceNumber && row.hasPassword) actions.append(action('草稿连接信息', () => connection(row), 'quiet'));
      actions.append(action('移除', () => {
        deleting = row; $('delete-text').textContent = '将分机 ' + (row.number || '（未填写）') + ' 从草稿中移除？';
        $('delete-dialog').showModal();
      }, 'quiet')); top.append(title, actions); card.append(top);
      const grid = node('div', 'form-grid');
      const numberInput = field('分机号' + (row.sourceNumber ? '（已保存，不可改号）' : '（2–8 位数字）'), row.number, 'text', value => {
        patch(row, 'number', value); numberLabel.textContent = value || '新分机';
      }, { inputMode: 'numeric', maxLength: 8, autocomplete: 'off', readOnly: Boolean(row.sourceNumber) });
      const display = field('显示名称（可选，最多 80 字节）', row.name, 'text', value => patch(row, 'name', value), { maxLength: 80 });
      const passwordBox = node('div', 'field'), modeId = 'v2-field-' + (++serial), modeLabel = node('label', '', '密码处理');
      modeLabel.htmlFor = modeId;
      const select = document.createElement('select'); select.id = modeId; select.disabled = locked();
      for (const [value, label] of [['keep', '保留已保存密码'], ['replace', '填写新密码'], ['generate', '保存时由服务器生成']]) {
        if (value === 'keep' && (!row.sourceNumber || !row.hasPassword)) continue;
        const option = node('option', '', label); option.value = value; select.append(option);
      }
      select.value = row.passwordAction;
      select.addEventListener('change', () => {
        try {
          const next = setV2PasswordAction(row, select.value); row.passwordAction = next.passwordAction; row.password = '';
          render(); cardFocus(row, 'select');
        } catch { banner('密码处理方式无效，请重新选择', true); }
      });
      passwordBox.append(modeLabel, select);
      if (row.passwordAction === 'replace') {
        const pass = field('新密码（12–128 位字母、数字、_ 或 -）', row.password, 'password', value => patch(row, 'password', value),
          { minLength: 12, maxLength: 128, autocomplete: 'new-password', spellcheck: false });
        passwordBox.append(pass.box);
      } else passwordBox.append(node('p', 'hint', row.passwordAction === 'generate' ? '点击保存才会生成；不会在浏览器生成或显示。' : '不读取、不回传现有密码。'));
      grid.append(numberInput.box, display.box, passwordBox); card.append(grid);
      const options = node('div', 'options');
      options.append(check('草稿启用账号', row.enabled, value => patch(row, 'enabled', value)),
        check('草稿接收外线群振', row.ring, value => patch(row, 'ring', value)));
      card.append(options);
      const policies = node('div', 'policy-grid');
      const callOpen = draft.features.calls && draft.features.carrier;
      policies.append(check('允许运营商外呼', row.allowOutgoing, value => patch(row, 'allowOutgoing', value), callOpen ? '可能产生运营商通话费用' : '电话或运营商线路全局关闭，权限暂不生效'),
        check('允许分机消息', row.allowMessages, value => patch(row, 'allowMessages', value), draft.features.messages ? 'SIP 分机之间的内部文本消息' : '分机消息全局关闭，权限暂不生效'),
        check('允许发送手机短信', row.allowMobileSmsSend, value => patch(row, 'allowMobileSmsSend', value), draft.features.sms ? '可能产生运营商短信费用' : '手机短信全局关闭，权限暂不生效'),
        check('允许接收手机短信', row.allowMobileSmsReceive, value => patch(row, 'allowMobileSmsReceive', value), draft.features.sms ? '接收经模组转发的入站短信' : '手机短信全局关闭，权限暂不生效'));
      card.append(policies);
      const max = field('最多注册终端', row.maxContacts, 'number', value => patch(row, 'maxContacts', Number(value)), { min: 1, max: 8, step: 1 });
      max.box.className = 'max-contacts'; card.append(max.box); list.append(card);
    }
    updateSave(); paintStatus(); application?.render();
  }
  function cardFocus(row, selector) {
    const index = draft?.extensions.indexOf(row);
    if (index >= 0) $('extensions').children[index]?.querySelector(selector)?.focus();
  }
  function adopt(value) {
    const next = adoptV2View(value);
    closeSecrets();
    for (const row of draft?.extensions ?? []) row.password = '';
    draft = next; saved = signature();
    $('sip-address').textContent = (next.sipServer.includes(':') && !next.sipServer.startsWith('[') ? '[' + next.sipServer + ']' : next.sipServer) + ':' + next.sipPort;
    render();
  }
  async function load() {
    const currentEpoch = epoch, value = await api('v2/extensions');
    if (currentEpoch !== epoch || !csrf) return;
    adopt(value); show('login', false); show('workspace', true); show('logout', true); await refreshStatus();
    if (currentEpoch === epoch && csrf) await application?.refresh();
  }
  async function connection(row) {
    if (busy || !draft) return;
    closeSecrets(); const currentEpoch = epoch, requestedRevision = draft.revision, requestedReveal = revealSerial;
    try {
      const data = await api('v2/connection', 'POST', { revision: requestedRevision, number: row.sourceNumber });
      if (currentEpoch !== epoch || requestedReveal !== revealSerial || !draft || draft.revision !== requestedRevision) return;
      if (data.revision !== requestedRevision || data.state !== 'saved-not-applied' || data.transport !== 'UDP' ||
          typeof data.server !== 'string' || !Number.isInteger(data.port) || data.port < 1 || data.port > 65535 ||
          data.username !== row.sourceNumber || typeof data.password !== 'string' || data.password.length < 12 || data.password.length > 128 || /[^A-Za-z0-9_-]/u.test(data.password))
        throw new Error('草稿连接信息无效，请重新加载后重试');
      connectionText = '草稿连接信息 · 是否生效请核对应用记录与终端状态\n服务器：' + data.server + '\n端口：' + data.port +
        '\n传输：UDP\n账号：' + data.username + '\n密码：' + data.password;
      $('connection-data').textContent = connectionText; $('connection').showModal();
    } catch (error) { if (currentEpoch === epoch) banner(error.message, true); }
  }

  // Shared page chrome, with no v1 event handlers installed in this mode.
  document.documentElement.dataset.configuration = 'v2';
  $('workspace-summary').textContent = '独立管理电话、分机消息和手机短信权限。保存只更新草稿，应用需单独确认。';
  $('extensions-summary').textContent = '在线状态来自 PBX 实际终端；草稿开关不代表当前已启用。现有分机暂不支持改号。';
  $('empty').textContent = '草稿中没有分机。可以保留为空，也可以添加一个默认关闭的新分机。';
  $('usage-note').textContent = '保存只更新私有草稿，不应用、不热加载、不主动拨号或发送短信。外呼与手机短信可能产生费用；新增账号的全部权限默认关闭。';
  $('save-hint').textContent = '保存不等于应用；先预检，再单独确认应用。草稿连接信息不表示终端已注册。';
  $('delete-hint').textContent = '仅从草稿移除。保存后，还需显式应用才会影响当前 PBX。';
  $('connection-hint').textContent = '以下是上次保存草稿的私密信息；是否生效请核对应用记录。本地未保存的密码不在其中，只交给可信终端。';
  $('connection-title').textContent = '草稿连接信息';
  $('v2-draft-notice').querySelector('strong').textContent = '草稿编辑';
  $('v2-draft-notice').querySelector('.draft-heading span').textContent = '保存与应用分开处理';
  $('save').textContent = '保存草稿'; show('v2-draft-notice', true); show('reload', true);
  application = createV2Application({ api, getDraft: () => draft, isDirty: changed,
    onChange: () => {
      updateSave();
      for (const input of $('extensions').querySelectorAll('input,select,button')) input.disabled = locked();
    },
  });
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault(); $('login-button').disabled = true; $('login-error').textContent = '';
    const currentEpoch = ++epoch;
    try {
      const result = await api('login', 'POST', { key: $('key').value }); $('key').value = '';
      if (currentEpoch !== epoch) return;
      if (typeof result.csrf !== 'string' || !result.csrf) throw new Error('登录响应无效，请稍后重试');
      csrf = result.csrf; await load();
    } catch (error) { $('key').value = ''; $('login-error').textContent = error.message; }
    finally { $('login-button').disabled = false; }
  });
  $('add').addEventListener('click', () => {
    if (!draft || locked()) return;
    if (draft.extensions.length >= 64) { banner('最多保留 64 个分机草稿', true); return; }
    const row = newV2Extension(); draft.extensions.push(row); render(); cardFocus(row, 'input');
  });
  $('refresh').addEventListener('click', async () => {
    await refreshStatus(); if (csrf) await application?.refresh();
    if (csrf) banner(stale ? '实际状态刷新失败，本地草稿保持不变' : '实际状态已刷新，草稿保持不变', stale);
  });
  $('reload').addEventListener('click', () => {
    if (changed()) $('reload-dialog').showModal(); else void reload();
  });
  async function reload() {
    busy = true; render(); banner('');
    try { await load(); if (csrf) banner('已重新读取已保存草稿，应用状态见上方'); }
    catch (error) { if (csrf) banner(error.message, true); }
    finally { busy = false; render(); }
  }
  $('cancel-reload').addEventListener('click', () => $('reload-dialog').close());
  $('confirm-reload').addEventListener('click', () => { $('reload-dialog').close(); void reload(); });
  $('save').addEventListener('click', async () => {
    if (locked()) return;
    let payload;
    try { payload = serializeV2Draft(draft); }
    catch (error) { banner(error instanceof V2ModelError ? error.message : '草稿无效，请检查填写内容', true); return; }
    busy = true; render(); banner(''); const currentEpoch = epoch;
    try {
      const response = await api('v2/extensions', 'PUT', payload);
      if (currentEpoch !== epoch || !csrf) return;
      adopt(response); banner('草稿已保存，尚未应用；当前 PBX 不受影响');
    } catch (error) { if (currentEpoch === epoch && csrf) banner(error.message, true); }
    finally {
      for (const row of payload.extensions) if (Object.hasOwn(row, 'password')) row.password = '';
      busy = false; render();
    }
  });
  $('logout').addEventListener('click', async () => {
    const pending = api('logout', 'POST', {}); expire();
    try { await pending; } catch { $('login-error').textContent = '本地密码已清除；若退出请求失败，请关闭此页面'; }
  });
  $('close-connection').addEventListener('click', closeSecrets);
  $('connection').addEventListener('close', closeSecrets);
  $('copy-connection').addEventListener('click', async () => {
    if (!connectionText) return;
    try { await navigator.clipboard.writeText(connectionText); if (connectionText) $('copy-status').textContent = '已复制私密草稿；请勿分享给不可信的人'; }
    catch { if (connectionText) $('copy-status').textContent = '浏览器不允许复制，可选中文本手动复制'; }
  });
  $('cancel-delete').addEventListener('click', () => { $('delete-dialog').close(); deleting = null; });
  $('confirm-delete').addEventListener('click', () => {
    if (deleting) deleting.password = '';
    if (draft) draft.extensions = draft.extensions.filter(row => row !== deleting);
    deleting = null; $('delete-dialog').close(); render();
  });
  window.addEventListener('beforeunload', event => { if (changed()) { event.preventDefault(); event.returnValue = ''; } });
  setInterval(() => { if (csrf && !busy) void refreshStatus(); }, 15000);
  try {
    const currentEpoch = epoch, session = await api('session');
    if (currentEpoch === epoch && typeof session.csrf === 'string' && session.csrf) { csrf = session.csrf; await load(); }
  } catch (error) { if (error.status !== 401) $('login-error').textContent = error.message; }
}
