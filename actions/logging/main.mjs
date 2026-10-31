import { appendFile, mkdtemp, chmod, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { exactVersion, inspectArchive, extractArchive } from './archive.mjs';
import { artifactUrl, downloadArtifact } from './transport.mjs';
import { recordInput, supportedHost } from './input.mjs';
import { childEnvironment, runCli, deliveryResult } from './process.mjs';

const diagnosticCodes = new Set(['none', 'input_unavailable', 'invalid_config', 'invalid_record', 'record_too_large', 'unsupported_schema',
  'unsupported_platform', 'artifact_unavailable', 'invalid_artifact', 'process_failure', 'authentication_failed', 'schema_rejected', 'identity_conflict',
  'rejected', 'protocol_failure', 'attempts_exhausted', 'deadline_exceeded', 'cancelled', 'sink_failure']);
export const escapeCommand = value => value.replaceAll('%', '%25').replaceAll('\r', '%0D').replaceAll('\n', '%0A');
const get = (env, name, fallback = '') => env[`INPUT_${name.toUpperCase()}`] ?? fallback;
function number(value, min, max) { if (!/^[0-9]{1,6}$/.test(value) || Number(value) < min || Number(value) > max) throw new Error('invalid_config'); return Number(value); }
function storageOptions(endpoint, key, token) {
  let url; try { url = new URL(endpoint); } catch { throw new Error('invalid_config'); }
  if (endpoint.length > 2048 || url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash
    || key.length < 16 || key.length > 1024 || !/^sb_publishable_[A-Za-z0-9_-]+$/.test(key)
    || token.length < 5 || token.length > 8192 || !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token)) throw new Error('invalid_config');
}

export async function runAction(env = process.env, write = text => process.stdout.write(text)) {
  let strict = false; let directory; let result; let signal;
  const cancellation = new AbortController();
  const cancel = () => cancellation.abort();
  process.once('SIGINT', cancel); process.once('SIGTERM', cancel);
  try {
    // Runner must also receive these through secrets/already-masked values before it renders inputs.
    for (const name of ['supabase-publishable-key', 'supabase-access-token', 'artifact-read-token']) {
      const secret = get(env, name); if (secret && secret.length <= 8192) write(`::add-mask::${escapeCommand(secret)}\n`);
    }
    const policy = get(env, 'strict-delivery', 'false');
    if (!['true', 'false'].includes(policy)) { strict = true; throw new Error('invalid_config'); }
    strict = policy === 'true';
    if (!await supportedHost()) throw new Error('unsupported_platform');
    const timeout = number(get(env, 'timeout-ms', '10000'), 1, 120000);
    const attempts = number(get(env, 'maximum-attempts', '3'), 1, 10);
    const delay = number(get(env, 'retry-delay-ms', '200'), 0, 10000);
    if (get(env, 'adapter', 'supabase') !== 'supabase') throw new Error('invalid_config');
    signal = AbortSignal.any([cancellation.signal, AbortSignal.timeout(45000 + timeout)]);
    const input = await recordInput(get(env, 'telemetry-json'), get(env, 'telemetry-json-file'), env);
    let text; try { text = new TextDecoder('utf-8', { fatal: true }).decode(input); } catch { throw new Error('invalid_record'); }
    if (!text.trim()) { result = { deliveryStatus: 'skipped', diagnosticCode: 'input_unavailable', attempts: 0 }; }
    else {
      let record; try { record = JSON.parse(text); } catch { throw new Error('invalid_record'); }
      if (!record || Array.isArray(record) || typeof record !== 'object') throw new Error('invalid_record');
      const endpoint = get(env, 'supabase-url'); const key = get(env, 'supabase-publishable-key'); const token = get(env, 'supabase-access-token');
      storageOptions(endpoint, key, token);
      const version = get(env, 'cli-version'); const url = get(env, 'cli-artifact-url'); const digest = get(env, 'cli-artifact-sha256');
      const readToken = get(env, 'artifact-read-token');
      if (!exactVersion(version) || !/^[a-f0-9]{64}$/.test(digest) || readToken.length > 8192 || /[\x00-\x20\x7f]/.test(readToken)) throw new Error('invalid_config');
      artifactUrl(url);
      const archive = await downloadArtifact(url, readToken, signal);
      const verified = inspectArchive(archive, digest, version);
      directory = await mkdtemp(join(tmpdir(), 'solus-logging-')); await chmod(directory, 0o700);
      const executable = await extractArchive(verified, directory);
      const identity = await runCli(executable, ['--version'], Buffer.alloc(0), childEnvironment(env), 5000, signal);
      let value; try { value = JSON.parse(identity.output); } catch { throw new Error('invalid_artifact'); }
      if (identity.code !== 0 || identity.stderr !== 0 || identity.signal || identity.error || !value
        || Object.keys(value).sort().join(',') !== 'protocol,runtimeIdentifier,version' || value.protocol !== 1
        || value.version !== version || value.runtimeIdentifier !== 'linux-x64') throw new Error('invalid_artifact');
      const delivered = await runCli(executable, ['--adapter', 'supabase', '--endpoint', endpoint, '--maximum-attempts', String(attempts),
        '--timeout-ms', String(timeout), '--retry-delay-ms', String(delay)], input,
      childEnvironment(env, { SOLUS_LOGGING_PUBLISHABLE_KEY: key, SOLUS_LOGGING_ACCESS_JWT: token }), timeout + 500, signal);
      result = deliveryResult(delivered, attempts);
    }
  } catch (error) {
    let diagnostic = diagnosticCodes.has(error.message) ? error.message : 'process_failure';
    if (cancellation.signal.aborted) diagnostic = 'cancelled';
    else if (signal?.aborted) diagnostic = 'deadline_exceeded';
    result = { deliveryStatus: 'failed', diagnosticCode: diagnostic, attempts: 0 };
  } finally {
    process.removeListener('SIGINT', cancel); process.removeListener('SIGTERM', cancel);
    if (directory) await rm(directory, { recursive: true, force: true });
  }
  if (env.GITHUB_OUTPUT) await appendFile(env.GITHUB_OUTPUT, `delivery-status=${result.deliveryStatus}\ndiagnostic-code=${result.diagnosticCode}\n`);
  const command = result.deliveryStatus === 'failed' ? strict ? 'error' : 'warning' : 'notice';
  write(`::${command}::Telemetry delivery ${result.deliveryStatus} (${result.diagnosticCode}).\n`);
  return { ...result, exitCode: strict && result.deliveryStatus === 'failed' ? 1 : 0 };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { process.exitCode = (await runAction()).exitCode; }
  catch { process.stdout.write('::error::Telemetry delivery failed (process_failure).\n'); process.exitCode = 1; }
}
