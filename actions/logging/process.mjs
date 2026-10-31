import { spawn } from 'node:child_process';

export function childEnvironment(host, storage = {}) {
  const env = { DOTNET_EnableDiagnostics: '0', DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION: '0', ...storage };
  for (const key of ['LANG', 'LC_ALL', 'TZ', 'SSL_CERT_FILE', 'SSL_CERT_DIR']) if (host[key]) env[key] = host[key];
  return env;
}

export async function runCli(executable, args, input, env, timeoutMs, signal) {
  return new Promise(resolve => {
    let child; let finished = false; let terminating = false; let reason; let output = ''; let stderr = 0; let outputBytes = 0;
    let grace; let finalWait;
    const finish = result => {
      if (finished) return; finished = true;
      clearTimeout(timer); clearTimeout(grace); clearTimeout(finalWait); signal.removeEventListener('abort', cancel);
      child?.stdin.destroy(); child?.stdout.destroy(); child?.stderr.destroy(); resolve(result);
    };
    const stop = diagnostic => {
      if (finished || terminating) return;
      terminating = true; reason = diagnostic; child?.kill('SIGTERM');
      grace = setTimeout(() => {
        child?.kill('SIGKILL');
        finalWait = setTimeout(() => finish({ error: diagnostic }), 1000);
      }, 1000);
    };
    const cancel = () => stop('cancelled');
    const timer = setTimeout(() => stop('deadline_exceeded'), timeoutMs);
    try { child = spawn(executable, args, { env, stdio: ['pipe', 'pipe', 'pipe'], shell: false }); }
    catch { finish({ error: 'process_failure' }); return; }
    signal.addEventListener('abort', cancel, { once: true });
    child.on('error', () => finish({ error: 'process_failure' }));
    child.stdout.on('data', bytes => {
      outputBytes += bytes.length;
      if (outputBytes > 1024) stop('protocol_failure'); else output += bytes.toString('utf8');
    });
    child.stderr.on('data', bytes => { stderr += bytes.length; if (stderr > 1024) stop('protocol_failure'); });
    child.stdin.on('error', () => {});
    child.on('close', (code, killedSignal) => finish(reason ? { error: reason } : { code, signal: killedSignal, output, stderr }));
    if (signal.aborted) cancel(); else child.stdin.end(input);
  });
}

const deliveryCodes = new Set(['none', 'input_unavailable', 'invalid_config', 'invalid_record', 'record_too_large', 'unsupported_schema',
  'authentication_failed', 'schema_rejected', 'identity_conflict', 'rejected', 'protocol_failure', 'attempts_exhausted', 'deadline_exceeded', 'cancelled', 'sink_failure']);
export function deliveryResult(processResult, maximumAttempts) {
  if (processResult.error) return { deliveryStatus: 'failed', diagnosticCode: processResult.error, attempts: 0 };
  let result; try { result = JSON.parse(processResult.output); } catch { throw new Error('protocol_failure'); }
  if (!result || Object.keys(result).sort().join(',') !== 'attempts,deliveryStatus,diagnosticCode'
    || !deliveryCodes.has(result.diagnosticCode) || !Number.isInteger(result.attempts) || result.attempts < 0 || result.attempts > maximumAttempts
    || processResult.stderr !== 0 || processResult.signal) throw new Error('protocol_failure');
  if (result.deliveryStatus === 'stored' || result.deliveryStatus === 'duplicate') {
    if (processResult.code !== 0 || result.diagnosticCode !== 'none' || result.attempts < 1) throw new Error('protocol_failure');
  } else if (result.deliveryStatus === 'skipped') {
    if (processResult.code !== 0 || result.diagnosticCode !== 'input_unavailable' || result.attempts !== 0) throw new Error('protocol_failure');
  } else if (result.deliveryStatus !== 'failed' || processResult.code !== 1 || ['none', 'input_unavailable'].includes(result.diagnosticCode)) throw new Error('protocol_failure');
  return result;
}
