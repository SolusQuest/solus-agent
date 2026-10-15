import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { selectScenarios, authenticatedEnvironment } from './selection.ts';
import { repository, run } from './common.ts';

function fixture(action: (root: string) => void) {
    const root = mkdtempSync(join(tmpdir(), 'solus-selection-'));
    for (const [name, extra] of [['Foundation', {}], ['LiveSmoke', { aliases: ['smoke-offline'], liveAlias: 'smoke-live', liveProfile: 'deepseek-flash-thinking-high' }]] as const) {
        mkdirSync(join(root, name)); writeFileSync(join(root, name, 'scenario.json'), JSON.stringify({ projects: ['Entry.csproj'], entrypoints: ['Entry.csproj'], ...extra }));
    }
    try { action(root); } finally { rmSync(root, { recursive: true, force: true }); }
}

test('all runs safe defaults and requirements cover selected executions', () => fixture(root => {
    const selected = selectScenarios(root, { scenarios: ['all'], requiredScenarios: ['foundation', 'smoke-offline'] });
    assert.equal(selected.length, 2); assert.ok(selected.every(value => value.arguments.length === 0));
    assert.throws(() => selectScenarios(root, { scenarios: ['foundation'], requiredScenarios: ['smoke-offline'] }), /unselected required/);
    assert.throws(() => selectScenarios(root, { scenarios: ['all'], requiredScenarios: ['apr'] }), /required scenario apr/);
    assert.throws(() => selectScenarios(root, { scenarios: ['all'], requiredScenarios: ['smoke-live'] }), /unselected required/);
}));

test('live selection is exclusive, explicit, non-CI and forwards exact argv without key load', () => fixture(root => {
    const input = { scenarios: ['smoke-live'], live: true, keyFile: '/synthetic/ignored-key' };
    const selected = selectScenarios(root, input, path => assert.equal(path, input.keyFile), false);
    assert.deepEqual(selected[0].arguments, ['smoke-live', '--live', '--key-file', input.keyFile, '--profile', 'deepseek-flash-thinking-high']);
    for (const options of [{ scenarios: ['smoke-live'] }, { ...input, scenarios: ['all'] }, { ...input, scenarios: ['foundation'] }, { ...input, scenarios: ['smoke-live', 'foundation'] }, { ...input, profile: 'other' }])
        assert.throws(() => selectScenarios(root, options, () => assert.fail('must reject before key validation'), false), /live:/);
    assert.throws(() => selectScenarios(root, input, () => assert.fail('CI must reject'), true), /live:/);
}));

test('aliases cannot redefine reserved names or collide across directories', () => fixture(root => {
    for (const aliases of [['all'], ['smoke-live'], ['foundation'], ['smoke-offline', 'smoke-offline']]) {
        writeFileSync(join(root, 'LiveSmoke/scenario.json'), JSON.stringify({ projects: ['Entry.csproj'], entrypoints: ['Entry.csproj'], aliases }));
        assert.throws(() => selectScenarios(root, { scenarios: ['all'] }), /scenario:/);
    }
}));

test('GitHub NuGet requires a supplied username and never invents an account or emits credentials', () => {
    const feed = 'https://nuget.pkg.github.com/example/index.json';
    assert.throws(() => authenticatedEnvironment(feed, { GITHUB_TOKEN: 'SYNTHETIC_SECRET' }), error => String(error).includes('GITHUB_ACTOR') && !String(error).includes('SYNTHETIC_SECRET'));
    assert.throws(() => authenticatedEnvironment(feed, { GITHUB_TOKEN: 'synthetic', GITHUB_ACTOR: 'a;Password=x' }), /credentials:/);
    assert.throws(() => authenticatedEnvironment(feed, { GITHUB_TOKEN: 'synthetic', GITHUB_ACTOR: ' account ' }), /credentials:/);
    assert.equal(authenticatedEnvironment(feed, { GITHUB_TOKEN: 'synthetic', GITHUB_ACTOR: 'account' }).NuGetPackageSourceCredentials_selected, 'Username=account;Password=synthetic;ValidAuthenticationTypes=Basic');
    assert.equal(authenticatedEnvironment(feed, { NuGetPackageSourceCredentials_selected: 'explicit' }).NuGetPackageSourceCredentials_selected, 'explicit');
    assert.equal(authenticatedEnvironment('/local/feed', { GITHUB_TOKEN: 'synthetic' }).NuGetPackageSourceCredentials_selected, undefined);
});

test('controlled success summaries remain available while failures suppress raw output', () => {
    assert.equal(run(process.execPath, ['-e', 'console.log("Controlled cases:43")'], repository, {}, true).trim(), 'Controlled cases:43');
    assert.throws(() => run(process.execPath, ['-e', 'console.error("SYNTHETIC_SECRET");process.exit(4)'], repository, {}, true), error => String(error).includes('entrypoint failed (4)') && !String(error).includes('SYNTHETIC_SECRET'));
});

test('key source inside copied scenario is rejected before manifest/key loading', () => fixture(root => {
    assert.throws(() => selectScenarios(root, { scenarios: ['smoke-live'], live: true, keyFile: join(root, 'LiveSmoke/.env') }, () => assert.fail('must deny before key validation'), false), /outside copied scenario/);
    assert.throws(() => selectScenarios(root, { scenarios: ['smoke-live'], live: true, keyFile: join(root, 'Foundation/scenario.json') }, () => assert.fail('must deny before key validation'), false), /outside copied scenario/);
}));
