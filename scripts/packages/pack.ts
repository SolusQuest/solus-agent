import { constants, copyFileSync, existsSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { inspectPackages, options, packages, repository, required, requireVersion, run, reportFailure } from './common.ts';

try {
    const args = options(['--version', '--output']);
    const version = requireVersion(required(args, '--version'));
    const output = resolve(required(args, '--output'));
    mkdirSync(output, { recursive: true });
    for (const id of Object.keys(packages)) {
        if (existsSync(resolve(output, `${id}.${version}.nupkg`))) throw new Error(`pack: refusing to overwrite ${id} at ${version}`);
    }
    mkdirSync(resolve(repository, '.local/packages'), { recursive: true });
    const staging = mkdtempSync(resolve(repository, '.local/packages/pack-'));
    try {
        for (const id of Object.keys(packages)) {
            run('dotnet', ['pack', `src/${id}/${id}.csproj`, '--configuration', 'Release', '--output', staging, `-p:PackageVersion=${version}`, `-p:Version=${version}`], repository);
        }
        inspectPackages(staging, version);
        for (const id of Object.keys(packages)) copyFileSync(resolve(staging, `${id}.${version}.nupkg`), resolve(output, `${id}.${version}.nupkg`), constants.COPYFILE_EXCL);
        console.log(`Packed and inspected five net10.0 production packages at ${version}.`);
    } finally { rmSync(staging, { recursive: true, force: true }); }
} catch (error) { reportFailure(error); }
