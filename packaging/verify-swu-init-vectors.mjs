// Independent numerical check of public synthetic fixtures. No project crypto
// implementation is imported, no SIM/hardware/network is used, no files written.
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';
const contract = JSON.parse(readFileSync(new URL('../contracts/swu-ike-init-v1.json', import.meta.url), 'utf8'));
const prfs = new Map([[2, 'sha1'], [5, 'sha256'], [6, 'sha384'], [7, 'sha512']]);
const integrityLengths = new Map([[2, 20], [12, 32], [13, 48], [14, 64]]);
for (const sample of contract.keys) {
  const hash = prfs.get(sample.prf);
  assert.ok(hash);
  const mac = (key, data) => createHmac(hash, key).update(data).digest();
  const ni = Buffer.from(sample.ni, 'hex');
  const nr = Buffer.from(sample.nr, 'hex');
  const shared = Buffer.from(sample.shared, 'hex');
  const skeyseed = mac(Buffer.concat([ni, nr]), shared);
  const seed = Buffer.concat([ni, nr, Buffer.from('01020304050607081112131415161718', 'hex')]);
  const prfLength = mac(skeyseed, seed).length;
  const lengths = [prfLength, integrityLengths.get(sample.integrity), integrityLengths.get(sample.integrity),
    sample.encryptionBits / 8, sample.encryptionBits / 8, prfLength, prfLength];
  const size = lengths.reduce((a, b) => a + b, 0);
  let stream = Buffer.alloc(0), previous = Buffer.alloc(0), counter = 1;
  while (stream.length < size) {
    previous = mac(skeyseed, Buffer.concat([previous, seed, Buffer.from([counter++])]));
    stream = Buffer.concat([stream, previous]);
  }
  let at = 0;
  const keys = lengths.map(length => {
    const key = stream.subarray(at, at + length).toString('hex'); at += length; return key;
  });
  assert.deepEqual(keys, sample.keys);
}
const modPow = (base, exponent, modulus) => {
  let result = 1n;
  while (exponent) {
    if (exponent & 1n) result = result * base % modulus;
    base = base * base % modulus; exponent >>= 1n;
  }
  return result;
};
for (const sample of contract.modp) {
  const prime = BigInt('0x' + sample.prime);
  const privateKey = BigInt('0x' + sample.private);
  const peer = BigInt('0x' + sample.peer);
  const encode = value => value.toString(16).padStart(sample.prime.length, '0');
  assert.equal(encode(modPow(2n, privateKey, prime)), sample.public);
  assert.equal(encode(modPow(peer, privateKey, prime)), sample.shared);
}
process.stdout.write('Independent HMAC schedules: ' + contract.keys.length + '; MODP vectors: ' + contract.modp.length + '\n');
