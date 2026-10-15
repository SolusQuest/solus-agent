import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { packages, repository } from '../packages/common.ts';
import { prepare } from './prepare.ts';
import { publish, validateContext, type ReleaseContext, type ReleaseOperations } from './core.ts';

const context: ReleaseContext = { event: 'workflow_dispatch', ref: 'refs/heads/main', sha: 'a'.repeat(40), checkoutSha: 'a'.repeat(40), version: '0.1.0-internal.1' };
const ready = 'private-no-inheritance';
function operations(events: string[]): ReleaseOperations {
    return {
        tagExists: async () => { events.push('tag-check'); return false; },
        packages: async () => { events.push('metadata'); return Object.keys(packages).map(name => ({ name, visibility: 'private', package_type: 'nuget' })); },
        versions: async id => { events.push('versions:' + id); return []; },
        push: async path => { events.push('push:' + path); },
        tag: async (tag, sha) => { events.push(`tag:${tag}:${sha}`); },
    };
}
const inspect = () => {};

test('canonical version, manual main and selected SHA reject before side effects', async () => {
    for (const change of [{ version: '0.1.0-internal.01' }, { version: '0.1.0' }, { version: '0.1.0-internal.1;false' }, { ref: 'refs/heads/other' }, { event: 'push' }, { checkoutSha: 'b'.repeat(40) }]) {
        assert.throws(() => validateContext({ ...context, ...change }), /version:|release:/);
        const events: string[] = [];
        await assert.rejects(publish({ ...context, ...change }, '/synthetic/feed', ready, operations(events), inspect));
        assert.deepEqual(events, []);
    }
});

test('prepare packs once then validates exactly the same paths and all required offline scenes', () => {
    const calls: string[][] = [];
    let feed = '';
    prepare(context, '/synthetic/feed', (_command, args) => { calls.push(args); return ''; }, input => {
        feed = input.feed;
        assert.deepEqual(input.scenarios, ['all']);
        assert.deepEqual(input.requiredScenarios, ['foundation', 'apr', 'scribe', 'smoke-offline']);
        return 'synthetic-workspace';
    });
    assert.equal(calls.length, 4);
    assert.equal(calls.filter(args => args.includes('scripts/packages/pack.ts')).length, 1);
    assert.equal(calls[3].at(-1), feed);
    assert.throws(() => prepare(context, '/synthetic/feed', () => '', () => { throw Error('scenario: missing required apr'); }), /missing required/);
});

test('unknown private access, registry errors, nonprivate metadata, version and tag conflicts never push', async () => {
    for (const mode of ['unverified', 'api', 'visibility', 'version', 'tag', 'pagination']) {
        const events: string[] = []; const io = operations(events);
        if (mode === 'api') io.packages = async () => { throw Error('registry: API operation failed (403)'); };
        if (mode === 'visibility') io.packages = async () => [{ name: 'SolusAgent.Api', visibility: 'public', package_type: 'nuget' }];
        if (mode === 'version') io.versions = async id => id === 'SolusAgent.Providers.DeepSeek' ? [context.version] : [];
        if (mode === 'tag') io.tagExists = async () => true;
        if (mode === 'pagination') io.packages = async () => { throw Error('registry: incomplete pagination'); };
        await assert.rejects(publish(context, '/synthetic/feed', mode === 'unverified' ? undefined : ready, io, inspect));
        assert.ok(events.every(value => !value.startsWith('push:') && !value.startsWith('tag:')));
        if (mode === 'unverified') assert.deepEqual(events, []);
    }
});

test('partial publication stops, reports known identities safely and never tags', async () => {
    const events: string[] = []; const io = operations(events);
    let count = 0;
    io.push = async path => { events.push('push:' + path); if (++count === 3) throw Error('SYNTHETIC_SECRET_RESPONSE'); };
    await assert.rejects(publish(context, '/synthetic/feed', ready, io, inspect), error =>
        String(error).includes('successful=[SolusAgent.Api,SolusAgent.Tools.Api]') && String(error).includes('failed-or-unknown=SolusAgent.Runtime.Api') && !String(error).includes('SYNTHETIC_SECRET'));
    assert.equal(count, 3); assert.ok(!events.some(value => value.startsWith('tag:')));
});

test('all five preflights precede pushes; tag is last at selected SHA with original archive paths', async () => {
    const events: string[] = []; const io = operations(events);
    await publish(context, '/synthetic/feed', ready, io, (feed, version) => { assert.equal(feed, '/synthetic/feed'); assert.equal(version, context.version); events.push('inspect'); });
    const firstPush = events.findIndex(value => value.startsWith('push:'));
    assert.equal(events.slice(0, firstPush).filter(value => value.startsWith('versions:')).length, 5);
    assert.deepEqual(events.filter(value => value.startsWith('push:')), Object.keys(packages).map(id => `push:${resolve('/synthetic/feed', `${id}.${context.version}.nupkg`)}`));
    assert.equal(events.at(-1), `tag:v${context.version}:${context.sha}`);
});

test('tag failure after all uploads remains a failure without replay', async () => {
    const events: string[] = []; const io = operations(events);
    io.tag = async () => { events.push('tag-attempt'); throw Error('synthetic'); };
    await assert.rejects(publish(context, '/synthetic/feed', ready, io, inspect), /tag failed or outcome unknown/);
    assert.equal(events.filter(value => value.startsWith('push:')).length, 5);
    assert.equal(events.filter(value => value === 'tag-attempt').length, 1);
});

function checkWorkflow(workflow: any): void {
    assert.deepEqual(Object.keys(workflow.on), ['workflow_dispatch']);
    assert.deepEqual(Object.keys(workflow.on.workflow_dispatch.inputs), ['version']);
    assert.equal(workflow.concurrency['cancel-in-progress'], false);
    assert.deepEqual(workflow.permissions, { contents: 'read' });
    assert.equal(Object.keys(workflow.jobs).length, 1);
    const job = workflow.jobs.publish;
    assert.equal(job['runs-on'], 'ubuntu-24.04');
    assert.deepEqual(job.permissions, { contents: 'write', packages: 'write' });
    assert.equal(job.environment, undefined); assert.equal(workflow.env, undefined); assert.equal(job.env, undefined);
    const checkout = job.steps.filter((step: any) => step.uses?.startsWith('actions/checkout@'));
    assert.equal(checkout.length, 1); assert.equal(checkout[0].with.ref, '${{ github.sha }}');
    assert.equal(checkout[0].with['persist-credentials'], false);
    assert.ok(job.steps[0].run.includes('workflow_dispatch') && job.steps[0].run.includes('refs/heads/main'));
    const prepareIndex = job.steps.findIndex((step: any) => step.id === 'prepare');
    const authenticated = job.steps.findIndex((step: any) => step.env?.GITHUB_TOKEN);
    assert.ok(prepareIndex > job.steps.indexOf(checkout[0]) && authenticated > prepareIndex);
    assert.equal(authenticated, job.steps.length - 1);
    for (const step of job.steps.slice(0, authenticated)) assert.ok(!JSON.stringify(step).includes('secrets.') && !step.env?.GITHUB_TOKEN && !step.env?.GH_TOKEN);
    assert.equal(job.steps[authenticated].env.GITHUB_TOKEN, '${{ secrets.GITHUB_TOKEN }}');
    assert.ok(job.steps[prepareIndex].run.includes('--output artifacts/internal-release'));
    assert.ok(job.steps[authenticated].run.includes('--feed artifacts/internal-release'));
    assert.ok(!JSON.stringify(workflow).includes('always()') && !JSON.stringify(workflow).includes('continue-on-error'));
}

test('parsed workflow rejects persisted checkout credentials and token leakage into offline preparation', () => {
    const path = resolve(repository, '.github/workflows/release.yml');
    const parsed = spawnSync('ruby', ['-ryaml', '-rjson', '-e', 'puts JSON.generate(YAML.safe_load(File.read(ARGV[0])))', path], { encoding: 'utf8' });
    assert.equal(parsed.status, 0, 'Existing Ruby standard library YAML parser is required for structural validation');
    const workflow = JSON.parse(parsed.stdout); checkWorkflow(workflow);
    for (const mutate of [
        (value: any) => { value.jobs.publish.steps[1].with['persist-credentials'] = true; },
        (value: any) => { delete value.jobs.publish.steps[1].with['persist-credentials']; },
        (value: any) => { value.jobs.publish.steps.find((step: any) => step.id === 'prepare').env.GITHUB_TOKEN = '${{ secrets.GITHUB_TOKEN }}'; },
        (value: any) => { value.jobs.publish.env = { GITHUB_TOKEN: '${{ secrets.GITHUB_TOKEN }}' }; },
        (value: any) => { value.jobs.publish.steps[1].with.ref = 'main'; },
        (value: any) => { value.jobs.publish.permissions.issues = 'write'; },
    ]) {
        const changed = structuredClone(workflow); mutate(changed); assert.throws(() => checkWorkflow(changed));
    }
    assert.ok(readFileSync(path, 'utf8').includes('persist-credentials: false'));
});

test('GitHub adapter reads paginated metadata and versions, verifies tag and closes unauthorized errors', async () => {
    const { githubOperations } = await import('./publish.ts');
    const original = globalThis.fetch;
    const requests: string[] = [];
    globalThis.fetch = async (input, init) => {
        const url = String(input); requests.push(url);
        assert.equal((init?.headers as Record<string, string>).Authorization, 'Bearer synthetic-token');
        if (url.includes('/git/ref/tags/')) return new Response('{}', { status: 404 });
        if (url.includes('/versions')) return Response.json([{ name: '0.1.0-internal.1' }]);
        if (new URL(url).searchParams.get('page') === '1') return Response.json(Array.from({ length: 100 }, (_, index) => ({ name: `synthetic-${index}`, visibility: 'private', package_type: 'nuget' })));
        return Response.json([]);
    };
    try {
        const io = githubOperations('owner', 'repo', 'synthetic-token');
        assert.equal((await io.packages()).length, 100);
        assert.ok(requests.some(url => url.includes('page=2')));
        assert.deepEqual(await io.versions('SolusAgent.Api'), ['0.1.0-internal.1']);
        assert.equal(await io.tagExists('v0.1.0-internal.1'), false);
        globalThis.fetch = async () => new Response('SYNTHETIC_SECRET_RESPONSE', { status: 403 });
        await assert.rejects(io.packages(), error => String(error).includes('(403)') && !String(error).includes('SYNTHETIC_SECRET'));
        globalThis.fetch = async () => Response.json({ incomplete: true });
        await assert.rejects(io.packages(), /malformed paginated response/);
    } finally { globalThis.fetch = original; }
});
