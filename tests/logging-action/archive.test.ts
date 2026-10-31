import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdtemp, stat, rm } from 'node:fs/promises';
import { gzipSync, gunzipSync } from 'node:zlib';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createArchive, inspectArchive, extractArchive, sha256, exactVersion } from '../../actions/logging/archive.mjs';
import { childEnvironment, deliveryResult } from '../../actions/logging/process.mjs';
import { artifactUrl } from '../../actions/logging/transport.mjs';
import { escapeCommand } from '../../actions/logging/main.mjs';

const version = '0.1.0-internal.1';
function tiny() {
  const elf = Buffer.alloc(64); Buffer.from('7f454c460201', 'hex').copy(elf); elf.writeUInt16LE(62, 18);
  const files = new Map([['Solus.Logging.Cli', elf], ['Solus.Logging.Cli.dll', Buffer.from('dll')],
    ['Solus.Logging.Cli.runtimeconfig.json', Buffer.from('{}')], ['libcoreclr.so', Buffer.from('runtime')]]);
  files.set('payload-manifest.json', Buffer.from(JSON.stringify({ format: 1, version, runtimeIdentifier: 'linux-x64', entrypoint: 'Solus.Logging.Cli', files: Object.fromEntries([...files].map(([name, bytes]) => [name, sha256(bytes)])) }) + '\n'));
  return files;
}
const verify = archive => inspectArchive(archive, sha256(archive), version);

test('built self-contained payload is required and fully verified, including runtime and ELF apphost', async () => {
  const report = JSON.parse(await readFile('artifacts/logging-cli/payload.json', 'utf8'));
  const archive = await readFile(join('artifacts/logging-cli', report.filename));
  const verified = inspectArchive(archive, report.sha256, report.version);
  assert.equal(verified.manifest.version, report.version); assert.ok(verified.files.size > 100);
  assert.ok(verified.files.has('System.Private.CoreLib.dll')); assert.ok(verified.files.has('Solus.Logging.Supabase.dll'));
});

test('owned archive extraction creates only verified flat files and a private executable', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'logging-archive-test-'));
  try {
    const executable = await extractArchive(verify(createArchive(tiny())), directory);
    assert.equal((await stat(executable)).mode & 0o777, 0o700);
    assert.equal((await stat(join(directory, 'Solus.Logging.Cli.dll'))).mode & 0o777, 0o600);
    await assert.rejects(extractArchive(verify(createArchive(tiny())), directory));
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('digest, exact version, complete runtime manifest and per-file hashes fail closed', () => {
  const bytes = createArchive(tiny());
  assert.throws(() => inspectArchive(bytes, '0'.repeat(64), version));
  assert.throws(() => inspectArchive(bytes, sha256(bytes), '0.2.0'));
  assert.throws(() => verify(bytes.subarray(0, bytes.length - 1)));
  for (const name of ['libcoreclr.so', 'Solus.Logging.Cli.dll', 'payload-manifest.json']) {
    const files = tiny(); files.delete(name); assert.throws(() => verify(createArchive(files)));
  }
  const files = tiny(); files.set('Solus.Logging.Cli.dll', Buffer.from('changed')); assert.throws(() => verify(createArchive(files)));
  assert.throws(() => createArchive(new Map([['../escape', Buffer.alloc(0)]])));
  for (const invalid of ['latest', 'v0.1.0', '1.*', '1.2.3+mutable', '../1.2.3', '']) assert.equal(exactVersion(invalid), false);
});

test('narrow parser rejects traversal, links, directories, PAX, duplicate entries, padding and trailing tar data', () => {
  const original = gunzipSync(createArchive(tiny()));
  for (const mutate of [
    bytes => bytes.write('../escape', 0, 'ascii'), bytes => bytes[156] = 50, bytes => bytes[156] = 53,
    bytes => bytes[156] = 120, bytes => bytes[148] = 57, bytes => bytes[600] = 1,
  ]) {
    const bytes = Buffer.from(original); mutate(bytes); assert.throws(() => verify(gzipSync(bytes)));
  }
  const repeated = Buffer.concat([original.subarray(0, -1024), original]);
  assert.throws(() => verify(gzipSync(repeated)));
  assert.throws(() => verify(gzipSync(Buffer.concat([original, Buffer.alloc(512)]))));
  const files = tiny(); const manifest = JSON.parse(files.get('payload-manifest.json').toString());
  manifest.runtimeIdentifier = 'osx-arm64'; files.set('payload-manifest.json', Buffer.from(JSON.stringify(manifest) + '\n'));
  assert.throws(() => verify(createArchive(files)));
});

test('HTTPS URL validation and child environment isolate artifact, storage and ambient execution authority', () => {
  for (const value of ['http://example.test/x', 'https://u:p@example.test/x', 'https://example.test/x#fragment', 'file:///tmp/archive']) assert.throws(() => artifactUrl(value));
  const env = childEnvironment({ INPUT_TELEMETRY_JSON: 'record', GITHUB_TOKEN: 'github', LD_PRELOAD: 'bad', DOTNET_STARTUP_HOOKS: 'bad',
    HTTPS_PROXY: 'proxy', SSL_CERT_FILE: '/synthetic/ca.pem', LANG: 'C.UTF-8' }, { SOLUS_LOGGING_ACCESS_JWT: 'storage' });
  assert.deepEqual(Object.keys(env).sort(), ['DOTNET_EnableDiagnostics', 'DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION', 'LANG', 'SOLUS_LOGGING_ACCESS_JWT', 'SSL_CERT_FILE'].sort());
  assert.equal(env.SOLUS_LOGGING_ACCESS_JWT, 'storage'); assert.equal(escapeCommand('x%\n::error::y\r'), 'x%25%0A::error::y%0D');
});

test('machine protocol rejects contradictory status/diagnostic/attempt/exit and raw child errors', () => {
  const okay = { deliveryStatus: 'stored', diagnosticCode: 'none', attempts: 1 };
  const process = result => ({ code: 0, signal: null, stderr: 0, output: JSON.stringify(result) });
  assert.deepEqual(deliveryResult(process(okay), 3), okay);
  for (const changes of [{ attempts: 0 }, { attempts: 4 }, { diagnosticCode: 'secret' }, { extra: 'canary' }, { deliveryStatus: 'failed' }, { deliveryStatus: 'skipped' }])
    assert.throws(() => deliveryResult(process({ ...okay, ...changes }), 3));
  assert.throws(() => deliveryResult({ ...process(okay), code: 1 }, 3));
  assert.throws(() => deliveryResult({ ...process(okay), stderr: 1 }, 3));
  assert.throws(() => deliveryResult({ ...process(okay), output: 'raw-canary' }, 3));
});
