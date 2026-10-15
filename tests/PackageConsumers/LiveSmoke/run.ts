import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// The common verifier owns clean restore/source validation and the exclusive live gate.
const verifier = fileURLToPath(new URL('../../../scripts/packages/verify-consumers.ts', import.meta.url));
const args = process.argv.slice(2);
if (!args.includes('--scenario')) args.push('--scenario', 'smoke-offline');
const result = spawnSync(process.execPath, [verifier, ...args], { encoding: 'utf8', timeout: 900_000, maxBuffer: 1024 * 1024 });
if (result.error || result.status !== 0) {
    console.error('LiveSmoke command failed; inspect the selected inputs and closed consumer failure category.');
    process.exitCode = 1;
} else {
    process.stdout.write(result.stdout);
}
