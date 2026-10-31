import { createHash } from 'node:crypto';
import { gzipSync, gunzipSync } from 'node:zlib';
import { writeFile, chmod } from 'node:fs/promises';
import { join } from 'node:path';

export const limits = Object.freeze({ compressed: 128 * 1024 ** 2, expanded: 256 * 1024 ** 2, file: 64 * 1024 ** 2, files: 512 });
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
export const exactVersion = value => typeof value === 'string' && /^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/.test(value) && value.length <= 80;
const nameOkay = name => /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/.test(name) && !name.includes('..');
const fail = () => { throw new Error('invalid_artifact'); };

// An owned flat USTAR subset: no links, directories, PAX headers or arbitrary tar dialects.
function header(name, size) {
  if (!nameOkay(name) || size > limits.file || size < 0) fail();
  const block = Buffer.alloc(512);
  block.write(name, 0, 100, 'ascii');
  const octal = (offset, width, value) => block.write(value.toString(8).padStart(width - 1, '0') + '\0', offset, width, 'ascii');
  octal(100, 8, name === 'Solus.Logging.Cli' ? 0o755 : 0o644);
  octal(108, 8, 0); octal(116, 8, 0); octal(124, 12, size); octal(136, 12, 0);
  block.fill(32, 148, 156); block[156] = 48;
  block.write('ustar\0', 257, 6, 'ascii'); block.write('00', 263, 2, 'ascii');
  const checksum = block.reduce((total, value) => total + value, 0);
  block.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'ascii');
  return block;
}

export function createArchive(files) {
  if (files.size > limits.files) fail();
  const parts = [];
  for (const [name, bytes] of [...files].sort(([a], [b]) => a.localeCompare(b, 'en'))) {
    parts.push(header(name, bytes.length), bytes, Buffer.alloc((512 - bytes.length % 512) % 512));
  }
  parts.push(Buffer.alloc(1024));
  const tar = Buffer.concat(parts); if (tar.length > limits.expanded) fail();
  const compressed = gzipSync(tar, { level: 9 }); if (compressed.length > limits.compressed) fail();
  return compressed;
}

export function inspectArchive(compressed, digest, version) {
  if (!exactVersion(version) || !/^[a-f0-9]{64}$/.test(digest) || compressed.length > limits.compressed || sha256(compressed) !== digest) fail();
  let tar; try { tar = gunzipSync(compressed, { maxOutputLength: limits.expanded }); } catch { fail(); }
  const files = new Map(); let offset = 0;
  while (offset + 512 <= tar.length && !tar.subarray(offset, offset + 512).every(b => b === 0)) {
    const block = tar.subarray(offset, offset + 512);
    const nul = block.subarray(0, 100).indexOf(0);
    if (nul < 1) fail();
    const name = block.subarray(0, nul).toString('ascii');
    const sizeField = block.subarray(124, 136).toString('ascii');
    if (!/^[0-7]{11}\0$/.test(sizeField)) fail();
    const size = Number.parseInt(sizeField, 8);
    if (!nameOkay(name) || files.has(name) || files.size >= limits.files || !block.equals(header(name, size))) fail();
    const end = offset + 512 + size;
    const paddedEnd = end + (512 - size % 512) % 512;
    if (paddedEnd > tar.length || !tar.subarray(end, paddedEnd).every(b => b === 0)) fail();
    files.set(name, tar.subarray(offset + 512, end)); offset = paddedEnd;
  }
  if (tar.length !== offset + 1024 || !tar.subarray(offset).every(b => b === 0)) fail();
  const manifestBytes = files.get('payload-manifest.json'); if (!manifestBytes || manifestBytes.length > 100000) fail();
  let manifest; try { manifest = JSON.parse(manifestBytes.toString('utf8')); } catch { fail(); }
  if (!Buffer.from(JSON.stringify(manifest) + '\n').equals(manifestBytes)
    || Object.keys(manifest).join(',') !== 'format,version,runtimeIdentifier,entrypoint,files'
    || manifest.format !== 1 || manifest.version !== version || manifest.runtimeIdentifier !== 'linux-x64'
    || manifest.entrypoint !== 'Solus.Logging.Cli' || !manifest.files || Array.isArray(manifest.files)
    || Object.keys(manifest.files).length !== files.size - 1) fail();
  for (const [name, bytes] of files) {
    if (name === 'payload-manifest.json') continue;
    if (manifest.files[name] !== sha256(bytes)) fail();
  }
  const executable = files.get(manifest.entrypoint);
  if (!executable || executable.length < 64 || executable.subarray(0, 5).toString('hex') !== '7f454c4602'
    || executable[5] !== 1 || executable.readUInt16LE(18) !== 62 || !files.has('Solus.Logging.Cli.dll')
    || !files.has('Solus.Logging.Cli.runtimeconfig.json') || !files.has('libcoreclr.so')) fail();
  return { files, manifest };
}

export async function extractArchive(verified, directory) {
  // Caller supplies its new private temp directory. All names were verified before any write.
  for (const [name, bytes] of verified.files) await writeFile(join(directory, name), bytes, { flag: 'wx', mode: 0o600 });
  const executable = join(directory, verified.manifest.entrypoint);
  await chmod(executable, 0o700); return executable;
}
