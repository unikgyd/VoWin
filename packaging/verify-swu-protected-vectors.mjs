// Independent Node/OpenSSL AES-CBC + HMAC oracle. No project crypto imports,
// network, hardware or file writes. --emit prints synthetic fixtures only.
import { createCipheriv, createDecipheriv, createHmac, timingSafeEqual } from 'node:crypto';
import { readFile } from 'node:fs/promises';
const algorithms = { 2: ['sha1', 12, 20], 12: ['sha256', 16, 32], 13: ['sha384', 24, 48], 14: ['sha512', 32, 64] };
function protect(v) {
  const plain = Buffer.from(v.plaintext, 'hex'), iv = Buffer.from(v.iv, 'hex');
  const padding = 15 - plain.length % 16, padded = Buffer.concat([plain, Buffer.alloc(padding), Buffer.from([padding])]);
  const cipher = createCipheriv(`aes-${v.bits}-cbc`, Buffer.from(v.encryption, 'hex'), iv); cipher.setAutoPadding(false);
  const encrypted = Buffer.concat([cipher.update(padded), cipher.final()]);
  const prefix = v.number ? Buffer.alloc(4) : Buffer.alloc(0);
  if (v.number) { prefix.writeUInt16BE(v.number); prefix.writeUInt16BE(v.total, 2); }
  const [hash, length] = algorithms[v.integrity], body = Buffer.concat([prefix, iv, encrypted]);
  const header = Buffer.alloc(32); Buffer.from('01020304050607081112131415161718', 'hex').copy(header);
  header[16] = v.number ? 53 : 46; header[17] = 0x20; header[18] = 35; header[19] = 32;
  header.writeUInt32BE(1, 20); header.writeUInt32BE(32 + body.length + length, 24);
  header[28] = v.first; header.writeUInt16BE(4 + body.length + length, 30);
  const signed = Buffer.concat([header, body]), mac = createHmac(hash, Buffer.from(v.authentication, 'hex')).update(signed).digest().subarray(0, length);
  padded.fill(0); return Buffer.concat([signed, mac]);
}
if (process.argv.includes('--emit')) {
  const payload = Buffer.concat([Buffer.from([0, 0, 0, 42]), Buffer.from('synthetic protected transport fixture!')]);
  if (payload.length !== 42) throw Error('fixture length');
  const vectors = [];
  for (const bits of [128, 192, 256]) for (const integrity of [2, 12, 13, 14]) {
    const base = { bits, integrity, encryption: Buffer.alloc(bits / 8, 0x42).toString('hex'), authentication: Buffer.alloc(algorithms[integrity][2], 0x24).toString('hex') };
    for (const number of [0, 1, 2]) {
      const v = { id: `aes${bits}-integ${integrity}-${number ? `skf${number}` : 'sk'}`, ...base,
        first: number === 2 ? 0 : 48, number, total: number ? 2 : 0,
        plaintext: (number === 0 ? payload : number === 1 ? payload.subarray(0, 17) : payload.subarray(17)).toString('hex'),
        iv: Buffer.from(Array.from({ length: 16 }, (_, i) => i + number)).toString('hex') };
      vectors.push({ ...v, packet: protect(v).toString('hex') });
    }
  }
  process.stdout.write(JSON.stringify({ version: 1, synthetic: true, chain: payload.toString('hex'), vectors }, null, 2) + '\n');
} else {
  const contract = JSON.parse(await readFile(new URL('../contracts/swu-protected-v1.json', import.meta.url), 'utf8'));
  for (const v of contract.vectors) {
    const packet = Buffer.from(v.packet, 'hex'), expected = protect(v);
    if (!timingSafeEqual(packet, expected)) throw Error(`bad independent protected vector ${v.id}`);
    const prefix = v.number ? 4 : 0, [, macLength] = algorithms[v.integrity];
    const decipher = createDecipheriv(`aes-${v.bits}-cbc`, Buffer.from(v.encryption, 'hex'), packet.subarray(32 + prefix, 48 + prefix));
    decipher.setAutoPadding(false);
    const padded = Buffer.concat([decipher.update(packet.subarray(48 + prefix, -macLength)), decipher.final()]);
    const plain = padded.subarray(0, padded.length - padded.at(-1) - 1);
    if (plain.toString('hex') !== v.plaintext) throw Error(`bad decrypt ${v.id}`); padded.fill(0);
  }
  process.stdout.write(`Independent AES-CBC/HMAC SK/SKF packets: ${contract.vectors.length}\n`);
}
