import { readFileSync, lstatSync, realpathSync, readdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { api, ids, dependencies, sha, fileSha, inside, version, actionSource, selectedSource, apiSource } from './common.ts';
import { readPackage, checkDependencies } from './package-archive.ts';
import { inspectArchive } from '../../actions/logging/archive.mjs';

export function packageSetDigest(packages: any[]) {
  return sha(JSON.stringify([...packages].sort((a, b) => a.id < b.id ? -1 : 1).map(p => ({ id: p.id, version: p.version, sha256: p.sha256, dllSha256: p.dllSha256, dependencies: p.dependencies }))));
}
export function candidateDigest(manifest: any) {
  return sha(JSON.stringify({ source: manifest.source, version: manifest.version, packageSetSha256: manifest.packageSetSha256,
    agentApi: manifest.agentApi, payload: { version: manifest.payload.version, sha256: manifest.payload.sha256, runtimeIdentifier: manifest.payload.runtimeIdentifier,
      manifestSha256: manifest.payload.manifestSha256, ociManifestSha256: manifest.payload.ociManifestSha256, ociConfigSha256: manifest.payload.ociConfigSha256 }, action: manifest.action, destinations: manifest.destinations }));
}
export function member(directory: string, name: string) {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,180}$/.test(name) || name.includes('..')) throw new Error('manifest_path');
  const path = join(directory, name);
  if (!lstatSync(path).isFile() || lstatSync(path).isSymbolicLink() || !inside(realpathSync(path), realpathSync(directory))) throw new Error('manifest_path');
  return path;
}
export function validateCandidate(feed: string, directory: string, manifest: any) {
  if (!manifest || manifest.format !== 1 || manifest.status !== 'prepared-not-published' || !Array.isArray(manifest.packages)
    || manifest.packages.length !== 4 || new Set(manifest.packages.map((p: any) => p.id)).size !== 4
    || !manifest.packages.every((p: any) => ids.includes(p.id)) || JSON.stringify(manifest.agentApi) !== JSON.stringify(api)) throw new Error('manifest_selection');
  version(manifest.version);
  if (!manifest.source || !/^[a-f0-9]{40}$/.test(manifest.source.commit) || !/^[a-f0-9]{40}$/.test(manifest.source.tree)
    || typeof manifest.source.dirty !== 'boolean' || !/^[a-f0-9]{64}$/.test(manifest.source.inventorySha256)
    || manifest.action?.sourceCommit !== actionSource || !manifest.action.files || Object.keys(manifest.action.files).length < 6
    || Object.entries(manifest.action.files).some(([name, digest]) => !/^actions\/logging\/[A-Za-z0-9.-]+$/.test(name) || !/^[a-f0-9]{64}$/.test(String(digest)))) throw new Error('manifest_source');
  if (lstatSync(feed).isSymbolicLink() || lstatSync(directory).isSymbolicLink()
    || JSON.stringify(readdirSync(feed).sort()) !== JSON.stringify(manifest.packages.map((p: any) => p.file).sort())) throw new Error('candidate_feed_inventory');
  const declarations: Record<string, string> = {};
  for (const p of manifest.packages) {
    if (p.version !== manifest.version || p.file !== `${p.id}.${manifest.version}.nupkg` || fileSha(member(feed, p.file)) !== p.sha256) throw new Error('candidate_hash');
    const inspected = readPackage(join(feed, p.file), p.id, p.version);
    const expected = dependencies(p.id, p.version); checkDependencies(inspected.dependencies, expected);
    if (JSON.stringify(p.dependencies) !== JSON.stringify(expected) || inspected.dllSha256 !== p.dllSha256) throw new Error('candidate_dependencies');
    declarations[`${p.id}.dll`] = p.dllSha256;
  }
  const payloadDirectory = resolve(directory, manifest.payload.directory);
  if (!inside(payloadDirectory, directory) || lstatSync(payloadDirectory).isSymbolicLink() || manifest.payload.runtimeIdentifier !== 'linux-x64' || manifest.payload.version !== manifest.version) throw new Error('manifest_payload');
  const archive = readFileSync(member(payloadDirectory, manifest.payload.filename));
  const verified = inspectArchive(archive, manifest.payload.sha256, manifest.payload.version);
  if (sha(verified.files.get('payload-manifest.json')!) !== manifest.payload.manifestSha256) throw new Error('payload_manifest_hash');
  const ociBytes = readFileSync(member(payloadDirectory, 'oci-manifest.json')); const oci = JSON.parse(ociBytes.toString('utf8'));
  const config = readFileSync(member(payloadDirectory, 'oci-config.json'));
  if (sha(ociBytes) !== manifest.payload.ociManifestSha256 || sha(config) !== manifest.payload.ociConfigSha256
    || oci.schemaVersion !== 2 || oci.layers?.length !== 1 || oci.layers[0].digest !== `sha256:${manifest.payload.sha256}`
    || oci.layers[0].size !== archive.length || oci.config?.digest !== `sha256:${sha(config)}` || oci.config.size !== config.length
    || oci.annotations?.['org.opencontainers.image.version'] !== manifest.version || oci.annotations?.['org.opencontainers.image.revision'] !== manifest.source.commit) throw new Error('oci_manifest');
  const expectedUrl = `https://ghcr.io/v2/solusquest/solus-logging-cli/blobs/sha256:${manifest.payload.sha256}`;
  if (manifest.destinations?.nuget !== api.registry || manifest.destinations.cli?.blobUrl !== expectedUrl
    || manifest.destinations.cli.versionTag !== manifest.version || manifest.destinations.cli.requiredVisibility !== 'private'
    || manifest.destinations.cli.availability !== 'unpublished') throw new Error('manifest_destination');
  for (const id of ['Solus.Telemetry.Contracts', 'Solus.Logging', 'Solus.Logging.Supabase'])
    if (sha(verified.files.get(`${id}.dll`) ?? Buffer.alloc(0)) !== declarations[`${id}.dll`]) throw new Error('payload_package_binary');
  if (verified.files.has('SolusAgent.Api.dll') || verified.files.has('SolusAgent.Telemetry.dll')) throw new Error('fixture_in_payload');
  if (packageSetDigest(manifest.packages) !== manifest.packageSetSha256 || candidateDigest(manifest) !== manifest.candidateSha256) throw new Error('manifest_digest');
  return { archive, payloadDirectory };
}

// Local-only publication preflight. It never acquires a credential or sends anything.
export function publicationFiles(feed: string, directory: string, manifest: any, genuineApiArchive: string) {
  validateCandidate(feed, directory, manifest);
  if (manifest.source.dirty || manifest.validationBaseline?.role !== 'genuine-published-api-bytes'
    || manifest.validationBaseline.packageSha256 !== api.packageSha256 || manifest.validationBaseline.dllSha256 !== api.dllSha256
    || fileSha(genuineApiArchive) !== api.packageSha256 || readPackage(genuineApiArchive, api.id, api.version).dllSha256 !== api.dllSha256) throw new Error('genuine_baseline_publication_gate');
  if (JSON.stringify(manifest.source) !== JSON.stringify(selectedSource()) || manifest.validationBaseline.sourceCommit !== api.sourceCommit
    || manifest.validationBaseline.sourceSha256 !== apiSource() || manifest.validationBaseline.neverPublish !== true) throw new Error('publication_source_provenance');
  if (manifest.packages.some((p: any) => p.id === api.id || p.role?.includes('fixture'))) throw new Error('fixture_publication_forbidden');
  return [...manifest.packages.map((p: any) => member(feed, p.file)), member(resolve(directory, manifest.payload.directory), manifest.payload.filename)];
}
