import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, chmodSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { context, credentials, hash, originalArchive, pullBearer, semantic, timeIdentity, equivalentRow,
    readURL, maskOutput, productResult, safeFailure, execute, actions, reads, selected } from './live-qualification.ts';

const A = '11111111-1111-4111-8111-111111111111', B = '22222222-2222-4222-8222-222222222222';
const S = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', P = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const origin = 'https://qualification.invalid', issuer = origin + '/auth/v1';
const jwt = (subject: string, extra = {}) => Buffer.from('{}').toString('base64url') + '.'
    + Buffer.from(JSON.stringify({ sub: subject, iss: issuer, role: 'authenticated', exp: Math.floor(Date.now() / 1000) + 3600, ...extra })).toString('base64url') + '.synthetic';
function fixture() {
    const directory = mkdtempSync(join(tmpdir(), 'solus-live-unit-'));
    const admission: any = { format: 1, operationHead: 'a'.repeat(40), actorId: 16307884,
        beforeWorkflowRunNumber: 13, expectedWorkflowRunNumber: 14,
        nonce: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', events: { scribe: S, apr: P },
        originSHA256: hash(origin), issuerSHA256: hash(issuer), writerASHA256: hash(A), writerBSHA256: hash(B),
        schemaEvidenceSHA256: 'd'.repeat(64), cleanupOwnerSHA256: 'e'.repeat(64),
        notBeforeEpochMs: Date.now() - 1000, expiresAtEpochMs: Date.now() + 1800000, budget: { posts: 7, gets: 10, rows: 3 } };
    const env: NodeJS.ProcessEnv = { GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_REPOSITORY: 'SolusQuest/solus-agent',
        GITHUB_REPOSITORY_ID: '1406091678', GITHUB_REF: 'refs/heads/codex/l1-124-live-qualification',
        GITHUB_WORKFLOW_REF: 'SolusQuest/solus-agent/.github/workflows/logging-release.yml@refs/heads/codex/l1-124-live-qualification',
        GITHUB_RUN_ID: '9001', GITHUB_RUN_NUMBER: '14', GITHUB_RUN_ATTEMPT: '1', GITHUB_ACTOR_ID: '16307884', GITHUB_ACTOR: 'synthetic-operator',
        GITHUB_SHA: admission.operationHead, WORKFLOW_SHA: admission.operationHead, RUNNER_TEMP: directory,
        GITHUB_EVENT_PATH: join(directory, 'event.json'), GITHUB_OUTPUT: join(directory, 'output'),
        SUPABASE_ORIGIN: origin, SUPABASE_PUBLISHABLE_KEY: 'sb_publishable_SYNTHETIC_L124', SUPABASE_A_JWT: jwt(A), SUPABASE_B_JWT: jwt(B) };
    const event: any = { inputs: { mode: 'qualify-live', member: 'CLI', 'source-commit': selected.source.commit,
        version: selected.version, 'expected-operation-head': admission.operationHead, 'live-qualification-admission': JSON.stringify(admission) } };
    writeFileSync(env.GITHUB_EVENT_PATH!, JSON.stringify(event)); writeFileSync(env.GITHUB_OUTPUT!, '');
    const stateDirectory = join(directory, 'solus-live-9001-' + admission.nonce);
    return { directory, stateDirectory, admission, env, event, close: () => rmSync(directory, { recursive: true, force: true }) };
}
function record(shape: string, id: string) {
    return { schemaVersion: 1, eventId: id, invocationId: shape + '-synthetic', occurredAt: '2026-01-02T03:04:05.1234567Z',
        product: shape, productVersion: 'synthetic-1', execution: { repository: 'synthetic/repository' },
        outcome: { kind: shape === 'apr' ? 'failed' : 'succeeded' },
        usage: { runs: [
            { inputTokens: { value: '9007199254740993', coverage: 'complete' } },
            { inputTokens: { value: '0', coverage: 'partial' }, outputTokens: { value: null, coverage: 'unavailable' } },
            { inputTokens: { value: '9223372036854775807', coverage: 'complete' } },
        ] }, extensions: { [shape]: { nested: [true, null, { ratio: 0.125, text: '你好😀' }] }, caller: { team: 'synthetic' } } };
}
function row(v: any, subject: string) {
    return { writer_id: subject, event_id: v.eventId, schema_version: v.schemaVersion, product: v.product,
        product_version: v.productVersion, invocation_id: v.invocationId, occurred_at: '2026-01-02T03:04:05.123456+00:00',
        occurred_at_residual_tick: 7, received_at: '2026-02-02T01:02:03+00:00',
        execution: v.execution, outcome: v.outcome, usage: v.usage, extensions: v.extensions };
}

test('one-run admission rejects a second distinct run, rerun, source/actor/ref drift and budget expansion before credential lookup', () => {
    const f = fixture();
    try {
        const proxy = new Proxy(f.env, { get(target, key) { if (String(key).includes('JWT') || key === 'GITHUB_TOKEN') throw Error('credential-read'); return target[key as string]; } });
        assert.equal(context(proxy, f.event).runNumber, 14);
        for (const change of [{ GITHUB_RUN_NUMBER: '15' }, { GITHUB_RUN_ATTEMPT: '2' }, { GITHUB_ACTOR_ID: '7' },
            { GITHUB_SHA: 'b'.repeat(40) }, { WORKFLOW_SHA: 'b'.repeat(40) }, { GITHUB_REF: 'refs/heads/main' },
            { GITHUB_REPOSITORY_ID: '7' }, { GITHUB_WORKFLOW_REF: 'wrong' }, { GITHUB_EVENT_NAME: 'push' }]) {
            assert.throws(() => context({ ...f.env, ...change }, f.event));
        }
        for (const mutate of [
            (a: any) => a.budget.posts++, (a: any) => a.budget.gets++, (a: any) => a.budget.rows++,
            (a: any) => a.originSHA256 = null, (a: any) => a.writerBSHA256 = a.writerASHA256,
            (a: any) => a.events.apr = a.events.scribe, (a: any) => a.secret = 'not-admitted',
            (a: any) => a.expiresAtEpochMs = Date.now() - 1, (a: any) => a.expectedWorkflowRunNumber++,
            (a: any) => a.notBeforeEpochMs = Date.now() + 100000,
        ]) {
            const a = structuredClone(f.admission); mutate(a);
            assert.throws(() => context(f.env, { inputs: { ...f.event.inputs, 'live-qualification-admission': JSON.stringify(a) } }));
        }
        for (const input of [{ mode: 'publish-one' }, { member: 'Solus.Logging' }, { version: '0.1.0-internal.2' },
            { 'source-commit': 'b'.repeat(40) }, { 'live-qualification-admission': '' }, { 'new-package-admission': '{}' }])
            assert.throws(() => context(f.env, { inputs: { ...f.event.inputs, ...input } }));
    } finally { f.close(); }
});

test('ordinary distinct writer metadata, exact origin and lifetime are checked without claiming JWT signature proof', () => {
    const f = fixture();
    try {
        const c = context(f.env, f.event);
        assert.equal(credentials(f.env, c).writers.A.subject, A);
        for (const extra of [{ role: 'service_role' }, { sub: B }, { iss: 'https://other.invalid/auth/v1' }, { exp: 1 }])
            assert.throws(() => credentials({ ...f.env, SUPABASE_A_JWT: jwt(A, extra) }, c));
        for (const value of ['http://qualification.invalid', origin + '/other', origin + '/?x=1', origin + '#fragment', 'https://user@qualification.invalid'])
            assert.throws(() => credentials({ ...f.env, SUPABASE_ORIGIN: value }, { ...c, admission: { ...c.admission, originSHA256: hash(value) } }));
        assert.throws(() => credentials({ ...f.env, SUPABASE_PUBLISHABLE_KEY: 'sb_secret_NOT_ALLOWED' }, c));
        const a = { ...c.admission, writerBSHA256: hash(A) };
        assert.throws(() => credentials({ ...f.env, SUPABASE_B_JWT: jwt(A) }, { ...c, admission: a }), /distinct-writers/);
    } finally { f.close(); }
});

test('archive GET admits one exact backing hop without forwarding credentials and proves complete bytes', async () => {
    const bytes = Buffer.from('synthetic transport bytes, not a published archive'); const calls: any[] = [];
    const url = 'https://nuget.pkg.github.com/SolusQuest/download/solus.logging/0.1.0-internal.1/solus.logging.0.1.0-internal.1.nupkg';
    const location = 'https://nugetregistryv2prod.blob.core.windows.net/archive?sig=SYNTHETIC_OPAQUE';
    const get: any = async (u: string, h: any) => { calls.push({ u, h }); return calls.length === 1 ? { status: 302, bytes: Buffer.alloc(0), location } : { status: 200, bytes }; };
    assert.deepEqual(await originalArchive(url, hash(bytes), bytes.length, 'synthetic', 'SYNTHETIC_TOKEN', get), bytes);
    assert.equal(calls.length, 2); assert.match(calls[0].h.Authorization, /^Basic /);
    assert.deepEqual(calls[1].h, { Accept: 'application/octet-stream' }); assert.equal(calls[1].u, location);
    for (const bad of ['https://evil.invalid/x?sig=x', 'https://user@nugetregistryv2prod.blob.core.windows.net/x?sig=x',
        'https://nugetregistryv2prod.blob.core.windows.net/x', 'https://nugetregistryv2prod.blob.core.windows.net/x?sig=x#fragment']) {
        let count = 0;
        await assert.rejects(originalArchive(url, hash(bytes), bytes.length, 'synthetic', 'SYNTHETIC_TOKEN', async () => { count++; return { status: 302, bytes: Buffer.alloc(0), location: bad }; }));
        assert.equal(count, 1);
    }
    await assert.rejects(originalArchive(url, hash(bytes), bytes.length, 'synthetic', 'SYNTHETIC_TOKEN', async () => ({ status: 200, bytes: Buffer.from('wrong') })));
    let count = 0;
    await assert.rejects(originalArchive(url, hash(bytes), bytes.length, 'synthetic', 'SYNTHETIC_TOKEN', async () => { count++; return { status: 302, bytes: Buffer.alloc(0), location }; }));
    assert.equal(count, 2); // no second backing redirect/fallback/retry
});

test('finite pull scope and mask-before-same-job-output are concrete; stale/hostile responses refuse', async () => {
    const f = fixture();
    try {
        const token = 'SYNTHETIC_PULL_CANARY';
        const body = { token, expires_in: 300, issued_at: new Date().toISOString() };
        const got = await pullBearer('synthetic', 'SYNTHETIC_GH_TOKEN', async (url, headers) => {
            assert.equal(url, 'https://ghcr.io/token?service=ghcr.io&scope=repository%3Asolusquest%2Fsolus-logging-cli%3Apull');
            assert.deepEqual(Object.keys(headers).sort(), ['Accept', 'Authorization']); return { status: 200, bytes: Buffer.from(JSON.stringify(body)) };
        });
        const writes: string[] = [];
        maskOutput(got, f.env, value => { assert.equal(readFileSync(f.env.GITHUB_OUTPUT!, 'utf8'), ''); writes.push(value); });
        assert.equal(writes[0], '::add-mask::' + token + '\n');
        assert.equal(readFileSync(f.env.GITHUB_OUTPUT!, 'utf8'), 'bearer=' + token + '\n');
        for (const value of [{ ...body, token: 'bad\ntoken' }, { ...body, expires_in: 0 }, { ...body, expires_in: 3601 },
            { ...body, issued_at: '1970-01-01T00:00:00Z' }, { ...body, token: 1 }])
            await assert.rejects(pullBearer('synthetic', 'SYNTHETIC_GH_TOKEN', async () => ({ status: 200, bytes: Buffer.from(JSON.stringify(value)) })));
        await assert.rejects(pullBearer('synthetic', 'SYNTHETIC_GH_TOKEN', async () => ({ status: 302, bytes: Buffer.alloc(0), location: 'https://evil.invalid' })));
    } finally { f.close(); }
});

test('Distribution optional fields and opaque aliases accept finite standard profiles without decoding', async () => {
    const instant = Date.parse('2026-11-03T12:00:00Z');
    const token = 'opaque.not-a-jwt~+/=';
    const values = [{ token }, { access_token: token }, { token, access_token: token },
        { token, expires_in: 60 }, { token, expires_in: 3600 },
        { token, issued_at: '2026-11-03T12:00:00Z' },
        { token, issued_at: '2026-11-03T13:00:00+01:00', expires_in: 60 }];
    for (const value of values) {
        let calls = 0;
        assert.equal(await pullBearer('synthetic', 'SYNTHETIC_GITHUB', async (url, headers, cap, timeout) => {
            calls++; assert.equal(new URL(url).origin, 'https://ghcr.io');
            assert.equal(new URL(url).searchParams.get('scope'), 'repository:solusquest/solus-logging-cli:pull');
            assert.deepEqual(Object.keys(headers).sort(), ['Accept', 'Authorization']);
            assert.equal(cap, 16384); assert.equal(timeout, 10000);
            return { status: 200, bytes: Buffer.from(JSON.stringify(value)) };
        }, () => instant), token);
        assert.equal(calls, 1);
    }
    // Receipt time is after the awaited exchange; transport time is not charged twice.
    let clock = instant;
    assert.equal(await pullBearer('synthetic', 'SYNTHETIC_GITHUB', async () => {
        clock += 120000; return { status: 200, bytes: Buffer.from(JSON.stringify({ token })) };
    }, () => clock), token);
});

test('finite pull expiry uses explicit issue time and strictly greater than five seconds at handoff', async () => {
    const instant = Date.parse('2026-11-03T12:00:00Z');
    async function check(issued: number, accepted: boolean, elapsed = 0) {
        const value = { token: 'synthetic', expires_in: 60, issued_at: new Date(issued).toISOString() };
        let clockCalls = 0;
        const call = pullBearer('synthetic', 'SYNTHETIC_GITHUB', async () => ({ status: 200, bytes: Buffer.from(JSON.stringify(value)) }),
            () => instant + (clockCalls++ === 0 ? 0 : elapsed));
        if (accepted) assert.equal(await call, 'synthetic');
        else await assert.rejects(call, /pull-(?:expiry|issued-at)$/);
    }
    await check(instant - 54999, true);
    await check(instant - 55000, false);
    await check(instant - 60000, false);
    await check(instant - 54999, false, 1);
    await check(instant + 60000, true);
    await check(instant + 60001, false);
});

test('all pull parser refusals are closed and secret-safe with no fallback or retry', async () => {
    const canary = 'PRIVATE_RESPONSE_CANARY_NEVER_PRINT';
    const instant = Date.parse('2026-11-03T12:00:00Z');
    const invalid = [null, [], false, 1, canary, {}, { token: '' }, { token: 1 },
        { token: null, access_token: 'valid' }, { token: 'valid', access_token: canary },
        { token: 'valid', access_token: null }, { token: canary + '\n' }, { token: canary + '\u2028' },
        { token: canary + '\x00' }, { token: canary.repeat(300) },
        ...[null, '60', 0, 59, 60.1, -1, 3601, Number.MAX_SAFE_INTEGER + 1].map(expires_in => ({ token: canary, expires_in })),
        ...[null, 0, canary, '2026-02-30T00:00:00Z', '2026-11-03', '2026-11-03T24:00:00Z',
            '2026-11-03T12:00:00+99:00', '1970-01-01T00:00:00Z'].map(issued_at => ({ token: canary, issued_at }))];
    for (const value of invalid) {
        let calls = 0;
        await assert.rejects(pullBearer('synthetic', 'SYNTHETIC_GITHUB', async () => {
            calls++; return { status: 200, bytes: Buffer.from(JSON.stringify(value)) };
        }, () => instant), (error: any) => {
            assert.match(error.message, /^pull-(json|bearer|lifetime|issued-at|expiry)$/);
            assert.ok(!String(error.stack).includes(canary));
            const safe = JSON.stringify(safeFailure(error)); assert.ok(!safe.includes(canary));
            assert.equal(safeFailure(error).reason, error.message); return true;
        });
        assert.equal(calls, 1);
    }
    for (const body of [Buffer.from(canary + '{'), Buffer.alloc(16385, 'x')]) {
        let calls = 0;
        await assert.rejects(pullBearer('synthetic', 'SYNTHETIC_GITHUB', async () => {
            calls++; return { status: 200, bytes: body };
        }, () => instant), /pull-(?:json|response)$/);
        assert.equal(calls, 1);
    }
});

test('complete row oracle preserves decimal strings/null/missing/arrays/extensions/time residual and typed identity', () => {
    const v = record('scribe', S), r = row(v, A);
    equivalentRow(r, v, A);
    assert.equal(timeIdentity(r.occurred_at, 7), timeIdentity(v.occurredAt));
    assert.equal(semantic({ a: 1, b: null }, { b: null, a: 1 }), true);
    for (const pair of [[null, '0'], [{ a: null }, {}], [['1', '2'], ['2', '1']], ['9007199254740993', 9007199254740993]])
        assert.equal(semantic(pair[0], pair[1]), false);
    for (const mutate of [(x: any) => x.writer_id = B, (x: any) => x.event_id = P, (x: any) => x.schema_version = '1',
        (x: any) => x.extra = 1, (x: any) => x.occurred_at_residual_tick = 0,
        (x: any) => x.usage.runs[0].inputTokens.value = 9007199254740993,
        (x: any) => delete x.usage.runs[1].outputTokens.value,
        (x: any) => x.extensions.scribe.nested.reverse()]) {
        const bad = structuredClone(r); mutate(bad); assert.throws(() => equivalentRow(bad, v, A));
    }
    assert.throws(() => timeIdentity('not-a-time', 0));
    const q = new URL(readURL(origin, A, [S, P], true));
    assert.equal(q.searchParams.get('select'), 'writer_id,event_id'); assert.equal(q.searchParams.get('limit'), '3');
    assert.equal(q.searchParams.get('writer_id'), 'eq.' + A); assert.equal(q.searchParams.get('event_id'), `in.(${S},${P})`);
});

test('APR actual exit7 remains separate from job success and bounded failures cannot leak arbitrary messages', () => {
    const v = record('apr', P), stdout = Buffer.from(JSON.stringify({ business: { status: 'failed' }, telemetryJson: JSON.stringify(v), telemetryDiagnostic: 'None' }));
    assert.equal(productResult({ status: 7, signal: null, stdout }, 'apr', P).businessExit, 7);
    assert.throws(() => productResult({ status: 0, signal: null, stdout }, 'apr', P));
    assert.throws(() => productResult({ status: 7, signal: 'SIGKILL', stdout }, 'apr', P));
    assert.throws(() => productResult({ status: 7, signal: null, stdout: Buffer.from(JSON.stringify({ business: { status: 'failed' }, telemetryJson: null })) }, 'apr', P));
    const canary = 'synthetic-private-token-and-path';
    const a = safeFailure({ message: canary, status: 31, signal: 'SIGKILL', output: canary + ' NU1301 MSB1009 CS2008 _CS1001 NU12345' });
    assert.equal(JSON.stringify(a).includes(canary), false); assert.equal(a.status, 31); assert.equal(a.signal, 'SIGKILL');
    assert.deepEqual(a.codes, ['NU1301', 'MSB1009', 'CS2008']); assert.equal(a.reason, 'controlled-failure');
    assert.equal(safeFailure({ message: 'read-http', httpStatus: 403 }).httpStatus, 403);
    assert.equal(safeFailure({ message: 'archive-http', httpStatus: '403' }).httpStatus, null);
});

test('production Action-result verifier requires every fixed status, including expected collision failures, without advancing on uncertainty', async () => {
    for (let index = 1; index <= 7; index++) {
        const f = fixture();
        try {
            await execute('guard', undefined, f.env);
            const path = join(f.stateDirectory, 'state.json'), s = JSON.parse(readFileSync(path, 'utf8'));
            s.validated = true; s.credentialsChecked = true; s.actions = index - 1;
            writeFileSync(path, JSON.stringify(s)); writeFileSync(join(f.stateDirectory, 'action-' + index + '.admitted'), 'attempted\n');
            const fixed = actions[index - 1];
            for (const wrong of [{ DELIVERY_STATUS: '' }, { DELIVERY_STATUS: 'unknown', DIAGNOSTIC_CODE: 'none' },
                { DELIVERY_STATUS: fixed[2], DIAGNOSTIC_CODE: 'transport_failure' }]) {
                await assert.rejects(execute('verify', String(index), { ...f.env, ...wrong }));
                assert.equal(JSON.parse(readFileSync(path, 'utf8')).actions, index - 1);
            }
            await execute('verify', String(index), { ...f.env, DELIVERY_STATUS: fixed[2], DIAGNOSTIC_CODE: fixed[3] });
            assert.equal(JSON.parse(readFileSync(path, 'utf8')).actions, index);
            await assert.rejects(execute('verify', String(index), { ...f.env, DELIVERY_STATUS: fixed[2], DIAGNOSTIC_CODE: fixed[3] }));
        } finally { f.close(); }
    }
});

test('production GET state machine proves all ten reads and rejects unknown/malformed/conflicting views without replay', async () => {
    const f = fixture();
    try {
        await execute('guard', undefined, f.env);
        const path = join(f.stateDirectory, 'state.json');
        let s = JSON.parse(readFileSync(path, 'utf8')); s.validated = true; s.credentialsChecked = true;
        const records = { scribe: record('scribe', S), apr: record('apr', P) };
        for (const [name, v] of Object.entries(records)) writeFileSync(join(f.stateDirectory, name + '.json'), JSON.stringify(v));
        writeFileSync(path, JSON.stringify(s));
        let requests = 0;
        for (let index = 1; index <= 10; index++) {
            s = JSON.parse(readFileSync(path, 'utf8')); s.actions = reads[index - 1][0]; writeFileSync(path, JSON.stringify(s));
            await execute('read', String(index), f.env, async (url, headers, cap, timeout) => {
                requests++; const q = new URL(url); assert.equal(q.origin, origin); assert.equal(q.pathname, '/rest/v1/telemetry_events');
                assert.equal(cap, 65536); assert.equal(timeout, 10000); assert.equal(headers['Accept-Profile'], 'public');
                if (index <= 2) { assert.equal(q.searchParams.get('select'), 'writer_id,event_id'); assert.equal(q.searchParams.get('event_id'), `in.(${S},${P})`); }
                const n = reads[index - 1][3];
                const value = n === 0 ? [] : n === 1 ? [row(records.scribe, B)] : [row(records.scribe, A), row(records.apr, A)];
                return { status: 200, type: 'application/json; charset=utf-8', bytes: Buffer.from(JSON.stringify(value)) };
            });
        }
        assert.equal(requests, 10);
        s = JSON.parse(readFileSync(path, 'utf8')); assert.equal(s.reads, 10); assert.equal(s.observations.length, 10);
        await assert.rejects(execute('read', '10', f.env, async () => { throw Error('must-not-read'); }));
        assert.equal(actions.length, 7); assert.equal(reads.length, 10);
    } finally { f.close(); }
    for (const response of [{ status: 403, bytes: Buffer.alloc(0) }, { status: 302, bytes: Buffer.alloc(0), location: 'https://evil.invalid' },
        { status: 200, type: 'application/json', bytes: Buffer.from('[') },
        { status: 200, type: 'application/json', bytes: Buffer.from(JSON.stringify([{ writer_id: A, event_id: S }])) }]) {
        const f = fixture();
        try {
            await execute('guard', undefined, f.env); const path = join(f.stateDirectory, 'state.json');
            const s = JSON.parse(readFileSync(path, 'utf8')); s.validated = true; s.credentialsChecked = true; writeFileSync(path, JSON.stringify(s));
            let calls = 0;
            await assert.rejects(execute('read', '1', f.env, async () => { calls++; return response; }));
            await assert.rejects(execute('read', '2', f.env, async () => { calls++; throw Error('must-not-read'); }));
            await assert.rejects(execute('pull', '1', f.env, async () => { calls++; throw Error('must-not-read'); }));
            assert.equal(calls, 1);
        } finally { f.close(); }
    }
});

test('real producer child exits7 while immutable output and once marker survive; no network or credentials enter the child', async () => {
    const f = fixture();
    try {
        await execute('guard', undefined, f.env);
        const path = join(f.stateDirectory, 'state.json'), bin = join(f.directory, 'bin'); mkdirSync(bin);
        // A credential-free process-boundary fixture, not a package installation or actual APR proof.
        const shim = join(bin, 'dotnet');
        writeFileSync(shim, `#!${process.execPath}\nconst fs=require('node:fs');
if(Object.keys(process.env).some(k=>/TOKEN|JWT|KEY|SUPABASE/.test(k)))process.exit(99);
fs.appendFileSync(process.argv[5],'apr-once\\n');
console.log(JSON.stringify(${JSON.stringify({ business: { status: 'failed' }, telemetryDiagnostic: 'None', telemetryJson: JSON.stringify(record('apr', P)) })}));
process.exit(7);\n`); chmodSync(shim, 0o700);
        const s = JSON.parse(readFileSync(path, 'utf8'));
        s.installed.apr = { directory: f.directory, executable: 'synthetic.dll', env: { PATH: bin } }; writeFileSync(path, JSON.stringify(s));
        const helper = new URL('./live-qualification.ts', import.meta.url);
        const result = spawnSync(process.execPath, [helper.pathname, 'emit', 'apr'], { env: f.env, encoding: 'utf8', timeout: 5000 });
        assert.equal(result.status, 7); assert.equal(result.signal, null); assert.equal(result.stderr, '');
        assert.equal(readFileSync(join(f.stateDirectory, 'apr.invocations'), 'utf8'), 'apr-once\n');
        assert.equal(JSON.parse(readFileSync(path, 'utf8')).products.apr.businessExit, 7);
        assert.match(readFileSync(f.env.GITHUB_OUTPUT!, 'utf8'), /^telemetry-json=/);
        assert.equal(JSON.parse(readFileSync(join(f.stateDirectory, 'apr-collision.json'), 'utf8')).productVersion, 'synthetic-collision-124');
        const repeated = spawnSync(process.execPath, [helper.pathname, 'emit', 'apr'], { env: f.env, encoding: 'utf8', timeout: 5000 });
        assert.equal(repeated.status, 1); assert.equal(readFileSync(join(f.stateDirectory, 'apr.invocations'), 'utf8'), 'apr-once\n');
        assert.equal(repeated.stderr.includes(f.directory), false);
    } finally { f.close(); }
});

test('new isolated job freezes every prior job and exact helper/lock while exercising pre-credential entry and fixed Action/read sequence', () => {
    const workflow = readFileSync('.github/workflows/logging-release.yml', 'utf8');
    const jobs = Object.fromEntries([...workflow.matchAll(/^  ([a-z][a-z-]+):\n/gm)].map((m, i, all) =>
        [m[1], workflow.slice(m.index!, all[i + 1]?.index ?? workflow.length)]));
    const frozen: Record<string, string> = {
        prepare: 'e62965534bb270a2f278ad0648e86d0d65e98f5fb292b6f6a40027c940725a20',
        'inspect-cli-upload': 'b9ba69ecb3567ce6ed5b3fde6e717bcde5579ea119ea343acf5fe0d1973c94be',
        'reconcile-contracts': '23207795f24da0c0cfe873b77771bd9923900a1cdf6b8940ecf735200ff65986',
        'exact-operation': '7d67fcc915bb05a02bc879aa9cbef59921732b115cc4c9eee09928efeb7a4572',
        'inspect-cli-readback': '6b994a7f5bf6261ef2de6a464539972e7ef557ae54aaddfb8d99a109a1b7b6e3',
    };
    for (const [name, digest] of Object.entries(frozen)) assert.equal(hash(jobs[name]), digest, name);
    const frozenFiles: Record<string, string> = {
        'accepted-candidate.json': 'c679d7273d49b4ee7ad1206d7c89ffb8bacd35b47195f2702ffa7a4787f19cf5',
        'api-baseline.json': 'a0d44f92c5de8c179e26bd4ccf3cde1979801608170c71ae40c15316f3f01219',
        'publish-exact.ts': '68e3cd171f600be5263e32fbfdb07e8997e9fa247284397012dd2e37d59bfd29',
        'nuget-resource.ts': '0a01a68d677ae8e3aec4ff54c3e8d9d935d9fe999fa6a74be41d0ddcc0c137e5',
        'reconcile-contracts.ts': '1314e5c7d6c6a4556fcfe987a03e742915a710643b9ac9e04e4a0e148b08ef4d',
        'inspect-cli-upload.ts': 'a85c0f034653f0db9f208c28cd262e72081194997947cdd894cc09888b9a47a6',
        'inspect-cli-readback.ts': 'a71aa8ab80177f420024d12afdb3edcd8c52a8d4a43f96cd68ddf09f62d0bbeb',
        'pack-exact-child.ts': 'cd355d204dc0b9d09f32e3bed6dee369ef8d0d9f8bd13849eb3d912f4df97b60',
    };
    for (const [name, digest] of Object.entries(frozenFiles)) assert.equal(hash(readFileSync('scripts/logging/' + name)), digest, name);
    const job = jobs['qualify-live']; assert.match(job, /contents: read\n      packages: read/);
    assert.doesNotMatch(job, /packages: write|publish-exact|pack\.ts|workflow_dispatch:|secrets: inherit/);
    assert.equal((job.match(/uses: SolusQuest\/solus-agent\/actions\/logging@9d2f3777401d71fdea84ce350dd5bd6240acb7a8/g) ?? []).length, 7);
    assert.equal((job.match(/run: node scripts\/logging\/live-qualification.ts read \d+/g) ?? []).length, 10);
    for (let i = 1; i <= 7; i++) {
        const send = job.split('        id: send_' + i + '\n')[1].split('      - name:')[0];
        assert.match(send, new RegExp(`steps.pull_${i}.outcome == 'success'`));
        assert.ok(send.includes('artifact-read-token: ${{ steps.pull_' + i + '.outputs.bearer }}'));
        assert.match(send, /maximum-attempts: '1'/); assert.match(send, /strict-delivery: 'false'/);
        assert.match(send, /retry-delay-ms: '0'/); assert.ok(send.includes(selected.payload.sha256));
    }
    assert.match(job, /id: apr\n        continue-on-error: true/);
    assert.match(job, /steps\.apr\.outcome == 'failure'/); assert.doesNotMatch(job, /^    outputs:/m);
    assert.match(job, /path: \$\{\{ runner.temp \}\}\/solus-logging-live-proof.json/);
    const entry = job.split("          python3 - <<'PYGUARD'\n")[1].split('\n          PYGUARD')[0].split('\n').map(s => s.slice(10)).join('\n');
    const f = fixture();
    try {
        const run = (env: NodeJS.ProcessEnv) => spawnSync('python3', ['-c', entry], { env: { PATH: process.env.PATH, ...env }, encoding: 'utf8', timeout: 5000 });
        assert.equal(run(f.env).status, 0);
        for (const delta of [{ GITHUB_RUN_NUMBER: '15' }, { GITHUB_RUN_ATTEMPT: '2' }, { GITHUB_SHA: 'b'.repeat(40) }, { GITHUB_ACTOR_ID: '1' }]) {
            const r = run({ ...f.env, ...delta }); assert.equal(r.status, 1); assert.match(r.stderr.trim(), /(?:^|\n)LIVE124_ENTRY_REFUSED$/);
            assert.equal(r.stderr.includes(f.directory), false);
        }
    } finally { f.close(); }
});
