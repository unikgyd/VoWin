// Mechanical, hash-checked source export for the standalone GPL Linux repository.
// Never exports drivers, native Windows binaries, personal configs or build outputs.
import { readFile, writeFile, readdir, mkdir } from 'node:fs/promises';
import { resolve, relative, dirname, extname, sep } from 'node:path';
import { createHash } from 'node:crypto';

const root = resolve(import.meta.dirname, '..');
const destination = resolve(root, 'SimAdmin-sip/protocol/shared');
const check = process.argv.includes('--check');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const unix = value => value.split(sep).join('/');
const directories = ['Common', 'Crypto', 'Euicc', 'Ike', 'Ipsec', 'Kernel', 'Modem', 'Sim', 'Sip', 'StateMachine', 'Telephony', 'GatewayHost']
  .map(name => `voSharp/src/VoSharp.${name}`).concat(['qmi/qmiSharp']);
const files = new Map();
async function visit(directory) {
  for (const entry of await readdir(resolve(root, directory), { withFileTypes: true })) {
    if (['bin', 'obj', '.git', 'TestResults'].includes(entry.name)) continue;
    const path = `${directory}/${entry.name}`;
    if (entry.isSymbolicLink()) throw new Error(`Symlinks are not exported: ${path}`);
    if (entry.isDirectory()) await visit(path);
    else if (['.cs', '.csproj', '.resx'].includes(extname(entry.name))) files.set(path, await readFile(resolve(root, path)));
  }
}
for (const directory of directories) await visit(directory);
files.set('LICENSE.VoWin', await readFile(resolve(root, 'LICENSE')));
files.set('Directory.Build.props', Buffer.from(`<Project>\n  <PropertyGroup>\n    <VoSharpIncludeWindowsAmr>false</VoSharpIncludeWindowsAmr>\n    <Deterministic>true</Deterministic>\n  </PropertyGroup>\n</Project>\n`));
files.set('SOURCE-NOTICE.txt', Buffer.from(`Shared protocol source exported from the VoWin workspace.\nThe governing repository license is preserved as LICENSE.VoWin.\nOriginal source notices and attribution are retained in each file.\nNative Windows drivers/DLLs and system .NET are not distributed by this export.\nUpdate with packaging/sync-linux-protocol-core.mjs in the Windows repository.\nDo not edit generated copies silently: changes must also be reconciled upstream.\n`));
const manifestPath = resolve(destination, 'source-manifest.json');
let previous = null;
try { previous = JSON.parse(await readFile(manifestPath, 'utf8')); }
catch (error) { if (error.code !== 'ENOENT') throw error; }
const digests = {};
for (const [path, bytes] of [...files].sort(([a], [b]) => a.localeCompare(b, 'en'))) {
  const target = resolve(destination, path);
  if (relative(destination, target).startsWith('..') || target === destination) throw new Error('Unsafe export target');
  const hash = sha(bytes);
  digests[path] = hash;
  let existing;
  try { existing = sha(await readFile(target)); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (check) {
    if (existing !== hash || previous?.files?.[path] !== hash) throw new Error(`Shared source is out of sync: ${path}`);
  } else {
    if (existing && existing !== hash && existing !== previous?.files?.[path]) throw new Error(`Refusing to overwrite locally edited shared source: ${path}`);
    if (existing !== hash) { await mkdir(dirname(target), { recursive: true }); await writeFile(target, bytes); }
  }
}
for (const path of Object.keys(previous?.files || {})) if (!files.has(path)) throw new Error(`Obsolete export needs explicit review, not automatic deletion: ${path}`);
if (!check) await writeFile(manifestPath, JSON.stringify({ version: 1, source: 'VoWin shared protocol sources', files: digests }, null, 2) + '\n');
process.stdout.write(`${check ? 'Verified' : 'Exported'} ${files.size} shared source files; no runtime or driver binaries.\n`);
