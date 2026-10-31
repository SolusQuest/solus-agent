import { mkdirSync, copyFileSync, readFileSync, writeFileSync, rmSync, constants } from 'node:fs';
import { basename, dirname, join, relative } from 'node:path';
import { root, api, apiSource, files, cleanEnvironment, nugetConfig, run, sha, fileSha, dependencies } from './common.ts';
import { readPackage, canonicalPackage, checkDependencies } from './package-archive.ts';

export function stageProject(stage: string, id: string, selected: string) {
  const directory = join(stage, 'src', id); mkdirSync(directory, { recursive: true });
  for (const path of files(join(root, 'src', id))) {
    const target = join(directory, relative(join(root, 'src', id), path)); mkdirSync(dirname(target), { recursive: true }); copyFileSync(path, target);
  }
  const project = join(directory, `${id}.csproj`);
  let source = readFileSync(project, 'utf8');
  const found: string[] = [];
  source = source.replace(/<ProjectReference\s+Include="([^"]+)"\s*\/>/g, (_, reference: string) => {
    const name = basename(reference, '.csproj'); found.push(name);
    return `<PackageReference Include="${name}" Version="[${name === api.id ? api.version : selected}]" />`;
  });
  const expected = id === api.id ? [] : id === 'Solus.Logging.Cli' ? ['Solus.Logging', 'Solus.Logging.Supabase', 'Solus.Telemetry.Contracts'] : Object.keys(dependencies(id, selected));
  if (JSON.stringify(found.sort()) !== JSON.stringify(expected.sort())) throw new Error('source_project_graph_changed');
  if (id === api.id) source = source.replace('</Project>', '<PropertyGroup><Description>TEST ONLY source-baseline fixture; never the published Agent API archive.</Description></PropertyGroup></Project>');
  writeFileSync(project, source); return project;
}

export function setupStage(stage: string) {
  copyFileSync(join(root, 'Directory.Build.props'), join(stage, 'Directory.Build.props')); copyFileSync(join(root, 'global.json'), join(stage, 'global.json'));
}
export function packageProperties(selected: string, commit: string, stage: string) {
  return [`-p:Version=${selected}`, `-p:PackageVersion=${selected}`, `-p:InformationalVersion=${selected}`, '-p:IncludeSourceRevisionInInformationalVersion=false',
    '-p:DebugType=none', '-p:Deterministic=true', `-p:PathMap=${stage}=/_/`, '-p:EnableSourceControlManagerQueries=false', '-p:EnableSourceLink=false',
    `-p:RepositoryCommit=${commit}`, '-p:RepositoryBranch=', '-p:NuGetAudit=false'];
}
export function fixtureApi(stage: string, destination: string) {
  const sourceSha256 = apiSource(); const project = stageProject(stage, api.id, api.version); const env = cleanEnvironment(join(stage, 'fixture-home'));
  const raw = join(stage, 'fixture-raw'); const config = join(stage, 'fixture.config');
  writeFileSync(config, nugetConfig(destination, destination));
  run('dotnet', ['pack', project, '-c', 'Release', '-o', raw, `-p:RestoreConfigFile=${config}`, ...packageProperties(api.version, api.sourceCommit, stage),
    `-p:InformationalVersion=${api.version}+source-baseline-fixture.${sourceSha256.slice(0, 12)}`], stage, env);
  const file = `${api.id}.${api.version}.nupkg`; const inspected = readPackage(join(raw, file), api.id, api.version);
  checkDependencies(inspected.dependencies, {});
  writeFileSync(join(destination, file), canonicalPackage(inspected.entries, api.id, api.version), { flag: 'wx' });
  const packageSha256 = fileSha(join(destination, file));
  if (packageSha256 === api.packageSha256 || inspected.dllSha256 === api.dllSha256) throw new Error('fixture_identity');
  return { role: 'source-baseline-fixture', file, packageSha256, dllSha256: inspected.dllSha256, sourceSha256, sourceCommit: api.sourceCommit, neverPublish: true };
}
export function actualApi(archive: string, destination: string) {
  if (fileSha(archive) !== api.packageSha256) throw new Error('genuine_api_archive_required');
  const inspected = readPackage(archive, api.id, api.version);
  if (inspected.dllSha256 !== api.dllSha256) throw new Error('genuine_api_binary_required');
  checkDependencies(inspected.dependencies, {});
  const file = `${api.id}.${api.version}.nupkg`; copyFileSync(archive, join(destination, file), constants.COPYFILE_EXCL);
  return { role: 'genuine-published-api-bytes', file, packageSha256: api.packageSha256, dllSha256: api.dllSha256, sourceSha256: apiSource(), sourceCommit: api.sourceCommit, neverPublish: true };
}
export function packLibrary(stage: string, id: string, selected: string, commit: string, feed: string, baseline: string) {
  const project = stageProject(stage, id, selected); const env = cleanEnvironment(join(stage, `${id}-home`));
  const config = join(stage, `${id}.config`); writeFileSync(config, nugetConfig(feed, baseline));
  const raw = join(stage, `${id}-raw`); const file = `${id}.${selected}.nupkg`;
  run('dotnet', ['pack', project, '-c', 'Release', '-o', raw, `-p:RestoreConfigFile=${config}`, ...packageProperties(selected, commit, stage)], stage, env);
  const inspected = readPackage(join(raw, file), id, selected); const expected = dependencies(id, selected); checkDependencies(inspected.dependencies, expected);
  const output = join(feed, file); writeFileSync(output, canonicalPackage(inspected.entries, id, selected), { flag: 'wx' });
  const normalized = readPackage(output, id, selected); checkDependencies(normalized.dependencies, expected);
  for (const name of [`${id}.nuspec`, `lib/net10.0/${id}.dll`, `lib/net10.0/${id}.xml`, '[Content_Types].xml'])
    if (!normalized.entries.get(name)!.equals(inspected.entries.get(name)!)) throw new Error('package_semantics_changed');
  return { id, version: selected, file, sha256: fileSha(output), dllSha256: inspected.dllSha256, dependencies: expected };
}
