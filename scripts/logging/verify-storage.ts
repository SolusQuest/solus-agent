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
        alter default privileges in schema public grant all on sequences to public, anon, authenticated;
        alter default privileges in schema public grant execute on functions to public, anon, authenticated;`);
    await admin.query(await readFile(path.join(root, 'database/logging/001_telemetry.sql'), 'utf8'));
    const writerA = await connect('authenticated', a), writerB = await connect('authenticated', b);
    const anonymous = await connect('anon'), noSubject = await connect('authenticated');
    await check('invoker, safe path, RLS and effective privileges override broad defaults', async () => {
        const fn = (await admin.query("select prosecdef, provolatile, proconfig from pg_proc where oid='public.ingest_telemetry(jsonb)'::regprocedure")).rows[0];
        assert.equal(fn.prosecdef, false); assert.equal(fn.provolatile, 'v'); assert.deepEqual(fn.proconfig, ['search_path=""']);
        const table = (await admin.query("select relrowsecurity, relforcerowsecurity from pg_class where oid='public.telemetry_events'::regclass")).rows[0];
        assert.equal(table.relrowsecurity, true); assert.equal(table.relforcerowsecurity, true);
        for (const column of ['id', 'writer_id', 'received_at']) {
            const privilege = (await writerA.query("select has_column_privilege(current_user,'public.telemetry_events',$1,'SELECT') as read, has_column_privilege(current_user,'public.telemetry_events',$1,'INSERT') as write", [column])).rows[0];
            assert.equal(privilege.read, true); assert.equal(privilege.write, false);
        }
        for (const role of ['anon', 'authenticated']) {
            const privileges = (await admin.query("select has_table_privilege($1,'public.telemetry_events','UPDATE') as update, has_table_privilege($1,'public.telemetry_events','DELETE') as delete, has_function_privilege($1,'public.ingest_telemetry(jsonb)','EXECUTE') as execute", [role])).rows[0];
            assert.equal(privileges.update, false); assert.equal(privileges.delete, false); assert.equal(privileges.execute, role === 'authenticated');
        }
    });
    await check('database-owned bigint identity is unique alongside the writer/event primary key', async () => {
        const identity = await admin.query(`select data_type, identity_generation from information_schema.columns
            where table_schema='public' and table_name='telemetry_events' and column_name='id'`);
        assert.deepEqual(identity.rows, [{ data_type: 'bigint', identity_generation: 'ALWAYS' }]);
        const keys = await admin.query(`select pg_get_constraintdef(oid) as definition from pg_constraint
            where conrelid='public.telemetry_events'::regclass and contype in ('p','u') order by contype`);
        assert.deepEqual(keys.rows.map(key => key.definition), ['PRIMARY KEY (writer_id, event_id)', 'UNIQUE (id)', 'UNIQUE (id, writer_id)']);
        for (const client of [anonymous, writerA]) {
            await denied(client, "select last_value from public.telemetry_events_id_seq");
            await denied(client, "select nextval('public.telemetry_events_id_seq')");
            await denied(client, "select setval('public.telemetry_events_id_seq', 1)");
        }
        await denied(writerA, 'insert into public.telemetry_events(id) overriding system value values (42)');
        await denied(writerA, 'update public.telemetry_events set id=default');
        await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify({ ...fixture, id: 42 })], '22023');
    });
    await check('first insertion and complete own-row readback retain every typed and JSONB value', async () => {
        assert.equal(await ingest(writerA, fixture), 'stored');
        const row = (await writerA.query(`select *, public.telemetry_event_json(e) as payload, to_char(occurred_at at time zone 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US') || occurred_at_residual_tick || '+00:00' as exact_time from public.telemetry_events e`)).rows[0];
        assert.match(row.id, /^[0-9]+$/); assert.equal(row.writer_id, a); assert.equal(row.event_id, fixture.eventId); assert.equal(row.schema_version, 1);
        assert.equal(row.product, fixture.product); assert.equal(row.product_version, fixture.productVersion); assert.equal(row.invocation_id, fixture.invocationId);
        assert.equal(row.exact_time, fixture.occurredAt); assert.ok(row.received_at instanceof Date);
        for (const field of ['execution', 'outcome', 'usage', 'extensions']) assert.deepEqual(row.payload[field], fixture[field]);
    });
    const original = (await writerA.query('select id, received_at::text, public.telemetry_event_json(e) as content from public.telemetry_events e')).rows[0];
    await check('duplicate and retry after ignored committed success retain first row and server receipt', async () => {
        assert.equal(await ingest(writerA, fixture), 'duplicate');
        const lost = { ...fixture, eventId: randomUUID() };
        await ingest(writerA, lost); // The successful return is deliberately discarded, as after response loss.
        assert.equal(await ingest(writerA, lost), 'duplicate');
        assert.deepEqual((await writerA.query('select id, received_at::text, public.telemetry_event_json(e) as content from public.telemetry_events e where event_id=$1', [fixture.eventId])).rows[0], original);
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
        assert.deepEqual((await writerA.query('select id, received_at::text, public.telemetry_event_json(e) as content from public.telemetry_events e where event_id=$1', [fixture.eventId])).rows[0], original);
    });
    await check('writer-scoped same-event insertion and read isolation', async () => {
        assert.equal((await writerB.query('select count(*)::int as n from public.telemetry_events')).rows[0].n, 0);
        assert.equal(await ingest(writerB, fixture), 'stored');
        assert.equal((await writerA.query('select count(*)::int as n from public.telemetry_events where writer_id=$1', [b])).rows[0].n, 0);
        const ownRows = (await writerB.query('select id from public.telemetry_events')).rows;
        assert.equal(ownRows.length, 1);
        assert.match(ownRows[0].id, /^[0-9]+$/);
        assert.notEqual(ownRows[0].id, original.id);
        assert.equal((await writerA.query('select id from public.telemetry_events where id=$1', [ownRows[0].id])).rowCount, 0);
        assert.equal((await writerB.query('select id from public.telemetry_events where id=$1', [original.id])).rowCount, 0);
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
        const winner = (await first.query('select id, product_version from public.telemetry_events where event_id=$1', [event.eventId])).rows;
        assert.equal(winner.length, 1);
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
        const rows = (await writerA.query('select id, product_version from public.telemetry_events where event_id=$1', [event.eventId])).rows;
        assert.equal(winner[0].product_version, fixture.productVersion);
        assert.deepEqual(rows, winner);
    });
    await check('timestamp last tick and day rollover reconstruct exact UTC without rounding', async () => {
        for (const instant of ['2026-01-02T23:59:59.9999999+00:00', '2026-01-03T00:00:00.0000001+00:00', '2026-01-03T00:00:00.0000000+00:00']) {
            const event = { ...fixture, eventId: randomUUID(), occurredAt: instant };
            assert.equal(await ingest(writerA, event), 'stored');
            const exact = (await writerA.query(`select to_char(occurred_at at time zone 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US') || occurred_at_residual_tick || '+00:00' as value from public.telemetry_events where event_id=$1`, [event.eventId])).rows[0].value;
            assert.equal(exact, instant);
        }
    });
    const multiple = JSON.parse(await readFile(path.join(root, 'tests/Solus.Logging.Tests/Supabase/telemetry-multiple-runs-v1.json'), 'utf8'));
    const fresh = (source = multiple) => ({ ...structuredClone(source), eventId: randomUUID() });
    async function snapshot(client: pg.Client, eventId: string) {
        const events = (await client.query('select id, received_at::text, public.telemetry_event_json(e) as payload from public.telemetry_events e where event_id=$1', [eventId])).rows;
        const runs = (await client.query('select r.* from public.telemetry_runs r join public.telemetry_events e on e.id=r.event_row_id where e.event_id=$1 order by r.ordinal', [eventId])).rows;
        return { events, runs };
    }
    await check('three ordered runs retain typed bigint, independent coverage, tools and complete payload', async () => {
        assert.equal(await ingest(writerA, multiple), 'stored');
        const stored = await snapshot(writerA, multiple.eventId);
        assert.deepEqual(stored.events[0].payload, multiple);
        assert.deepEqual(stored.runs.map(r => r.ordinal), [1, 2, 3]);
        assert.deepEqual(stored.runs.map(r => r.execution_id), multiple.usage.runs.map(r => r.executionId));
        assert.equal(stored.runs[0].input_tokens, '9223372036854775807');
        assert.equal(stored.runs[1].input_tokens, '0'); assert.equal(stored.runs[1].input_coverage, 'partial');
        assert.equal(stored.runs[1].output_tokens, null); assert.equal(stored.runs[1].output_coverage, 'overflow');
        assert.equal(stored.runs[1].tools_present, false); assert.equal(stored.runs[2].tools_present, true);
        assert.equal(stored.runs[2].physical_attempts, null);
        for (const [column, field] of [['provider_counters', 'providerCounters'], ['accounting', 'accounting'], ['estimated_costs', 'estimatedCosts']])
            assert.deepEqual(stored.runs[0][column], multiple.usage.runs[0][field]);
        assert.equal(await ingest(writerA, multiple), 'duplicate');
        assert.deepEqual(await snapshot(writerA, multiple.eventId), stored);
    });
    await check('every projected run fact, ordering and retained JSON participates in immutable equality', async () => {
        const mutations: ((event: any) => void)[] = [
            e => e.usage.runs.reverse(), e => e.usage.runs.pop(),
            e => e.usage.runs[0].executionId = randomUUID(),
            e => e.usage.runs[0].inputTokens.value = '9007199254740993',
            e => e.usage.runs[0].inputTokens.coverage = 'partial',
            e => e.usage.runs[0].outputTokens = { value: '0', coverage: 'complete' },
            e => e.usage.runs[1].inventoryCoverage = 'complete',
            e => e.usage.runs[0].logicalCalls = '2', e => e.usage.runs[0].physicalAttempts = '3',
            ...['admitted', 'invoked', 'reservedUnstarted', 'releasedUnstarted'].map(key => (e: any) => e.usage.runs[0].tools[key] = '4'),
            e => e.usage.runs[0].providerSettlement = 'settled',
            e => e.usage.runs[0].providerCounters[0].relationship = 'includedInOutput',
            e => e.usage.runs[0].accounting[0].input.value = '-2',
            e => e.usage.runs[0].accounting[0].input.knowledge = 'provisional',
            e => e.usage.runs[0].estimatedCosts[0].amount = '0.12346',
            e => e.usage.runs[0].estimatedCosts[0].currency = 'EUR',
            e => delete e.usage.runs[1].tools, e => delete e.usage.runs[2].logicalCalls,
            e => e.usage.runs[1].accounting = null, e => delete e.usage.runs[1].providerSettlement,
            e => delete e.execution.job, e => e.execution.job = null, e => delete e.outcome.code,
        ];
        const before = await snapshot(writerA, multiple.eventId);
        for (const mutate of mutations) {
            const changed = structuredClone(multiple); mutate(changed);
            await assert.rejects(ingest(writerA, changed), (error: any) => error.code === '23505' && error.message === 'telemetry_identity_conflict');
        }
        assert.deepEqual(await snapshot(writerA, multiple.eventId), before);
        const withLetters = fresh(); withLetters.usage.runs[0].executionId = 'abcdefab-1234-4321-abcd-123456789abc';
        assert.equal(await ingest(writerA, withLetters), 'stored');
        withLetters.usage.runs[0].executionId = withLetters.usage.runs[0].executionId.toUpperCase();
        await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify(withLetters)], '23505');
        // Reordering retained arrays is meaningful, even though JSON object key order is not.
        const twoCosts = fresh(); twoCosts.usage.runs[0].estimatedCosts.push({ source: 'providerClaim', currency: 'EUR', amount: '1.2345678901234567890123456789', knowledge: 'known' });
        assert.equal(await ingest(writerA, twoCosts), 'stored');
        assert.deepEqual((await snapshot(writerA, twoCosts.eventId)).events[0].payload, twoCosts);
        twoCosts.usage.runs[0].estimatedCosts.reverse();
        await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify(twoCosts)], '23505');
    });
    await check('optional absent, explicit null, empty collections and supplied all-null tools roundtrip', async () => {
        for (const variant of ['absent', 'null', 'empty']) {
            const event = fresh(); event.execution = {}; event.outcome = { kind: 'unknown' };
            const run = event.usage.runs[2];
            for (const key of ['logicalCalls', 'physicalAttempts', 'tools', 'providerSettlement', 'providerCounters', 'accounting', 'estimatedCosts']) delete run[key];
            if (variant !== 'absent') Object.assign(run, { logicalCalls: null, physicalAttempts: null, tools: null, providerSettlement: null,
                providerCounters: variant === 'empty' ? [] : null, accounting: variant === 'empty' ? [] : null, estimatedCosts: variant === 'empty' ? [] : null });
            if (variant === 'empty') run.tools = { admitted: null, invoked: null, reservedUnstarted: null, releasedUnstarted: null };
            assert.equal(await ingest(writerA, event), 'stored');
            assert.deepEqual((await snapshot(writerA, event.eventId)).events[0].payload, event);
        }
    });
    await check('malformed projections and later child failures roll back the entire event', async () => {
        const failures: [((e: any) => void), string][] = [
            [e => e.execution.extra = 1, '22023'], [e => e.outcome.extra = 1, '22023'],
            [e => e.usage.extra = 1, '22023'], [e => e.execution.workflowRunId = 123, '22023'],
            [e => e.usage.runs[1].extra = 1, '22023'], [e => e.usage.runs[1].inputTokens.extra = 1, '22023'],
            [e => e.usage.runs[0].tools.extra = 1, '22023'], [e => delete e.usage.runs[0].tools.invoked, '22023'],
            [e => e.usage.runs[1].inputTokens.value = '9223372036854775808', '22023'],
            [e => e.usage.runs[1].logicalCalls = 1, '22023'], [e => e.usage.runs[1].logicalCalls = '01', '22023'],
            [e => e.usage.runs[1].executionId = e.usage.runs[0].executionId, '22023'],
            [e => e.usage.runs[1].inputTokens = { value: null, coverage: 'complete' }, '23514'],
            [e => e.usage.runs[1].providerCounters = {}, '23514'],
            [e => e.usage.runs[1].inventoryCoverage = null, '22023'],
            [e => e.usage.runs[1].providerSettlement = 'invented', '23514'],
            [e => e.usage.runs[1].inputTokens.coverage = 'invented', '23514'],
        ];
        for (const [mutate, code] of failures) {
            const event = fresh(); mutate(event);
            await denied(writerA, 'select public.ingest_telemetry($1::jsonb)', [JSON.stringify(event)], code);
            assert.deepEqual(await snapshot(writerA, event.eventId), { events: [], runs: [] });
        }
        assert.equal((await admin.query('select count(*)::int n from public.telemetry_runs r left join public.telemetry_events e on e.id=r.event_row_id where e.id is null')).rows[0].n, 0);
    });
    const writable = async (table: string) => (await writerA.query(`select attname from pg_attribute where attrelid=$1::regclass and attnum>0 and not attisdropped and has_column_privilege(current_user,$1,attname,'INSERT') order by attnum`, [table])).rows.map(r => r.attname as string);
    const eventColumns = await writable('public.telemetry_events'), runColumns = await writable('public.telemetry_runs');
    assert.ok([...eventColumns, ...runColumns].every(c => /^[a-z_]+$/.test(c)));
    async function directEvent(client: pg.Client, id: string, count: number) {
        const projection = eventColumns.map(c => c === 'event_id' ? '$2::uuid' : c === 'run_count' ? '$3::smallint' : c).join(',');
        return (await client.query(`insert into public.telemetry_events(${eventColumns.join(',')}) select ${projection} from public.telemetry_events where event_id=$1 returning id`, [fixture.eventId, id, count])).rows[0].id;
    }
    async function directRun(client: pg.Client, id: string, ordinal: number, source: string, executionId = randomUUID()) {
        const projection = runColumns.map(c => c === 'event_row_id' ? '$1::bigint' : c === 'ordinal' ? '$2::smallint' : c === 'execution_id_text' ? '$4::text' : c).join(',');
        await client.query(`insert into public.telemetry_runs(${runColumns.join(',')}) select ${projection} from public.telemetry_runs where event_row_id=$3 and ordinal=1`, [id, ordinal, source, executionId]);
    }
    await check('direct SQL cannot commit partial or gapped events, and cannot append to sealed events', async () => {
        const incomplete = randomUUID(); await assert.rejects(directEvent(writerA, incomplete, 1), (e: any) => e.code === '23514');
        assert.deepEqual(await snapshot(writerA, incomplete), { events: [], runs: [] });
        const partial = randomUUID(); await writerA.query('begin');
        const partialId = await directEvent(writerA, partial, 2); await directRun(writerA, partialId, 2, original.id);
        await denied(writerA, 'commit', [], '23514'); assert.deepEqual(await snapshot(writerA, partial), { events: [], runs: [] });
        const complete = randomUUID(); await writerA.query('begin');
        const completeId = await directEvent(writerA, complete, 2);
        await directRun(writerA, completeId, 1, original.id); await directRun(writerA, completeId, 2, original.id);
        await writerA.query('set constraints all immediate'); await writerA.query('commit');
        const before = await snapshot(writerA, complete);
        for (const ordinal of [0, 3]) await assert.rejects(directRun(writerA, completeId, ordinal, original.id), (e: any) => e.code === '23514');
        await assert.rejects(directRun(writerA, completeId, 1, original.id), (e: any) => e.code === '23505');
        assert.deepEqual(await snapshot(writerA, complete), before);
        const zero = randomUUID(); const zeroId = await directEvent(writerA, zero, 0);
        await assert.rejects(directRun(writerA, zeroId, 1, original.id), (e: any) => e.code === '23514');
        const early = randomUUID(); await writerA.query('begin'); await directEvent(writerA, early, 1);
        await denied(writerA, 'set constraints all immediate', [], '23514'); await writerA.query('rollback');
        assert.deepEqual(await snapshot(writerA, early), { events: [], runs: [] });
        for (const timing of ['immediate', 'deferred']) {
            const event = fresh(); await writerA.query(`begin; set constraints all ${timing}`);
            assert.equal(await ingest(writerA, event), 'stored'); await writerA.query('commit');
            assert.deepEqual((await snapshot(writerA, event.eventId)).events[0].payload, event);
        }
    });
    await check('both tables and all helpers enforce effective ordinary-role access', async () => {
        const security = (await admin.query("select relname,relrowsecurity,relforcerowsecurity from pg_class where oid in ('public.telemetry_events'::regclass,'public.telemetry_runs'::regclass)")).rows;
        assert.equal(security.length, 2); assert.ok(security.every(r => r.relrowsecurity && r.relforcerowsecurity));
        const functions = (await admin.query("select proname,prosecdef,proconfig,has_function_privilege('anon',oid,'EXECUTE') as anon from pg_proc where pronamespace='public'::regnamespace and (proname like 'telemetry_%' or proname='ingest_telemetry')")).rows;
        assert.equal(functions.length, 11); assert.ok(functions.every(f => !f.prosecdef && !f.anon && JSON.stringify(f.proconfig) === JSON.stringify(['search_path=""'])));
        for (const table of ['telemetry_events', 'telemetry_runs']) {
            for (const client of [writerA, anonymous]) {
                await denied(client, `update public.${table} set writer_id=$1`, [b]);
                await denied(client, `delete from public.${table}`); await denied(client, `truncate public.${table}`);
                await denied(client, `insert into public.${table}(writer_id) values ($1)`, [b]);
            }
            await denied(anonymous, `select * from public.${table}`);
            assert.equal((await noSubject.query(`select count(*)::int n from public.${table}`)).rows[0].n, 0);
        }
        assert.equal((await writerA.query("select has_column_privilege(current_user,'public.telemetry_runs','execution_id','INSERT') allowed")).rows[0].allowed, false);
        await denied(writerA, 'insert into public.telemetry_runs(execution_id) values ($1)', [randomUUID()], '428C9');
        await denied(anonymous, 'select public.telemetry_project_run($1::jsonb)', [JSON.stringify(multiple.usage.runs[0])]);
        const bId = (await writerB.query('select id from public.telemetry_events where event_id=$1', [fixture.eventId])).rows[0].id;
        await assert.rejects(directRun(writerB, original.id, 1, bId), (e: any) => e.code === '42501');
        await assert.rejects(directRun(writerA, bId, 1, original.id), (e: any) => e.code === '42501');
        assert.equal((await writerA.query('select count(*)::int n from public.telemetry_runs where event_row_id=$1', [bId])).rows[0].n, 0);
        assert.equal((await writerB.query('select count(*)::int n from public.telemetry_runs where event_row_id=$1', [original.id])).rows[0].n, 0);
        // Identical execution UUIDs are legal in other events and for other writers.
        assert.equal(await ingest(writerB, multiple), 'stored'); assert.equal(await ingest(writerA, fresh()), 'stored');
    });
    for (const mode of ['duplicate', 'order-conflict', 'detail-conflict', 'rollback']) await check(`multi-run contention observes complete winner: ${mode}`, async () => {
        const first = await connect('authenticated', a), second = await connect('authenticated', a);
        const event = fresh(), competing = structuredClone(event);
        if (mode === 'order-conflict') competing.usage.runs.reverse();
        if (mode === 'detail-conflict') competing.usage.runs[0].estimatedCosts[0].amount = '0.54321';
        await first.query('begin'); assert.equal(await ingest(first, event), 'stored');
        const before = await snapshot(first, event.eventId);
        const pid = (await second.query('select pg_backend_pid() pid')).rows[0].pid;
        const pending = ingest(second, competing).then(status => ({ status, code: null, message: null }), error => ({ status: null, code: error.code, message: error.message }));
        let blocked = false;
        for (let i = 0; i < 100; i++) {
            if ((await admin.query('select wait_event_type from pg_stat_activity where pid=$1', [pid])).rows[0]?.wait_event_type === 'Lock') { blocked = true; break; }
            await delay(10);
        }
        assert.equal(blocked, true); await first.query(mode === 'rollback' ? 'rollback' : 'commit');
        const result = await pending;
        if (mode.endsWith('conflict')) assert.deepEqual(result, { status: null, code: '23505', message: 'telemetry_identity_conflict' });
        else assert.equal(result.status, mode === 'rollback' ? 'stored' : 'duplicate');
        const after = await snapshot(writerA, event.eventId); assert.equal(after.runs.length, 3);
        if (mode === 'rollback') { assert.deepEqual(after.events[0].payload, event); assert.notEqual(after.events[0].id, before.events[0].id); }
        else assert.deepEqual(after, before);
    });
    await check('typed execution/outcome filters and independent coverage-aware exact SQL totals', async () => {
        const records: any[] = [];
        for (const kind of ['complete', 'partial-membership', 'unavailable', 'empty', 'partial-zero', 'overflow']) {
            const event = fresh(); event.invocationId = `aggregate-${kind}`;
            event.usage = { inventoryCoverage: kind === 'partial-membership' ? 'partial' : kind === 'unavailable' ? 'unavailable' : 'complete', runs: [] };
            if (!['unavailable', 'empty'].includes(kind)) {
                const run = structuredClone(multiple.usage.runs[0]); run.providerCounters = []; run.logicalCalls = '1'; run.physicalAttempts = '1';
                run.inputTokens = { value: '9007199254740993', coverage: 'complete' }; run.outputTokens = { value: '0', coverage: 'complete' };
                if (kind === 'partial-zero') run.inputTokens = { value: '0', coverage: 'partial' };
                if (kind === 'overflow') run.inputTokens = { value: null, coverage: 'overflow' };
                event.usage.runs.push(run);
                if (kind === 'complete') event.usage.runs.push({ ...structuredClone(run), executionId: randomUUID() });
            }
            assert.equal(await ingest(writerA, event), 'stored'); records.push(event);
        }
        const rows = (await writerA.query(`select e.invocation_id,
            case when e.usage_inventory_coverage='complete' and count(r.ordinal) filter(where r.input_coverage='complete')=count(r.ordinal)
                then coalesce(sum(r.input_tokens),0)::text end input_total,
            case when e.usage_inventory_coverage='complete' and count(r.ordinal) filter(where r.output_coverage='complete')=count(r.ordinal)
                then coalesce(sum(r.output_tokens),0)::text end output_total,
            sum(r.input_tokens)::text known_input_subtotal
            from public.telemetry_events e left join public.telemetry_runs r on r.event_row_id=e.id
            where e.event_id=any($1::uuid[]) and e.repository=$2 and e.workflow_run_id=$3 and e.run_attempt=2 and e.outcome_kind='partial'
            group by e.id,e.invocation_id,e.usage_inventory_coverage order by e.invocation_id`, [records.map(e => e.eventId), fixture.execution.repository, fixture.execution.workflowRunId])).rows;
        const byName = Object.fromEntries(rows.map(r => [r.invocation_id.replace('aggregate-', ''), r]));
        assert.equal(rows.length, 6); assert.equal(byName.complete.input_total, '18014398509481986');
        assert.equal(byName.empty.input_total, '0'); assert.equal(byName.unavailable.input_total, null);
        assert.equal(byName['partial-membership'].input_total, null);
        assert.equal(byName['partial-zero'].known_input_subtotal, '0'); assert.equal(byName['partial-zero'].input_total, null);
        assert.equal(byName.overflow.input_total, null); assert.equal(byName.overflow.output_total, '0');
        for (const index of ['telemetry_events_execution', 'telemetry_events_outcome_time', 'telemetry_runs_writer_event'])
            assert.ok((await admin.query('select to_regclass($1) name', [`public.${index}`])).rows[0].name);
    });
    console.log(`PASS ${checks} real PostgreSQL storage scenarios; synthetic roles only; no remote service qualification`);
} finally {
    await Promise.allSettled(connections.map(client => client.end()));
    await engine.stop();
    await rm(owned, { recursive: true, force: true });
}
