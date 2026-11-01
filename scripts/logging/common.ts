import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, lstatSync, realpathSync, mkdirSync, writeFileSync, mkdtempSync } from 'node:fs';
import { dirname, resolve, relative, isAbsolute, sep, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

export const root = realpathSync(fileURLToPath(new URL('../../', import.meta.url)));
export const api = JSON.parse(readFileSync(new URL('./api-baseline.json', import.meta.url), 'utf8'));
export const ids = ['Solus.Telemetry.Contracts', 'SolusAgent.Telemetry', 'Solus.Logging', 'Solus.Logging.Supabase'];
export const actionSource = '9d2f3777401d71fdea84ce350dd5bd6240acb7a8';
export const sha = (bytes: string | Buffer) => createHash('sha256').update(bytes).digest('hex');
export const fileSha = (path: string) => sha(readFileSync(path));
export const inside = (path: string, parent: string) => { const rel = relative(parent, path); return !isAbsolute(rel) && rel !== '..' && !rel.startsWith('..' + sep); };
export const xml = (text: string) => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
export function version(value: string) { if (!/^0\.1\.0-internal\.(0|[1-9][0-9]*)$/.test(value)) throw new Error('invalid_version'); return value; }
export function options(allowed: string[], flags: string[] = []) {
  const result: Record<string, string> = {};
  for (let i = 2; i < process.argv.length; i++) {
    const key = process.argv[i];
    if (!allowed.includes(key) || Object.hasOwn(result, key)) throw new Error('invalid_arguments');
    const value = flags.includes(key) ? 'true' : process.argv[++i];
    if (!value || value.startsWith('--')) throw new Error('invalid_arguments'); result[key] = value;
  }
  return result;
}
export function required(options: Record<string, string>, key: string) { if (!options[key]) throw new Error('missing_argument'); return options[key]; }
export const main = (url: string) => process.argv[1] && resolve(process.argv[1]) === fileURLToPath(url);

export function cleanEnvironment(home: string): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { DOTNET_CLI_HOME: home, DOTNET_CLI_UI_LANGUAGE: 'en-US', DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_EnableDiagnostics: '0', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false', DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: 'false' };
  for (const key of ['PATH', 'SystemRoot', 'TMPDIR', 'TMP', 'TEMP', 'LANG', 'LC_ALL', 'TZ']) if (process.env[key]) env[key] = process.env[key];
  env.NUGET_PACKAGES = join(home, 'packages'); env.NUGET_HTTP_CACHE_PATH = join(home, 'http'); env.NUGET_PLUGINS_CACHE_PATH = join(home, 'plugins'); env.NUGET_SCRATCH = join(home, 'scratch');
  return env;
}
export class ToolFailure extends Error {
  readonly command: string;
  readonly status: number | null;
  readonly output: string;
  constructor(command: string, status: number | null, output: string) {
    super(`tool_failure:${command}:${status ?? 'execution'}:${[...output.matchAll(/\bNU[0-9]{4}\b/g)].map(m => m[0]).filter((v, i, a) => a.indexOf(v) === i).join(',')}`);
    this.command = command; this.status = status; this.output = output;
  }
}
export function run(command: string, args: string[], cwd = root, env = process.env, allowedExit = [0]): Buffer {
  const result = spawnSync(command, args, { cwd, env, timeout: 180000, killSignal: 'SIGKILL', maxBuffer: 16 * 1024 ** 2 });
  if (result.error || result.signal || !allowedExit.includes(result.status!)) throw new ToolFailure(command, result.status, `${result.stdout ?? ''}\n${result.stderr ?? ''}`);
  return result.stdout;
}
export const text = (command: string, args: string[], cwd = root, env = process.env) => run(command, args, cwd, env).toString('utf8');
export function report(error: unknown) {
  if (error instanceof ToolFailure) {
    const path = join(root, '.local/logging-tool-failures'); mkdirSync(path, { recursive: true });
    writeFileSync(join(path, `failure-${Date.now()}.log`), error.output);
  }
  console.error(error instanceof Error ? error.message : 'logging_tool_failure'); process.exitCode = 1;
}
export function scratch(prefix: string) { return realpathSync(mkdtempSync(join(tmpdir(), prefix))); }
export function files(directory: string): string[] {
  return readdirSync(directory).sort().flatMap(name => {
    if (['bin', 'obj', '.git', '.local', 'artifacts', 'node_modules'].includes(name)) return [];
    const path = join(directory, name); const stat = lstatSync(path);
    if (stat.isSymbolicLink() || !stat.isFile() && !stat.isDirectory()) throw new Error('source_nonregular');
    return stat.isDirectory() ? files(path) : [path];
  });
}
export function dependencies(id: string, selected: string): Record<string, string> {
  return id === 'Solus.Telemetry.Contracts' ? {} : id === 'SolusAgent.Telemetry' ? { 'Solus.Telemetry.Contracts': selected, [api.id]: api.version }
    : id === 'Solus.Logging' ? { 'Solus.Telemetry.Contracts': selected } : { 'Solus.Logging': selected, 'Solus.Telemetry.Contracts': selected };
}
export function nugetConfig(feed: string, baselineFeed: string, runtime = false) {
  return `<configuration><packageSources><clear/><add key="candidate" value="${xml(feed)}"/><add key="baseline" value="${xml(baselineFeed)}"/>${runtime ? '<add key="runtime" value="https://api.nuget.org/v3/index.json"/>' : ''}</packageSources><packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Solus.*"/><package pattern="SolusAgent.*"/></packageSource><packageSource key="baseline"><package pattern="SolusAgent.Api"/></packageSource>${runtime ? '<packageSource key="runtime"><package pattern="Microsoft.NETCore.App.*"/><package pattern="Microsoft.AspNetCore.App.*"/></packageSource>' : ''}</packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><disabledPackageSources><clear/></disabledPackageSources></configuration>\n`;
}
export function apiSource() {
  const paths = [...files(join(root, 'src/SolusAgent.Api')).map(p => relative(root, p).split(sep).join('/')), 'Directory.Build.props', 'global.json'].sort();
  const current: Record<string, string> = {}; const pinned: Record<string, string> = {};
  for (const path of paths) { current[path] = fileSha(join(root, path)); pinned[path] = sha(run('git', ['show', `${api.sourceCommit}:${path}`])); }
  const expected = text('git', ['ls-tree', '-r', '--name-only', api.sourceCommit, '--', 'src/SolusAgent.Api']).trim().split('\n').sort();
  if (JSON.stringify(paths.filter(p => p.startsWith('src/'))) !== JSON.stringify(expected) || JSON.stringify(current) !== JSON.stringify(pinned)) throw new Error('api_source_baseline_changed');
  return sha(JSON.stringify(current));
}
export function selectedSource() {
  const inventory: Record<string, string> = {};
  for (const id of [...ids, 'Solus.Logging.Cli']) for (const path of files(join(root, 'src', id))) inventory[relative(root, path).split(sep).join('/')] = fileSha(path);
  for (const path of files(join(root, 'scripts/logging'))) inventory[relative(root, path).split(sep).join('/')] = fileSha(path);
  inventory['Directory.Build.props'] = fileSha(join(root, 'Directory.Build.props')); inventory['global.json'] = fileSha(join(root, 'global.json'));
  return { commit: text('git', ['rev-parse', 'HEAD']).trim(), tree: text('git', ['rev-parse', 'HEAD^{tree}']).trim(), dirty: text('git', ['status', '--porcelain']).trim().length !== 0, inventorySha256: sha(JSON.stringify(inventory)) };
}
