import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync } from 'node:fs';
import { resolve, dirname, join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { root, api, apiSource, actionSource, sha, fileSha, scratch, options, required, run, main, report, inside } from './common.ts';
import { readPackage } from './package-archive.ts';
import { actualApi } from './pack-projects.ts';
import { validateCandidate } from './manifest.ts';
import { inventory, installScenario } from './installed.ts';
import { supportedHost } from '../../actions/logging/input.mjs';
import { uploaderProof } from './uploader-proof.ts';

export async function verifyConsumers(input: { feed: string; manifest: string; scenario: string; apiPackage?: string; packagesOnly?: boolean; consumers?: string; report?: string }) {
  const path = resolve(input.manifest); const directory = dirname(path); const feed = resolve(input.feed);
  const manifest = JSON.parse(readFileSync(path, 'utf8')); const selected = validateCandidate(feed, directory, manifest);
  if (resolve(directory, manifest.packageDirectory) !== feed || manifest.action.sourceCommit !== actionSource) throw new Error('selected_source');
  if (!input.packagesOnly && !await supportedHost()) throw new Error('full_qualification_requires_Ubuntu24.04_x64');
  const source = resolve(input.consumers ?? join(root, 'tests/TelemetryConsumers'));
  const all = readdirSync(source).filter(name => !name.startsWith('.')).map(name => ({ name, path: join(source, name) }));
  for (const scenario of all) inventory(scenario.path);
  if (input.scenario === 'all' && !['Scribe', 'Apr', 'InstalledSink'].every(name => all.some(s => s.name === name))) throw new Error('required_scenario');
  const scenarios = input.scenario === 'all' ? all : all.filter(s => s.name.toLowerCase() === input.scenario.toLowerCase());
  if (!scenarios.length) throw new Error('required_scenario');
  const workspace = scratch('solus-telemetry-consumers-');
  if (inside(workspace, root)) throw new Error('independent_workspace');
  try {
    let baseline: any; let baselineFeed: string;
    if (input.apiPackage) { baselineFeed = join(workspace, 'genuine-api'); mkdirSync(baselineFeed); baseline = actualApi(resolve(input.apiPackage), baselineFeed); }
    else {
      const declared = manifest.validationBaseline;
      if (!declared || declared.sourceSha256 !== apiSource() || declared.neverPublish !== true) throw new Error('baseline_provenance');
      baselineFeed = resolve(directory, declared.directory);
      if (!inside(baselineFeed, directory) || declared.file !== `${api.id}.${api.version}.nupkg`) throw new Error('baseline_provenance');
      const archive = join(baselineFeed, declared.file); const inspected = readPackage(archive, api.id, api.version);
      if (fileSha(archive) !== declared.packageSha256 || inspected.dllSha256 !== declared.dllSha256) throw new Error('baseline_hash');
      if (declared.role === 'genuine-published-api-bytes') {
        if (declared.packageSha256 !== api.packageSha256 || declared.dllSha256 !== api.dllSha256) throw new Error('genuine_api_archive_required');
      } else if (declared.role !== 'source-baseline-fixture' || declared.packageSha256 === api.packageSha256
        || !inspected.nuspec.includes('TEST ONLY source-baseline fixture')) throw new Error('baseline_provenance');
      baseline = declared;
    }
    const products: any[] = []; const installed: any[] = [];
    for (const scenario of scenarios) {
      const current = installScenario(scenario.path, workspace, scenario.name, feed, baselineFeed, manifest, baseline);
      if (current.specification.kind === 'library') {
        const observed = JSON.parse(run('dotnet', [current.executable], current.directory, current.env).toString('utf8'));
        if (observed.installedLibraries !== true || observed.transportInvoked !== false) throw new Error('installed_library_probe');
      } else {
        const marker = join(workspace, `${scenario.name}-invocations`);
        const output = run('dotnet', [current.executable, randomUUID(), '{"team":"synthetic","nested":[true,null,{"costCenter":"test"}]}', marker], current.directory, current.env, [current.specification.businessExit]);
        if (output.length > 300000) throw new Error('product_output');
        const envelope = JSON.parse(output.toString('utf8'));
        if (!envelope.business || typeof envelope.telemetryJson !== 'string' || envelope.telemetryDiagnostic !== 'None') throw new Error('product_telemetry');
        products.push({ ...current, envelope, marker, exitCode: current.specification.businessExit });
        for (const caller of ['[]', '{', '{"x":1,"\\u0078":2}', JSON.stringify({ padding: 'x'.repeat(32769) })]) {
          const unavailable = JSON.parse(run('dotnet', [current.executable, randomUUID(), caller, join(workspace, `${scenario.name}-${randomUUID()}`)], current.directory, current.env, [current.specification.businessExit]).toString('utf8'));
          if (unavailable.telemetryJson !== null || JSON.stringify(unavailable.business) !== JSON.stringify(envelope.business)) throw new Error('unavailable_changes_business');
        }
      }
      installed.push({ scenario: current.name, packages: current.resolved, freshCaches: true, sourceLinks: false, exactBytes: true });
    }
    const execution = input.packagesOnly ? null : await uploaderProof(products, manifest, selected.archive, workspace);
    const evidence = { format: 1, profile: baseline.role, registryAccessVerified: false, publishedApiBytesVerified: baseline.role === 'genuine-published-api-bytes',
      agentApiPackageSha256: baseline.packageSha256, agentApiDllSha256: baseline.dllSha256, sourceCommit: manifest.source.commit,
      packageSetSha256: manifest.packageSetSha256, candidateSha256: manifest.candidateSha256, packages: manifest.packages.map((p: any) => ({ id: p.id, version: p.version, sha256: p.sha256 })),
      stage: input.packagesOnly ? 'installed-packages-only' : 'Linux-uploader-and-installed-packages', installed, execution, publicationPerformed: false };
    if (input.report) writeFileSync(resolve(input.report), JSON.stringify(evidence, null, 2) + '\n', { flag: 'wx' });
    console.log(JSON.stringify(evidence)); return evidence;
  } finally { rmSync(workspace, { recursive: true, force: true }); }
}
if (main(import.meta.url)) {
  try {
    const args = options(['--feed', '--manifest', '--scenario', '--api-package', '--packages-only', '--consumers', '--report'], ['--packages-only']);
    await verifyConsumers({ feed: required(args, '--feed'), manifest: required(args, '--manifest'), scenario: required(args, '--scenario'), apiPackage: args['--api-package'], packagesOnly: args['--packages-only'] === 'true', consumers: args['--consumers'], report: args['--report'] });
  } catch (error) { report(error); }
}
