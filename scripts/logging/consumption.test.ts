import test from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { root, api, scratch, sha, ToolFailure, run } from './common.ts';
import { readPackage, canonicalPackage } from './package-archive.ts';
import { validateCandidate, publicationFiles, candidateDigest } from './manifest.ts';
import { installScenario } from './installed.ts';
import { pack } from './pack.ts';

const manifestPath = resolve(process.env.LOGGING_TEST_MANIFEST ?? 'artifacts/logging-release.json');
const directory = dirname(manifestPath); const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const feed = resolve(directory, manifest.packageDirectory); const baselineFeed = resolve(directory, manifest.validationBaseline.directory);

test('canonical OPC metadata preserves semantic package entries and is idempotent', () => {
  const p = manifest.packages[0]; const inspected = readPackage(join(feed, p.file), p.id, p.version);
  assert.equal(sha(canonicalPackage(inspected.entries, p.id, p.version)), p.sha256);
  const changed = new Map(inspected.entries); const old = [...changed.keys()].find(n => n.endsWith('.psmdcp'))!;
  const alternate = old.replace(/[a-f0-9]{32}\.psmdcp$/, '01234567890123456789012345678901.psmdcp');
  changed.set(alternate, changed.get(old)!); changed.delete(old);
  changed.set('_rels/.rels', Buffer.from(changed.get('_rels/.rels')!.toString().replace(old, alternate).replace('Id="RCoreProperties"', 'Id="RRandomSDKValue"')));
  assert.equal(sha(canonicalPackage(changed, p.id, p.version)), p.sha256);
  for (const name of [`${p.id}.nuspec`, `lib/net10.0/${p.id}.dll`, `lib/net10.0/${p.id}.xml`, '[Content_Types].xml']) assert.deepEqual(changed.get(name), inspected.entries.get(name));
});

test('candidate selection, integrity and publication input roles fail closed', async () => {
  assert.equal(validateCandidate(feed, directory, manifest).archive[9], 255);
  const bad = structuredClone(manifest); bad.packages[0].sha256 = '0'.repeat(64);
  assert.throws(() => validateCandidate(feed, directory, bad), /candidate_hash/);
  const range = structuredClone(manifest); range.packages[1].dependencies[api.id] = '0.1.0-internal.3';
  assert.throws(() => validateCandidate(feed, directory, range), /candidate_dependencies/);
  const payload = structuredClone(manifest); payload.payload.manifestSha256 = '0'.repeat(64);
  assert.throws(() => validateCandidate(feed, directory, payload), /payload_manifest_hash/);
  const fixture = structuredClone(manifest); fixture.source.dirty = false;
  fixture.validationBaseline.role = 'source-baseline-fixture'; fixture.candidateSha256 = candidateDigest(fixture);
  assert.throws(() => publicationFiles(feed, directory, fixture, join(baselineFeed, manifest.validationBaseline.file)), /genuine_baseline_publication_gate/);
  await assert.rejects(() => pack(manifest.version, feed), /candidate_overwrite/);
  const temporary = scratch('logging-extra-feed-');
  try { cpSync(feed, temporary, { recursive: true }); writeFileSync(join(temporary, 'unselected.nupkg'), 'extra');
    assert.throws(() => validateCandidate(temporary, directory, manifest), /candidate_feed_inventory/);
  } finally { rmSync(temporary, { recursive: true, force: true }); }
});

// Every mutation goes through the same evaluated/cold-restore path as the normal verifier.
async function mutation(change: (directory: string, owned: string) => void, expected: RegExp, mutateFeed?: (directory: string) => void) {
  const owned = scratch('logging-consumer-negative-');
  try {
    const original = join(owned, 'input'); cpSync(join(root, 'tests/TelemetryConsumers/Scribe'), original, { recursive: true });
    change(original, owned); const selectedFeed = join(owned, 'feed'); cpSync(feed, selectedFeed, { recursive: true });
    mutateFeed?.(selectedFeed);
    assert.throws(() => installScenario(original, owned, 'independent', selectedFeed, baselineFeed, manifest, manifest.validationBaseline), error => {
      assert.match(error instanceof Error ? error.message : '', expected);
      if (error instanceof ToolFailure) assert.equal(error.command, 'dotnet');
      return true;
    });
  } finally { rmSync(owned, { recursive: true, force: true }); }
}
function project(directory: string, replace: (source: string) => string) {
  const path = join(directory, 'Scribe.csproj'); writeFileSync(path, replace(readFileSync(path, 'utf8')));
}
test('external project links are rejected by evaluated boundaries', () => mutation((directory) => project(directory, s => s.replace('</Project>', `<ItemGroup><ProjectReference Include="${join(root, 'src/Solus.Telemetry.Contracts/Solus.Telemetry.Contracts.csproj')}"/></ItemGroup></Project>`)), /boundary_project/));
test('linked compile source is rejected by evaluated boundaries', () => mutation((directory, owned) => {
  const external = join(owned, 'outside.cs'); writeFileSync(external, '// synthetic external source');
  project(directory, s => s.replace('</Project>', `<ItemGroup><Compile Include="${external}" Link="outside.cs"/></ItemGroup></Project>`));
}, /boundary_compile/));
test('direct assembly references are rejected', () => mutation(directory => project(directory, s => s.replace('</Project>', '<ItemGroup><Reference Include="Unselected"><HintPath>/unselected.dll</HintPath></Reference></ItemGroup></Project>')), /boundary_assembly/));
test('external imports are rejected after actual MSBuild evaluation', () => mutation((directory, owned) => {
  const external = join(owned, 'outside.props'); writeFileSync(external, '<Project/>');
  project(directory, s => s.replace('</Project>', `<Import Project="${external}"/></Project>`));
}, /boundary_import/));
test('friend assembly identities and undeclared project inventory are rejected', async () => {
  await mutation(directory => project(directory, s => s.replace('</PropertyGroup>', '<AssemblyName>Solus.Logging.Tests</AssemblyName></PropertyGroup>')), /boundary_identity/);
  await mutation(directory => writeFileSync(join(directory, 'Undeclared.csproj'), '<Project/>'), /scenario_inventory/);
});
test('non-exact and wrong requested package versions are rejected', async () => {
  await mutation(directory => project(directory, s => s.replace('[$(TelemetryVersion)]', '$(TelemetryVersion)')), /boundary_package_version/);
  await mutation(directory => project(directory, s => s.replace('[$(AgentApiVersion)]', '[0.1.0-internal.3]')), /boundary_package_version/);
});
test('missing candidate versions cannot be rescued by any ambient warm cache', () => mutation(() => {}, /tool_failure:dotnet:.*NU1101/, selected => rmSync(join(selected, manifest.packages[0].file))));
test('a wrong version in an otherwise valid package feed cannot satisfy exact pins', () => mutation(() => {}, /tool_failure:dotnet:.*NU1102/, selected => {
  const p = manifest.packages[0]; const path = join(selected, p.file); const original = readPackage(path, p.id, p.version);
  const wrong = `0.1.0-internal.${BigInt(manifest.version.split('.').at(-1)) + 1n}`; original.entries.set(`${p.id}.nuspec`, Buffer.from(original.nuspec.replace(`<version>${p.version}</version>`, `<version>${wrong}</version>`)));
  rmSync(path); writeFileSync(join(selected, `${p.id}.${wrong}.nupkg`), canonicalPackage(original.entries, p.id, wrong));
}));
test('wrong source cannot rescue an exact API baseline mapping', async () => {
  const owned = scratch('logging-wrong-source-');
  try {
    const wrong = join(owned, 'candidate'); cpSync(feed, wrong, { recursive: true });
    cpSync(join(baselineFeed, manifest.validationBaseline.file), join(wrong, manifest.validationBaseline.file));
    const empty = join(owned, 'expected-api-feed'); mkdirSync(empty);
    assert.throws(() => installScenario(join(root, 'tests/TelemetryConsumers/Scribe'), owned, 'consumer', wrong, empty, manifest, manifest.validationBaseline), /tool_failure:dotnet:.*NU1101/);
  } finally { rmSync(owned, { recursive: true, force: true }); }
});

test('a genuinely populated unrelated cache cannot rescue missing candidates or API; discovery accepts an independent name', () => {
  const owned = scratch('logging-cold-cache-'); const previous = process.env.NUGET_PACKAGES;
  try {
    const installed = installScenario(join(root, 'tests/TelemetryConsumers/Scribe'), owned, 'RenamedIndependentProduct', feed, baselineFeed, manifest, manifest.validationBaseline);
    const marker = join(owned, 'once');
    const observed = JSON.parse(run('dotnet', [installed.executable, '30000000-0000-0000-0000-000000000001', '{"custom":true}', marker], installed.directory, installed.env).toString());
    assert.equal(observed.business.status, 'succeeded'); assert.equal(JSON.parse(observed.telemetryJson).extensions.caller.custom, true);
    process.env.NUGET_PACKAGES = installed.cache;
    const empty = join(owned, 'empty'); mkdirSync(empty);
    for (const [name, candidate, apiFeed] of [['missing-candidates', empty, baselineFeed], ['missing-api', feed, empty]]) {
      assert.throws(() => installScenario(join(root, 'tests/TelemetryConsumers/Scribe'), owned, name, candidate, apiFeed, manifest, manifest.validationBaseline), /tool_failure:dotnet:.*NU1101/);
    }
  } finally {
    if (previous === undefined) delete process.env.NUGET_PACKAGES; else process.env.NUGET_PACKAGES = previous;
    rmSync(owned, { recursive: true, force: true });
  }
});
