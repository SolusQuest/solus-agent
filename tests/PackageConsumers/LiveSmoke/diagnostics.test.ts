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
