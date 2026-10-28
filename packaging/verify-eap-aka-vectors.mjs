// Independent synthetic EAP-AKA/AKA' oracle. No project code, hardware,
// network or file writes; --emit prints fixtures. FIPS G != padded SHA-1.
import { createHash, createHmac, timingSafeEqual } from 'node:crypto';
import { readFile } from 'node:fs/promises';
const hex = value => Buffer.from(value, 'hex'), hash = (name, input) => createHash(name).update(input).digest();
const hmac = (name, key, input) => createHmac(name, key).update(input).digest();
function g(seed) {
  const w = new Uint32Array(80); for (let i = 0; i < 5; i++) w[i] = seed.readUInt32BE(i * 4);
  const rol = (n, k) => (n << k | n >>> (32 - k)) >>> 0;
  for (let i = 16; i < 80; i++) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
  const initial = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0]; let [a, b, c, d, e] = initial;
  for (let i = 0; i < 80; i++) {
    const f = i < 20 ? b & c | ~b & d : i < 40 ? b ^ c ^ d : i < 60 ? b & c | b & d | c & d : b ^ c ^ d;
    const k = i < 20 ? 0x5a827999 : i < 40 ? 0x6ed9eba1 : i < 60 ? 0x8f1bbcdc : 0xca62c1d6;
    [a, b, c, d, e] = [(rol(a, 5) + f + e + k + w[i]) >>> 0, a, rol(b, 30), c, d];
  }
  const out = Buffer.alloc(20); [a, b, c, d, e].forEach((n, i) => out.writeUInt32BE((n + initial[i]) >>> 0, i * 4)); w.fill(0); return out;
}
function fips(seed, length) {
  let key = BigInt(`0x${seed.toString('hex')}`), result = Buffer.alloc(0);
  while (result.length < length) { const word = g(hex(key.toString(16).padStart(40, '0'))); result = Buffer.concat([result, word]); key = (key + BigInt(`0x${word.toString('hex')}`) + 1n) & ((1n << 160n) - 1n); }
  return result.subarray(0, length);
}
function derive(v) {
  const identity = Buffer.from(v.identity), ik = hex(v.ik), ck = hex(v.ck); let stream, at;
  if (v.method === 23) { stream = fips(hash('sha1', Buffer.concat([identity, ik, ck])), 160); at = 32; }
  else {
    const name = Buffer.from(v.network), length = Buffer.alloc(2); length.writeUInt16BE(name.length);
    const ckik = hmac('sha256', Buffer.concat([ck, ik]), Buffer.concat([Buffer.from([0x20]), name, length, hex(v.autn).subarray(0, 6), Buffer.from([0, 6])]));
    const key = Buffer.concat([ckik.subarray(16), ckik.subarray(0, 16)]), seed = Buffer.concat([Buffer.from("EAP-AKA'"), identity]);
    let previous = Buffer.alloc(0); stream = Buffer.alloc(0);
    for (let i = 1; stream.length < 208; i++) { previous = hmac('sha256', key, Buffer.concat([previous, seed, Buffer.from([i])])); stream = Buffer.concat([stream, previous]); }
    at = 80; key.fill(0); ckik.fill(0); previous.fill(0);
  }
  const result = { k_encr: stream.subarray(0, 16).toString('hex'), k_aut: stream.subarray(16, v.method === 23 ? 32 : 48).toString('hex'), msk: stream.subarray(at, at + 64).toString('hex'), emsk: stream.subarray(at + 64, at + 128).toString('hex') };
  stream.fill(0); return result;
}
function attribute(kind, value) { const out = Buffer.alloc(Math.ceil((value.length + 2) / 4) * 4); out[0] = kind; out[1] = out.length / 4; value.copy(out, 2); return out; }
function packet(code, id, method, data) { const out = Buffer.alloc(5 + data.length); out[0] = code; out[1] = id; out.writeUInt16BE(out.length, 2); out[4] = method; data.copy(out, 5); return out; }
function signed(code, id, method, subtype, attrs, key, reserved = [0, 0]) {
  const out = packet(code, id, method, Buffer.concat([Buffer.from([subtype, ...reserved]), ...attrs, attribute(11, Buffer.alloc(18))]));
  hmac(method === 23 ? 'sha1' : 'sha256', key, out).copy(out, out.length - 16, 0, 16); return out;
}
function flow(method, protectedResult) {
  const imsi = '460001234567890', identity = `${method === 23 ? '0' : '6'}${imsi}@nai.epc.mnc000.mcc460.3gppnetwork.org`;
  const autn = Buffer.alloc(16, 0x34); if (method === 50) autn[6] |= 0x80;
  const v = { id: `method${method}-${protectedResult ? 'protected' : 'plain'}`, method, imsi, mcc: '460', mnc: '00', identity, ik: 'ee'.repeat(16), ck: 'cc'.repeat(16), rand: '12'.repeat(16), autn: autn.toString('hex'), res: '1122334455667788', network: 'WLAN', protectedResult };
  const keys = derive(v), attrs = [attribute(1, Buffer.concat([Buffer.from([0x55, 0xa7]), hex(v.rand)])), attribute(2, Buffer.concat([Buffer.alloc(2), autn]))];
  if (method === 50) attrs.push(attribute(23, Buffer.concat([Buffer.from([0, 4]), Buffer.from('WLAN')])), attribute(24, Buffer.from([0, 1])));
  if (protectedResult) attrs.push(attribute(135, Buffer.alloc(2)));
  attrs.push(attribute(200, Buffer.from([7, 8])));
  const responseAttrs = [attribute(3, Buffer.concat([Buffer.from([0, 64]), hex(v.res)]))];
  if (protectedResult) responseAttrs.push(attribute(135, Buffer.alloc(2)));
  return { ...v, ...keys,
    request: signed(1, 40, method, 1, attrs, hex(keys.k_aut), [0x55, 0xa7]).toString('hex'),
    response: signed(2, 40, method, 1, responseAttrs, hex(keys.k_aut)).toString('hex'),
    notificationRequest: signed(1, 42, method, 12, [attribute(12, Buffer.from([0x80, 0]))], hex(keys.k_aut)).toString('hex'),
    notificationResponse: signed(2, 42, method, 12, [], hex(keys.k_aut)).toString('hex'),
    success: Buffer.from([3, protectedResult ? 42 : 40, 0, 4]).toString('hex') };
}
if (process.argv.includes('--emit')) {
  const flows = [flow(23, false), flow(23, true), flow(50, false), flow(50, true)];
  const rfc = [{ id: 'rfc5448-case1', method: 50, identity: '0555444333222111', network: 'WLAN', ik: '9744871ad32bf9bbd1dd5ce54e3e2e5a', ck: '5349fbe098649f948f5d2e973a81c00f', autn: 'bb52e91c747ac3ab2a5c23d15ee351d5' },
    { id: 'rfc5448-case3', method: 50, identity: '0555444333222111', network: 'WLAN', ik: 'b0'.repeat(16), ck: 'c0'.repeat(16), autn: 'a0'.repeat(16) }].map(v => ({ ...v, ...derive(v) }));
  if (rfc[0].k_encr !== '766fa0a6c317174b812d52fbcd11a179' || rfc[1].k_aut !== 'c40700e7722483ae3dc7139eb0b88bb558cb3081eccd057f9207d1286ee7dd53') throw Error('RFC oracle mismatch');
  process.stdout.write(JSON.stringify({ version: 1, synthetic: true, fips: { seed: 'bd029bbe7f51960bcf9edb2b61f06f0feb5a38b6', output: '2070b3223dba372fde1c0ffc7b2e3b498b2606143c6c18bacb0f6c55babb13788e20d737a3275116' }, keys: rfc, flows }, null, 2) + '\n');
} else {
  const c = JSON.parse(await readFile(new URL('../contracts/eap-aka-v1.json', import.meta.url), 'utf8'));
  if (!timingSafeEqual(fips(hex(c.fips.seed), 40), hex(c.fips.output))) throw Error('Independent FIPS oracle mismatch');
  for (const v of [...c.keys, ...c.flows]) for (const [name, value] of Object.entries(derive(v))) if (value !== v[name]) throw Error(`Bad EAP key vector ${v.id}/${name}`);
  for (const v of c.flows) for (const [name, value] of Object.entries(flow(v.method, v.protectedResult))) if (value !== v[name]) throw Error(`Bad EAP flow ${v.id}/${name}`);
  process.stdout.write(`Independent EAP keys: ${c.keys.length + c.flows.length}; full flows: ${c.flows.length}; FIPS known answer: 1\n`);
}
