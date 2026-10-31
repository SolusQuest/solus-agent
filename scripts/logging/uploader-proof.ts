import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { cpSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { controlledStorage } from '../../tests/logging-action/controlled-storage.mjs';
import { root, fileSha, cleanEnvironment } from './common.ts';

export function outputFile(path: string, values: Record<string, string>) {
  const lines: string[] = [];
  for (const [name, value] of Object.entries(values)) {
    if (!/^[a-z][a-z0-9-]*$/.test(name) || Buffer.byteLength(value) > 262144) throw new Error('product_output');
    const delimiter = `solus_${randomUUID().replaceAll('-', '')}`;
    if (value.split(/\r?\n/).includes(delimiter)) throw new Error('product_output');
    lines.push(`${name}<<${delimiter}\n${value}\n${delimiter}\n`);
  }
  writeFileSync(path, lines.join(''));
}
export function readOutputFile(path: string) {
  const lines = readFileSync(path, 'utf8').split('\n'); const values: Record<string, string> = {};
  for (let i = 0; i < lines.length - 1; i++) {
    const match = /^([a-z][a-z0-9-]*)<<(solus_[a-f0-9]{32})$/.exec(lines[i]);
    if (!match || Object.hasOwn(values, match[1])) throw new Error('product_output');
    const content: string[] = []; let closed = false;
    while (++i < lines.length) { if (lines[i] === match[2]) { closed = true; break; } content.push(lines[i]); }
    if (!closed) throw new Error('product_output'); values[match[1]] = content.join('\n');
  }
  return values;
}

export async function uploaderProof(products: any[], manifest: any, archive: Buffer, workspace: string) {
  const backend = await controlledStorage();
  try {
    const action = join(workspace, 'action'); cpSync(join(root, 'actions/logging'), action, { recursive: true });
    for (const [path, digest] of Object.entries(manifest.action.files)) {
      if (fileSha(join(workspace, 'action', path.slice('actions/logging/'.length))) !== digest) throw new Error('action_code_hash');
    }
    const key = 'sb_publishable_SYNTHETIC_CONSUMER'; const token = 'SYNTHETIC.CONSUMER.JWT'; const readToken = 'SYNTHETIC_ARTIFACT_READ';
    let mode = 'normal'; let sends = 0; let downloads = 0;
    const origin = await backend.serve(async (req: any, res: any) => {
      if (req.url === '/candidate') {
        downloads++; assert.equal(req.headers.authorization, `Bearer ${readToken}`); res.end(archive); return;
      }
      sends++; assert.equal(req.method, 'POST'); assert.equal(req.url, '/rest/v1/rpc/ingest_telemetry');
      assert.equal(req.headers.authorization, `Bearer ${token}`); assert.equal(req.headers.apikey, key);
      res.setHeader('content-type', 'application/json');
      if (mode === 'auth') { res.writeHead(401); res.end('SYNTHETIC_SERVER_CANARY'); return; }
      const pieces: Buffer[] = []; let length = 0;
      for await (const bytes of req) { length += bytes.length; assert.ok(length <= 262154); pieces.push(bytes); }
      const body = Buffer.concat(pieces).toString('utf8'); assert.ok(body.startsWith('{"event":') && body.endsWith('}'));
      try {
        const result = await backend.writer.query('select public.ingest_telemetry($1::jsonb) as status', [body.slice(9, -1)]);
        res.end(JSON.stringify(result.rows[0].status));
      } catch (error: any) { res.writeHead(error.code === '23505' ? 409 : 400); res.end('SYNTHETIC_SERVER_CANARY'); }
    });
    let sequence = 0; const scenarios: any[] = [];
    async function upload(wire: string, strict = false) {
      const id = ++sequence; const file = join(workspace, `telemetry-${id}.json`); const outputs = join(workspace, `delivery-${id}.txt`);
      writeFileSync(file, wire);
      const env = { ...cleanEnvironment(join(workspace, 'action-home')), NODE_EXTRA_CA_CERTS: backend.cert, SSL_CERT_FILE: backend.cert,
        GITHUB_WORKSPACE: workspace, RUNNER_TEMP: workspace, GITHUB_OUTPUT: outputs,
        'INPUT_TELEMETRY-JSON': wire, 'INPUT_SUPABASE-URL': origin, 'INPUT_SUPABASE-PUBLISHABLE-KEY': key, 'INPUT_SUPABASE-ACCESS-TOKEN': token,
        'INPUT_CLI-VERSION': manifest.version, 'INPUT_CLI-ARTIFACT-URL': origin + '/candidate', 'INPUT_CLI-ARTIFACT-SHA256': manifest.payload.sha256,
        'INPUT_ARTIFACT-READ-TOKEN': readToken, 'INPUT_STRICT-DELIVERY': String(strict), 'INPUT_TIMEOUT-MS': '3000', 'INPUT_RETRY-DELAY-MS': '0' };
      const child = spawn(process.execPath, [join(action, 'main.mjs')], { env, stdio: ['ignore', 'pipe', 'pipe'] });
      let stdout = ''; let stderr = ''; const timer = setTimeout(() => child.kill('SIGKILL'), 15000);
      child.stdout.on('data', bytes => { stdout += bytes; if (stdout.length > 32768) child.kill('SIGKILL'); });
      child.stderr.on('data', bytes => { stderr += bytes; if (stderr.length > 1024) child.kill('SIGKILL'); });
      let result: any;
      try { result = await new Promise((resolve, reject) => { child.once('error', reject); child.once('close', (code, signal) => resolve({ code, signal })); }); }
      finally { clearTimeout(timer); }
      assert.equal(result.signal, null); assert.equal(stderr, '');
      const ordinary = stdout.split('\n').filter(line => !line.startsWith('::add-mask::')).join('\n');
      for (const secret of [key, token, readToken, 'SYNTHETIC_SERVER_CANARY', 'SYNTHETIC_RECORD_CANARY']) assert.ok(!ordinary.includes(secret));
      assert.ok(stdout.includes(`::add-mask::${key}`)); assert.ok(stdout.includes(`::add-mask::${token}`));
      const values = Object.fromEntries(readFileSync(outputs, 'utf8').trim().split('\n').map(line => line.split('=')));
      assert.equal(result.code, strict && values['delivery-status'] === 'failed' ? 1 : 0);
      return values;
    }
    for (const product of products) {
      const originalBusiness = JSON.stringify(product.envelope.business);
      const productOutput = join(workspace, `${product.name}-product-output`);
      outputFile(productOutput, { 'business-result': originalBusiness, 'telemetry-json': product.envelope.telemetryJson });
      const captured = readOutputFile(productOutput); assert.equal(captured['business-result'], originalBusiness);
      const wire = captured['telemetry-json'];
      assert.deepEqual(await upload(wire), { 'delivery-status': 'stored', 'diagnostic-code': 'none' });
      assert.deepEqual(await upload(wire), { 'delivery-status': 'duplicate', 'diagnostic-code': 'none' });
      const record = JSON.parse(wire);
      const row = (await backend.writer.query(`select usage = ($1::jsonb)->'usage' as usage, extensions = ($1::jsonb)->'extensions' as extensions,
        execution = ($1::jsonb)->'execution' as execution, outcome = ($1::jsonb)->'outcome' as outcome from public.telemetry_events where event_id=$2`, [wire, record.eventId])).rows[0];
      assert.deepEqual(row, { usage: true, extensions: true, execution: true, outcome: true });
      assert.equal(record.usage.runs.length, 3); assert.equal(record.extensions.caller.team, 'synthetic');
      if (record.product === 'scribe') {
        assert.equal(record.usage.runs[0].inputTokens.value, '9007199254740993'); assert.equal(record.usage.runs[2].inputTokens.value, '9223372036854775807');
        assert.deepEqual(record.usage.runs[1].inputTokens, { value: '0', coverage: 'partial' }); assert.equal(record.usage.runs[1].outputTokens.value, null);
        assert.equal(record.usage.runs[0].providerCounters[0].relationship, 'includedInInput');
      } else {
        assert.equal(record.usage.runs[0].inputTokens.value, '7'); assert.equal(record.usage.runs[1].inputTokens.value, '9007199254740993');
        assert.equal(record.usage.runs[2].inventoryCoverage, 'unavailable'); assert.equal(record.usage.runs[2].inputTokens.value, null);
        assert.ok(!wire.includes('20000000-0000-0000-0000-000000000001'));
      }
      assert.equal(record.extensions[record.product].historicalAccounting.inputTokens, '100');
      assert.deepEqual(await upload(wire.replace('synthetic-1', 'collision-2')), { 'delivery-status': 'failed', 'diagnostic-code': 'identity_conflict' });
      mode = 'auth'; assert.deepEqual(await upload(wire), { 'delivery-status': 'failed', 'diagnostic-code': 'authentication_failed' });
      assert.deepEqual(await upload(wire, true), { 'delivery-status': 'failed', 'diagnostic-code': 'authentication_failed' }); mode = 'normal';
      const before = [sends, downloads]; assert.deepEqual(await upload(''), { 'delivery-status': 'skipped', 'diagnostic-code': 'input_unavailable' });
      assert.deepEqual([sends, downloads], before); assert.equal(JSON.stringify(product.envelope.business), originalBusiness);
      assert.equal(readFileSync(product.marker, 'utf8'), `${record.product}-once\n`);
      scenarios.push({ shape: record.product, realUploader: true, storageReadback: true, businessExit: product.exitCode, producerInvocations: 1 });
    }
    return { scenarios, sends, downloads, storage: 'real-disposable-UTF8-PostgreSQL-low-privilege', tls: 'verified-child-scoped-synthetic-CA' };
  } finally { await backend.close(); }
}
