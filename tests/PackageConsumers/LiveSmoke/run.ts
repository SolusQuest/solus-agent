import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { isMain, smokeFailureDiagnostic } from '../../../scripts/packages/common.ts';

// The common verifier owns clean restore/source validation and the exclusive live gate.
const verifier = fileURLToPath(new URL('../../../scripts/packages/verify-consumers.ts', import.meta.url));
export function executeSmokeCommand(input: string[], entry = verifier): { status: number; output: string; diagnostic?: string } {
    const args = [...input];
    if (!args.includes('--scenario')) args.push('--scenario', 'smoke-offline');
    const result = spawnSync(process.execPath, [entry, ...args], { encoding: 'utf8', timeout: 900_000, maxBuffer: 1024 * 1024 });
    if (result.error || result.status !== 0) {
        const diagnostic = !result.error && !result.signal && result.status !== null
            ? smokeFailureDiagnostic(result.stderr ?? '', result.stdout ?? '') : undefined;
        return { status: 1, output: '', diagnostic: diagnostic ?? 'LiveSmoke command failed; check the selected inputs.' };
    }
    return { status: 0, output: result.stdout };
}

if (isMain(import.meta.url)) {
    const result = executeSmokeCommand(process.argv.slice(2));
    if (result.diagnostic) console.error(result.diagnostic);
    else process.stdout.write(result.output);
    process.exitCode = result.status;
}
