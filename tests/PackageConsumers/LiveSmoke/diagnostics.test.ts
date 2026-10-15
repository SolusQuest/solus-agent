import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { test } from 'node:test';
import { repository, run, smokeFailureDiagnostic } from '../../../scripts/packages/common.ts';
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
