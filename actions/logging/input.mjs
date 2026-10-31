import { constants } from 'node:fs';
import { open, lstat, realpath, readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute, sep, parse, join } from 'node:path';

export const inside = (path, root) => { const rel = relative(root, path); return rel !== '..' && !rel.startsWith('..' + sep) && !isAbsolute(rel); };
const fail = diagnostic => { throw new Error(diagnostic); };

export async function recordInput(inline, filename, env) {
  if (inline.length && filename.length) fail('invalid_config');
  if (!filename) {
    if (Buffer.byteLength(inline, 'utf8') > 65536) fail('record_too_large');
    return Buffer.from(inline, 'utf8');
  }
  if (filename.length > 4096 || filename.includes('\0')) fail('invalid_config');
  let file;
  try {
    const roots = await Promise.all([env.GITHUB_WORKSPACE, env.RUNNER_TEMP].filter(Boolean).map(root => realpath(root)));
    const path = resolve(env.GITHUB_WORKSPACE ?? process.cwd(), filename);
    if (!roots.some(root => inside(path, root))) fail('invalid_config');
    let component = parse(path).root;
    for (const name of path.slice(component.length).split(sep)) {
      component = join(component, name); if ((await lstat(component)).isSymbolicLink()) fail('invalid_config');
    }
    const before = await lstat(path);
    if (!before.isFile() || before.size > 262144) fail(before.size > 262144 ? 'record_too_large' : 'invalid_config');
    file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
    const actual = await realpath(`/proc/self/fd/${file.fd}`);
    const after = await file.stat();
    if (!after.isFile() || before.dev !== after.dev || before.ino !== after.ino || actual !== path || !roots.some(root => inside(actual, root))) fail('invalid_config');
    const bytes = Buffer.alloc(262145); let size = 0;
    while (size < bytes.length) { const { bytesRead } = await file.read(bytes, size, bytes.length - size, null); if (!bytesRead) break; size += bytesRead; }
    if (size > 262144) fail('record_too_large');
    return bytes.subarray(0, size);
  } catch (error) {
    if (['record_too_large', 'invalid_config'].includes(error.message)) throw error;
    fail('input_unavailable');
  } finally { await file?.close(); }
}

export async function supportedHost() {
  if (process.platform !== 'linux' || process.arch !== 'x64') return false;
  try {
    const os = await readFile('/etc/os-release', 'utf8');
    return /^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os);
  } catch { return false; }
}
