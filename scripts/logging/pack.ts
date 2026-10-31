import { copyFileSync, mkdirSync, readFileSync, writeFileSync, existsSync, rmSync, constants } from 'node:fs';
import { resolve, dirname, join, basename, relative } from 'node:path';
import { root, api, ids, actionSource, files, sha, fileSha, cleanEnvironment, nugetConfig, options, required, version, scratch, selectedSource, run, main, report } from './common.ts';
import { setupStage, fixtureApi, actualApi, packLibrary, stageProject, packageProperties } from './pack-projects.ts';
import { buildPayload } from './build-payload.ts';
import { inspectArchive } from '../../actions/logging/archive.mjs';
import { packageSetDigest, candidateDigest, validateCandidate } from './manifest.ts';

export async function pack(selected: string, output: string, apiArchive?: string) {
  version(selected); output = resolve(output);
  const parent = dirname(output); const manifestPath = join(parent, 'logging-release.json');
  const payloadPath = join(parent, 'logging-candidate-cli'); const baselinePath = join(parent, 'logging-validation-api');
  if ([output, manifestPath, payloadPath, baselinePath].some(existsSync)) throw new Error('candidate_overwrite');
  const stage = scratch('solus-logging-pack-');
  const created: string[] = [];
  let completed = false;
  try {
    setupStage(stage); const source = selectedSource();
    const sdk = run('dotnet', ['--version'], stage, cleanEnvironment(join(stage, 'sdk-home'))).toString().trim();
    const expectedSdk = JSON.parse(readFileSync(join(root, 'global.json'), 'utf8')).sdk.version;
    if (sdk !== expectedSdk || process.versions.node !== '24.21.0') throw new Error('candidate_toolchain_requires_pinned_SDK_and_Node24.21.0');
    const feed = join(stage, 'feed'); const baseline = join(stage, 'baseline'); const payload = join(stage, 'payload');
    mkdirSync(feed); mkdirSync(baseline);
    const baselineEvidence = apiArchive ? actualApi(resolve(apiArchive), baseline) : fixtureApi(stage, baseline);
    const packages = ids.map(id => packLibrary(stage, id, selected, source.commit, feed, baseline));
    const project = stageProject(stage, 'Solus.Logging.Cli', selected); const env = cleanEnvironment(join(stage, 'payload-home'));
    const config = join(stage, 'payload.config'); writeFileSync(config, nugetConfig(feed, baseline, true));
    const built = await buildPayload(payload, selected, project, env, [...packageProperties(selected, source.commit, stage), `-p:RestoreConfigFile=${config}`]);
    const verified = inspectArchive(readFileSync(join(payload, built.filename)), built.sha256, selected);
    for (const p of packages.filter(p => p.id !== 'SolusAgent.Telemetry')) if (sha(verified.files.get(`${p.id}.dll`) ?? Buffer.alloc(0)) !== p.dllSha256) throw new Error('payload_package_binary');
    const actionFiles: Record<string, string> = {};
    for (const path of files(join(root, 'actions/logging'))) {
      const name = relative(root, path).split('\\').join('/'); const current = readFileSync(path);
      if (!current.equals(run('git', ['show', `${actionSource}:${name}`]))) throw new Error('action_source_changed'); actionFiles[name] = sha(current);
    }
    const manifest: any = {
      format: 1, status: 'prepared-not-published', source, version: selected,
      buildTools: { dotnetSdk: sdk, node: process.versions.node, zlib: process.versions.zlib,
        runtimePack: Object.keys(JSON.parse(verified.files.get('Solus.Logging.Cli.deps.json')!.toString('utf8')).libraries).find(name => name.startsWith('runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/')) },
      packageDirectory: basename(output), packages, packageSetSha256: packageSetDigest(packages), agentApi: api,
      payload: { ...built, directory: basename(payloadPath), manifestSha256: sha(verified.files.get('payload-manifest.json')!), fileSha256: JSON.parse(verified.files.get('payload-manifest.json')!.toString('utf8')).files },
      action: { sourceCommit: actionSource, files: actionFiles },
      destinations: { nuget: api.registry, cli: { registry: 'ghcr.io', repository: 'solusquest/solus-logging-cli', versionTag: selected,
        blobUrl: `https://ghcr.io/v2/solusquest/solus-logging-cli/blobs/sha256:${built.sha256}`, requiredVisibility: 'private', availability: 'unpublished', authentication: 'operator-supplied repository-scoped registry bearer token' } },
      validationBaseline: { ...baselineEvidence, directory: basename(baselinePath), existingApiIsNotANewPublication: true },
    };
    // A future immutable version tag must retain an OCI manifest referring to the
    // original archive blob. A bare unreferenced blob is not a publication.
    const ociConfig = Buffer.from('{}\n');
    const oci = Buffer.from(JSON.stringify({ schemaVersion: 2, mediaType: 'application/vnd.oci.image.manifest.v1+json',
      artifactType: 'application/vnd.solus.logging.cli.v1', config: { mediaType: 'application/vnd.oci.empty.v1+json', digest: `sha256:${sha(ociConfig)}`, size: ociConfig.length },
      layers: [{ mediaType: 'application/vnd.solus.logging.cli.payload.v1+gzip', digest: `sha256:${built.sha256}`, size: readFileSync(join(payload, built.filename)).length,
        annotations: { 'org.opencontainers.image.title': built.filename } }], annotations: { 'org.opencontainers.image.version': selected, 'org.opencontainers.image.revision': source.commit } }) + '\n');
    writeFileSync(join(payload, 'oci-config.json'), ociConfig); writeFileSync(join(payload, 'oci-manifest.json'), oci);
    manifest.payload.ociManifestSha256 = sha(oci); manifest.payload.ociConfigSha256 = sha(ociConfig);
    manifest.candidateSha256 = candidateDigest(manifest);
    mkdirSync(parent, { recursive: true });
    for (const path of [output, payloadPath, baselinePath]) { mkdirSync(path); created.push(path); }
    for (const p of packages) copyFileSync(join(feed, p.file), join(output, p.file), constants.COPYFILE_EXCL);
    for (const path of files(payload)) copyFileSync(path, join(payloadPath, basename(path)), constants.COPYFILE_EXCL);
    copyFileSync(join(baseline, baselineEvidence.file), join(baselinePath, baselineEvidence.file), constants.COPYFILE_EXCL);
    validateCandidate(output, parent, manifest);
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
    completed = true;
    console.log(JSON.stringify({ status: manifest.status, source: source.commit, provisional: source.dirty, baselineRole: baselineEvidence.role,
      packageSetSha256: manifest.packageSetSha256, candidateSha256: manifest.candidateSha256, packages: packages.map(p => ({ id: p.id, version: p.version, sha256: p.sha256 })), payloadSha256: built.sha256 }));
    return manifest;
  } finally {
    if (!completed) for (const path of created) rmSync(path, { recursive: true, force: true });
    rmSync(stage, { recursive: true, force: true });
  }
}

if (main(import.meta.url)) {
  try { const args = options(['--version', '--output', '--api-package']); await pack(version(required(args, '--version')), resolve(required(args, '--output')), args['--api-package']); }
  catch (error) { report(error); }
}
