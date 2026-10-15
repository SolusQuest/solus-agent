import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { isMain, options, reportFailure, required } from '../packages/common.ts';
import { environmentContext } from './prepare.ts';
import { publish, type PackageMetadata, type ReleaseOperations } from './core.ts';

export function githubOperations(owner: string, repo: string, token: string): ReleaseOperations {
    const base = 'https://api.github.com';
    async function request(path: string, method = 'GET', body?: unknown, allowMissing = false): Promise<Response> {
        const response = await fetch(`${base}${path}`, { method, headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'error', signal: AbortSignal.timeout(30_000) });
        if (!response.ok && !(allowMissing && response.status === 404)) throw new Error(`registry: API operation failed (${response.status}); verify token permissions and access settings`);
        return response;
    }
    async function pages(path: string): Promise<unknown[]> {
        const results: unknown[] = [];
        for (let page = 1; page <= 100; page++) {
            const response = await request(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`);
            const values = await response.json();
            if (!Array.isArray(values)) throw new Error('registry: malformed paginated response');
            results.push(...values);
            if (values.length < 100) return results;
        }
        throw new Error('registry: incomplete pagination; refusing publication');
    }
    return {
        packages: async () => await pages(`/orgs/${owner}/packages?package_type=nuget`) as PackageMetadata[],
        versions: async id => (await pages(`/orgs/${owner}/packages/nuget/${encodeURIComponent(id)}/versions`)).map(value => (value as { name: string }).name),
        tagExists: async tag => (await request(`/repos/${owner}/${repo}/git/ref/tags/${encodeURIComponent(tag)}`, 'GET', undefined, true)).status !== 404,
        push: async path => {
            const result = spawnSync('dotnet', ['nuget', 'push', path, '--source', `https://nuget.pkg.github.com/${owner}/index.json`, '--api-key', token, '--timeout', '120'], { stdio: 'pipe', timeout: 150_000, maxBuffer: 1024 * 1024 });
            if (result.error || result.status !== 0) throw new Error('registry: push failed or outcome unknown');
        },
        tag: async (tag, sha) => { await request(`/repos/${owner}/${repo}/git/refs`, 'POST', { ref: `refs/tags/${tag}`, sha }); },
    };
}
if (isMain(import.meta.url)) {
    try {
        const args = options(['--version', '--feed']);
        const target = process.env.GITHUB_REPOSITORY ?? '';
        if (target !== 'SolusQuest/solus-agent' || !process.env.GITHUB_TOKEN) throw new Error('release: expected repository and scoped workflow token are required');
        await publish(environmentContext(required(args, '--version')), resolve(required(args, '--feed')), process.env.SOLUS_INTERNAL_PACKAGE_ACCESS,
            githubOperations('SolusQuest', 'solus-agent', process.env.GITHUB_TOKEN));
        console.log('Published five validated private packages and created the selected-source version tag.');
    } catch (error) {
        // Never serialize transport errors, credentials or registry response bodies.
        reportFailure(error instanceof Error && /^(version:|arguments:|archive:|release:|privacy:|conflict:|registry: API operation|registry: incomplete|registry: malformed|registry: unknown|registry: ambiguous|publication stopped:|tag failed)/.test(error.message)
            ? error : new Error('publication failed: registry or transport outcome unknown; inspect original run before any recovery'));
    }
}
