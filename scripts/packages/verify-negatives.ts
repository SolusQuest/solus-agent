import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { inspectPackages, options, reportFailure, repository, required, requireVersion, run } from './common.ts';
import { verifyConsumers } from './verify-consumers.ts';

try {
    const args = options(['--feed', '--version']);
    const feed = resolve(required(args, '--feed'));
    const version = requireVersion(required(args, '--version'));
    inspectPackages(feed, version);
    mkdirSync(resolve(repository, '.local/packages'), { recursive: true });
    const scratch = mkdtempSync(resolve(repository, '.local/packages/negatives-'));
    let warm = '';
    const ambient = process.env.NUGET_PACKAGES;
    let count = 0;
    const verify = (selectedFeed = feed, selectedVersion = version, consumers?: string, scenarios = ['foundation'], toolingFeed?: string) =>
        verifyConsumers({ feed: selectedFeed, version: selectedVersion, consumers, scenarios, toolingFeed });
    const reject = (name: string, expected: RegExp, action: () => unknown) => {
        try { action(); }
        catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            if (!expected.test(message)) throw new Error(`Negative ${name} failed for an unrelated reason: ${message}`);
            count++; console.log(`Negative ${name} passed.`); return;
        }
        throw new Error(`Negative ${name} unexpectedly succeeded.`);
    };
    const fixture = (name: string, mutate: (root: string) => void): string => {
        const root = join(scratch, name);
        cpSync(resolve(repository, 'tests/PackageConsumers/Foundation'), join(root, 'Foundation'), { recursive: true });
        mutate(join(root, 'Foundation'));
        return root;
    };
    const appendProject = (root: string, text: string) => {
        const path = join(root, 'Startup/Startup.csproj');
        writeFileSync(path, readFileSync(path, 'utf8').replace('</Project>', text + '\n</Project>'));
    };
    try {
        reject('same-version-overwrite', /refusing to overwrite/, () => run('node', ['scripts/packages/pack.ts', '--version', version, '--output', feed], repository));
        warm = verifyConsumers({ feed, version, scenarios: ['foundation'], keepWorkspace: true });
        process.env.NUGET_PACKAGES = join(warm, 'Foundation/.local/packages');
        const missing = join(scratch, 'missing-package');
        cpSync(feed, missing, { recursive: true });
        rmSync(join(missing, `SolusAgent.Api.${version}.nupkg`));
        reject('missing-package-with-warm-cache', /NU1101|NU1102/, () => verify(missing));
        const number = BigInt(version.split('.').at(-1)!);
        const absent = `0.1.0-internal.${number > 0n ? number - 1n : number + 1n}`;
        reject('exact-version-with-newer-substitute', /NU1102|NU1103/, () => verify(feed, absent));
        const empty = join(scratch, 'empty-feed'); mkdirSync(empty);
        reject('wrong-source-with-warm-cache-and-alternate-feed', /NU1101|NU1102/, () => verify(empty, version, undefined, ['foundation'], feed));
        reject('missing-required-scenario', /scenario: missing or ambiguous required scenario/, () => verify(feed, version, undefined, ['foundation', 'absent-required']));
        const missingProject = fixture('missing-project', root => rmSync(join(root, 'Business/Business.csproj')));
        reject('missing-required-project', /scenario: required project inventory/, () => verify(feed, version, missingProject));
        const foreign = fixture('foreign-project', root => appendProject(root, `<ItemGroup><ProjectReference Include="${resolve(repository, 'src/SolusAgent.Runtime/SolusAgent.Runtime.csproj')}" /></ItemGroup>`));
        reject('production-project-reference', /boundary: ProjectReference/, () => verify(feed, version, foreign));
        const outside = join(scratch, 'Outside.cs'); writeFileSync(outside, 'public class Outside {}\n');
        const linked = fixture('linked-source', root => appendProject(root, `<ItemGroup><Compile Include="${outside}" Link="Outside.cs" /></ItemGroup>`));
        reject('linked-source', /boundary: Compile source/, () => verify(feed, version, linked));
        const assembly = fixture('assembly-reference', root => appendProject(root, '<ItemGroup><Reference Include="Foreign"><HintPath>foreign.dll</HintPath></Reference></ItemGroup>'));
        reject('direct-assembly-reference', /boundary: direct assembly Reference/, () => verify(feed, version, assembly));
        const imported = fixture('external-import', root => appendProject(root, `<Import Project="${resolve(repository, 'Directory.Build.props')}" />`));
        reject('external-msbuild-import', /boundary: external MSBuild import/, () => verify(feed, version, imported));
        const friend = fixture('friend-identity', root => {
            const path = join(root, 'Startup/Startup.csproj'); writeFileSync(path, readFileSync(path, 'utf8').replace('PackageConsumers.Foundation.Startup', 'SolusAgent.ContractTests'));
        });
        reject('friend-assembly-identity', /boundary: forbidden production\/friend assembly/, () => verify(feed, version, friend));
        const internal = fixture('internal-access', root => {
            const path = join(root, 'Startup/Program.cs'); writeFileSync(path, readFileSync(path, 'utf8').replace('var provider =', 'Console.WriteLine(typeof(SolusAgent.Runtime.Execution.RuntimeAgent).FullName);\nvar provider ='));
        });
        reject('private-implementation-access', /CS0122/, () => verify(feed, version, internal));
        const independent = join(scratch, 'independent');
        cpSync(resolve(repository, 'tests/PackageConsumers/Foundation'), join(independent, 'Independent'), { recursive: true });
        verify(feed, version, independent, ['independent']);
        console.log(`Passed ${count} deliberate negatives and independent scenario discovery through the same verifier.`);
    } finally {
        if (ambient === undefined) delete process.env.NUGET_PACKAGES; else process.env.NUGET_PACKAGES = ambient;
        if (warm) rmSync(warm, { recursive: true, force: true });
        rmSync(scratch, { recursive: true, force: true });
    }
} catch (error) { reportFailure(error); }
