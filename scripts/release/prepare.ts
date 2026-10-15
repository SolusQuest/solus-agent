import { resolve } from 'node:path';
import { isMain, options, reportFailure, repository, required, run } from '../packages/common.ts';
import { verifyConsumers } from '../packages/verify-consumers.ts';
import { requiredScenarios, validateContext, type ReleaseContext } from './core.ts';

export function prepare(context: ReleaseContext, output: string, execute = run, verify = verifyConsumers): void {
    validateContext(context);
    execute('dotnet', ['restore', 'SolusAgent.slnx'], repository);
    execute('dotnet', ['build', 'SolusAgent.slnx', '--configuration', 'Release', '--no-restore'], repository);
    execute('dotnet', ['test', 'tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj', '--configuration', 'Release', '--no-build'], repository);
    execute(process.execPath, ['scripts/packages/pack.ts', '--version', context.version, '--output', output], repository);
    verify({ feed: output, version: context.version, scenarios: ['all'], requiredScenarios });
}
export function environmentContext(version: string): ReleaseContext {
    return { event: process.env.GITHUB_EVENT_NAME ?? '', ref: process.env.GITHUB_REF ?? '', sha: process.env.GITHUB_SHA ?? '', checkoutSha: run('git', ['rev-parse', 'HEAD'], repository).trim(), version };
}
if (isMain(import.meta.url)) {
    try {
        const args = options(['--version', '--output']);
        if (process.env.GITHUB_TOKEN || process.env.GH_TOKEN || process.env.NuGetPackageSourceCredentials_selected) throw new Error('prepare: publication credentials must be absent');
        prepare(environmentContext(required(args, '--version')), resolve(required(args, '--output')));
    } catch (error) { reportFailure(error); }
}
