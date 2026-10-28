// Independent OpenSSL-backed composition of the existing IMS ESP fixtures
// inside RFC 4303 tunnel ESP. No project codec, hardware or network calls.
import { readFile } from 'node:fs/promises';
import { createCipheriv, createHmac } from 'node:crypto';
const base = JSON.parse(await readFile(new URL('../contracts/ims-esp-v1.json', import.meta.url), 'utf8'));
const key = Buffer.from(Array.from({ length: 32 }, (_, i) => 0x80 + i));
const auth = Buffer.from(Array.from({ length: 32 }, (_, i) => 0xa0 + i));
const iv = Buffer.from(Array.from({ length: 16 }, (_, i) => i));
function protect(hex, ipv6, sequence) {
  const packet = Buffer.from(hex, 'hex'); const pad = (16 - (packet.length + 2) % 16) % 16;
  const clear = Buffer.concat([packet, Buffer.from(Array.from({ length: pad }, (_, i) => i + 1)), Buffer.from([pad, ipv6 ? 41 : 4])]);
  const cipher = createCipheriv('aes-256-cbc', key, iv); cipher.setAutoPadding(false);
  const header = Buffer.alloc(8); header.writeUInt32BE(0x11223344); header.writeUInt32BE(sequence, 4);
  const value = Buffer.concat([header, iv, cipher.update(clear), cipher.final()]);
  return Buffer.concat([value, createHmac('sha256', auth).update(value).digest().subarray(0, 16)]).toString('hex');
}
function ipValid(hex, ipv6) {
  const b = Buffer.from(hex, 'hex');
  if (ipv6) return b.length >= 40 && b[0] >> 4 === 6 && b.length === 40 + b.readUInt16BE(4) && b[6] === 50;
  if (b.length < 20 || b[0] >> 4 !== 4 || b.length !== b.readUInt16BE(2) || b[9] !== 50 || (b.readUInt16BE(6) & 0x8000)) return false;
  const size = (b[0] & 15) * 4; if (size < 20 || size > b.length) return false;
  let sum = 0; for (let i = 0; i < size; i += 2) sum += b.readUInt16BE(i);
  while (sum > 65535) sum = (sum & 65535) + (sum >>> 16);
  return sum === 65535;
}
const expected = { version: 1, description: 'Synthetic nested IMS ESP inside SWu ESP, independently composed with Node/OpenSSL.',
  keyHex: key.toString('hex'), authHex: auth.toString('hex'), ckHex: base.ckHex, ikHex: base.ikHex, payloadHex: base.payloadHex,
  cases: base.cases.map(v => {
    const valid = base.cases.find(a => a.ipv6 === v.ipv6 && a.integrity === v.integrity && a.encryption === v.encryption && a.id.endsWith('-valid'));
    return { ...v, outerAccepted: ipValid(v.packetHex, v.ipv6), espHex: protect(v.packetHex, v.ipv6, 41), followupHex: protect(valid.packetHex, v.ipv6, 42) };
  }) };
if (process.argv.includes('--emit')) {
  const group = Number(process.argv[process.argv.indexOf('--group') + 1]);
  process.stdout.write(JSON.stringify({ ...expected, cases: expected.cases.slice(group * 8, group * 8 + 8) }));
} else {
  const actual = JSON.parse(await readFile(new URL('../contracts/nested-ims-esp-v1.json', import.meta.url), 'utf8'));
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error('Nested IMS composition contract differs');
  process.stdout.write(`Independent nested IMS/SWu frames: ${actual.cases.length}\n`);
}
