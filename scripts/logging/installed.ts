import { cpSync, readFileSync, writeFileSync, mkdirSync, realpathSync, existsSync } from 'node:fs';
import { join, dirname, resolve, isAbsolute, relative } from 'node:path';
import { root, api, ids, inside, files, nugetConfig, cleanEnvironment, text, run, fileSha } from './common.ts';

const forbidden = [...ids, api.id, 'Solus.Logging.Cli', 'Solus.Logging.Tests', 'SolusAgent.ContractTests'];
const same = (a: string[], b: string[]) => JSON.stringify([...a].sort()) === JSON.stringify([...b].sort());

export function inventory(directory: string) {
  const manifest = JSON.parse(readFileSync(join(directory, 'scenario.json'), 'utf8'));
  if (manifest.format !== 1 || !['product', 'library'].includes(manifest.kind) || !Array.isArray(manifest.projects) || !manifest.projects.length
    || manifest.projects.some((p: any) => typeof p !== 'string' || isAbsolute(p) || !p.endsWith('.csproj') || !inside(resolve(directory, p), directory))
    || new Set(manifest.projects).size !== manifest.projects.length || !manifest.projects.includes(manifest.entrypoint)
    || !Array.isArray(manifest.packages) || new Set(manifest.packages).size !== manifest.packages.length
    || manifest.packages.some((id: string) => ![...ids, api.id].includes(id))
    || !same(manifest.projects.map((p: string) => resolve(directory, p)), files(directory).filter(p => p.endsWith('.csproj')))) throw new Error('scenario_inventory');
  if (manifest.kind === 'product' && !['scribe', 'apr'].includes(manifest.shape)) throw new Error('scenario_shape');
  return manifest;
}

function evaluate(project: string, directory: string, properties: string[], env: NodeJS.ProcessEnv) {
  const output = text('dotnet', ['msbuild', project, '-nologo', ...properties,
    '-getProperty:TargetFramework,IsPackable,PublishAot,PublishTrimmed,AssemblyName,OutputType,MSBuildToolsPath', '-getItem:Compile,ProjectReference,PackageReference,Reference'], directory, env);
  const result = JSON.parse(output.slice(output.indexOf('{')));
  const expanded = join(directory, '.validation/evaluated.xml');
  run('dotnet', ['msbuild', project, '-nologo', ...properties, `-preprocess:${expanded}`], directory, env);
  result.imports = [...readFileSync(expanded, 'utf8').matchAll(/<!--([\s\S]*?)-->/g)].filter(m => m[1].trimStart().startsWith('===================='))
    .flatMap(m => m[1].split(/\r?\n/).map(s => s.trim()).filter(isAbsolute));
  return result;
}
function boundary(project: string, directory: string, specification: any, evaluation: any, cache: string, versions: Record<string, string>) {
  const props = evaluation.Properties; const items = evaluation.Items;
  if (props.TargetFramework !== 'net10.0' || props.IsPackable !== 'false' || props.PublishAot !== 'false' || props.PublishTrimmed !== 'false'
    || forbidden.some(n => n.toLowerCase() === props.AssemblyName.toLowerCase())) throw new Error('boundary_identity');
  for (const reference of items.ProjectReference ?? []) if (!specification.projects.map((p: string) => resolve(directory, p)).includes(resolve(dirname(project), reference.FullPath || reference.Identity))) throw new Error('boundary_project');
  for (const item of items.Compile ?? []) if (!inside(realpathSync(resolve(dirname(project), item.FullPath || item.Identity)), directory)) throw new Error('boundary_compile');
  if (items.Reference?.length) throw new Error('boundary_assembly');
  const sdk = props.MSBuildToolsPath;
  for (const imported of evaluation.imports) if (![directory, cache, sdk, resolve(sdk, '../../packs'), resolve(sdk, '../../sdk-manifests')].some(p => inside(imported, p))) throw new Error('boundary_import');
  const references = items.PackageReference ?? [];
  if (!same(references.map((p: any) => p.Identity), specification.packages) || references.some((p: any) => p.Version !== `[${versions[p.Identity]}]`)) throw new Error('boundary_package_version');
}
function assets(project: string, directory: string, specification: any, cache: string, feed: string, baselineFeed: string, manifest: any, baseline: any) {
  const data = JSON.parse(readFileSync(join(dirname(project), 'obj/project.assets.json'), 'utf8'));
  const folders = Object.keys(data.packageFolders);
  if (folders.length !== 1 || realpathSync(folders[0]) !== realpathSync(cache)) throw new Error('restore_cache');
  const expected = new Map(manifest.packages.map((p: any) => [p.id, { ...p, feed }]));
  expected.set(api.id, { id: api.id, version: api.version, sha256: baseline.packageSha256, dllSha256: baseline.dllSha256, feed: baselineFeed });
  const found: string[] = [];
  for (const [identity, item] of Object.entries(data.libraries) as [string, any][]) {
    if (item.type === 'project') {
      if (!specification.projects.map((p: string) => resolve(directory, p)).includes(resolve(dirname(project), item.msbuildProject ?? item.path))) throw new Error('restore_project');
      continue;
    }
    const at = identity.lastIndexOf('/'); const id = identity.slice(0, at); const selected: any = expected.get(id);
    if (!selected || identity.slice(at + 1) !== selected.version) throw new Error('restore_version');
    const packageRoot = join(cache, item.path); const meta = JSON.parse(readFileSync(join(packageRoot, '.nupkg.metadata'), 'utf8'));
    if (resolve(meta.source) !== resolve(selected.feed)) throw new Error('restore_source');
    const nupkg = join(packageRoot, `${id.toLowerCase()}.${selected.version}.nupkg`);
    if (fileSha(nupkg) !== selected.sha256 || fileSha(join(packageRoot, `lib/net10.0/${id}.dll`)) !== selected.dllSha256) throw new Error('restore_bytes');
    found.push(id);
  }
  if (specification.packages.some((id: string) => !found.includes(id))) throw new Error('restore_missing');
  return found;
}

// This exact path is also invoked by deliberate cold-restore and topology negatives.
export function installScenario(original: string, workspace: string, name: string, feed: string, baselineFeed: string, manifest: any, baseline: any) {
  const directory = join(workspace, name);
  cpSync(original, directory, { recursive: true, filter: p => !['bin', 'obj', '.local', '.validation'].includes(p.split(/[\\/]/).at(-1)!) });
  const specification = inventory(directory);
  const home = join(directory, '.validation'); mkdirSync(home); const env = cleanEnvironment(home); mkdirSync(env.NUGET_PACKAGES!, { recursive: true });
  writeFileSync(join(directory, 'global.json'), readFileSync(join(root, 'global.json')));
  const config = join(home, 'NuGet.Config'); writeFileSync(config, nugetConfig(feed, baselineFeed));
  const properties = [`-p:TelemetryVersion=${manifest.version}`, `-p:AgentApiVersion=${api.version}`, '-p:Configuration=Release',
    '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ManagePackageVersionsCentrally=false', '-p:NuGetAudit=false', '-p:RestoreFallbackFolders=', `-p:RestorePackagesPath=${env.NUGET_PACKAGES}`];
  const versions = Object.fromEntries(manifest.packages.map((p: any) => [p.id, p.version])); versions[api.id] = api.version;
  const resolved = new Set<string>();
  for (const member of specification.projects) {
    const project = join(directory, member);
    boundary(project, directory, specification, evaluate(project, directory, properties, env), env.NUGET_PACKAGES!, versions);
    run('dotnet', ['restore', project, '--configfile', config, '--no-http-cache', ...properties], directory, env);
    assets(project, directory, specification, env.NUGET_PACKAGES!, feed, baselineFeed, manifest, baseline).forEach(id => resolved.add(id));
    boundary(project, directory, specification, evaluate(project, directory, properties, env), env.NUGET_PACKAGES!, versions);
    run('dotnet', ['build', project, '-c', 'Release', '--no-restore', ...properties], directory, env);
  }
  const entry = join(directory, specification.entrypoint); const evaluation = evaluate(entry, directory, properties, env);
  if (evaluation.Properties.OutputType !== 'Exe') throw new Error('scenario_entrypoint');
  const executable = join(dirname(entry), 'bin/Release/net10.0', `${evaluation.Properties.AssemblyName}.dll`);
  for (const id of resolved) {
    const expected = id === api.id ? baseline.dllSha256 : manifest.packages.find((p: any) => p.id === id).dllSha256;
    if (fileSha(join(dirname(executable), `${id}.dll`)) !== expected) throw new Error('executed_dependency_bytes');
  }
  return { name, directory, specification, executable, env, cache: env.NUGET_PACKAGES!, resolved: [...resolved].sort() };
}
