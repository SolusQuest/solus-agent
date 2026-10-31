import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readdir, readFile, writeFile, rm } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { createArchive, inspectArchive, sha256, exactVersion } from '../../actions/logging/archive.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const args = process.argv.slice(2); const options = new Map();
for (let i = 0; i < args.length; i += 2) {
  if (!['--output', '--version'].includes(args[i]) || !args[i + 1] || options.has(args[i])) throw new Error('Expected --output <directory> [--version <exact-version>].');
  options.set(args[i], args[i + 1]);
}
const version = options.get('--version') ?? '0.1.0-internal.1';
if (!exactVersion(version)) throw new Error('An exact payload version is required.');
const output = resolve(root, options.get('--output') ?? 'artifacts/logging-cli');
const temporary = await mkdtemp(join(tmpdir(), 'solus-logging-build-'));
try {
  const publish = join(temporary, 'publish');
  const built = spawnSync('dotnet', ['publish', join(root, 'src/Solus.Logging.Cli/Solus.Logging.Cli.csproj'), '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
    '-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', `-p:Version=${version}`, `-p:InformationalVersion=${version}`,
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-o', publish], { cwd: root, stdio: 'inherit', timeout: 180000, killSignal: 'SIGKILL' });
  if (built.error || built.status !== 0) throw new Error('Payload publish failed.');
  const files = new Map();
  for (const entry of await readdir(publish, { withFileTypes: true })) {
    if (!entry.isFile()) throw new Error('The payload format requires flat regular files.');
    files.set(entry.name, await readFile(join(publish, entry.name)));
  }
  const hashes = Object.fromEntries([...files].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([name, bytes]) => [name, sha256(bytes)]));
  files.set('payload-manifest.json', Buffer.from(JSON.stringify({ format: 1, version, runtimeIdentifier: 'linux-x64', entrypoint: 'Solus.Logging.Cli', files: hashes }) + '\n'));
  const archive = createArchive(files); const digest = sha256(archive);
  inspectArchive(archive, digest, version);
  await mkdir(output, { recursive: true });
  const filename = `solus-logging-cli-${version}-linux-x64.tar.gz`;
  await writeFile(join(output, filename), archive);
  await writeFile(join(output, filename + '.sha256'), `${digest}  ${filename}\n`);
  await writeFile(join(output, 'payload.json'), JSON.stringify({ format: 1, version, runtimeIdentifier: 'linux-x64', filename, sha256: digest }, null, 2) + '\n');
  console.log(`Prepared ${filename} (${archive.length} bytes), SHA-256 ${digest}. No artifact was published.`);
} finally { await rm(temporary, { recursive: true, force: true }); }
