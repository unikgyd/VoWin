// NEW V2 UI model contract. Synthetic values only; no DOM, server, process or PBX.
// A missing implementation is an explicit assertion failure, not an import/setup crash.
import assert from 'node:assert/strict';
import test from 'node:test';

let loaded = null;
try { loaded = await import('../web/v2-model.mjs'); } catch { /* Proven by each test's API assertion. */ }
const exportsRequired = ['V2ModelError', 'adoptV2View', 'newV2Extension', 'setV2PasswordAction', 'serializeV2Draft'];
function api() {
  assert.ok(loaded, 'The new V2 UI model module must exist and import successfully');
  for (const name of exportsRequired) assert.equal(typeof loaded[name], 'function', 'Missing V2 UI model API: ' + name);
  return loaded;
}
const revision = 'A'.repeat(43), sentinel = 'synthetic-private-never-reflect';
const grants = ['allowOutgoing', 'allowMessages', 'allowMobileSmsSend', 'allowMobileSmsReceive'];
const viewRowKeys = ['number', 'sourceNumber', 'name', 'enabled', 'ring', 'maxContacts', ...grants, 'hasPassword'].sort();
const writeRowKeys = ['number', 'sourceNumber', 'name', 'enabled', 'ring', 'maxContacts', ...grants, 'passwordAction'].sort();
function view() {
  return {
    schemaVersion: 2, revision, state: 'saved-not-applied', canApply: false,
    sipServer: 'pbx.example.invalid', sipPort: 5090,
    features: { calls: false, carrier: true, messages: false, sms: true },
    extensions: [
      { number: '2101', sourceNumber: '2101', name: 'Synthetic first', enabled: true, ring: false,
        maxContacts: 2, allowOutgoing: true, allowMessages: false,
        allowMobileSmsSend: true, allowMobileSmsReceive: false, hasPassword: true },
      { number: '2102', sourceNumber: '2102', name: 'Synthetic second', enabled: false, ring: true,
        maxContacts: 3, allowOutgoing: false, allowMessages: true,
        allowMobileSmsSend: false, allowMobileSmsReceive: true, hasPassword: true },
    ],
  };
}
function newNumbered(model, number = '2103') {
  return { ...model.newV2Extension(), number };
}
function invalid(model, callback) {
  let caught;
  assert.throws(callback, error => {
    caught = error;
    return error instanceof model.V2ModelError;
  }, 'Invalid UI drafts must fail using the safe typed model error');
  assert.equal(typeof caught.message, 'string');
  assert.ok(caught.message.length > 0);
  assert.equal(caught.message.includes(sentinel), false);
  return caught.message;
}

test('v2 model exposes all five pure contract APIs and a real Error type', () => {
  const model = api();
  assert.ok(model.V2ModelError.prototype instanceof Error);
});

test('adoption preserves the complete saved-not-applied view without deriving active status', () => {
  const model = api(), source = view(), before = structuredClone(source);
  const draft = model.adoptV2View(source);
  assert.deepEqual(source, before);
  for (const key of ['schemaVersion', 'revision', 'state', 'canApply', 'sipServer', 'sipPort', 'features'])
    assert.deepEqual(draft[key], source[key]);
  for (let index = 0; index < source.extensions.length; index++) {
    for (const key of viewRowKeys) assert.deepEqual(draft.extensions[index][key], source.extensions[index][key]);
    assert.equal(draft.extensions[index].passwordAction, 'keep');
    assert.equal(draft.extensions[index].password, '');
  }
  assert.equal(Object.hasOwn(draft, 'running'), false);
  assert.equal(Object.hasOwn(draft, 'applied'), false);
});

test('adoption never imports plaintext, secret references, hashes or password actions from a GET response', () => {
  const model = api(), source = view();
  source.password = sentinel;
  source.secretRefs = { private: sentinel };
  source.extensions[0].password = sentinel;
  source.extensions[0].secretRef = sentinel;
  source.extensions[0].passwordHash = sentinel;
  source.extensions[0].passwordAction = 'generate';
  const draft = model.adoptV2View(source);
  assert.equal(JSON.stringify(draft).includes(sentinel), false);
  assert.equal(draft.extensions[0].passwordAction, 'keep');
  assert.equal(draft.extensions[0].password, '');
  assert.equal(Object.hasOwn(draft.extensions[0], 'secretRef'), false);
  assert.equal(Object.hasOwn(draft.extensions[0], 'passwordHash'), false);
});

test('adoption and serialization allocate independent features, row arrays and payload objects', () => {
  const model = api(), source = view(), draft = model.adoptV2View(source);
  draft.features.calls = true;
  draft.extensions[0].name = 'Caller owned draft';
  assert.equal(source.features.calls, false);
  assert.equal(source.extensions[0].name, 'Synthetic first');
  const payload = model.serializeV2Draft(draft);
  payload.extensions[0].name = 'Caller owned payload';
  payload.extensions.pop();
  assert.equal(draft.extensions[0].name, 'Caller owned draft');
  assert.equal(draft.extensions.length, 2);
});

test('new rows are fully disabled and require an explicit password choice rather than automatic generation', () => {
  const model = api(), first = model.newV2Extension(), second = model.newV2Extension();
  assert.deepEqual(first, {
    number: '', sourceNumber: '', name: '', enabled: false, ring: false, maxContacts: 4,
    allowOutgoing: false, allowMessages: false, allowMobileSmsSend: false, allowMobileSmsReceive: false,
    hasPassword: false, passwordAction: 'replace', password: '',
  });
  first.name = 'Unshared new row';
  assert.equal(second.name, '');
  const draft = model.adoptV2View(view());
  draft.extensions.push({ ...second, number: '2103' });
  invalid(model, () => model.serializeV2Draft(draft));
});

test('all four permissions serialize independently even when the corresponding global feature is off', () => {
  const model = api();
  for (const selected of grants) {
    const draft = model.adoptV2View(view());
    draft.features = { calls: false, carrier: false, messages: false, sms: false };
    for (const key of grants) draft.extensions[0][key] = key === selected;
    const payload = model.serializeV2Draft(draft);
    for (const key of grants) assert.equal(payload.extensions[0][key], key === selected, selected + ' must not change ' + key);
    assert.equal(payload.extensions[0].ring, false);
    assert.equal(payload.extensions[0].enabled, true);
  }
});

test('saved and new empty extension lists remain legal without inventing an account or runtime identity', () => {
  const model = api(), source = view();
  source.extensions = [];
  const draft = model.adoptV2View(source);
  assert.deepEqual(draft.extensions, []);
  assert.deepEqual(model.serializeV2Draft(draft), { revision, extensions: [] });
});

test('serialization sends only the exact revision and declared writable extension keys', () => {
  const model = api(), draft = model.adoptV2View(view());
  draft.uiState = 'not part of HTTP';
  draft.extensions[0].clientId = 'not part of HTTP';
  draft.extensions[0].secretRef = sentinel;
  const payload = model.serializeV2Draft(draft);
  assert.deepEqual(Object.keys(payload).sort(), ['extensions', 'revision']);
  assert.equal(payload.revision, revision);
  for (const row of payload.extensions) {
    assert.deepEqual(Object.keys(row).sort(), writeRowKeys);
    assert.equal(Object.hasOwn(row, 'password'), false);
    assert.equal(Object.hasOwn(row, 'hasPassword'), false);
  }
  assert.equal(JSON.stringify(payload).includes(sentinel), false);
});

test('keep requires the same saved account and an existing password marker', () => {
  const model = api();
  for (const mutate of [
    row => { row.hasPassword = false; },
    row => { row.sourceNumber = ''; },
    row => { row.number = '2199'; },
  ]) {
    const draft = model.adoptV2View(view());
    mutate(draft.extensions[0]);
    invalid(model, () => model.serializeV2Draft(draft));
  }
  const draft = model.adoptV2View(view());
  assert.equal(model.serializeV2Draft(draft).extensions[0].passwordAction, 'keep');
});

test('an existing readonly source number cannot be renamed through any password action', () => {
  const model = api();
  for (const action of ['keep', 'replace', 'generate']) {
    const draft = model.adoptV2View(view());
    draft.extensions[0].number = '2199';
    draft.extensions[0].passwordAction = action;
    draft.extensions[0].password = action === 'replace' ? 'synthetic-replacement-password' : '';
    invalid(model, () => model.serializeV2Draft(draft));
  }
});

test('generate is an explicit action and never generates or sends a browser-side password', () => {
  const model = api(), source = newNumbered(model), before = structuredClone(source);
  const generated = model.setV2PasswordAction(source, 'generate');
  assert.deepEqual(source, before);
  assert.equal(generated.passwordAction, 'generate');
  assert.equal(generated.password, '');
  assert.equal(generated.hasPassword, false);
  const draft = model.adoptV2View(view());
  draft.extensions = [generated];
  assert.equal(model.serializeV2Draft(draft).extensions[0].passwordAction, 'generate');
  assert.equal(Object.hasOwn(model.serializeV2Draft(draft).extensions[0], 'password'), false);
});

test('password mode transitions return a new row and clear old plaintext without modifying permissions', () => {
  const model = api(), row = model.adoptV2View(view()).extensions[0];
  row.passwordAction = 'replace';
  row.password = sentinel;
  const before = structuredClone(row);
  for (const action of ['keep', 'replace', 'generate']) {
    const next = model.setV2PasswordAction(row, action);
    assert.notEqual(next, row);
    assert.equal(next.passwordAction, action);
    assert.equal(next.password, '');
    for (const key of viewRowKeys) assert.deepEqual(next[key], row[key]);
    assert.deepEqual(row, before);
  }
});

test('replacement accepts exact safe ASCII boundaries and serializes password only for replace', () => {
  const model = api();
  for (const password of ['a'.repeat(12), 'a'.repeat(128), 'Synthetic_safe-Password_123']) {
    const draft = model.adoptV2View(view());
    draft.extensions = [{ ...newNumbered(model), passwordAction: 'replace', password }];
    const payload = model.serializeV2Draft(draft);
    assert.deepEqual(Object.keys(payload.extensions[0]).sort(), [...writeRowKeys, 'password'].sort());
    assert.equal(payload.extensions[0].password, password);
    assert.equal(payload.extensions[0].sourceNumber, '');
    assert.equal(draft.extensions[0].password, password, 'serialization must not mutate the pending local secret');
  }
});

test('empty, unsafe or mistyped replacement secrets and stale plaintext on other actions are rejected safely', () => {
  const model = api();
  for (const password of ['', 'a'.repeat(11), 'a'.repeat(129), sentinel + '\n', sentinel + ';', sentinel + ' ', '密码密码密码密码密码密码', null, true, 12]) {
    const draft = model.adoptV2View(view());
    draft.extensions[0].passwordAction = 'replace';
    draft.extensions[0].password = password;
    invalid(model, () => model.serializeV2Draft(draft));
  }
  for (const action of ['keep', 'generate']) {
    const draft = model.adoptV2View(view());
    draft.extensions[0].passwordAction = action;
    draft.extensions[0].password = sentinel;
    invalid(model, () => model.serializeV2Draft(draft));
  }
});

test('missing saved passwords select replace rather than manufacturing a keep or generation choice', () => {
  const model = api(), source = view();
  source.extensions[0].hasPassword = false;
  const draft = model.adoptV2View(source);
  assert.equal(draft.extensions[0].passwordAction, 'replace');
  assert.equal(draft.extensions[0].password, '');
  invalid(model, () => model.serializeV2Draft(draft));
});

test('adopting a successful redacted save clears pending secrets and resets actions without claiming application', () => {
  const model = api(), source = view(), draft = model.adoptV2View(source);
  draft.extensions[0].passwordAction = 'replace';
  draft.extensions[0].password = sentinel;
  const response = structuredClone(source);
  response.revision = 'B'.repeat(43);
  response.extensions[0].hasPassword = true;
  const saved = model.adoptV2View(response);
  assert.equal(saved.revision, response.revision);
  assert.equal(saved.state, 'saved-not-applied');
  assert.equal(saved.canApply, false);
  assert.equal(saved.extensions[0].passwordAction, 'keep');
  assert.equal(JSON.stringify(saved).includes(sentinel), false);
});

test('revision, schema, state and apply capability are strict and never downgraded into a legacy view', () => {
  const model = api();
  for (const value of ['', 'A'.repeat(42), 'A'.repeat(44), '+'.repeat(43), null, 43]) {
    const source = view(); source.revision = value;
    invalid(model, () => model.adoptV2View(source));
    const draft = model.adoptV2View(view()); draft.revision = value;
    invalid(model, () => model.serializeV2Draft(draft));
  }
  for (const [key, value] of [['schemaVersion', 1], ['schemaVersion', '2'], ['state', 'applied'], ['canApply', true], ['canApply', null]]) {
    const source = view(); source[key] = value;
    invalid(model, () => model.adoptV2View(source));
  }
});

test('every account grant and feature flag is a strict boolean rather than truthiness or legacy defaults', () => {
  const model = api();
  for (const key of ['enabled', 'ring', ...grants, 'hasPassword']) {
    for (const value of [undefined, null, 0, 1, 'false']) {
      const source = view(); source.extensions[0][key] = value;
      invalid(model, () => model.adoptV2View(source));
    }
  }
  for (const key of ['enabled', 'ring', ...grants]) {
    const draft = model.adoptV2View(view()); draft.extensions[0][key] = 'false';
    invalid(model, () => model.serializeV2Draft(draft));
  }
  for (const key of ['calls', 'carrier', 'messages', 'sms']) {
    const source = view(); source.features[key] = 'false';
    invalid(model, () => model.adoptV2View(source));
  }
});

test('account count, numeric identity, source membership, display text and contact limits have bounded validation', () => {
  const model = api();
  for (const number of ['', '1', '123456789', '21x1', '2101\n', '+2101', '２１０１']) {
    const draft = model.adoptV2View(view());
    draft.extensions = [{ ...newNumbered(model, number), passwordAction: 'generate' }];
    invalid(model, () => model.serializeV2Draft(draft));
  }
  for (const limit of [0, 9, 1.5, true, '4', null]) {
    const draft = model.adoptV2View(view()); draft.extensions[0].maxContacts = limit;
    invalid(model, () => model.serializeV2Draft(draft));
  }
  for (const name of ['a'.repeat(81), sentinel + '\n', sentinel + '\0', null]) {
    const draft = model.adoptV2View(view()); draft.extensions[0].name = name;
    invalid(model, () => model.serializeV2Draft(draft));
  }
  const duplicate = model.adoptV2View(view());
  duplicate.extensions.push({ ...duplicate.extensions[0] });
  invalid(model, () => model.serializeV2Draft(duplicate));
  const tooMany = model.adoptV2View(view());
  tooMany.extensions = Array.from({ length: 65 }, (_, index) => ({ ...newNumbered(model, String(3000 + index)), passwordAction: 'generate' }));
  invalid(model, () => model.serializeV2Draft(tooMany));
  for (const maxContacts of [1, 8]) {
    const valid = model.adoptV2View(view()); valid.extensions[0].maxContacts = maxContacts;
    assert.equal(model.serializeV2Draft(valid).extensions[0].maxContacts, maxContacts);
  }
});

test('invalid password actions and malformed containers fail with one fixed safe error message', () => {
  const model = api(), messages = [];
  for (const action of ['', 'KEEP', 'automatic', sentinel, null]) {
    const draft = model.adoptV2View(view()); draft.extensions[0].passwordAction = action;
    messages.push(invalid(model, () => model.serializeV2Draft(draft)));
    messages.push(invalid(model, () => model.setV2PasswordAction(draft.extensions[0], action)));
  }
  for (const value of [null, [], true, sentinel]) {
    messages.push(invalid(model, () => model.adoptV2View(value)));
    messages.push(invalid(model, () => model.serializeV2Draft(value)));
  }
  assert.equal(new Set(messages).size, 1, 'No untrusted value or exception detail may become a diagnostic');
});
