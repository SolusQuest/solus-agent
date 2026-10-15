import { resolve } from 'node:path';
import { inspectPackages, packages, requireVersion } from '../packages/common.ts';

export const requiredScenarios = ['foundation', 'apr', 'scribe', 'smoke-offline'];
export interface ReleaseContext { event: string; ref: string; sha: string; checkoutSha: string; version: string }
export interface PackageMetadata { name: string; visibility: string; package_type: string }
export interface ReleaseOperations {
    packages(): Promise<PackageMetadata[]>;
    versions(id: string): Promise<string[]>;
    tagExists(tag: string): Promise<boolean>;
    push(path: string): Promise<void>;
    tag(tag: string, sha: string): Promise<void>;
}
export function validateContext(context: ReleaseContext): void {
    requireVersion(context.version);
    if (context.event !== 'workflow_dispatch' || context.ref !== 'refs/heads/main'
        || !/^[a-f0-9]{40}$/.test(context.sha) || context.checkoutSha !== context.sha)
        throw new Error('release: manual main dispatch and exact selected source checkout are required');
}

export async function publish(context: ReleaseContext, feed: string, access: string | undefined, operations: ReleaseOperations, inspect = inspectPackages): Promise<void> {
    validateContext(context);
    if (access !== 'private-no-inheritance') throw new Error('privacy: maintainer must verify private creation, disabled inheritance, intended ACL and publisher Actions access before setting SOLUS_INTERNAL_PACKAGE_ACCESS=private-no-inheritance');
    inspect(feed, context.version);
    const tag = `v${context.version}`;
    if (await operations.tagExists(tag)) throw new Error('conflict: release tag already exists; select a new internal version');
    const metadata = await operations.packages();
    if (!Array.isArray(metadata)) throw new Error('registry: unknown readable package inventory');
    for (const id of Object.keys(packages)) {
        const entries = metadata.filter(value => value.name?.toLowerCase() === id.toLowerCase());
        if (entries.length > 1) throw new Error('registry: ambiguous package identity');
        if (entries.length) {
            if (entries[0].visibility !== 'private' || entries[0].package_type !== 'nuget') throw new Error(`privacy: ${id} is not a verified private NuGet package`);
            const versions = await operations.versions(id);
            if (!Array.isArray(versions) || versions.some(value => typeof value !== 'string')) throw new Error('registry: unknown version inventory');
            if (versions.includes(context.version)) throw new Error(`conflict: ${id} already contains the selected version; select a new internal version`);
        }
    }
    const completed: string[] = [];
    for (const id of Object.keys(packages)) {
        try { await operations.push(resolve(feed, `${id}.${context.version}.nupkg`)); }
        catch { throw new Error(`publication stopped: successful=[${completed.join(',')}]; failed-or-unknown=${id}; no success tag created; do not overwrite/delete; choose a new internal version`); }
        completed.push(id);
    }
    try { await operations.tag(tag, context.sha); }
    catch { throw new Error('tag failed or outcome unknown after five successful pushes; inspect original run, do not overwrite/delete or replay this version'); }
}
