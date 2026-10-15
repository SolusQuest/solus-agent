import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, chmodSync, mkdirSync, symlinkSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { test } from 'node:test';
import { repository, run, smokeFailureDiagnostic } from '../../../scripts/packages/common.ts';
import { ignoredKeyFile, selectScenarios } from '../../../scripts/packages/selection.ts';
import { executeSmokeCommand } from './run.ts';

const source = readFileSync(new URL('./Protocol.cs', import.meta.url), 'utf8');
const categories = /internal enum Failure \{ ([^}]+) \}/.exec(source)![1].split(',').map(value => value.trim());
const diagnostic = 'LiveSmoke failed: CredentialSource.';
const secret = 'SYNTHETIC_KEY_CONTEXT_EXCEPTION_CANARY';
const rejected = [
    `LiveSmoke failed: ${secret}.`, 'LiveSmoke failed: Unknown.', 'LiveSmoke failed: CredentialSource',
    'livesmoke failed: CredentialSource.', ` ${diagnostic}`, `${diagnostic} `, `${diagnostic}\r`,
    `${diagnostic}\n\n`, `${diagnostic}\r\n\n`, `${diagnostic}\n${diagnostic}\n`,
    `${diagnostic}\n${secret}`, `${secret}\n${diagnostic}`, `Error: ${diagnostic}`, JSON.stringify(diagnostic),
    `\u001b[31m${diagnostic}\u001b[0m`, `${diagnostic}\u0000${secret}`, `${diagnostic}\u2028${secret}`,
    'LiveSmoke failed: CredentialSource\u200b.', `${diagnostic}${secret}`, '',
];

test('every current C# failure category survives only as an exact reconstructed line', () => {
    assert.equal(categories.length, 17);
    for (const category of categories) {
        const expected = `LiveSmoke failed: ${category}.`;
        for (const ending of ['', '\n', '\r\n']) {
            assert.equal(smokeFailureDiagnostic(expected + ending), expected);
            assert.throws(() => run(process.execPath, ['-e', `process.stderr.write(${JSON.stringify(expected + ending)});process.exit(7)`], repository, {}, true),
                error => error instanceof Error && error.message === expected);
        }
    }
});

test('unknown, forged, multi-line and injected output stays private at the actual child boundary', () => {
    for (const stderr of rejected) {
        assert.equal(smokeFailureDiagnostic(stderr), undefined);
        assert.throws(() => run(process.execPath, ['-e', `process.stderr.write(${JSON.stringify(stderr)});process.exit(7)`], repository, {}, true),
            error => error instanceof Error && error.message === 'consumer entrypoint failed (7)');
    }
    for (const stdout of [secret, diagnostic, '\n']) {
        assert.equal(smokeFailureDiagnostic(diagnostic, stdout), undefined);
        assert.throws(() => run(process.execPath, ['-e', `process.stdout.write(${JSON.stringify(stdout)});process.stderr.write(${JSON.stringify(diagnostic)});process.exit(7)`], repository, {}, true),
            error => error instanceof Error && error.message === 'consumer entrypoint failed (7)');
    }
    assert.throws(() => run(process.execPath, ['-e', `process.stderr.write(${JSON.stringify(diagnostic)});process.kill(process.pid,'SIGTERM')`], repository, {}, true),
        error => error instanceof Error && error.message === 'consumer entrypoint failed (execution)');
});

test('common runner to verifier to local wrapper retains the category and rejects injected streams', () => {
    const root = mkdtempSync(join(tmpdir(), 'solus-smoke-diagnostics-'));
    const entry = join(root, 'verifier.ts');
    const common = pathToFileURL(join(repository, 'scripts/packages/common.ts')).href;
    function probe(stdout: string, stderr: string) {
        const child = `process.stdout.write(${JSON.stringify(stdout)});process.stderr.write(${JSON.stringify(stderr)});process.exit(3)`;
        writeFileSync(entry, `import {run,reportFailure} from ${JSON.stringify(common)};\ntry { run(process.execPath,['-e',${JSON.stringify(child)}],${JSON.stringify(root)},{},true); } catch(error) { reportFailure(error); }\n`);
        return executeSmokeCommand([], entry);
    }
    try {
        assert.deepEqual(probe('', diagnostic + '\n'), { status: 1, output: '', diagnostic });
        for (const stderr of rejected) {
            const result = probe('', stderr);
            assert.deepEqual(result, { status: 1, output: '', diagnostic: 'LiveSmoke command failed; check the selected inputs.' });
            assert.ok(!JSON.stringify(result).includes(secret));
        }
        assert.deepEqual(probe(secret, diagnostic + '\n'), { status: 1, output: '', diagnostic: 'LiveSmoke command failed; check the selected inputs.' });
        // A verifier itself cannot smuggle another stream alongside an otherwise recognized category.
        writeFileSync(entry, `process.stdout.write(${JSON.stringify(secret)});process.stderr.write(${JSON.stringify(diagnostic + '\n')});process.exit(1);\n`);
        assert.deepEqual(executeSmokeCommand([], entry), { status: 1, output: '', diagnostic: 'LiveSmoke command failed; check the selected inputs.' });
        writeFileSync(entry, `process.stderr.write(${JSON.stringify(diagnostic + '\n')});process.kill(process.pid,'SIGTERM');\n`);
        assert.deepEqual(executeSmokeCommand([], entry), { status: 1, output: '', diagnostic: 'LiveSmoke command failed; check the selected inputs.' });
    } finally { rmSync(root, { recursive: true, force: true }); }
});

 test('real explicit private Gitless and ignored-file metadata admission is fail closed', () => {
    const root = mkdtempSync(join(tmpdir(), 'solus-key-source-'));
    const key = join(root, 'synthetic.key');
    const denied = (path: string) => assert.throws(() => ignoredKeyFile(path), error => error instanceof Error && error.message === 'LiveSmoke failed: CredentialSource.');
    const git = (...args: string[]) => { const result = spawnSync('git', ['-C', root, ...args], {encoding:'utf8'}); assert.equal(result.status,0); };
    try {
        writeFileSync(key, secret, {mode:0o600}); ignoredKeyFile(key);
        const bin=join(root,'.local','bin'); mkdirSync(bin,{recursive:true}); const executable=join(bin,'git');
        const priorPath=process.env.PATH;
        try {
            process.env.PATH=join(root,'absent-bin'); denied(key);
            process.env.PATH=bin;
            for(const script of ["echo 'fatal: permission denied' >&2; exit 128", "printf '%09000d' 0; exit 128", "exec /bin/sleep 30"]) {
                writeFileSync(executable,'#!/bin/sh\n'+script+'\n',{mode:0o700}); denied(key);
            }
        } finally {if(priorPath===undefined) delete process.env.PATH; else process.env.PATH=priorPath;}

        denied('relative.key'); denied(root); denied(join(root,'missing'));
        const link=join(root,'link'); symlinkSync(key,link); denied(link);
        chmodSync(key,0o644); denied(key); chmodSync(key,0o600);
        writeFileSync(key,''); denied(key); writeFileSync(key,'x'.repeat(4097)); denied(key); writeFileSync(key,secret);
        git('init','--quiet','--template='); denied(key);
        writeFileSync(join(root,'.gitignore'),'synthetic.key\n'); ignoredKeyFile(key);
        const configuredTrace=join(root,'configured-trace'); git('config','trace2.eventTarget',configuredTrace); ignoredKeyFile(key); assert.equal(existsSync(configuredTrace),false); git('config','--unset','trace2.eventTarget');
        git('add','--force','synthetic.key'); denied(key); git('rm','--cached','--quiet','synthetic.key'); ignoredKeyFile(key);
        const trace=join(root,'trace'); const prior=process.env.GIT_TRACE2_EVENT; process.env.GIT_TRACE2_EVENT=trace;
        try { ignoredKeyFile(key); assert.equal(existsSync(trace),false); } finally { if(prior===undefined) delete process.env.GIT_TRACE2_EVENT; else process.env.GIT_TRACE2_EVENT=prior; }
        const override=process.env.GIT_DIR; process.env.GIT_DIR=join(root,'absent');
        try { denied(key); } finally { if(override===undefined) delete process.env.GIT_DIR; else process.env.GIT_DIR=override; }
        mkdirSync(join(root,'Scene')); writeFileSync(join(root,'Scene','scenario.json'),JSON.stringify({projects:['Entry.csproj'],entrypoints:['Entry.csproj'],liveAlias:'smoke-live',liveProfile:'deepseek-flash-thinking-high'}));
        const inside=join(root,'Scene','synthetic.key'); writeFileSync(inside,secret,{mode:0o600}); writeFileSync(join(root,'.gitignore'),'synthetic.key\nScene/synthetic.key\n');
        assert.throws(()=>selectScenarios(root,{scenarios:['smoke-live'],live:true,keyFile:inside},ignoredKeyFile,false),/outside copied scenario/);
        const alias=join(root,'alias'); symlinkSync(join(root,'Scene'),alias);
        assert.throws(()=>selectScenarios(root,{scenarios:['smoke-live'],live:true,keyFile:join(alias,'synthetic.key')},ignoredKeyFile,false),/outside copied scenario/);
        writeFileSync(join(root,'.git','config'),'[broken\n'); denied(key);
        rmSync(join(root,'.git'),{recursive:true,force:true}); writeFileSync(join(root,'.git'),'gitdir: absent-directory\n'); denied(key);
    } finally {rmSync(root,{recursive:true,force:true});}
});

const facts = {
    stage: 'producer', dispatches: 1, toolEntries: 0, toolEffects: 0, elapsedMilliseconds: 123,
    httpStatus: 503, providerError: null, requestProfileMatch: true, responseProfileMatch: null, replyMatch: null,
    currentInputTokens: null, currentOutputTokens: null, historicalInputTokens: null, historicalOutputTokens: null, contextRemoved: true,
};
const envelope = (value: unknown) => `${diagnostic}\nLiveSmoke facts: ${JSON.stringify(value)}`;

test('closed facts survive both private boundaries with nulls and known numeric observations', () => {
    const root = mkdtempSync(join(tmpdir(), 'solus-safe-facts-'));
    const entry = join(root, 'verifier.ts');
    const common = pathToFileURL(join(repository, 'scripts/packages/common.ts')).href;
    try {
        for (const value of [facts, { ...facts, stage: 'restore', dispatches: 6, toolEntries: 3, toolEffects: 3,
            elapsedMilliseconds: 310000, httpStatus: 599, providerError: 11, replyMatch: false,
            currentInputTokens: Number.MAX_SAFE_INTEGER, currentOutputTokens: 0, historicalInputTokens: 10, historicalOutputTokens: 5,
            contextRemoved: false }, { ...facts, dispatches: null, toolEntries: null, toolEffects: null,
            elapsedMilliseconds: null, httpStatus: null, requestProfileMatch: null, contextRemoved: null }]) {
            const expected = envelope(value);
            for (const ending of ['', '\n', '\r\n']) assert.equal(smokeFailureDiagnostic(expected + ending), expected);
            const child = `process.stderr.write(${JSON.stringify(expected + '\n')});process.exit(1)`;
            writeFileSync(entry, `import {run,reportFailure} from ${JSON.stringify(common)};try{run(process.execPath,['-e',${JSON.stringify(child)}],${JSON.stringify(root)},{},true)}catch(error){reportFailure(error)}`);
            assert.deepEqual(executeSmokeCommand([], entry), { status: 1, output: '', diagnostic: expected });
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
});

test('facts reject every schema neighbor, duplicate, noncanonical, partial, and injected envelope', () => {
    const invalid: string[] = [];
    for (const key of Object.keys(facts)) {
        const missing: Record<string, unknown> = { ...facts }; delete missing[key]; invalid.push(envelope(missing));
        for (const value of [secret, [], {}, undefined]) invalid.push(envelope({ ...facts, [key]: value }));
    }
    for (const key of ['dispatches', 'toolEntries', 'toolEffects', 'elapsedMilliseconds', 'httpStatus', 'providerError',
        'currentInputTokens', 'currentOutputTokens', 'historicalInputTokens', 'historicalOutputTokens']) {
        for (const value of [-1, 0.5, Number.MAX_SAFE_INTEGER + 1, true]) invalid.push(envelope({ ...facts, [key]: value }));
    }
    for (const [key, value] of Object.entries({ stage: 'unknown', dispatches: 7, toolEntries: 4, toolEffects: 4,
        elapsedMilliseconds: 900001, httpStatus: 99, providerError: 12, replyMatch: 1, contextRemoved: 0 }))
        invalid.push(envelope({ ...facts, [key]: value }));
    const good = envelope(facts);
    invalid.push(envelope({ ...facts, unknown: null }), good.replace('"dispatches":1', '"dispatches":1,"dispatches":1'),
        good.replace('"dispatches":1', '"dispatches":1.0'), good.replace('"dispatches":1', '"dispatches":1e0'),
        good.replace('"toolEntries":0', '"toolEntries":-0'), good + ' ', good + '{}', good + '\n',
        good.replace('{', '{ '), good + `\n${secret}`, `${good}\u0000`, `${good}\u001b`,
        `${diagnostic}\nLiveSmoke facts: ${' '.repeat(8192)}`, `${diagnostic}\nLiveSmoke facts: {`,
        envelope(Object.fromEntries(Object.entries(facts).reverse())));
    const root = mkdtempSync(join(tmpdir(), 'solus-invalid-facts-'));
    const entry = join(root, 'verifier.ts');
    const common = pathToFileURL(join(repository, 'scripts/packages/common.ts')).href;
    try {
        for (const value of invalid) {
            // The final newline is framing; a second newline inside the envelope is rejected.
            assert.equal(smokeFailureDiagnostic(value + '\n'), undefined);
            const child = `process.stderr.write(${JSON.stringify(value + '\n')});process.exit(1)`;
            writeFileSync(entry, `import {run,reportFailure} from ${JSON.stringify(common)};try{run(process.execPath,['-e',${JSON.stringify(child)}],${JSON.stringify(root)},{},true)}catch(error){reportFailure(error)}`);
            const result = executeSmokeCommand([], entry);
            assert.deepEqual(result, { status: 1, output: '', diagnostic: 'LiveSmoke command failed; check the selected inputs.' });
            assert.ok(!JSON.stringify(result).includes(secret));
        }
        writeFileSync(entry, `process.stderr.write(${JSON.stringify(good + '\n')});process.kill(process.pid,'SIGTERM')`);
        assert.equal(executeSmokeCommand([], entry).diagnostic, 'LiveSmoke command failed; check the selected inputs.');
        assert.throws(() => run(process.execPath, ['-e', `process.stderr.write(${JSON.stringify(good + '\n')});process.kill(process.pid,'SIGTERM')`], root, {}, true),
            error => error instanceof Error && error.message === 'consumer entrypoint failed (execution)');
        assert.equal(smokeFailureDiagnostic(good, secret), undefined);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
