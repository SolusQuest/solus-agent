import { spawnSync } from 'node:child_process';
import { readdirSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const [nodeMajor, nodeMinor] = process.versions.node.split('.').map(Number);
if (nodeMajor < 24 || nodeMajor === 24 && nodeMinor < 12) throw new Error('Node.js 24.12 or newer is required for native TypeScript.');

export const repository = realpathSync(resolve(dirname(fileURLToPath(import.meta.url)), '../..'));
export const packages: Record<string, string[]> = {
    'SolusAgent.Api': [],
    'SolusAgent.Tools.Api': [],
    'SolusAgent.Runtime.Api': ['SolusAgent.Api', 'SolusAgent.Tools.Api'],
    'SolusAgent.Runtime': ['SolusAgent.Api', 'SolusAgent.Tools.Api', 'SolusAgent.Runtime.Api'],
    'SolusAgent.Providers.DeepSeek': ['SolusAgent.Runtime.Api'],
};

export function requireVersion(value: string): string {
    if (!/^0\.1\.0-internal\.(0|[1-9]\d*)$/.test(value ?? ''))
        throw new Error('version: expected an explicit 0.1.0-internal.N version');
    return value;
}

export function options(allowed: string[], repeated: string[] = [], flags: string[] = []): Record<string, string[]> {
    const result: Record<string, string[]> = {};
    for (let index = 2; index < process.argv.length; index++) {
        const key = process.argv[index];
        if (!allowed.includes(key) || result[key] && !repeated.includes(key)) throw new Error(`arguments: unknown or duplicate option ${key}`);
        const value = flags.includes(key) ? 'true' : process.argv[++index];
        if (!value || value.startsWith('--')) throw new Error(`arguments: missing value for ${key}`);
        (result[key] ??= []).push(value);
    }
    return result;
}

export function required(args: Record<string, string[]>, name: string): string {
    if (!args[name]?.[0]) throw new Error(`arguments: ${name} is required`);
    return args[name][0];
}

export function within(path: string, root: string): boolean {
    const difference = relative(resolve(root), resolve(path));
    return difference === '' || !isAbsolute(difference) && difference !== '..' && !difference.startsWith(`..${sep}`);
}

// One exact stderr envelope at every private boundary; facts are observations, never authority.
export function smokeFailureDiagnostic(stderr: string, stdout = ''): string | undefined {
    if (stdout !== '' || Buffer.byteLength(stderr, 'utf8') > 8192) return undefined;
    const text = stderr.endsWith('\r\n') ? stderr.slice(0, -2) : stderr.endsWith('\n') ? stderr.slice(0, -1) : stderr;
    const lines = text.split('\n');
    const categories = ['Arguments', 'OptIn', 'Profile', 'Ci', 'CredentialSource', 'Credential', 'DispatchLimit',
        'ToolLimit', 'Deadline', 'Capacity', 'Transport', 'UnexpectedReply', 'UnknownUsage', 'Authority', 'Child', 'Protocol', 'Confinement'];
    const category = categories.find(value => lines[0] === `LiveSmoke failed: ${value}.`);
    if (!category || lines.length > 2) return undefined;
    if (lines.length === 1) return `LiveSmoke failed: ${category}.`;
    const prefix = 'LiveSmoke facts: ';
    if (!lines[1].startsWith(prefix)) return undefined;
    const raw = lines[1].slice(prefix.length);
    try {
        const facts = JSON.parse(raw);
        const integer = (value: unknown, maximum: number, minimum = 0) => value === null
            || typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= maximum;
        const boolean = (value: unknown) => value === null || typeof value === 'boolean';
        const rules: Record<string, (value: unknown) => boolean> = {
            stage: value => ['preparation', 'credential', 'producer', 'restore'].includes(value as string),
            dispatches: value => integer(value, 6), toolEntries: value => integer(value, 3), toolEffects: value => integer(value, 3),
            elapsedMilliseconds: value => integer(value, 900000), httpStatus: value => integer(value, 599, 100),
            providerError: value => integer(value, 11), requestProfileMatch: boolean, responseProfileMatch: boolean,
            replyMatch: boolean, currentInputTokens: value => integer(value, Number.MAX_SAFE_INTEGER),
            currentOutputTokens: value => integer(value, Number.MAX_SAFE_INTEGER),
            historicalInputTokens: value => integer(value, Number.MAX_SAFE_INTEGER),
            historicalOutputTokens: value => integer(value, Number.MAX_SAFE_INTEGER), contextRemoved: boolean,
        };
        if (!facts || Array.isArray(facts) || typeof facts !== 'object'
            || JSON.stringify(Object.keys(facts)) !== JSON.stringify(Object.keys(rules))) return undefined;
        const rebuilt: Record<string, unknown> = {};
        for (const [key, rule] of Object.entries(rules)) {
            if (!rule(facts[key])) return undefined;
            rebuilt[key] = facts[key];
        }
        const canonical = JSON.stringify(rebuilt);
        // Also rejects duplicate keys, noncanonical numbers, escapes, and trailing text.
        if (raw !== canonical) return undefined;
        return `LiveSmoke failed: ${category}.\n${prefix}${canonical}`;
    } catch { return undefined; }
}

export function run(command: string, args: string[], cwd: string, env = process.env, privateOutput = false): string {
    const result = spawnSync(command, args, { cwd, env: { ...env, DOTNET_CLI_UI_LANGUAGE: 'en-US', DOTNET_NOLOGO: '1' }, encoding: 'utf8', timeout: 300_000, maxBuffer: 32 * 1024 * 1024 });
    if (result.error || result.status !== 0) {
        if (privateOutput) {
            const diagnostic = !result.error && !result.signal && result.status !== null
                ? smokeFailureDiagnostic(result.stderr ?? '', result.stdout ?? '') : undefined;
            throw new Error(diagnostic ?? `consumer entrypoint failed (${result.status ?? 'execution'})`);
        }
        let detail = `${result.error?.message ?? ''}\n${result.stdout ?? ''}\n${result.stderr ?? ''}`;
        if (env.GITHUB_TOKEN) detail = detail.split(env.GITHUB_TOKEN).join('[redacted]');
        throw new Error(`${command} failed (${result.status ?? 'execution'}): ${detail}`);
    }
    return result.stdout;
}

export function inspectPackages(feed: string, version: string): void {
    const expectedFiles = Object.keys(packages).map(id => `${id}.${version}.nupkg`).sort();
    const files = readdirSync(feed).filter(name => name.endsWith('.nupkg')).sort();
    if (JSON.stringify(files) !== JSON.stringify(expectedFiles)) throw new Error('archive: expected exactly the five production packages at the selected version');
    for (const [id, dependencies] of Object.entries(packages)) {
        const archive = resolve(feed, `${id}.${version}.nupkg`);
        const entries = run('unzip', ['-Z1', archive], feed).trim().split(/\r?\n/);
        const requiredEntries = [`${id}.nuspec`, `lib/net10.0/${id}.dll`, `lib/net10.0/${id}.xml`];
        if (requiredEntries.some(entry => !entries.includes(entry))) throw new Error(`archive: ${id} is missing its nuspec, DLL or XML documentation`);
        if (entries.some(entry => !requiredEntries.includes(entry) && entry !== '[Content_Types].xml' && entry !== '_rels/.rels' && !/^package\/services\/metadata\/core-properties\/[a-f0-9]+\.psmdcp$/.test(entry)))
            throw new Error(`archive: ${id} contains unexpected assets`);
        const nuspec = run('unzip', ['-p', archive, `${id}.nuspec`], feed);
        if (!nuspec.includes(`<id>${id}</id>`) || !nuspec.includes(`<version>${version}</version>`)) throw new Error(`archive: ${id} identity/version mismatch`);
        const actual = [...nuspec.matchAll(/<dependency\s+([^>]+)\/?\s*>/g)].map(match => {
            const depId = /\bid="([^"]+)"/.exec(match[1])?.[1];
            const range = /\bversion="([^"]+)"/.exec(match[1])?.[1];
            // SDK pack emits a lower bound; clean consumers additionally require exact resolved equality.
            if (!range || ![version, `[${version}, )`, `[${version},)`, `[${version}]`].includes(range)) throw new Error(`archive: ${id} dependency version mismatch`);
            return depId;
        }).sort();
        if (JSON.stringify(actual) !== JSON.stringify([...dependencies].sort())) throw new Error(`archive: ${id} dependency graph mismatch`);
    }
}

export function isMain(url: string): boolean {
    return !!process.argv[1] && resolve(process.argv[1]) === fileURLToPath(url);
}

export function reportFailure(error: unknown): void {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
}
