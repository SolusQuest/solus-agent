import { existsSync, lstatSync, readFileSync, readdirSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, join } from 'node:path';
import { run, within } from './common.ts';

export interface SelectionOptions {
    scenarios: string[];
    requiredScenarios?: string[];
    live?: boolean;
    keyFile?: string;
    profile?: string;
}
export interface SelectedScenario { name: string; path: string; arguments: string[] }
const valid = (value: unknown): value is string => typeof value === 'string' && /^[a-z0-9][a-z0-9-]*$/i.test(value) && value.toLowerCase() !== 'all';

export function ignoredKeyFile(path: string): void {
    if (!isAbsolute(path) || !lstatSync(path).isFile() || lstatSync(path).isSymbolicLink()) throw new Error('live: key source must be an absolute regular local file');
    const root = run('git', ['rev-parse', '--show-toplevel'], dirname(path)).trim();
    run('git', ['check-ignore', '--quiet', path], root);
}

export function selectScenarios(root: string, options: SelectionOptions, checkKey = ignoredKeyFile, ci = !!process.env.CI): SelectedScenario[] {
    const inventory = new Map<string, { scenario: SelectedScenario; live: boolean; profile?: string }>();
    const defaults: SelectedScenario[] = [];
    for (const name of readdirSync(root).sort()) {
        if (['.local', '.git', 'bin', 'obj'].includes(name)) continue;
        const path = join(root, name);
        if (!lstatSync(path).isDirectory()) continue;
        if (!valid(name)) throw new Error('scenario: invalid directory name');
        if (options.keyFile && (within(options.keyFile, path) || existsSync(options.keyFile) && within(realpathSync(options.keyFile), realpathSync(path))))
            throw new Error('live: key source must be outside copied scenario source roots');
        const manifest = JSON.parse(readFileSync(join(path, 'scenario.json'), 'utf8'));
        const scenario = { name, path, arguments: [] as string[] };
        defaults.push(scenario);
        const aliases = manifest.aliases ?? [];
        if (!Array.isArray(aliases) || aliases.some(alias => !valid(alias))) throw new Error('scenario: invalid aliases');
        const add = (alias: string, live: boolean) => {
            const key = alias.toLowerCase();
            if (!live && key === 'smoke-live') throw new Error('scenario: reserved live alias cannot name a default');
            if (inventory.has(key)) throw new Error('scenario: duplicate or ambiguous alias');
            inventory.set(key, { scenario, live, profile: manifest.liveProfile });
        };
        add(name, false);
        for (const alias of aliases) add(alias, false);
        if (manifest.liveAlias !== undefined) {
            if (manifest.liveAlias !== 'smoke-live' || manifest.liveProfile !== 'deepseek-flash-thinking-high'
                || !Array.isArray(manifest.entrypoints) || manifest.entrypoints.length !== 1)
                throw new Error('scenario: invalid live declaration');
            add(manifest.liveAlias, true);
        } else if (manifest.liveProfile !== undefined) throw new Error('scenario: profile requires a live alias');
    }
    const requested = options.scenarios.map(name => name.toLowerCase());
    if (!requested.length || new Set(requested).size !== requested.length) throw new Error('scenario: explicit unique selection is required');
    const all = requested.includes('all');
    if (all && requested.length !== 1) throw new Error('scenario: all cannot be combined with other selections');
    const selections = all ? defaults.map(scenario => ({ scenario, live: false, profile: undefined as string | undefined })) : requested.map(name => {
        const selected = inventory.get(name);
        if (!selected) throw new Error(`scenario: missing or ambiguous required scenario ${name}`);
        return selected;
    });
    if (!selections.length || new Set(selections.map(value => value.scenario.path)).size !== selections.length) throw new Error('scenario: empty or duplicate executions');
    const live = selections.some(value => value.live);
    const liveOptions = options.live || options.keyFile !== undefined || options.profile !== undefined;
    if (live) {
        if (all || selections.length !== 1 || requested[0] !== 'smoke-live' || !options.live || !options.keyFile || ci)
            throw new Error('live: exclusive smoke-live, --live, ignored key source and non-CI execution are required');
        const profile = options.profile ?? selections[0].profile;
        if (profile !== 'deepseek-flash-thinking-high') throw new Error('live: unsupported profile');
        checkKey(options.keyFile);
        selections[0] = { ...selections[0], scenario: { ...selections[0].scenario, arguments: ['smoke-live', '--live', '--key-file', options.keyFile, '--profile', profile] } };
    } else if (liveOptions) throw new Error('live: options are forbidden for offline/default selections');
    const requirements = (options.requiredScenarios ?? []).map(name => name.toLowerCase());
    if (new Set(requirements).size !== requirements.length || requirements.some(name => !valid(name))) throw new Error('scenario: invalid required set');
    for (const requirement of requirements) {
        const target = inventory.get(requirement);
        if (!target || !selections.some(value => value.scenario.path === target.scenario.path && value.live === target.live))
            throw new Error(`scenario: missing or unselected required scenario ${requirement}`);
    }
    return selections.map(value => value.scenario);
}

export function authenticatedEnvironment(feed: string, input: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
    const env = { ...input };
    if (feed.startsWith('https://nuget.pkg.github.com/') && env.GITHUB_TOKEN && !env.NuGetPackageSourceCredentials_selected) {
        if (!env.GITHUB_ACTOR || !/^[a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?(?:\[bot\])?$/i.test(env.GITHUB_ACTOR))
            throw new Error('credentials: explicit GITHUB_ACTOR account is required for GitHub NuGet authentication');
        env.NuGetPackageSourceCredentials_selected = `Username=${env.GITHUB_ACTOR};Password=${env.GITHUB_TOKEN};ValidAuthenticationTypes=Basic`;
    }
    return env;
}
