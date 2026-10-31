import https from 'node:https';
import { limits } from './archive.mjs';

export function artifactUrl(value) {
  if (typeof value !== 'string' || value.length > 4096) throw new Error('invalid_config');
  let url; try { url = new URL(value); } catch { throw new Error('invalid_config'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.hash) throw new Error('invalid_config');
  return url;
}

export async function downloadArtifact(url, readToken, parentSignal) {
  const timeout = AbortSignal.timeout(30000);
  const signal = AbortSignal.any([parentSignal, timeout]);
  let current = artifactUrl(url); let token = readToken;
  for (let redirects = 0; redirects <= 3; redirects++) {
    const response = await new Promise((resolve, reject) => {
      const request = https.get(current, { signal, agent: false, maxHeaderSize: 16384,
        headers: token ? { Authorization: `Bearer ${token}` } : {} }, resolve);
      request.on('error', () => reject(new Error('artifact_unavailable')));
    });
    if ([301, 302, 303, 307, 308].includes(response.statusCode)) {
      const location = response.headers.location; response.destroy();
      if (!location || redirects === 3) throw new Error('artifact_unavailable');
      const next = artifactUrl(new URL(location, current).href);
      if (next.origin !== current.origin) token = ''; // Never restore a stripped credential on a later hop.
      current = next; continue;
    }
    if (response.statusCode !== 200 || response.headers['content-encoding'] && response.headers['content-encoding'] !== 'identity'
      || Number(response.headers['content-length']) > limits.compressed) {
      response.destroy(); throw new Error('artifact_unavailable');
    }
    const parts = []; let size = 0;
    try {
      for await (const part of response) {
        size += part.length; if (size > limits.compressed) { response.destroy(); throw new Error(); }
        parts.push(part);
      }
    } catch { response.destroy(); throw new Error('artifact_unavailable'); }
    return Buffer.concat(parts, size);
  }
  throw new Error('artifact_unavailable');
}
