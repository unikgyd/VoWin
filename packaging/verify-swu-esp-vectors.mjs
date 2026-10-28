// Independent Node/OpenSSL RFC 4303 tunnel fixtures. Synthetic identities only.
// --emit prints a fresh deterministic contract, never writes files or runs project code.
import { createCipheriv, createHmac } from 'node:crypto';
import { readFile } from 'node:fs/promises';
const hex = bytes => Buffer.from(bytes).toString('hex');
const checksum = bytes => {
  let sum = 0;
  for (let i = 0; i < bytes.length; i += 2) sum += (bytes[i] << 8) + (bytes[i + 1] ?? 0);
  while (sum > 65535) sum = (sum & 65535) + (sum >>> 16);
  return (~sum) & 65535;
};
const address = (ipv6, last) => ipv6 ? Buffer.from(`20010db800000000000000000000000${last}`, 'hex') : Buffer.from([10, 0, 0, last]);
function packet(ipv6) {
  const source = address(ipv6, 1), destination = address(ipv6, 2);
  const payload = Buffer.from('synthetic SWu UDP\0\u0080', 'utf8');
  const udp = Buffer.alloc(8 + payload.length);
  udp.writeUInt16BE(5060, 0); udp.writeUInt16BE(45000, 2); udp.writeUInt16BE(udp.length, 4); payload.copy(udp, 8);
  const pseudo = ipv6 ? Buffer.from([0, 0, 0, udp.length, 0, 0, 0, 17]) : Buffer.from([0, 17, 0, udp.length]);
  udp.writeUInt16BE(checksum(Buffer.concat([source, destination, pseudo, udp])) || 65535, 6);
  const ip = Buffer.alloc(ipv6 ? 40 : 20); ip[0] = ipv6 ? 0x60 : 0x45;
  if (ipv6) { ip.writeUInt16BE(udp.length, 4); ip[6] = 17; ip[7] = 64; source.copy(ip, 8); destination.copy(ip, 24); }
  else { ip.writeUInt16BE(ip.length + udp.length, 2); ip[6] = 0x40; ip[8] = 64; ip[9] = 17; source.copy(ip, 12); destination.copy(ip, 16); ip.writeUInt16BE(checksum(ip), 10); }
  return Buffer.concat([ip, udp]);
}
function protect(v) {
  const inner = Buffer.from(v.inner, 'hex'); const pad = (16 - (inner.length + 2) % 16) % 16;
  const plain = Buffer.concat([inner, Buffer.from(Array.from({ length: pad }, (_, i) => i + 1)), Buffer.from([pad, v.nextHeader])]);
  if (v.badPadding && pad) plain[inner.length] ^= 255;
  const c = createCipheriv(`aes-${v.bits}-cbc`, Buffer.from(v.key, 'hex'), Buffer.from(v.iv, 'hex')); c.setAutoPadding(false);
  const prefix = Buffer.alloc(8); prefix.writeUInt32BE(v.spi, 0); prefix.writeUInt32BE(v.sequence, 4);
  const value = Buffer.concat([prefix, Buffer.from(v.iv, 'hex'), c.update(plain), c.final()]);
  return Buffer.concat([value, createHmac(v.integrity === 1 ? 'md5' : v.integrity === 2 ? 'sha1' : 'sha256', Buffer.from(v.auth, 'hex')).update(value).digest().subarray(0, v.integrity === 12 ? 16 : 12)]);
}
function fixtures() {
  const vectors = [], policies = [];
  for (const ipv6 of [false, true]) {
    for (const bits of [128, 192, 256]) for (const integrity of [1, 2, 12]) {
      const v = { id: `${ipv6 ? 'v6' : 'v4'}-aes${bits}-auth${integrity}`, bits, integrity,
        key: hex(Buffer.from(Array.from({ length: bits / 8 }, (_, i) => i + 1))),
        auth: hex(Buffer.from(Array.from({ length: integrity === 1 ? 16 : integrity === 2 ? 20 : 32 }, (_, i) => 0xa0 + i))),
        iv: '00112233445566778899aabbccddeeff', spi: 0x11223344, sequence: 1,
        inner: hex(packet(ipv6)), nextHeader: ipv6 ? 41 : 4, local: ipv6 ? '2001:db8::2' : '10.0.0.2', peer: ipv6 ? '2001:db8::1' : '10.0.0.1' };
      v.esp = hex(protect(v)); vectors.push(v);
    }
    const base = vectors.find(v => v.bits === 128 && v.integrity === 2 && v.nextHeader === (ipv6 ? 41 : 4));
    const add = (name, mutate, accepted, extra = {}) => {
      const inner = mutate(Buffer.from(base.inner, 'hex'));
      const v = { ...base, id: `${ipv6 ? 'v6' : 'v4'}-${name}`, inner: hex(inner), sequence: 1000, accepted, ...extra };
      v.esp = hex(protect(v)); policies.push(v);
    };
    add('valid', p => p, true);
    add('bad-udp-checksum', p => { p[p.length - 1] ^= 1; return p; }, false);
    add('zero-udp-checksum', p => { p.writeUInt16BE(0, (ipv6 ? 40 : 20) + 6); return p; }, !ipv6);
    add('wrong-next-header', p => p, false, { nextHeader: ipv6 ? 4 : 41 });
    add('bad-padding', p => p, false, { badPadding: true });
    add('wrong-port', p => {
      const at = ipv6 ? 40 : 20; p.writeUInt16BE(5061, at); p.writeUInt16BE(0, at + 6);
      const udp = p.subarray(at), pseudo = ipv6 ? Buffer.from([0, 0, 0, udp.length, 0, 0, 0, 17]) : Buffer.from([0, 17, 0, udp.length]);
      const source = p.subarray(ipv6 ? 8 : 12, ipv6 ? 24 : 16), destination = p.subarray(ipv6 ? 24 : 16, ipv6 ? 40 : 20);
      p.writeUInt16BE(checksum(Buffer.concat([source, destination, pseudo, udp])) || 65535, at + 6); return p;
    }, false);
    if (!ipv6) {
      add('reserved-ip-flag', p => { p[6] |= 0x80; p.writeUInt16BE(0, 10); p.writeUInt16BE(checksum(p.subarray(0, 20)), 10); return p; }, false);
      add('bad-ip-checksum', p => { p[10] ^= 1; return p; }, false);
    } else {
      const extend = (p, type, fragment = 0) => {
        const result = Buffer.concat([p.subarray(0, 40), Buffer.alloc(8), p.subarray(40)]);
        result.writeUInt16BE(result.length - 40, 4); result[6] = type; result[40] = p[6];
        if (type === 44) result.writeUInt16BE(fragment, 42); return result;
      };
      add('destination-options', p => extend(p, 60), true);
      add('atomic-fragment', p => extend(p, 44), true);
      add('atomic-bad-checksum', p => { const v = extend(p, 44); v[v.length - 1] ^= 1; return v; }, false);
      add('reserved-fragment-bits', p => extend(p, 44, 2), false);
      add('duplicate-fragment', p => extend(extend(p, 44), 44), false);
      add('noninitial-fragment-restricted-port', p => extend(p, 44, 8), false);
      add('truncated-options', p => { const v = extend(p, 60); v[41] = 255; return v; }, false);
    }
  }
  return { version: 1, description: 'Independent synthetic tunnel ESP and strict UDP policy frames; no real SIM or keys.', vectors, policies };
}
const expected = fixtures();
if (process.argv.includes('--emit')) process.stdout.write(JSON.stringify(expected, null, 2) + '\n');
else {
  const actual = JSON.parse(await readFile(new URL('../contracts/swu-esp-v1.json', import.meta.url), 'utf8'));
  if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error('Independent SWu ESP contract differs');
  process.stdout.write(`Independent tunnel ESP: ${actual.vectors.length}; inner policy frames: ${actual.policies.length}\n`);
}
