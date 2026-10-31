// Real disposable PostgreSQL semantics. No Supabase account, Docker daemon or production credential.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import EmbeddedPostgres from 'embedded-postgres';
import pg from 'pg';

if (process.argv.length !== 3 || process.argv[2] !== '--local') throw new Error('Usage: node scripts/logging/verify-storage.ts --local');
if (process.getuid?.() === 0) throw new Error('Run as a non-root user; this verifier never creates OS users.');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const scratch = path.join(root, '.local/logging-storage');
await mkdir(scratch, { recursive: true });
const owned = await mkdtemp(path.join(scratch, 'verify-'));
const socket = createServer();
await new Promise<void>((resolve, reject) => { socket.once('error', reject); socket.listen(0, '127.0.0.1', resolve); });
const port = (socket.address() as { port: number }).port;
await new Promise<void>((resolve, reject) => socket.close(error => error ? reject(error) : resolve()));
// Run the pinned platform package's standard setup even when npm's install scripts are disabled.
const require = createRequire(import.meta.url);
const platformRoot = path.resolve(path.dirname(require.resolve(`@embedded-postgres/${process.platform}-${process.arch}`)), '..');
const hydration = spawnSync(process.execPath, [path.join(platformRoot, 'scripts/hydrate-symlinks.js')], { cwd: platformRoot, encoding: 'utf8', timeout: 30000 });
assert.equal(hydration.status, 0, 'Pinned PostgreSQL platform setup must succeed');
const password = randomBytes(24).toString('hex');
const engine = new EmbeddedPostgres({ databaseDir: path.join(owned, 'cluster'), user: 'postgres', password, port,
    persistent: false, createPostgresUser: false, initdbFlags: ['--encoding=UTF8', '--locale=C'], postgresFlags: ['-h', '127.0.0.1', '-c', 'unix_socket_directories='],
    onLog: () => {}, onError: () => {} });
const connections: pg.Client[] = [];
let checks = 0;
async function check(name: string, action: () => Promise<void>) {
    await action(); checks++; console.log(`PASS ${name}`);
}
async function connect(role?: 'authenticated' | 'anon', writer?: string) {
    const client = new pg.Client({ host: '127.0.0.1', port, database: 'postgres',
        user: role ? 'synthetic_client' : 'postgres', password, connectionTimeoutMillis: 5000,
        statement_timeout: 10000, query_timeout: 15000 });
    connections.push(client); await client.connect();
    if (role) {
        await client.query(`set role ${role}`);
        await client.query("select set_config('request.jwt.claim.sub', $1, false)", [writer ?? '']);
        const effective = (await client.query('select current_user as name, rolsuper, rolbypassrls from pg_roles where rolname=current_user')).rows[0];
        assert.equal(effective.name, role); assert.equal(effective.rolsuper, false); assert.equal(effective.rolbypassrls, false);
    }
    return client;
}
async function denied(client: pg.Client, sql: string, params: unknown[] = [], code = '42501') {
    await assert.rejects(client.query(sql, params), (error: unknown) => (error as { code?: string }).code === code);
}
async function ingest(client: pg.Client, event: unknown) {
    return (await client.query('select public.ingest_telemetry($1::jsonb) as status', [JSON.stringify(event)])).rows[0].status;
}
const a = '10000000-0000-0000-0000-000000000001';
const b = '10000000-0000-0000-0000-000000000002';
const fixture = JSON.parse(await readFile(path.join(root, 'tests/Solus.Logging.Tests/Supabase/telemetry-v1.json'), 'utf8'));
try {
    await engine.initialise(); await engine.start();
    const admin = await connect();
    assert.equal((await admin.query('show server_encoding')).rows[0].server_encoding, 'UTF8');
    console.log((await admin.query('select version() as version')).rows[0].version);
    await admin.query(`create role anon nologin; create role authenticated nologin;
        create role synthetic_client login noinherit password '${password}';
        grant anon, authenticated to synthetic_client;
        create schema auth; grant usage on schema auth to anon, authenticated;
        create function auth.uid() returns uuid language sql stable as $$
            select nullif(current_setting('request.jwt.claim.sub', true),'')::uuid $$;
        grant execute on function auth.uid() to anon, authenticated;
        alter default privileges in schema public grant all on tables to anon, authenticated;
        alter default privileges in schema public grant execute on functions to public, anon, authenticated;`);
    await admin.query(await readFile(path.join(root, 'database/logging/001_telemetry.sql'), 'utf8'));
    const writerA = await connect('authenticated', a), writerB = await connect('authenticated', b);
    const anonymous = await connect('anon'), noSubject = await connect('authenticated');
    await check('invoker, safe path, RLS and effective privileges override broad defaults', async () => {
        const fn = (await admin.query("select prosecdef, provolatile, proconfig from pg_proc where oid='public.ingest_telemetry(jsonb)'::regprocedure")).rows[0];
        assert.equal(fn.prosecdef, false); assert.equal(fn.provolatile, 'v'); assert.deepEqual(fn.proconfig, ['search_path=""']);
        const table = (await admin.query("select relrowsecurity, relforcerowsecurity from pg_class where oid='public.telemetry_events'::regclass")).rows[0];
        assert.equal(table.relrowsecurity, true); assert.equal(table.relforcerowsecurity, true);
        for (const column of ['writer_id', 'received_at']) {
            const privilege = (await writerA.query("select has_column_privilege(current_user,'public.telemetry_events',$1,'SELECT') as read, has_column_privilege(current_user,'public.telemetry_events',$1,'INSERT') as write", [column])).rows[0];
            assert.equal(privilege.read, true); assert.equal(privilege.write, false);
        }
        for (const role of ['anon', 'authenticated']) {
            const privileges = (await admin.query("select has_table_privilege($1,'public.telemetry_events','UPDATE') as update, has_table_privilege($1,'public.telemetry_events','DELETE') as delete, has_function_privilege($1,'public.ingest_telemetry(jsonb)','EXECUTE') as execute", [role])).rows[0];
            assert.equal(privileges.update, false); assert.equal(privileges.delete, false); assert.equal(privileges.execute, role === 'authenticated');
        }
    });
    await check('first insertion and complete own-row readback retain every typed and JSONB value', async () => {
        assert.equal(await ingest(writerA, fixture), 'stored');
        const row = (await writerA.query(`select *, to_char(occurred_at at time zone 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US') || occurred_at_residual_tick || '+00:00' as exact_time from public.telemetry_events`)).rows[0];
        assert.equal(row.writer_id, a); assert.equal(row.event_id, fixture.eventId); assert.equal(row.schema_version, 1);
        assert.equal(row.product, fixture.product); assert.equal(row.product_version, fixture.productVersion); assert.equal(row.invocation_id, fixture.invocationId);
        assert.equal(row.exact_time, fixture.occurredAt); assert.ok(row.received_at instanceof Date);
        for (const field of ['execution', 'outcome', 'usage', 'extensions']) assert.deepEqual(row[field], fixture[field]);
    });
    const original = (await writerA.query('select received_at::text, usage, extensions from public.telemetry_events')).rows[0];
    await check('duplicate and retry after ignored committed success retain first row and server receipt', async () => {
        assert.equal(await ingest(writerA, fixture), 'duplicate');
        const lost = { ...fixture, eventId: randomUUID() };
        await ingest(writerA, lost); // The successful return is deliberately discarded, as after response loss.
        assert.equal(await ingest(writerA, lost), 'duplicate');
        assert.deepEqual((await writerA.query('select received_at::text, usage, extensions from public.telemetry_events where event_id=$1', [fixture.eventId])).rows[0], original);
    });
    await check('semantic key order/numeric spelling and normalized UTC timestamp are duplicate', async () => {
        const text = JSON.stringify(fixture).replace('0.125', '0.1250').replace('+00:00', 'Z');
        const reordered = Object.fromEntries(Object.entries(JSON.parse(text)).reverse());
        assert.equal(await ingest(writerA, reordered), 'duplicate');
        // Preserve numeric spelling in raw JSON, rather than letting JS normalize it first.
        assert.equal((await writerA.query('select public.ingest_telemetry($1::jsonb) as status', [text])).rows[0].status, 'duplicate');
    });
    await check('each immutable scalar/JSON field and null-versus-absence collision fails without overwrite', async () => {
        const changes = [ ['productVersion', '2.0-test'], ['invocationId', 'other-invocation'], ['occurredAt', fixture.occurredAt.replace('9999997', '9999998')],
            ['product', 'apr'], ['execution', { ...fixture.execution, job: 'other' }], ['outcome', { kind: 'failed' }],
            ['usage', { inventoryCoverage: 'unavailable', runs: [] }], ['extensions', { scribe: { nested: [null] } }],
            ['extensions', { ...fixture.extensions, caller: { ...fixture.extensions.caller, absent: null } }] ];
        for (const [field, value] of changes) await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify({ ...fixture, [field as string]: value })], '23505');
        assert.deepEqual((await writerA.query('select received_at::text, usage, extensions from public.telemetry_events where event_id=$1', [fixture.eventId])).rows[0], original);
    });
    await check('writer-scoped same-event insertion and read isolation', async () => {
        assert.equal((await writerB.query('select count(*)::int as n from public.telemetry_events')).rows[0].n, 0);
        assert.equal(await ingest(writerB, fixture), 'stored');
        assert.equal((await writerA.query('select count(*)::int as n from public.telemetry_events where writer_id=$1', [b])).rows[0].n, 0);
        assert.equal((await writerB.query('select count(*)::int as n from public.telemetry_events')).rows[0].n, 1);
    });
    await check('anonymous, missing subject, forged writer, server receipt and update/delete are denied', async () => {
        await denied(anonymous, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify(fixture)]);
        await denied(anonymous, 'select * from public.telemetry_events');
        await denied(anonymous, 'insert into public.telemetry_events(event_id) values ($1)', [randomUUID()]);
        await denied(noSubject, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify(fixture)]);
        assert.equal((await noSubject.query('select count(*)::int as n from public.telemetry_events')).rows[0].n, 0);
        await denied(writerB, 'insert into public.telemetry_events(writer_id,event_id) values ($1,$2)', [a, randomUUID()]);
        await denied(writerA, 'insert into public.telemetry_events(received_at,event_id) values (now(),$1)', [randomUUID()]);
        await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify({ ...fixture, writer_id: a })], '22023');
        await denied(writerA, "update public.telemetry_events set product_version='forged'");
        await denied(writerA, 'delete from public.telemetry_events');
    });
    await check('malformed essential envelopes, unsupported schema and JSONB-incompatible NUL fail atomically', async () => {
        for (const change of [{ schemaVersion: 2 }, { extensions: null }, { usage: [] }, { eventId: null }, { writerId: a }])
            await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify({ ...fixture, ...change })], '22023');
        const nul = { ...fixture, eventId: randomUUID(), extensions: { scribe: { text: '\0' } } };
        await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify(nul)], '22P05');
        assert.equal((await writerA.query('select count(*)::int as n from public.telemetry_events where event_id=$1', [nul.eventId])).rows[0].n, 0);
    });
    for (const collision of [false, true]) await check(`real overlapping inserts: ${collision ? 'collision' : 'duplicate'}`, async () => {
        const first = await connect('authenticated', a), second = await connect('authenticated', a);
        const event = { ...fixture, eventId: randomUUID() };
        await first.query('begin'); assert.equal(await ingest(first, event), 'stored');
        const pid = (await second.query('select pg_backend_pid() as pid')).rows[0].pid;
        // Capture rejection immediately; do not leave a temporarily unhandled rejected promise.
        const pending = ingest(second, collision ? { ...event, productVersion: 'collision' } : event)
            .then(status => ({ status, code: null }), error => ({ status: null, code: error.code }));
        let blocked = false;
        for (let i = 0; i < 100; i++) {
            const state = (await admin.query('select wait_event_type from pg_stat_activity where pid=$1', [pid])).rows[0];
            if (state?.wait_event_type === 'Lock') { blocked = true; break; }
            await delay(10);
        }
        assert.equal(blocked, true, 'Second session must actually wait on the unresolved first insertion');
        await first.query('commit');
        const result = await pending;
        assert.deepEqual(result, collision ? { status: null, code: '23505' } : { status: 'duplicate', code: null });
        const rows = (await writerA.query('select product_version from public.telemetry_events where event_id=$1', [event.eventId])).rows;
        assert.deepEqual(rows, [{ product_version: fixture.productVersion }]);
    });
    await check('timestamp last tick and day rollover reconstruct exact UTC without rounding', async () => {
        for (const instant of ['2026-01-02T23:59:59.9999999+00:00', '2026-01-03T00:00:00.0000001+00:00', '2026-01-03T00:00:00.0000000+00:00']) {
            const event = { ...fixture, eventId: randomUUID(), occurredAt: instant };
            assert.equal(await ingest(writerA, event), 'stored');
            const exact = (await writerA.query(`select to_char(occurred_at at time zone 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US') || occurred_at_residual_tick || '+00:00' as value from public.telemetry_events where event_id=$1`, [event.eventId])).rows[0].value;
            assert.equal(exact, instant);
        }
    });
    console.log(`PASS ${checks} real PostgreSQL storage scenarios; synthetic roles only; no remote service qualification`);
} finally {
    await Promise.allSettled(connections.map(client => client.end()));
    await engine.stop();
    await rm(owned, { recursive: true, force: true });
}
