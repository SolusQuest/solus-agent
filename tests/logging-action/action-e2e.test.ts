import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { readFile, writeFile, symlink } from 'node:fs/promises';
import { join } from 'node:path';
import { controlledStorage } from './controlled-storage.mjs';
import { createArchive, inspectArchive, sha256 } from '../../actions/logging/archive.mjs';
import { escapeCommand } from '../../actions/logging/main.mjs';
import { recordInput, supportedHost } from '../../actions/logging/input.mjs';

const linux = await supportedHost();
if (process.env.CI === 'true') assert.equal(linux, true, 'CI proof requires actual Ubuntu24.04 x64.');

test('actual Action → verified built Linux CLI → controlled HTTPS → real SQL/RLS', { skip: linux ? false : 'Linux process and /proc proof runs on exact-head Ubuntu CI; macOS does not execute ELF.', timeout: 240000 }, async t => {
  const backend = await controlledStorage();
  try {
    const report = JSON.parse(await readFile('artifacts/logging-cli/payload.json', 'utf8'));
    const archive = await readFile(join('artifacts/logging-cli', report.filename));
    const original = await readFile('tests/Solus.Logging.Tests/Supabase/telemetry-v1.json', 'utf8');
    // Use the exact fixture identity rather than assuming its text's current eventId.
    const event = text => JSON.parse(text).eventId;
    const newRecord = () => original.replace(event(original), randomUUID());
    const key = 'sb_publishable_SYNTHETIC_CANARY'; const token = 'SYNTHETIC_ACCESS.CANARY.JWT'; const readToken = 'SYNTHETIC_DOWNLOAD_CANARY';
    let mode = 'normal'; let requests = 0; let downloads = 0; let onRequest; let child;
    const seenDownloads = [];
    const secondOrigin = await backend.serve((req, res) => {
      seenDownloads.push(req.headers.authorization ?? null); res.end(archive);
    });
    const origin = await backend.serve(async (req, res) => {
      if (req.url.startsWith('/artifact')) {
        downloads++; seenDownloads.push(req.headers.authorization ?? null);
        if (req.url === '/artifact-cross') { res.writeHead(302, { location: secondOrigin + '/payload' }); res.end(); return; }
        if (req.url === '/artifact-down') { res.writeHead(302, { location: 'http://127.0.0.1/no' }); res.end(); return; }
        if (req.url === '/artifact-loop') { res.writeHead(302, { location: '/artifact-loop' }); res.end(); return; }
        if (req.url === '/artifact-bad') { res.end('invalid-SYNTHETIC_SERVER_CANARY'); return; }
        if (req.url === '/artifact-identity') { res.end(identityArchive); return; }
        res.end(archive); return;
      }
      requests++; assert.equal(req.url, '/rest/v1/rpc/ingest_telemetry'); assert.equal(req.method, 'POST');
      assert.equal(req.headers.authorization, `Bearer ${token}`); assert.equal(req.headers.apikey, key);
      assert.equal(req.headers['x-artifact-token'], undefined);
      if (onRequest) await onRequest();
      if (mode === 'stall') return;
      res.setHeader('content-type', 'application/json');
      if (mode === 'auth') { res.writeHead(401); res.end('SYNTHETIC_SERVER_CANARY'); return; }
      if (mode === 'schema') { res.writeHead(404); res.end('SYNTHETIC_SERVER_CANARY'); return; }
      if (mode === 'transient') { res.writeHead(503); res.end('SYNTHETIC_SERVER_CANARY'); return; }
      if (mode === 'protocol') { res.end('{"message":"SYNTHETIC_SERVER_CANARY"}'); return; }
      const parts = []; let size = 0;
      for await (const bytes of req) { size += bytes.length; assert.ok(size < 263000); parts.push(bytes); }
      const body = Buffer.concat(parts).toString('utf8');
      assert.ok(body.startsWith('{"event":') && body.endsWith('}'));
      const raw = body.slice('{"event":'.length, -1); // Preserve Int64/decimal spelling; no JS reserialization.
      try {
        const status = (await backend.writer.query('select public.ingest_telemetry($1::jsonb) as status', [raw])).rows[0].status;
        if (mode === 'lost') { mode = 'normal'; req.socket.destroy(); return; }
        res.end(JSON.stringify(status));
      } catch (error) { t.diagnostic(`Controlled SQL code: ${/^[0-9A-Z]{5}$/.test(error.code) ? error.code : 'unknown'}`); res.writeHead(error.code === '23505' ? 409 : 400); res.end('SYNTHETIC_SERVER_CANARY'); }
    });
    const verified = inspectArchive(archive, report.sha256, report.version);
    const wrong = { ...verified.manifest, version: '0.1.0-internal.2' };
    const files = new Map(verified.files); files.set('payload-manifest.json', Buffer.from(JSON.stringify(wrong) + '\n'));
    const identityArchive = createArchive(files);
    let invocation = 0;
    async function action(text, inputs = {}, settings = {}) {
      invocation++;
      const output = join(backend.owned, `output-${invocation}`);
      const env = { ...process.env, NODE_EXTRA_CA_CERTS: backend.cert, SSL_CERT_FILE: backend.cert,
        GITHUB_WORKSPACE: process.cwd(), RUNNER_TEMP: backend.owned, GITHUB_OUTPUT: output,
        INPUT_ADAPTER: 'supabase', INPUT_TELEMETRY_JSON: text,
        'INPUT_SUPABASE-URL': origin, 'INPUT_SUPABASE-PUBLISHABLE-KEY': key, 'INPUT_SUPABASE-ACCESS-TOKEN': token,
        'INPUT_CLI-VERSION': report.version, 'INPUT_CLI-ARTIFACT-URL': origin + '/artifact', 'INPUT_CLI-ARTIFACT-SHA256': report.sha256,
        'INPUT_ARTIFACT-READ-TOKEN': readToken, 'INPUT_RETRY-DELAY-MS': '0', 'INPUT_TIMEOUT-MS': '3000', ...inputs };
      // Native Action input names retain hyphens; telemetry-json does too.
      env['INPUT_TELEMETRY-JSON'] = text; delete env.INPUT_TELEMETRY_JSON;
      const started = performance.now();
      try { child = spawn(process.execPath, ['actions/logging/main.mjs'], { env, stdio: ['ignore', 'pipe', 'pipe'] }); }
      catch (error) { if (settings.spawnError) return { error }; throw error; }
      let stdout = ''; let stderr = ''; child.stdout.on('data', b => { stdout += b; }); child.stderr.on('data', b => { stderr += b; });
      const timer = setTimeout(() => child.kill('SIGKILL'), 15000);
      const finished = await new Promise(resolve => { child.on('error', error => resolve({ error })); child.on('close', (code, signal) => resolve({ code, signal })); });
      clearTimeout(timer);
      if (settings.spawnError) return finished;
      assert.equal(finished.error, undefined); assert.equal(finished.signal, null); assert.equal(stderr, '');
      const safeOutput = stdout.split('\n').filter(line => !line.startsWith('::add-mask::')).join('\n');
      for (const canary of [key, token, readToken, 'SYNTHETIC_SERVER_CANARY', 'SYNTHETIC_RECORD_CANARY']) assert.ok(!safeOutput.includes(canary), 'No canary outside mask control commands.');
      for (const name of ['SUPABASE-PUBLISHABLE-KEY', 'SUPABASE-ACCESS-TOKEN', 'ARTIFACT-READ-TOKEN']) {
        const value = env[`INPUT_${name}`]; if (value && value.length <= 8192) assert.ok(stdout.includes(`::add-mask::${escapeCommand(value)}`));
      }
      const outputs = Object.fromEntries((await readFile(output, 'utf8')).trim().split('\n').map(line => line.split('=')));
      assert.deepEqual(Object.keys(outputs).sort(), ['delivery-status', 'diagnostic-code']);
      assert.ok((await readFile(output, 'utf8')).length < 100);
      return { ...finished, stdout: safeOutput, outputs, elapsed: performance.now() - started };
    }
    const expect = (actual, status, code, exit = 0) => {
      assert.equal(actual.code, exit); assert.equal(actual.outputs['diagnostic-code'], code); assert.equal(actual.outputs['delivery-status'], status);
    };

    await t.test('stored/duplicate/collision retain identity and full nested JSONB, Int64, null and partial coverage', async () => {
      const text = newRecord();
      expect(await action(text), 'stored', 'none'); expect(await action(text), 'duplicate', 'none');
      expect(await action(text.replace('1.0-test', '2.0-collision')), 'failed', 'identity_conflict');
      const exact = (await backend.writer.query(`select usage = ($1::jsonb)->'usage' as usage, extensions = ($1::jsonb)->'extensions' as extensions,
        execution = ($1::jsonb)->'execution' as execution, outcome = ($1::jsonb)->'outcome' as outcome from public.telemetry_events where event_id=$2`, [text, event(text)])).rows[0];
      assert.deepEqual(exact, { usage: true, extensions: true, execution: true, outcome: true });
      assert.ok(text.includes('9223372036854775807')); assert.ok(text.includes('null'));
    });
    await t.test('valid nested record canaries and mask command metacharacters never become annotations or outputs', async () => {
      const record = JSON.parse(newRecord()); record.extensions.caller = { note: 'SYNTHETIC_RECORD_CANARY\n::error::record-injection', nested: [true, null, { n: 1 }] };
      expect(await action(JSON.stringify(record)), 'stored', 'none');
      const before = [requests, downloads];
      const bad = await action(newRecord(), { 'INPUT_ARTIFACT-READ-TOKEN': 'mask%\n::error::credential-injection' });
      expect(bad, 'failed', 'invalid_config'); assert.ok(!bad.stdout.includes('credential-injection'));
      assert.deepEqual([requests, downloads], before);
    });
    await t.test('lost committed response retries only same immutable event and returns duplicate', async () => {
      mode = 'lost'; const text = newRecord(); const before = requests;
      expect(await action(text), 'duplicate', 'none'); assert.equal(requests - before, 2);
      assert.equal((await backend.writer.query('select count(*)::int as n from public.telemetry_events where event_id=$1', [event(text)])).rows[0].n, 1);
    });
    await t.test('bounded auth/schema/protocol/transient faults default warn; strict fails logging only', async () => {
      for (const [fault, diagnostic, count] of [['auth', 'authentication_failed', 1], ['schema', 'schema_rejected', 1], ['protocol', 'protocol_failure', 1], ['transient', 'attempts_exhausted', 3]]) {
        mode = fault; const before = requests; const result = await action(newRecord());
        expect(result, 'failed', diagnostic); assert.equal(requests - before, count); assert.ok(result.stdout.includes('::warning::'));
      }
      mode = 'auth'; expect(await action(newRecord(), { 'INPUT_STRICT-DELIVERY': 'true' }), 'failed', 'authentication_failed', 1); mode = 'normal';
    });
    await t.test('empty visible skip and malformed input do not acquire artifact or contact storage', async () => {
      const before = [requests, downloads];
      const empty = await action('', { 'INPUT_SUPABASE-URL': '', 'INPUT_CLI-ARTIFACT-URL': '' });
      expect(empty, 'skipped', 'input_unavailable'); assert.ok(empty.stdout.includes('::notice::'));
      for (const malformed of ['{"secret":"SYNTHETIC_RECORD_CANARY"', '\uFEFF', '\u00A0']) expect(await action(malformed), 'failed', 'invalid_record');
      assert.deepEqual([requests, downloads], before);
    });
    await t.test('bad configuration fails before acquisition, full .NET record validation fails before storage', async () => {
      for (const changes of [{ 'INPUT_ADAPTER': 'other' }, { 'INPUT_TIMEOUT-MS': '0' }, { 'INPUT_SUPABASE-URL': 'http://bad.invalid' },
        { 'INPUT_SUPABASE-ACCESS-TOKEN': 'malformed' }, { 'INPUT_CLI-VERSION': 'latest' }, { 'INPUT_CLI-ARTIFACT-SHA256': 'bad' }]) {
        const before = [requests, downloads]; expect(await action(newRecord(), changes), 'failed', 'invalid_config'); assert.deepEqual([requests, downloads], before);
      }
      const before = requests; expect(await action('{}'), 'failed', 'invalid_record'); assert.equal(requests, before);
      expect(await action(newRecord(), { 'INPUT_STRICT-DELIVERY': 'yes' }), 'failed', 'invalid_config', 1);
    });
    await t.test('artifact bytes, executable version, redirect limits and downgrade reject before storage', async () => {
      const before = requests;
      expect(await action(newRecord(), { 'INPUT_CLI-ARTIFACT-SHA256': '0'.repeat(64) }), 'failed', 'invalid_artifact');
      expect(await action(newRecord(), { 'INPUT_CLI-ARTIFACT-URL': origin + '/artifact-bad' }), 'failed', 'invalid_artifact');
      expect(await action(newRecord(), { 'INPUT_CLI-ARTIFACT-URL': origin + '/artifact-identity', 'INPUT_CLI-VERSION': wrong.version,
        'INPUT_CLI-ARTIFACT-SHA256': sha256(identityArchive) }), 'failed', 'invalid_artifact');
      expect(await action(newRecord(), { 'INPUT_CLI-ARTIFACT-URL': origin + '/artifact-down' }), 'failed', 'invalid_config');
      expect(await action(newRecord(), { 'INPUT_CLI-ARTIFACT-URL': origin + '/artifact-loop' }), 'failed', 'artifact_unavailable');
      assert.equal(requests, before);
    });
    await t.test('download bearer token is stripped across origins; storage still uses only dedicated credentials', async () => {
      seenDownloads.length = 0; expect(await action(newRecord(), { 'INPUT_CLI-ARTIFACT-URL': origin + '/artifact-cross' }), 'stored', 'none');
      assert.deepEqual(seenDownloads, [`Bearer ${readToken}`, null]);
    });
    await t.test('failed producer process executes once, retains exit7 and usable telemetry, and is never replayed', async () => {
      const marker = join(backend.owned, 'producer-count'); const text = newRecord();
      const producer = spawnSync(process.execPath, ['-e', 'require("node:fs").appendFileSync(process.argv[1],"once\\n");process.stdout.write(process.argv[2]);process.exitCode=7', marker, text], { encoding: 'utf8' });
      assert.equal(producer.status, 7); expect(await action(producer.stdout), 'stored', 'none');
      assert.equal(producer.status, 7); assert.equal(await readFile(marker, 'utf8'), 'once\n');
    });
    await t.test('inline64KiB admission, Linux actual environment ceiling, and full256KiB same-job file transport', async () => {
      const text = newRecord(); const near = text + ' '.repeat(65536 - Buffer.byteLength(text));
      expect(await action(near), 'stored', 'none');
      const before = [requests, downloads]; expect(await action(near + ' '), 'failed', 'record_too_large'); assert.deepEqual([requests, downloads], before);
      const maximum = await readFile('.local/issue-121/fixtures/maximum.json'); assert.equal(maximum.length, 262144);
      const actualOsBoundary = await action(maximum.toString(), {}, { spawnError: true }); assert.equal(actualOsBoundary.error?.code, 'E2BIG');
      const filename = join(backend.owned, 'maximum.json'); await writeFile(filename, maximum);
      expect(await action('', { 'INPUT_TELEMETRY-JSON-FILE': filename }), 'stored', 'none');
      const equivalent = (await backend.writer.query("select usage = ($1::jsonb)->'usage' as usage, extensions = ($1::jsonb)->'extensions' as extensions from public.telemetry_events where event_id=$2", [maximum.toString(), event(maximum.toString())])).rows[0];
      assert.deepEqual(equivalent, { usage: true, extensions: true });
      await writeFile(filename, Buffer.concat([maximum, Buffer.from(' ')])); expect(await action('', { 'INPUT_TELEMETRY-JSON-FILE': filename }), 'failed', 'record_too_large');
    });
    await t.test('same-job files reject ambiguity, symlink parents/final files and out-of-scope authority', async () => {
      const filename = join(backend.owned, 'record.json'); await writeFile(filename, newRecord());
      const link = join(backend.owned, 'link'); await symlink(filename, link);
      const parentLink = join(backend.owned, 'parent-link'); await symlink(backend.owned, parentLink);
      const before = [requests, downloads];
      for (const path of [link, join(parentLink, 'record.json'), '/etc/hostname']) expect(await action('', { 'INPUT_TELEMETRY-JSON-FILE': path }), 'failed', 'invalid_config');
      expect(await action(newRecord(), { 'INPUT_TELEMETRY-JSON-FILE': filename }), 'failed', 'invalid_config');
      assert.deepEqual([requests, downloads], before);
      // Replaced-file authority is checked on the actual opened descriptor under /proc.
      assert.deepEqual(await recordInput('', filename, { GITHUB_WORKSPACE: process.cwd(), RUNNER_TEMP: backend.owned }), Buffer.from(await readFile(filename)));
    });
    await t.test('actual stalled response reaches CLI deadline with no extra attempts', async () => {
      mode = 'stall'; const before = requests; const result = await action(newRecord(), { 'INPUT_TIMEOUT-MS': '400' });
      expect(result, 'failed', 'deadline_exceeded'); assert.equal(requests - before, 1); assert.ok(result.elapsed < 6000); mode = 'normal';
    });
    await t.test('caller cancellation terminates actual CLI and remains a bounded independent failure', async () => {
      mode = 'stall'; onRequest = async () => { child.kill('SIGTERM'); }; const before = requests;
      const result = await action(newRecord()); expect(result, 'failed', 'cancelled'); assert.equal(requests - before, 1); assert.ok(result.elapsed < 6000);
      onRequest = undefined; mode = 'normal';
    });
    await t.test('actual stopped .NET child requires watchdog SIGTERM→SIGKILL, returns finite failure and is reaped', async () => {
      let stopped;
      mode = 'stall'; onRequest = async () => {
        const children = (await readFile(`/proc/${child.pid}/task/${child.pid}/children`, 'utf8')).trim().split(/\s+/).filter(Boolean);
        assert.equal(children.length, 1); stopped = Number(children[0]); process.kill(stopped, 'SIGSTOP');
      };
      const before = requests; const result = await action(newRecord(), { 'INPUT_TIMEOUT-MS': '700' });
      expect(result, 'failed', 'deadline_exceeded'); assert.equal(requests - before, 1); assert.ok(result.elapsed < 7000);
      assert.throws(() => process.kill(stopped, 0), error => error.code === 'ESRCH'); onRequest = undefined; mode = 'normal';
    });
  } finally { await backend.close(); }
});
