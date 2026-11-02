// One bounded issue124 operator bridge. Ordinary import/test performs no network operation.
import { createHash } from 'node:crypto';
import https from 'node:https';
import { readFileSync, writeFileSync, mkdirSync, lstatSync, realpathSync, renameSync, appendFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = fileURLToPath(new URL('../../', import.meta.url));
const locked = JSON.parse(readFileSync(new URL('./accepted-candidate.json', import.meta.url), 'utf8'));
export const selected = locked.manifest;
export const hash = (value: string | Uint8Array) => createHash('sha256').update(value).digest('hex');
const hex = /^[a-f0-9]{64}$/;
const oid = /^[a-f0-9]{40}$/;
const uuid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const branch = 'refs/heads/codex/l1-124-live-qualification';
const workflow = 'SolusQuest/solus-agent/.github/workflows/logging-release.yml@' + branch;
const fail = (code: string): never => { throw new Error(code); };
const requireThat = (value: unknown, code: string) => { if (!value) fail(code); };
function keys(value: any, names: string[]) {
    requireThat(value && typeof value === 'object' && !Array.isArray(value)
        && Object.keys(value).sort().join(',') === [...names].sort().join(','), 'admission-shape');
}
const positive = (v: unknown) => Number.isSafeInteger(v) && Number(v) > 0;
const validUuid = (v: unknown) => typeof v === 'string' && uuid.test(v) && v !== '00000000-0000-0000-0000-000000000000';
const archiveURLs = new Set([...selected.packages, selected.agentApi].map((p: any) =>
    `https://nuget.pkg.github.com/SolusQuest/download/${p.id.toLowerCase()}/${p.version}/${p.id.toLowerCase()}.${p.version}.nupkg`));

export function context(env: NodeJS.ProcessEnv, event: any, now = Date.now()) {
    requireThat(env.GITHUB_EVENT_NAME === 'workflow_dispatch' && env.GITHUB_REPOSITORY === 'SolusQuest/solus-agent'
        && env.GITHUB_REPOSITORY_ID === '1406091678' && env.GITHUB_REF === branch
        && env.GITHUB_WORKFLOW_REF === workflow && env.GITHUB_RUN_ATTEMPT === '1'
        && /^[1-9][0-9]{0,19}$/.test(env.GITHUB_RUN_ID ?? ''), 'context');
    const input = event?.inputs;
    requireThat(input?.mode === 'qualify-live' && input.member === 'CLI'
        && input['source-commit'] === selected.source.commit && input.version === selected.version
        && !input['new-package-admission'] && oid.test(input['expected-operation-head'] ?? '')
        && env.GITHUB_SHA === input['expected-operation-head'] && env.WORKFLOW_SHA === env.GITHUB_SHA, 'source-context');
    const wire = input['live-qualification-admission'];
    requireThat(typeof wire === 'string' && Buffer.byteLength(wire) <= 8192, 'admission-missing');
    let a: any; try { a = JSON.parse(wire); } catch { fail('admission-json'); }
    keys(a, ['format', 'operationHead', 'actorId', 'beforeWorkflowRunNumber', 'expectedWorkflowRunNumber',
        'nonce', 'events', 'originSHA256', 'issuerSHA256', 'writerASHA256', 'writerBSHA256',
        'schemaEvidenceSHA256', 'cleanupOwnerSHA256', 'notBeforeEpochMs', 'expiresAtEpochMs', 'budget']);
    keys(a.events, ['scribe', 'apr']); keys(a.budget, ['posts', 'gets', 'rows']);
    requireThat(a.format === 1 && a.operationHead === env.GITHUB_SHA && positive(a.actorId)
        && String(a.actorId) === env.GITHUB_ACTOR_ID && positive(a.beforeWorkflowRunNumber)
        && a.expectedWorkflowRunNumber === a.beforeWorkflowRunNumber + 1
        && String(a.expectedWorkflowRunNumber) === env.GITHUB_RUN_NUMBER, 'one-run-admission');
    requireThat(validUuid(a.nonce) && validUuid(a.events.scribe) && validUuid(a.events.apr)
        && a.events.scribe !== a.events.apr && a.writerASHA256 !== a.writerBSHA256, 'identity-admission');
    for (const field of ['originSHA256', 'issuerSHA256', 'writerASHA256', 'writerBSHA256', 'schemaEvidenceSHA256', 'cleanupOwnerSHA256'])
        requireThat(typeof a[field] === 'string' && hex.test(a[field]), 'target-admission');
    requireThat(Number.isSafeInteger(a.notBeforeEpochMs) && Number.isSafeInteger(a.expiresAtEpochMs)
        && a.expiresAtEpochMs > a.notBeforeEpochMs && a.expiresAtEpochMs - a.notBeforeEpochMs <= 3600000
        && now >= a.notBeforeEpochMs && now <= a.expiresAtEpochMs, 'operation-window');
    requireThat(a.budget.posts === 7 && a.budget.gets === 10 && a.budget.rows === 3, 'effect-budget');
    return { admission: a, admissionSHA256: hash(wire), runId: env.GITHUB_RUN_ID!, head: env.GITHUB_SHA!, runNumber: a.expectedWorkflowRunNumber };
}

export function credentials(env: NodeJS.ProcessEnv, c: any, minimumSeconds = 1500, now = Date.now()) {
    const rawOrigin = env.SUPABASE_ORIGIN ?? '', key = env.SUPABASE_PUBLISHABLE_KEY ?? '';
    let url: URL; try { url = new URL(rawOrigin); } catch { fail('target-origin'); }
    requireThat((rawOrigin === url!.origin || rawOrigin === url!.origin + '/') && url!.protocol === 'https:' && !url!.username && !url!.password
        && (!url!.port || url!.port === '443') && hash(rawOrigin) === c.admission.originSHA256, 'target-origin');
    requireThat(key.length >= 16 && key.length <= 1024 && /^sb_publishable_[A-Za-z0-9_-]+$/.test(key), 'publishable-key');
    const writers: any = {};
    for (const role of ['A', 'B']) {
        const token = env['SUPABASE_' + role + '_JWT'] ?? '';
        requireThat(token.length >= 5 && token.length <= 8192 && /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token), 'writer-token');
        let claim: any; try { claim = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8')); } catch { fail('writer-claims'); }
        // These are metadata checks, not local signature/authentication proof.
        requireThat(claim.role === 'authenticated' && validUuid(claim.sub) && typeof claim.iss === 'string'
            && hash(claim.sub) === c.admission['writer' + role + 'SHA256'] && hash(claim.iss) === c.admission.issuerSHA256
            && Number.isSafeInteger(claim.exp) && claim.exp * 1000 >= now + minimumSeconds * 1000, 'writer-identity-expiry');
        writers[role] = { token, subject: claim.sub };
    }
    requireThat(writers.A.subject !== writers.B.subject, 'distinct-writers');
    return { origin: url!.origin, key, writers };
}

export type Reply = { status: number; bytes: Buffer; type?: string; encoding?: string; location?: string };
export async function requestBytes(url: string, headers: Record<string, string>, cap: number, timeoutMs: number): Promise<Reply> {
    return new Promise((accept, reject) => {
        const req = https.get(url, { agent: false, maxHeaderSize: 16384, headers, signal: AbortSignal.timeout(timeoutMs) }, async response => {
            try {
                const status = response.statusCode ?? 0;
                if (status !== 200) { response.destroy(); accept({ status, bytes: Buffer.alloc(0), location: status === 302 ? response.headers.location : undefined }); return; }
                const chunks: Buffer[] = []; let size = 0;
                for await (const part of response) { size += part.length; if (size > cap) { response.destroy(); fail('response-limit'); } chunks.push(part); }
                accept({ status, bytes: Buffer.concat(chunks), type: response.headers['content-type'], encoding: response.headers['content-encoding'] });
            } catch { reject(Object.assign(new Error('bounded-network-read'), { httpStatus: response.statusCode ?? null })); }
        });
        req.on('error', () => reject(new Error('bounded-network-read')));
    });
}

// Only the fixed caller below supplies URLs/hashes. The seam permits credential-free transport tests.
export async function originalArchive(url: string, digest: string, cap: number, actor: string, token: string, get = requestBytes) {
    requireThat(archiveURLs.has(url) && hex.test(digest)
        && /^[A-Za-z0-9_-]{1,64}$/.test(actor) && token.length > 0 && token.length <= 8192 && !/[\x00-\x20\x7f]/.test(token), 'archive-input');
    let r = await get(url, { Authorization: 'Basic ' + Buffer.from(actor + ':' + token).toString('base64'), Accept: 'application/octet-stream' }, cap, 30000);
    if (r.status === 302) {
        const location = r.location; requireThat(typeof location === 'string' && location.length <= 4096, 'archive-redirect');
        let target: URL; try { target = new URL(location!); } catch { fail('archive-redirect'); }
        requireThat(target!.origin === 'https://nugetregistryv2prod.blob.core.windows.net' && !target!.username
            && !target!.password && !target!.hash && target!.pathname !== '/' && !!target!.search, 'archive-redirect');
        r = await get(target!.href, { Accept: 'application/octet-stream' }, cap, 30000);
    }
    if (r.status !== 200) throw Object.assign(new Error('archive-http'), { httpStatus: r.status });
    requireThat(r.bytes.length <= cap && hash(r.bytes) === digest, 'original-archive');
    return r.bytes;
}

export async function pullBearer(actor: string, token: string, get = requestBytes) {
    requireThat(/^[A-Za-z0-9_-]{1,64}$/.test(actor) && !!token && token.length <= 8192 && !/[\x00-\x20\x7f]/.test(token), 'pull-input');
    const r = await get('https://ghcr.io/token?service=ghcr.io&scope=repository%3Asolusquest%2Fsolus-logging-cli%3Apull',
        { Authorization: 'Basic ' + Buffer.from(actor + ':' + token).toString('base64'), Accept: 'application/json' }, 16384, 10000);
    if (r.status !== 200) throw Object.assign(new Error('pull-http'), { httpStatus: r.status });
    requireThat(r.bytes.length <= 16384, 'pull-response');
    let v: any; try { v = JSON.parse(r.bytes.toString('utf8')); } catch { fail('pull-response'); }
    const bearer = v.token ?? v.access_token;
    const issued = Date.parse(v.issued_at);
    requireThat(typeof bearer === 'string' && bearer.length > 0 && bearer.length <= 8192 && !/[\x00-\x20\x7f]/.test(bearer)
        && Number.isSafeInteger(v.expires_in) && v.expires_in >= 120 && v.expires_in <= 3600
        && Number.isFinite(issued) && issued <= Date.now() + 60000 && issued + v.expires_in * 1000 >= Date.now() + 65000, 'pull-response');
    return bearer as string;
}

export const actions = [
    ['A', 'scribe', 'stored', 'none', 2], ['A', 'apr', 'stored', 'none', 2],
    ['A', 'scribe', 'duplicate', 'none', 4], ['A', 'apr', 'duplicate', 'none', 4],
    ['A', 'scribe', 'failed', 'identity_conflict', 5], ['A', 'apr', 'failed', 'identity_conflict', 5],
    ['B', 'scribe', 'stored', 'none', 6],
] as const;
export const reads = [
    [0, 'A', 'A', 0], [0, 'B', 'B', 0], [2, 'A', 'A', 2], [2, 'B', 'A', 0],
    [4, 'A', 'A', 2], [6, 'A', 'A', 2], [7, 'B', 'B', 1], [7, 'B', 'A', 0],
    [7, 'A', 'B', 0], [7, 'A', 'A', 2],
] as const;
const columns = ['writer_id', 'event_id', 'schema_version', 'product', 'product_version', 'invocation_id',
    'occurred_at', 'occurred_at_residual_tick', 'received_at', 'execution', 'outcome', 'usage', 'extensions'];

export function semantic(a: any, b: any): boolean {
    if (a === b) return true;
    if (a === null || b === null || typeof a !== typeof b || typeof a !== 'object') return false;
    if (Array.isArray(a) || Array.isArray(b)) return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((v, i) => semantic(v, b[i]));
    return Object.keys(a).sort().join('\0') === Object.keys(b).sort().join('\0') && Object.keys(a).every(k => semantic(a[k], b[k]));
}
export function timeIdentity(instant: string, residual = 0) {
    requireThat(typeof instant === 'string' && Number.isInteger(residual) && residual >= 0 && residual <= 9, 'row-time');
    const m = /^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(instant);
    requireThat(m && Number.isFinite(Date.parse(instant)), 'row-time');
    const digits = (m![2] ?? '').padEnd(7, '0');
    requireThat(!residual || digits[6] === '0', 'row-time');
    return m![1] + '.' + digits.slice(0, 6) + (residual || digits[6]) + 'Z';
}
export function equivalentRow(row: any, record: any, subject: string) {
    keys(row, columns);
    requireThat(row.writer_id === subject && row.event_id === record.eventId && row.schema_version === record.schemaVersion
        && row.product === record.product && row.product_version === record.productVersion && row.invocation_id === record.invocationId
        && timeIdentity(row.occurred_at, row.occurred_at_residual_tick) === timeIdentity(record.occurredAt)
        && typeof row.received_at === 'string' && Number.isFinite(Date.parse(row.received_at)), 'row-scalars');
    for (const field of ['execution', 'outcome', 'usage', 'extensions']) requireThat(semantic(row[field], record[field]), 'row-json');
}
export function readURL(origin: string, subject: string, events: string[], preflight: boolean) {
    requireThat(validUuid(subject) && events.length > 0 && events.length <= 2 && events.every(validUuid), 'read-filter');
    const url = new URL(origin + '/rest/v1/telemetry_events');
    url.searchParams.set('select', preflight ? 'writer_id,event_id' : columns.join(','));
    url.searchParams.set('writer_id', 'eq.' + subject); url.searchParams.set('event_id', 'in.(' + events.join(',') + ')'); url.searchParams.set('limit', '3');
    return url.href;
}

function git(source: string, args: string[]) {
    const r = spawnSync('git', args, { cwd: source, env: { PATH: process.env.PATH, LANG: 'C' }, timeout: 5000, maxBuffer: 65536 });
    requireThat(!r.error && !r.signal && r.status === 0, 'source-git'); return r.stdout.toString().trim();
}
function sourceGuard(env: NodeJS.ProcessEnv, c: any) {
    requireThat(realpathSync(env.GITHUB_WORKSPACE!) === realpathSync(root)
        && git(root, ['rev-parse', 'HEAD']) === c.head && git(root, ['status', '--porcelain']) === '', 'checkout');
    const source = join(realpathSync(root), '.tmp/qualification-c96');
    requireThat(git(source, ['rev-parse', 'HEAD']) === selected.source.commit && git(source, ['status', '--porcelain']) === '', 'candidate-source');
    return source;
}
function statePath(env: NodeJS.ProcessEnv, c: any) {
    return join(realpathSync(env.RUNNER_TEMP!), 'solus-live-' + c.runId + '-' + c.admission.nonce);
}
function save(directory: string, state: any) {
    const next = join(directory, 'state.next');
    writeFileSync(next, JSON.stringify(state) + '\n', { flag: 'wx', mode: 0o600 }); renameSync(next, join(directory, 'state.json'));
}
function state(directory: string, c: any) {
    const path = join(directory, 'state.json'); const s = lstatSync(path);
    requireThat(s.isFile() && !s.isSymbolicLink() && s.size < 524288, 'state-file');
    const v = JSON.parse(readFileSync(path, 'utf8'));
    requireThat(v.runId === c.runId && v.head === c.head && v.admissionSHA256 === c.admissionSHA256, 'state-binding'); return v;
}
function once(directory: string, marker: string) { writeFileSync(join(directory, marker), 'attempted\n', { flag: 'wx', mode: 0o600 }); }
function output(env: NodeJS.ProcessEnv, key: string, value: string) {
    requireThat(/^[a-z][a-z0-9-]*$/.test(key) && !/[\r\n]/.test(value) && Buffer.byteLength(value) <= 65536, 'step-output');
    appendFileSync(env.GITHUB_OUTPUT!, key + '=' + value + '\n');
}
const escape = (v: string) => v.replaceAll('%', '%25').replaceAll('\r', '%0D').replaceAll('\n', '%0A');
export function maskOutput(value: string, env: NodeJS.ProcessEnv, write = (v: string) => process.stdout.write(v)) {
    write('::add-mask::' + escape(value) + '\n'); output(env, 'bearer', value);
}

export function productResult(r: any, shape: string, event: string) {
    const expectedExit = shape === 'apr' ? 7 : 0;
    if (r.error || r.signal || r.status !== expectedExit || !r.stdout || r.stdout.length > 300000) {
        throw Object.assign(new Error('producer-process'), { status: r.status, signal: r.signal,
            timedOut: r.error?.code === 'ETIMEDOUT', spawnFailed: !!r.error && r.error.code !== 'ETIMEDOUT',
            output: String(r.stderr ?? '').slice(0, 300000) });
    }
    let result: any; try { result = JSON.parse(r.stdout.toString()); } catch { fail('producer-json'); }
    requireThat(result.telemetryDiagnostic === 'None' && typeof result.telemetryJson === 'string' && result.business
        && result.business.status === (shape === 'apr' ? 'failed' : 'succeeded'), 'producer-envelope');
    const wire = result.telemetryJson;
    requireThat(Buffer.byteLength(wire) <= 65536 && !/[\r\n]/.test(wire), 'producer-limit');
    let record: any; try { record = JSON.parse(wire); } catch { fail('producer-wire'); }
    requireThat(record.eventId === event && record.schemaVersion === 1 && record.product === shape && record.productVersion === 'synthetic-1'
        && record.usage?.runs?.length === 3 && record.extensions?.[shape] && record.extensions?.caller, 'producer-shape');
    return { wire, record, businessExit: expectedExit };
}

let phase = 'arguments';
export async function execute(command: string, arg?: string, env = process.env, get = requestBytes) {
    requireThat(['guard', 'prepare', 'emit', 'validate', 'credentials', 'pull', 'verify', 'read', 'report'].includes(command), 'command');
    phase = command;
    const eventFile = env.GITHUB_EVENT_PATH!;
    requireThat(lstatSync(eventFile).size <= 131072, 'event-limit');
    const c = context(env, JSON.parse(readFileSync(eventFile, 'utf8')));
    const directory = statePath(env, c);
    if (command === 'guard') {
        mkdirSync(directory, { mode: 0o700 });
        save(directory, { runId: c.runId, head: c.head, admissionSHA256: c.admissionSHA256, actions: 0, reads: 0,
            installed: {}, products: {}, observations: [], acquisition: [], credentialsChecked: false }); return;
    }
    const s = state(directory, c);
    requireThat(s.observations.filter((v: any) => v.read).length === s.reads, 'previous-read-incomplete');
    if (command === 'prepare') {
        once(directory, 'prepare.attempted'); const source = sourceGuard(env, c);
        const feed = join(directory, 'feed'), baseline = join(directory, 'baseline'); mkdirSync(feed); mkdirSync(baseline);
        const api = { ...selected.agentApi, sha256: selected.agentApi.packageSha256, file: `${selected.agentApi.id}.${selected.agentApi.version}.nupkg`, dependencies: {} };
        const packages = [...selected.packages, api];
        const actor = env.GITHUB_ACTOR ?? '', token = env.GITHUB_TOKEN ?? '';
        for (const p of packages) {
            const filename = p.id.toLowerCase() + '.' + p.version + '.nupkg';
            const url = 'https://nuget.pkg.github.com/SolusQuest/download/' + p.id.toLowerCase() + '/' + p.version + '/' + filename;
            const cap = locked.sizes[p.file] ?? 4 * 1024 * 1024;
            let archiveRequests = 0;
            const countedGet: typeof requestBytes = (...args) => { archiveRequests++; return get(...args); };
            const bytes = await originalArchive(url, p.sha256, cap, actor, token, countedGet);
            if (locked.sizes[p.file]) requireThat(bytes.length === locked.sizes[p.file], 'archive-size');
            writeFileSync(join(p.id === api.id ? baseline : feed, p.file), bytes, { flag: 'wx', mode: 0o600 });
            s.acquisition.push({ id: p.id, version: p.version, sha256: hash(bytes), bytes: bytes.length, archiveHTTPGETs: archiveRequests, freshCache: true,
                source: 'https://nuget.pkg.github.com/SolusQuest/index.json' });
        }
        // No source/inspection/build child inherits the acquisition credential.
        delete env.GITHUB_TOKEN;
        const { readPackage, checkDependencies } = await import(pathToFileURL(join(source, 'scripts/logging/package-archive.ts')).href);
        const { installScenario } = await import(pathToFileURL(join(source, 'scripts/logging/installed.ts')).href);
        for (const p of packages) {
            phase = 'package-inspection'; const info = readPackage(join(p.id === api.id ? baseline : feed, p.file), p.id, p.version);
            requireThat(info.dllSha256 === p.dllSha256, 'package-dll'); checkDependencies(info.dependencies, p.dependencies);
        }
        for (const name of ['Scribe', 'Apr']) {
            phase = name === 'Scribe' ? 'install-scribe' : 'install-apr';
            const v = installScenario(join(source, 'tests/TelemetryConsumers', name), directory, name, feed, baseline, selected,
                { packageSha256: api.sha256, dllSha256: api.dllSha256 });
            s.installed[name.toLowerCase()] = { directory: v.directory, executable: v.executable, env: v.env, packages: v.resolved };
        }
        save(directory, s); return;
    }
    if (command === 'emit') {
        requireThat(arg === 'scribe' || arg === 'apr', 'shape'); once(directory, arg + '.producer.attempted');
        const v = s.installed[arg]; requireThat(v, 'installed-missing'); const marker = join(directory, arg + '.invocations');
        const caller = JSON.stringify({ team: 'synthetic', nested: [true, null, { costCenter: 'test' }], qualification: { issue: 124, nonce: c.admission.nonce } });
        const r = spawnSync('dotnet', [v.executable, c.admission.events[arg], caller, marker],
            { cwd: v.directory, env: v.env, timeout: 30000, killSignal: 'SIGKILL', maxBuffer: 300000 });
        const value = productResult(r, arg, c.admission.events[arg]);
        requireThat(readFileSync(marker, 'utf8') === arg + '-once\n', 'producer-count');
        writeFileSync(join(directory, arg + '.json'), value.wire, { flag: 'wx', mode: 0o600 });
        const collision = { ...value.record, productVersion: 'synthetic-collision-124' };
        writeFileSync(join(directory, arg + '-collision.json'), JSON.stringify(collision), { flag: 'wx', mode: 0o600 });
        s.products[arg] = { wireSHA256: hash(value.wire), businessExit: value.businessExit, invocations: 1 };
        save(directory, s); output(env, 'telemetry-json', value.wire); output(env, 'collision-json', JSON.stringify(collision));
        process.exitCode = value.businessExit; return;
    }
    if (command === 'validate') {
        requireThat(s.products.scribe?.businessExit === 0 && s.products.apr?.businessExit === 7
            && s.products.scribe.invocations === 1 && s.products.apr.invocations === 1, 'producer-validation');
        s.validated = true; save(directory, s); return;
    }
    requireThat(s.validated, 'records-not-validated');
    if (command === 'credentials') {
        credentials(env, c); s.credentialsChecked = true; save(directory, s); return;
    }
    requireThat(s.credentialsChecked, 'credentials-not-checked');
    if (command === 'pull') {
        requireThat(/^[1-7]$/.test(arg ?? ''), 'action-index'); const index = Number(arg);
        requireThat(s.actions === index - 1 && s.reads === actions[index - 1][4], 'action-order'); once(directory, 'action-' + index + '.admitted');
        maskOutput(await pullBearer(env.GITHUB_ACTOR ?? '', env.GITHUB_TOKEN ?? '', get), env); return;
    }
    if (command === 'verify') {
        requireThat(/^[1-7]$/.test(arg ?? ''), 'action-index'); const index = Number(arg);
        requireThat(lstatSync(join(directory, 'action-' + index + '.admitted')).isFile() && s.actions === index - 1
            && env.DELIVERY_STATUS === actions[index - 1][2] && env.DIAGNOSTIC_CODE === actions[index - 1][3], 'action-result');
        once(directory, 'action-' + index + '.verified'); s.actions = index;
        s.observations.push({ action: index, writer: actions[index - 1][0], shape: actions[index - 1][1], status: env.DELIVERY_STATUS, diagnostic: env.DIAGNOSTIC_CODE });
        save(directory, s); return;
    }
    if (command === 'read') {
        requireThat(/^(?:[1-9]|10)$/.test(arg ?? ''), 'read-index'); const index = Number(arg), spec = reads[index - 1];
        requireThat(s.reads === index - 1 && s.actions === spec[0], 'read-order'); once(directory, 'read-' + index + '.attempted');
        // Claim before I/O: an uncertain request is never automatically replayed.
        s.reads = index; save(directory, s);
        const secret = credentials(env, c, 65);
        const eventIds = index <= 2 || spec[2] === 'A' ? [c.admission.events.scribe, c.admission.events.apr] : [c.admission.events.scribe];
        const r = await get(readURL(secret.origin, secret.writers[spec[2]].subject, eventIds, index <= 2),
            { apikey: secret.key, Authorization: 'Bearer ' + secret.writers[spec[1]].token, Accept: 'application/json', 'Accept-Profile': 'public' }, 65536, 10000);
        if (r.status !== 200) throw Object.assign(new Error('read-http'), { httpStatus: r.status });
        requireThat(/^application\/json(?:;|$)/i.test(r.type ?? '') && (!r.encoding || r.encoding === 'identity'), 'read-response');
        let rows: any; try { rows = JSON.parse(r.bytes.toString('utf8')); } catch { fail('read-json'); }
        requireThat(Array.isArray(rows) && rows.length === spec[3], 'read-row-count');
        if (spec[3]) {
            requireThat(new Set(rows.map((v: any) => v.event_id)).size === rows.length, 'read-row-identities');
            for (const row of rows) {
                const shape = row.event_id === c.admission.events.scribe ? 'scribe' : row.event_id === c.admission.events.apr ? 'apr' : fail('read-event');
                requireThat(eventIds.includes(row.event_id), 'read-event');
                equivalentRow(row, JSON.parse(readFileSync(join(directory, shape + '.json'), 'utf8')), secret.writers[spec[2]].subject);
            }
            rows.sort((a: any, b: any) => a.event_id.localeCompare(b.event_id));
            if (index === 3) s.firstRows = rows;
            else if (spec[2] === 'A') requireThat(semantic(rows, s.firstRows), 'immutable-readback');
        }
        s.observations.push({ read: index, principal: spec[1], selectedWriter: spec[2], rows: rows.length, completeMatch: true });
        save(directory, s); return;
    }
    requireThat(command === 'report' && s.actions === 7 && s.reads === 10
        && s.observations.filter((v: any) => v.read).length === 10, 'incomplete-proof');
    const report = { profile: 'actual-published-action-to-hosted-supabase', repository: env.GITHUB_REPOSITORY,
        repositoryId: env.GITHUB_REPOSITORY_ID, runId: c.runId, runAttempt: 1, runNumber: c.runNumber, workflowSha: c.head,
        source: selected.source, candidateSHA256: selected.candidateSha256, packageSetSHA256: selected.packageSetSha256,
        actionPin: selected.action.sourceCommit, cliSHA256: selected.payload.sha256, acquisition: s.acquisition,
        installed: Object.entries(s.installed).map(([shape, v]: any) => ({ shape, packages: v.packages, freshCaches: true, sourceLinks: false, exactBytes: true })),
        products: s.products, observations: s.observations, admissionSHA256: c.admissionSHA256,
        operatorSuppliedSchemaEvidenceSHA256: c.admission.schemaEvidenceSHA256, providerSchemaDefinitionVerifiedByHelper: false,
        cleanupOwnerSHA256: c.admission.cleanupOwnerSHA256,
        effects: { actionInvocations: 7, actionDeliveryMaximumAttempts: 1, storagePostsDerivedFromOriginalActionAndOutputs: 7,
            independentServerHTTPTraceClaim: false, readbackGETs: 10, expectedNewRows: 3, observedOwnA: 2, observedOwnB: 1,
            crossWriterVisible: 0, automaticRetry: false, cleanupPerformed: false, retainedSyntheticRows: 3 },
        noModelOrProductData: true, noPublicationOrMigration: true, noNewPersistentCredential: true,
        registryPullBearerExchangesDerivedFromVerifiedControlFlow: 7, metadataOnly: true };
    writeFileSync(join(realpathSync(env.RUNNER_TEMP!), 'solus-logging-live-proof.json'), JSON.stringify(report, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
}

export function safeFailure(value: any) {
    const status = Number.isInteger(value?.status) && value.status >= 0 && value.status <= 255 ? value.status : null;
    const text = typeof value?.output === 'string' ? value.output : '';
    const reasons = new Set(['admission-shape', 'context', 'source-context', 'admission-missing', 'admission-json',
        'one-run-admission', 'identity-admission', 'target-admission', 'operation-window', 'effect-budget',
        'target-origin', 'publishable-key', 'writer-token', 'writer-claims', 'writer-identity-expiry', 'distinct-writers',
        'bounded-network-read', 'archive-input', 'archive-redirect', 'archive-http', 'original-archive', 'pull-input', 'pull-http', 'pull-response',
        'row-time', 'row-scalars', 'row-json', 'read-filter', 'source-git', 'checkout', 'candidate-source', 'state-file',
        'state-binding', 'step-output', 'producer-process', 'producer-json', 'producer-envelope', 'producer-limit',
        'producer-shape', 'command', 'event-limit', 'previous-read-incomplete', 'archive-size', 'package-dll', 'shape',
        'installed-missing', 'producer-count', 'producer-validation', 'records-not-validated', 'credentials-not-checked',
        'action-index', 'action-order', 'action-result', 'read-index', 'read-order', 'read-response', 'read-json',
        'read-http', 'read-row-count', 'read-row-identities', 'read-event', 'immutable-readback', 'incomplete-proof']);
    return { phase, status, codes: [...new Set(text.match(/\b(?:NU[0-9]{4}|MSB[0-9]{4}|CS[0-9]{4})\b/g) ?? [])].slice(0, 8),
        httpStatus: Number.isInteger(value?.httpStatus) && value.httpStatus >= 100 && value.httpStatus <= 599 ? value.httpStatus : null,
        signal: ['SIGTERM', 'SIGKILL', 'SIGINT', 'SIGABRT', 'SIGSEGV'].includes(value?.signal) ? value.signal : null,
        timedOut: typeof value?.timedOut === 'boolean' ? value.timedOut : null,
        spawnFailed: typeof value?.spawnFailed === 'boolean' ? value.spawnFailed : null,
        reason: reasons.has(value?.message) ? value.message : 'controlled-failure' };
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    execute(process.argv[2], process.argv[3]).catch(error => { console.error(JSON.stringify(safeFailure(error))); process.exitCode = 1; });
}
