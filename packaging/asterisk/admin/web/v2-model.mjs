// Pure draft model. No HTTP, browser state, runtime action or secret generation.
const flags = ['enabled', 'ring', 'allowOutgoing', 'allowMessages', 'allowMobileSmsSend', 'allowMobileSmsReceive'];
const featureFlags = ['calls', 'carrier', 'messages', 'sms'];
const safeMessage = '草稿数据无效，请检查分机、权限及密码设置';
export class V2ModelError extends Error {
  constructor() { super(safeMessage); this.name = 'V2ModelError'; }
}
const fail = () => { throw new V2ModelError(); };
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const requireRecord = value => { if (!record(value)) fail(); return value; };
const boolean = value => { if (typeof value !== 'boolean') fail(); return value; };
const integer = (value, min, max) => { if (!Number.isInteger(value) || value < min || value > max) fail(); return value; };
function text(value, maximum, empty = true) {
  if (typeof value !== 'string' || (!empty && !value) || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) fail();
  // TextEncoder replaces invalid UTF-16; reject it instead of silently changing input.
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) fail();
    } else if (code >= 0xdc00 && code <= 0xdfff) fail();
  }
  if (new TextEncoder().encode(value).length > maximum) fail();
  return value;
}
function number(value) { if (typeof value !== 'string' || value.length < 2 || value.length > 8 || /[^0-9]/u.test(value)) fail(); return value; }
function revision(value) { if (typeof value !== 'string' || value.length !== 43 || /[^A-Za-z0-9_-]/u.test(value)) fail(); return value; }
function rowFields(value, saved) {
  requireRecord(value);
  const result = { number: number(value.number), sourceNumber: value.sourceNumber, name: text(value.name, 80) };
  if (/[;<>"\\$]/u.test(result.name)) fail();
  if (typeof result.sourceNumber !== 'string' || (result.sourceNumber !== '' && result.sourceNumber !== result.number)) fail();
  if (saved && result.sourceNumber !== result.number) fail();
  for (const key of flags) result[key] = boolean(value[key]);
  result.maxContacts = integer(value.maxContacts, 1, 8);
  result.hasPassword = boolean(value.hasPassword);
  return result;
}
function rows(value, mapper) {
  if (!Array.isArray(value) || value.length > 64) fail();
  const seen = new Set();
  return value.map(source => {
    const row = mapper(source);
    if (seen.has(row.number)) fail();
    seen.add(row.number);
    return row;
  });
}
export function adoptV2View(value) {
  requireRecord(value);
  if (value.schemaVersion !== 2 || value.state !== 'saved-not-applied' || value.canApply !== false) fail();
  const sourceFeatures = requireRecord(value.features), features = {};
  for (const key of featureFlags) features[key] = boolean(sourceFeatures[key]);
  return {
    schemaVersion: 2, revision: revision(value.revision), state: 'saved-not-applied', canApply: false,
    sipServer: text(value.sipServer, 256, false), sipPort: integer(value.sipPort, 1, 65535), features,
    extensions: rows(value.extensions, source => {
      const row = rowFields(source, true);
      return { ...row, passwordAction: row.hasPassword ? 'keep' : 'replace', password: '' };
    }),
  };
}
export function newV2Extension() {
  return {
    number: '', sourceNumber: '', name: '', enabled: false, ring: false, maxContacts: 4,
    allowOutgoing: false, allowMessages: false, allowMobileSmsSend: false, allowMobileSmsReceive: false,
    hasPassword: false, passwordAction: 'replace', password: '',
  };
}
export function setV2PasswordAction(row, action) {
  requireRecord(row);
  if (!['keep', 'replace', 'generate'].includes(action)) fail();
  if (action === 'keep' && (!row.hasPassword || typeof row.sourceNumber !== 'string' || !row.sourceNumber || row.sourceNumber !== row.number)) fail();
  return { ...row, passwordAction: action, password: '' };
}
export function serializeV2Draft(draft) {
  requireRecord(draft);
  return {
    revision: revision(draft.revision),
    extensions: rows(draft.extensions, source => {
      const row = rowFields(source, false), action = source.passwordAction;
      if (!['keep', 'replace', 'generate'].includes(action)) fail();
      if (action === 'keep' && (!row.hasPassword || !row.sourceNumber)) fail();
      const result = { number: row.number, sourceNumber: row.sourceNumber, name: row.name,
        enabled: row.enabled, ring: row.ring, maxContacts: row.maxContacts,
        allowOutgoing: row.allowOutgoing, allowMessages: row.allowMessages,
        allowMobileSmsSend: row.allowMobileSmsSend, allowMobileSmsReceive: row.allowMobileSmsReceive,
        passwordAction: action };
      if (action === 'replace') {
        if (typeof source.password !== 'string' || source.password.length < 12 || source.password.length > 128 || /[^A-Za-z0-9_-]/u.test(source.password)) fail();
        result.password = source.password;
      } else if (source.password !== '') fail();
      return result;
    }),
  };
}
