import { cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { selectScenarios, authenticatedEnvironment } from './selection.ts';
import { isMain, options, packages, reportFailure, repository, required, requireVersion, run, within } from './common.ts';

export interface VerificationOptions {
    feed: string;
    version: string;
    scenarios: string[];
    consumers?: string;
    toolingFeed?: string;
    keepWorkspace?: boolean;
    requiredScenarios?: string[];
    live?: boolean;
    keyFile?: string;
    profile?: string;
}

type Manifest = { projects: string[]; entrypoints: string[] };
type Item = Record<string, string>;
type Evaluation = { Properties: Record<string, string>; Items: Record<string, Item[]> };
const ignored = new Set(['bin', 'obj', '.local', '.git', 'TestResults']);
const xml = (value: string) => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

function source(value: string): string {
    if (/^https:\/\//i.test(value)) {
        const url = new URL(value);
        if (url.username || url.password || url.search || url.hash) throw new Error('source: credentials/query must not be included in a feed URL');
        return url.href;
    }
    if (/^[a-z]+:\/\//i.test(value)) throw new Error('source: only local directories or HTTPS feeds are supported');
    const path = realpathSync(resolve(value));
    if (!lstatSync(path).isDirectory()) throw new Error('source: local feed must be a directory');
    return path;
}

function files(root: string): string[] {
    const found: string[] = [];
    for (const name of readdirSync(root)) {
        if (ignored.has(name)) continue;
        const path = join(root, name);
        const stat = lstatSync(path);
        if (stat.isSymbolicLink()) throw new Error('boundary: scenario sources must not contain symlinks');
        if (stat.isDirectory()) found.push(...files(path));
        else if (stat.isFile()) found.push(path);
        else throw new Error('boundary: scenario contains a non-regular source');
    }
    return found;
}

function projectList(value: unknown, root: string, label: string): string[] {
    if (!Array.isArray(value) || !value.length || value.some(path => typeof path !== 'string' || !path.endsWith('.csproj') || isAbsolute(path) || !within(resolve(root, path), root)))
        throw new Error(`scenario: invalid or empty ${label}`);
    const paths = value.map(path => resolve(root, path));
    if (new Set(paths).size !== paths.length) throw new Error(`scenario: duplicate ${label}`);
    return paths;
}

function manifest(root: string): Manifest {
    if (!existsSync(join(root, 'scenario.json'))) throw new Error('scenario: missing scenario.json');
    const value = JSON.parse(readFileSync(join(root, 'scenario.json'), 'utf8'));
    const projects = projectList(value.projects, root, 'required projects').sort();
    const entrypoints = projectList(value.entrypoints, root, 'entrypoints');
    const discovered = files(root).filter(path => path.endsWith('.csproj')).sort();
    if (JSON.stringify(projects) !== JSON.stringify(discovered)) throw new Error('scenario: required project inventory does not match discovered projects');
    if (entrypoints.some(path => !projects.includes(path))) throw new Error('scenario: entrypoint must be a required project');
    return { projects, entrypoints };
}

function evaluate(project: string, cwd: string, properties: string[], env: NodeJS.ProcessEnv): Evaluation {
    const output = run('dotnet', ['msbuild', project, '-nologo', ...properties,
        '-getProperty:TargetFramework,PublishAot,PublishTrimmed,IsPackable,AssemblyName,OutputType,MSBuildAllProjects,MSBuildToolsPath',
        '-getItem:ProjectReference,PackageReference,Compile,Reference'], cwd, env);
    const evaluation = JSON.parse(output.slice(output.indexOf('{'))) as Evaluation;
    const expanded = join(cwd, '.local/evaluated.xml');
    run('dotnet', ['msbuild', project, '-nologo', ...properties, `-preprocess:${expanded}`], cwd, env);
    // MSBuildAllProjects can omit explicit imports. Native preprocessing records the actual import paths.
    const imports = [...readFileSync(expanded, 'utf8').matchAll(/<!--([\s\S]*?)-->/g)]
        .filter(match => match[1].trimStart().startsWith('===================='))
        .flatMap(match => match[1].split(/\r?\n/).map(line => line.trim()).filter(line => isAbsolute(line)));
    evaluation.Properties.PackageConsumerImports = imports.join(';');
    return evaluation;
}

function checkBoundary(project: string, evaluation: Evaluation, scenario: string, projects: string[], cache: string, version: string): void {
    const property = evaluation.Properties;
    if (property.TargetFramework !== 'net10.0' || property.PublishAot === 'true' || property.PublishTrimmed === 'true' || property.IsPackable !== 'false')
        throw new Error('boundary: consumers must be non-packable managed net10.0 projects');
    if (['SolusAgent.ContractTests', ...Object.keys(packages)].some(name => name.toLowerCase() === property.AssemblyName.toLowerCase()))
        throw new Error('boundary: forbidden production/friend assembly identity');
    for (const item of evaluation.Items.ProjectReference ?? []) {
        const path = resolve(dirname(project), item.FullPath || item.Identity);
        if (!projects.includes(path) || !within(path, scenario)) throw new Error('boundary: ProjectReference must target a project in the same scenario');
    }
    for (const item of evaluation.Items.Compile ?? []) {
        const path = realpathSync(resolve(dirname(project), item.FullPath || item.Identity));
        if (!within(path, scenario)) throw new Error('boundary: Compile source escapes the scenario');
    }
    if (evaluation.Items.Reference?.length) throw new Error('boundary: direct assembly Reference is forbidden; use PackageReference');
    const sdkRoots = [property.MSBuildToolsPath, resolve(property.MSBuildToolsPath, '../../sdk-manifests'), resolve(property.MSBuildToolsPath, '../../packs')];
    for (const path of (property.PackageConsumerImports ?? '').split(';').filter(Boolean)) {
        if (!within(path, scenario) && !within(path, cache) && !sdkRoots.some(root => within(path, root)))
            throw new Error(`boundary: external MSBuild import escapes the scenario and SDK: ${path} (SDK ${property.MSBuildToolsPath})`);
    }
    for (const item of evaluation.Items.PackageReference ?? []) {
        if (item.Identity.toLowerCase().startsWith('solusagent.') && (!Object.hasOwn(packages, item.Identity) || item.Version !== `[${version}]`))
            throw new Error('version: SolusAgent direct PackageReference must use exact [version] equality');
    }
}

function checkAssets(project: string, cache: string, feed: string, version: string, projects: string[]): void {
    const assets = JSON.parse(readFileSync(join(dirname(project), 'obj/project.assets.json'), 'utf8'));
    const folders = Object.keys(assets.packageFolders);
    if (folders.length !== 1 || realpathSync(folders[0]) !== realpathSync(cache)) throw new Error('source: restore used a foreign package cache or fallback folder');
    for (const [identity, value] of Object.entries(assets.libraries) as [string, { type: string; path: string; msbuildProject?: string }][]) {
        if (value.type === 'project') {
            const path = resolve(dirname(project), value.msbuildProject ?? value.path);
            if (!projects.includes(path)) throw new Error('boundary: restored graph contains a foreign project');
            continue;
        }
        const slash = identity.lastIndexOf('/');
        const id = identity.slice(0, slash);
        if (!id.toLowerCase().startsWith('solusagent.')) continue;
        if (!Object.hasOwn(packages, id) || identity.slice(slash + 1) !== version) throw new Error('version: resolved SolusAgent package version mismatch');
        const metadata = JSON.parse(readFileSync(join(cache, value.path, '.nupkg.metadata'), 'utf8'));
        if (metadata.source.replace(/\/$/, '') !== feed.replace(/\/$/, '')) throw new Error('source: SolusAgent package came from a different feed');
    }
}

export function verifyConsumers(input: VerificationOptions): string {
    const version = requireVersion(input.version);
    const root = realpathSync(input.consumers ?? resolve(repository, 'tests/PackageConsumers'));
    const selected = selectScenarios(root, input);
    for (const scenario of selected) manifest(scenario.path); // Validate the complete set before any subset runs.
    const feed = source(input.feed);
    const tooling = source(input.toolingFeed ?? 'https://api.nuget.org/v3/index.json');
    const workspace = realpathSync(mkdtempSync(join(tmpdir(), 'solus-package-consumers-')));
    if (within(workspace, repository)) throw new Error('boundary: clean workspace must be outside the repository');
    try {
        writeFileSync(join(workspace, 'global.json'), readFileSync(join(repository, 'global.json')));
        for (const selection of selected) {
            const original = selection.path;
            const name = relative(root, original);
            const scenario = join(workspace, name);
            cpSync(original, scenario, { recursive: true, filter: path => !ignored.has(path.split(/[\\/]/).at(-1)!) });
            const inventory = manifest(scenario);
            const cache = join(scenario, '.local/packages');
            mkdirSync(cache, { recursive: true });
            const config = join(scenario, '.local/NuGet.Config');
            writeFileSync(config, `<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources><clear /><add key="selected" value="${xml(feed)}" /><add key="tooling" value="${xml(tooling)}" /></packageSources>\n  <packageSourceMapping><clear /><packageSource key="selected"><package pattern="SolusAgent.*" /></packageSource><packageSource key="tooling"><package pattern="*" /></packageSource></packageSourceMapping>\n  <fallbackPackageFolders><clear /></fallbackPackageFolders>\n  <disabledPackageSources><clear /></disabledPackageSources>\n</configuration>\n`);
            const env: NodeJS.ProcessEnv = { ...authenticatedEnvironment(feed, process.env), NUGET_PACKAGES: cache, NUGET_HTTP_CACHE_PATH: join(scenario, '.local/http-cache'), NUGET_PLUGINS_CACHE_PATH: join(scenario, '.local/plugin-cache') };
            const properties = [`-p:SolusAgentVersion=${version}`, '-p:Configuration=Release', '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ManagePackageVersionsCentrally=false', '-p:NuGetAudit=false', '-p:RestoreFallbackFolders=', `-p:RestorePackagesPath=${cache}`];
            const evaluations = inventory.projects.map(project => evaluate(project, scenario, properties, env));
            inventory.projects.forEach((project, index) => checkBoundary(project, evaluations[index], scenario, inventory.projects, cache, version));
            if (new Set(evaluations.map(value => value.Properties.AssemblyName.toLowerCase())).size !== evaluations.length) throw new Error('boundary: consumer assembly identities must be distinct');
            for (const entrypoint of inventory.entrypoints) {
                if (evaluations[inventory.projects.indexOf(entrypoint)].Properties.OutputType !== 'Exe') throw new Error('scenario: required entrypoint is not executable');
            }
            for (const project of inventory.projects) {
                run('dotnet', ['restore', project, '--configfile', config, '--no-http-cache', ...properties], scenario, env);
                checkAssets(project, cache, feed, version, inventory.projects);
                checkBoundary(project, evaluate(project, scenario, properties, env), scenario, inventory.projects, cache, version);
                run('dotnet', ['build', project, '--configuration', 'Release', '--no-restore', ...properties], scenario, env);
            }
            for (const entrypoint of inventory.entrypoints) {
                const summary = run('dotnet', [join(dirname(entrypoint), 'bin/Release/net10.0', `${evaluations[inventory.projects.indexOf(entrypoint)].Properties.AssemblyName}.dll`), ...selection.arguments], scenario, env, true);
                if (summary.trim()) console.log(summary.trim());
            }
            console.log(`Scenario ${name.toLowerCase()} passed: ${inventory.projects.length} clean projects, ${inventory.entrypoints.length} executed entrypoints, exact ${version}.`);
        }
        return workspace;
    } finally { if (!input.keepWorkspace) rmSync(workspace, { recursive: true, force: true }); }
}

if (isMain(import.meta.url)) {
    try {
        const args = options(['--feed', '--version', '--scenario', '--consumers', '--tooling-feed', '--keep-workspace', '--require', '--live', '--key-file', '--profile'], ['--scenario'], ['--keep-workspace', '--live']);
        const workspace = verifyConsumers({ feed: required(args, '--feed'), version: required(args, '--version'), scenarios: (args['--scenario'] ?? []).flatMap(value => value.split(',')), consumers: args['--consumers']?.[0], toolingFeed: args['--tooling-feed']?.[0], keepWorkspace: !!args['--keep-workspace'], requiredScenarios: args['--require']?.[0].split(','), live: !!args['--live'], keyFile: args['--key-file']?.[0], profile: args['--profile']?.[0] });
        if (args['--keep-workspace']) console.log(`Retained clean workspace: ${workspace}`);
    } catch (error) { reportFailure(error); }
}
